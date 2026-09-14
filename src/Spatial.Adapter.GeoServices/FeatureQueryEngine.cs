using System.Globalization;
using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Evaluates Feature Service queries (spec §9.1.4) and the Feature (object)
/// resource (spec §9.1.2): matches the supported query subset against the
/// store, applies <c>orderByFields</c> and paging, reprojects to <c>outSR</c>,
/// and writes the Esri JSON responses. Split out of <see cref="FeatureService"/>
/// so the protocol facade keeps only its per-operation surface and the query
/// path's fan-out stays cohesive (ADR-0040).
/// </summary>
internal static class FeatureQueryEngine
{
    /// <summary>Executes a query and writes the spec §9.1.4.3 response.</summary>
    public static async Task<IResult> QueryAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriFeatureQuery query,
        IGeometryOperations operations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var layerCrs = EsriLayerModel.LayerCoordinateReference(dataset.Srid);
        var scheme = EsriObjectIdScheme.For(dataset);
        var queryGeometry = TransformQueryGeometry(query.Geometry, layerCrs, transforms, cancellationToken);
        var matches = await MatchAsync(dataset, store, query, queryGeometry, operations, scheme, cancellationToken);
        return Project(dataset, matches, query, layerCrs, transforms, cancellationToken);
    }

    /// <summary>
    /// Shapes a matched feature set into the spec §9.1.4.3 response: ordering,
    /// the mutually exclusive result shapes, paging, <c>outSR</c> reprojection
    /// and the Esri JSON. Shared by the Feature/Map query path and the Image
    /// Service catalog query (spec §8.0.5, ADR-0051), whose catalog is a
    /// feature-like table of raster items.
    /// </summary>
    internal static IResult Project(
        DatasetDescription dataset,
        IReadOnlyList<MatchedFeature> matches,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var ordered = ApplyOrderBy([.. matches], CompileOrderBy(dataset, query));
        if (query.ReturnIdsOnly)
        {
            return IdsOnly(ordered);
        }

        if (query.ReturnCountOnly)
        {
            return query.ReturnDistinctValues
                ? DistinctCount(dataset, ordered, query)
                : EsriJson.Value(new EsriCountResponse(ordered.Count));
        }

        if (query.ReturnExtentOnly)
        {
            return ExtentOnly(ordered, layerCrs, query.OutSr, transforms, cancellationToken);
        }

        if (query.ReturnDistinctValues)
        {
            return DistinctValues(dataset, ordered, query, layerCrs);
        }

        if (query.OutStatistics is not null)
        {
            return Statistics(dataset, ordered, query);
        }

        if (query.ReturnUniqueIdsOnly)
        {
            return UniqueIdsOnly(dataset, ordered);
        }

        var page = Page(ordered, query);
        var features = page.Items
            .Select(item => TransformFeature(item, query, layerCrs, transforms, cancellationToken))
            .ToArray();
        return WriteFeatures(dataset, layerCrs, query, features, page.Exceeded, page.NextToken);
    }

    /// <summary>
    /// Reads one feature by its Esri <c>OBJECTID</c> (the Feature resource,
    /// spec §9.1.2) and writes the <c>{"feature": ...}</c> envelope. The
    /// object id is resolved exactly as <c>query</c> does — the identity
    /// column when the layer has one, otherwise the scan ordinal — so the
    /// resource agrees with <c>returnIdsOnly</c>.
    /// </summary>
    public static async Task<IResult> FeatureAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        long objectId,
        EsriFeatureQuery query,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var layerCrs = EsriLayerModel.LayerCoordinateReference(dataset.Srid);
        var scheme = EsriObjectIdScheme.For(dataset);
        var batches = await store.ScanAsync(dataset.Id, cancellationToken);
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            ordinal++;
            if (!scheme.TryResolve(feature, ordinal, out var candidate))
            {
                throw new EsriInteropException(
                    EsriErrorCodes.ServerError,
                    $"The identity column of layer '{dataset.Id}' is not an integer.");
            }

            if (candidate != objectId)
            {
                continue;
            }

            var transformed = TransformFeature(new MatchedFeature(objectId, feature), query, layerCrs, transforms, cancellationToken);
            return WriteFeature(transformed, query);
        }

        throw new EsriInteropException(EsriErrorCodes.NotFound, $"Feature {objectId} does not exist in layer '{dataset.Id}'.");
    }

    private static async Task<List<MatchedFeature>> MatchAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriFeatureQuery query,
        IGeometry? queryGeometry,
        IGeometryOperations operations,
        EsriObjectIdScheme scheme,
        CancellationToken cancellationToken)
    {
        var batches = await store.ScanAsync(dataset.Id, cancellationToken);
        var matches = new List<MatchedFeature>();
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            ordinal++;
            if (!scheme.TryResolve(feature, ordinal, out var objectId))
            {
                throw new EsriInteropException(
                    EsriErrorCodes.ServerError,
                    $"The identity column of layer '{dataset.Id}' is not an integer.");
            }

            var uniqueId = EsriUniqueIdScheme.ResolveFor(query, dataset, feature);
            if (Matches(query, feature, objectId, queryGeometry, operations, cancellationToken, uniqueId))
            {
                matches.Add(new MatchedFeature(objectId, feature));
            }
        }

        return matches;
    }

    internal static bool Matches(
        EsriFeatureQuery query,
        Feature feature,
        long objectId,
        IGeometry? queryGeometry,
        IGeometryOperations operations,
        CancellationToken cancellationToken,
        string? uniqueId = null)
    {
        if (query.ObjectIds is { } ids && !ids.Contains(objectId))
        {
            return false;
        }

        // T-036: the string-ID filter (spec §9.1.4, 11.5+). A null unique id
        // never equals a requested id; layers without a string unique-id
        // model are rejected when the match loops resolve the id.
        if (query.UniqueIds is { } wanted
            && (uniqueId is null || !wanted.Contains(uniqueId, StringComparer.Ordinal)))
        {
            return false;
        }

        if (query.Where is { } where && !where.Matches(feature, SyntheticObjectId(objectId)))
        {
            return false;
        }

        if (query.Time is { } time && !MatchesTime(feature, time))
        {
            return false;
        }

        return queryGeometry is null || SpatialMatch(feature, queryGeometry, query.SpatialRel, operations, cancellationToken);
    }

    /// <summary>
    /// Applies the <c>time</c> extent to the feature's date attributes: the
    /// feature matches when any date value falls inside the (inclusive)
    /// bounds, where a <c>null</c> bound is infinite. A feature with no date
    /// values matches unconditionally — ArcGIS Server ignores <c>time</c> on
    /// layers without time-aware (date) fields.
    /// </summary>
    private static bool MatchesTime(Feature feature, EsriTimeExtent time)
    {
        var dated = false;
        foreach (var attribute in feature.Attributes)
        {
            if (attribute.Kind != AttributeKind.DateTimeOffset)
            {
                continue;
            }

            dated = true;
            var milliseconds = attribute.DateTimeOffsetValue.ToUnixTimeMilliseconds();
            if ((time.StartMs is null || milliseconds >= time.StartMs)
                && (time.EndMs is null || milliseconds <= time.EndMs))
            {
                return true;
            }
        }

        return !dated;
    }

    /// <summary>The synthetic <c>OBJECTID</c> a where clause may reference (ADR-0037).</summary>
    private static EsriSyntheticField SyntheticObjectId(long objectId) =>
        new(EsriLayerModel.ObjectIdField, AttributeValue.FromInt64(objectId));

    private static bool SpatialMatch(Feature feature, IGeometry queryGeometry, string spatialRel, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        var geometry = FeatureGeometry.Find(feature);
        if (geometry is null || geometry.Envelope is not { } featureEnvelope || queryGeometry.Envelope is not { } queryEnvelope)
        {
            return false;
        }

        if (string.Equals(spatialRel, EsriFeatureQuery.EnvelopeIntersects, StringComparison.Ordinal))
        {
            return featureEnvelope.Intersects(queryEnvelope);
        }

        if (string.Equals(spatialRel, EsriFeatureQuery.Intersects, StringComparison.Ordinal))
        {
            return !operations.Intersection(geometry, queryGeometry, cancellationToken).IsEmpty;
        }

        return spatialRel switch
        {
            var rel when string.Equals(rel, EsriFeatureQuery.Contains, StringComparison.Ordinal) => Contains(geometry, queryGeometry, featureEnvelope, queryEnvelope, operations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Within, StringComparison.Ordinal) => Contains(queryGeometry, geometry, queryEnvelope, featureEnvelope, operations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Touches, StringComparison.Ordinal) => Touches(geometry, queryGeometry, featureEnvelope, queryEnvelope, operations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Overlaps, StringComparison.Ordinal) => Overlaps(geometry, queryGeometry, featureEnvelope, queryEnvelope, operations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Crosses, StringComparison.Ordinal) => Crosses(geometry, queryGeometry, featureEnvelope, queryEnvelope, operations, cancellationToken),
            _ => throw EsriInteropException.Invalid($"spatialRel '{spatialRel}' is not supported."),
        };
    }

    /// <summary>
    /// DE-9IM contains approximated with the available verbs: the container
    /// envelope must contain the containee envelope and the intersection must
    /// cover the containee (envelope-equal). Boundary cases (containee on the
    /// container boundary) read as contained; exact boundary exclusion needs
    /// a boundary verb the engine does not expose.
    /// </summary>
    private static bool Contains(IGeometry container, IGeometry containee, Envelope containerEnvelope, Envelope containeeEnvelope, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        if (!containerEnvelope.Contains(containeeEnvelope))
        {
            return false;
        }

        var intersection = operations.Intersection(container, containee, cancellationToken);
        return !intersection.IsEmpty && intersection.Envelope is { } envelope && EnvelopesEqual(envelope, containeeEnvelope);
    }

    private static bool Touches(IGeometry left, IGeometry right, Envelope leftEnvelope, Envelope rightEnvelope, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        var intersection = operations.Intersection(left, right, cancellationToken);
        if (intersection.IsEmpty || intersection.Envelope is not { } envelope)
        {
            return false;
        }

        if (Contains(left, right, leftEnvelope, rightEnvelope, operations, cancellationToken)
            || Contains(right, left, rightEnvelope, leftEnvelope, operations, cancellationToken))
        {
            return false;
        }

        return IsDegenerate(envelope);
    }

    private static bool Overlaps(IGeometry left, IGeometry right, Envelope leftEnvelope, Envelope rightEnvelope, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        if (Dimension(left) != Dimension(right))
        {
            return false;
        }

        var intersection = operations.Intersection(left, right, cancellationToken);
        if (intersection.IsEmpty || Dimension(intersection) != Dimension(left))
        {
            return false;
        }

        return !Contains(left, right, leftEnvelope, rightEnvelope, operations, cancellationToken)
            && !Contains(right, left, rightEnvelope, leftEnvelope, operations, cancellationToken);
    }

    private static bool Crosses(IGeometry left, IGeometry right, Envelope leftEnvelope, Envelope rightEnvelope, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        if (Dimension(left) == Dimension(right))
        {
            return false;
        }

        var intersection = operations.Intersection(left, right, cancellationToken);
        if (intersection.IsEmpty)
        {
            return false;
        }

        return !Contains(left, right, leftEnvelope, rightEnvelope, operations, cancellationToken)
            && !Contains(right, left, rightEnvelope, leftEnvelope, operations, cancellationToken);
    }

    private static int Dimension(IGeometry geometry) => geometry.Type switch
    {
        GeometryType.Point or GeometryType.MultiPoint => 0,
        GeometryType.LineString or GeometryType.MultiLineString => 1,
        GeometryType.Polygon or GeometryType.MultiPolygon => 2,
        _ => -1,
    };

    private static bool EnvelopesEqual(Envelope left, Envelope right) =>
        left.MinX == right.MinX && left.MinY == right.MinY && left.MaxX == right.MaxX && left.MaxY == right.MaxY;

    private static bool IsDegenerate(Envelope envelope) => envelope.MinX == envelope.MaxX || envelope.MinY == envelope.MaxY;

    internal static IGeometry? TransformQueryGeometry(
        IGeometry? geometry,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        if (geometry is null || layerCrs is null || geometry.CoordinateReference is not { } source || source == layerCrs)
        {
            return geometry;
        }

        return transforms.Transform(geometry, source.ToString(), layerCrs.Value.ToString(), cancellationToken);
    }

    /// <summary>
    /// Validates <c>orderByFields</c> against the dataset schema and compiles
    /// each entry to a key plus direction. <c>OBJECTID</c> is accepted even
    /// though it is synthetic, because it is the layer's advertised object-id
    /// field and clients (QGIS) order by it for a stable paged sequence.
    /// Unknown fields and geometry fields are typed invalid-argument failures
    /// (HTTP 400).
    /// </summary>
    private static OrderKey[]? CompileOrderBy(DatasetDescription dataset, EsriFeatureQuery query)
    {
        if (query.OrderByFields is not { Count: > 0 } fields)
        {
            return null;
        }

        var keys = new OrderKey[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (string.Equals(field.Name, EsriLayerModel.ObjectIdField, StringComparison.OrdinalIgnoreCase))
            {
                keys[i] = new OrderKey(-1, field.Descending, ObjectId: true);
                continue;
            }

            var index = dataset.Schema.IndexOf(field.Name);
            if (index < 0)
            {
                throw EsriInteropException.Invalid(
                    $"'orderByFields' names unknown field '{field.Name}' in layer '{dataset.Id}'.");
            }

            if (dataset.Schema[index].Kind == AttributeKind.Geometry)
            {
                throw EsriInteropException.Invalid(
                    $"'orderByFields' cannot order by geometry field '{field.Name}' in layer '{dataset.Id}'.");
            }

            keys[i] = new OrderKey(index, field.Descending);
        }

        return keys;
    }

    /// <summary>
    /// Applies the compiled ordering to the matched features before paging.
    /// LINQ's ordering is a stable sort, so equal keys keep their scan order.
    /// </summary>
    private static List<MatchedFeature> ApplyOrderBy(List<MatchedFeature> matches, OrderKey[]? keys)
    {
        if (keys is null)
        {
            return matches;
        }

        IOrderedEnumerable<MatchedFeature>? ordered = null;
        foreach (var key in keys)
        {
            Func<MatchedFeature, AttributeValue> selector = key.ObjectId
                ? match => AttributeValue.FromInt64(match.ObjectId)
                : match => match.Feature[key.Index];
            ordered = ordered is null
                ? Order(matches, selector, key.Descending)
                : ThenOrder(ordered, selector, key.Descending);
        }

        return ordered!.ToList();
    }

    private static IOrderedEnumerable<MatchedFeature> Order(
        IEnumerable<MatchedFeature> matches, Func<MatchedFeature, AttributeValue> selector, bool descending) =>
        descending
            ? matches.OrderByDescending(selector, AttributeValueComparer.Instance)
            : matches.OrderBy(selector, AttributeValueComparer.Instance);

    private static IOrderedEnumerable<MatchedFeature> ThenOrder(
        IOrderedEnumerable<MatchedFeature> ordered, Func<MatchedFeature, AttributeValue> selector, bool descending) =>
        descending
            ? ordered.ThenByDescending(selector, AttributeValueComparer.Instance)
            : ordered.ThenBy(selector, AttributeValueComparer.Instance);

    private static PageResult Page(List<MatchedFeature> matches, EsriFeatureQuery query)
    {
        var offset = Math.Min(ResolveOffset(query), matches.Count);
        var count = EffectivePageSize(query);
        var items = matches.Skip(offset).Take(count).ToArray();
        var exceeded = offset + items.Length < matches.Count;
        return new PageResult(items, exceeded, exceeded ? ResultPagination.Encode(offset + items.Length) : null);
    }

    /// <summary>
    /// The page start: the opaque <c>resultPaginationToken</c> cursor when
    /// the client continues a token workflow, else <c>resultOffset</c>. Parse
    /// already rejects the combination, so the token simply wins by presence.
    /// </summary>
    private static int ResolveOffset(EsriFeatureQuery query) =>
        query.ResultPaginationToken is { } token ? ResultPagination.Decode(token) : query.ResultOffset ?? 0;

    /// <summary>
    /// The <c>returnUniqueIdsOnly</c> response (spec §9.1.4, 11.5+): the
    /// string-ID analogue of <see cref="IdsOnly"/>, symmetric in shape.
    /// Layers without a string unique-id model never reach here — the match
    /// loops reject the param first — so a missing scheme is defensive.
    /// </summary>
    private static IResult UniqueIdsOnly(DatasetDescription dataset, IReadOnlyList<MatchedFeature> matches)
    {
        var scheme = EsriUniqueIdScheme.For(dataset)
            ?? throw EsriInteropException.Invalid(
                $"The 'returnUniqueIdsOnly' parameter is not supported on layer '{dataset.Id}': the layer has no string unique-id field; address its integer features with 'objectIds'.");
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("uniqueIdFieldName", scheme.FieldName);
            writer.WritePropertyName("uniqueIds");
            writer.WriteStartArray();
            foreach (var match in matches)
            {
                writer.WriteStringValue(scheme.Resolve(match.Feature, dataset));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// The effective page cap: <c>maxRecordCount × maxRecordCountFactor</c>
    /// (T-021). <c>returnExceededLimitFeatures</c> is accepted so the REST JS
    /// <c>queryAllFeatures</c> loop runs unmodified; the
    /// <c>exceededTransferLimit</c> flag stays correct either way.
    /// </summary>
    internal static int EffectivePageSize(EsriFeatureQuery query)
    {
        var cap = EsriLayerModel.MaxRecordCount * (query.MaxRecordCountFactor ?? 1);
        return Math.Min(query.ResultRecordCount ?? cap, cap);
    }

    private static MatchedFeature TransformFeature(
        MatchedFeature match,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var feature = match.Feature;
        var geometryIndex = FeatureGeometry.Index(feature.Schema);
        if (geometryIndex < 0 || feature[geometryIndex].Kind != AttributeKind.Geometry)
        {
            return match;
        }

        var geometry = feature[geometryIndex].GeometryValue;
        if (query.OutSr is { } target && layerCrs is not null && target != layerCrs)
        {
            geometry = transforms.Transform(geometry, layerCrs.Value.ToString(), target.ToString(), cancellationToken);
        }

        if (query.GeometryPrecision is { } precision)
        {
            geometry = RoundGeometry(geometry, precision);
        }
        else if (ReferenceEquals(geometry, feature[geometryIndex].GeometryValue))
        {
            return match;
        }

        var attributes = feature.Attributes.ToArray();
        attributes[geometryIndex] = AttributeValue.FromGeometry(geometry);
        return new MatchedFeature(match.ObjectId, new Feature(feature.Id, feature.Schema, attributes));
    }

    internal static IGeometry RoundGeometry(IGeometry geometry, int precision)
    {
        var crs = geometry.CoordinateReference;
        return geometry switch
        {
            Point point when point.Coordinate is { } coordinate =>
                GeometryFactory.CreatePoint(RoundCoordinate(coordinate, precision), crs),
            MultiPoint multiPoint =>
                GeometryFactory.CreateMultiPoint(multiPoint.Points.Select(point => GeometryFactory.CreatePoint(RoundCoordinate(point.Coordinate ?? new Coordinate(0, 0), precision), crs)), crs),
            LineString line => RoundLine(line, crs, precision),
            MultiLineString multiLine =>
                GeometryFactory.CreateMultiLineString(multiLine.LineStrings.Select(line => RoundLine(line, null, precision)), crs),
            Polygon polygon => RoundPolygon(polygon, crs, precision),
            MultiPolygon multiPolygon =>
                GeometryFactory.CreateMultiPolygon(multiPolygon.Polygons.Select(polygon => RoundPolygon(polygon, null, precision)), crs),
            GeometryCollection collection =>
                GeometryFactory.CreateGeometryCollection(collection.Geometries.Select(member => RoundGeometry(member, precision)), crs),
            _ => geometry,
        };
    }

    private static LineString RoundLine(LineString line, CoordinateReference? crs, int precision)
    {
        var sequence = line.Sequence;
        var rounded = new Coordinate[sequence.Count];
        for (var i = 0; i < rounded.Length; i++)
        {
            rounded[i] = RoundCoordinate(sequence.GetCoordinate(i), precision);
        }

        return GeometryFactory.CreateLineString(rounded, sequence.Layout, crs ?? line.CoordinateReference);
    }

    private static Polygon RoundPolygon(Polygon polygon, CoordinateReference? crs, int precision)
    {
        var exterior = RoundLine(polygon.ExteriorRing, null, precision);
        var holes = polygon.InteriorRings.Select(ring => RoundLine(ring, null, precision));
        return GeometryFactory.CreatePolygon(exterior, holes, crs ?? polygon.CoordinateReference);
    }

    private static Coordinate RoundCoordinate(Coordinate coordinate, int precision) => new(
        Math.Round(coordinate.X, precision),
        Math.Round(coordinate.Y, precision),
        RoundOrdinate(coordinate.Z, precision),
        RoundOrdinate(coordinate.M, precision));

    private static double? RoundOrdinate(double? value, int precision) =>
        value is null || double.IsNaN(value.Value) ? value : Math.Round(value.Value, precision);

    private static IResult WriteFeature(MatchedFeature feature, EsriFeatureQuery query)
    {
        var options = new EsriFeatureWriteOptions(EsriLayerModel.ObjectIdField, feature.ObjectId, query.OutFields, query.ReturnGeometry, query.ReturnEnvelope);
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("feature");
            EsriFeatureCodec.Write(writer, feature.Feature, options);
            writer.WriteEndObject();
        });
    }

    private static IResult IdsOnly(List<MatchedFeature> matches) =>
        EsriJson.Value(new EsriObjectIdsResponse(EsriLayerModel.ObjectIdField, matches.Select(match => match.ObjectId).ToArray()));

    /// <summary>
    /// The <c>returnExtentOnly</c> response: the envelope of the full matched
    /// set (before paging), in <c>outSR</c> when supplied, else the layer SR.
    /// A matchless query yields <c>"extent": null</c>.
    /// </summary>
    private static IResult ExtentOnly(
        IReadOnlyList<MatchedFeature> matches,
        CoordinateReference? layerCrs,
        CoordinateReference? outSr,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var extent = Envelope.Empty;
        foreach (var match in matches)
        {
            if (FeatureGeometry.Find(match.Feature) is not { } geometry)
            {
                continue;
            }

            var projected = TransformGeometry(geometry, layerCrs, outSr, transforms, cancellationToken);
            if (projected.Envelope is { } envelope)
            {
                extent = extent.Union(envelope);
            }
        }

        return WriteExtent(extent, outSr ?? layerCrs);
    }

    private static IResult WriteExtent(Envelope extent, CoordinateReference? coordinateReference) =>
        EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            if (extent.IsEmpty)
            {
                writer.WriteNull("extent");
            }
            else
            {
                writer.WritePropertyName("extent");
                writer.WriteStartObject();
                writer.WriteNumber("xmin", extent.MinX);
                writer.WriteNumber("ymin", extent.MinY);
                writer.WriteNumber("xmax", extent.MaxX);
                writer.WriteNumber("ymax", extent.MaxY);
                WriteSpatialReference(writer, coordinateReference);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        });

    /// <summary>
    /// The COUNT DISTINCT response (S3): the number of deduplicated
    /// combinations of the projected fields, the count analogue of
    /// <see cref="DistinctValues"/>. Backs the advertised
    /// <c>supportsCountDistinct</c> flag.
    /// </summary>
    private static IResult DistinctCount(
        DatasetDescription dataset,
        IReadOnlyList<MatchedFeature> matches,
        EsriFeatureQuery query)
    {
        var fields = ResolveDistinctFields(dataset, query.OutFields);
        return EsriJson.Value(new EsriCountResponse(DistinctRows(matches, fields).Count));
    }

    /// <summary>
    /// The <c>returnDistinctValues</c> response: the deduplicated combinations
    /// of the projected fields, no geometry. Paging is applied after dedupe.
    /// </summary>
    private static IResult DistinctValues(
        DatasetDescription dataset,
        IReadOnlyList<MatchedFeature> matches,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs)
    {
        var fields = ResolveDistinctFields(dataset, query.OutFields);
        var rows = DistinctRows(matches, fields);
        var offset = Math.Min(ResolveOffset(query), rows.Count);
        var count = EffectivePageSize(query);
        var page = rows.Skip(offset).Take(count).ToArray();
        var exceeded = offset + page.Length < rows.Count;
        return WriteDistinctValues(dataset, query.OutSr ?? layerCrs, fields, page, exceeded, exceeded ? ResultPagination.Encode(offset + page.Length) : null);
    }

    /// <summary>
    /// Deduplicates the projected fields over the matched set, preserving
    /// first-seen order. Shared by the distinct-values response and the
    /// COUNT DISTINCT response so both agree on what "distinct" means.
    /// </summary>
    private static List<AttributeValue[]> DistinctRows(IReadOnlyList<MatchedFeature> matches, IReadOnlyList<DistinctField> fields)
    {
        var rows = new List<AttributeValue[]>();
        var seen = new HashSet<AttributeValue[]>(AttributeRowComparer.Instance);
        foreach (var match in matches)
        {
            var row = new AttributeValue[fields.Count];
            for (var i = 0; i < fields.Count; i++)
            {
                row[i] = match.Feature[fields[i].Index];
            }

            if (seen.Add(row))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static List<DistinctField> ResolveDistinctFields(DatasetDescription dataset, IReadOnlyList<string>? outFields)
    {
        var schema = dataset.Schema;
        var names = outFields is { Count: > 0 }
            ? outFields
            : schema.Fields.Where(field => field.Kind != AttributeKind.Geometry).Select(field => field.Name).ToArray();
        var fields = new List<DistinctField>(names.Count);
        foreach (var name in names)
        {
            var index = schema.IndexOf(name);
            if (index < 0 || schema[index].Kind == AttributeKind.Geometry)
            {
                throw EsriInteropException.Invalid(
                    $"The 'outFields' value '{name}' is not a distinctable attribute of layer '{dataset.Id}'.");
            }

            fields.Add(new DistinctField(name, index));
        }

        return fields;
    }

    private static IResult WriteDistinctValues(
        DatasetDescription dataset,
        CoordinateReference? coordinateReference,
        IReadOnlyList<DistinctField> fields,
        IReadOnlyList<AttributeValue[]> rows,
        bool exceeded,
        string? nextToken)
    {
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("objectIdFieldName", EsriLayerModel.ObjectIdField);
            writer.WriteString("geometryType", EsriLayerModel.GeometryType(dataset.GeometryType));
            WriteSpatialReference(writer, coordinateReference);
            WriteFields(writer, dataset);
            writer.WritePropertyName("features");
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("attributes");
                writer.WriteStartObject();
                for (var i = 0; i < fields.Count; i++)
                {
                    EsriAttributeCodec.Write(writer, fields[i].Name, row[i]);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("exceededTransferLimit", exceeded);
            WritePaginationToken(writer, nextToken);
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// The <c>outStatistics</c> response (10.x): aggregations over the matched
    /// set, optionally grouped with a <c>having</c> filter on the groups.
    /// Percentile statistics (S3 <c>percentile_cont</c>/<c>percentile_disc</c>)
    /// aggregate the same way but never combine with <c>having</c>.
    /// Shape per Koop: <c>{displayFieldName, fields, features: [{attributes}]}</c>
    /// with no geometry. A statistics query over an empty set with no grouping
    /// yields one row of nulls (Esri response example 5).
    /// </summary>
    private static IResult Statistics(
        DatasetDescription dataset,
        IReadOnlyList<MatchedFeature> matches,
        EsriFeatureQuery query)
    {
        var statistics = query.OutStatistics!;
        var groupFields = ResolveGroupFields(dataset, query.GroupByFields);
        var statInputs = ResolveStatisticInputs(dataset, statistics);
        var groups = GroupMatches(matches, groupFields);
        var rows = new List<StatisticRow>();
        if (groups.Count == 0 && groupFields.Count == 0)
        {
            rows.Add(NullRow(groupFields, statistics, statInputs));
        }
        else
        {
            foreach (var group in groups)
            {
                rows.Add(ComputeRow(group.Key, group.Value, groupFields, statistics, statInputs));
            }
        }

        if (query.Having is { } having)
        {
            rows = rows.Where(row => HavingMatches(row, groupFields, statistics, having)).ToList();
        }

        rows = ApplyStatisticOrder(rows, groupFields, statistics, query.OrderByFields, dataset);
        var offset = Math.Min(ResolveOffset(query), rows.Count);
        var count = EffectivePageSize(query);
        var page = rows.Skip(offset).Take(count).ToArray();
        var exceeded = offset + page.Length < rows.Count;
        return WriteStatistics(dataset, groupFields, statistics, statInputs, page, exceeded, exceeded ? ResultPagination.Encode(offset + page.Length) : null);
    }

    private static List<GroupField> ResolveGroupFields(DatasetDescription dataset, IReadOnlyList<string>? names)
    {
        var fields = new List<GroupField>();
        if (names is null)
        {
            return fields;
        }

        foreach (var name in names)
        {
            var index = dataset.Schema.IndexOf(name);
            if (index < 0)
            {
                throw EsriInteropException.Invalid($"'groupByFieldsForStatistics' names unknown field '{name}' in layer '{dataset.Id}'.");
            }

            if (dataset.Schema[index].Kind == AttributeKind.Geometry)
            {
                throw EsriInteropException.Invalid($"'groupByFieldsForStatistics' cannot group by geometry field '{name}'.");
            }

            if (fields.Any(field => string.Equals(field.Name, dataset.Schema[index].Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw EsriInteropException.Invalid($"Duplicate group field '{name}'.");
            }

            fields.Add(new GroupField(dataset.Schema[index].Name, index, dataset.Schema[index].Kind));
        }

        return fields;
    }

    private static List<StatisticInput> ResolveStatisticInputs(DatasetDescription dataset, IReadOnlyList<EsriOutStatistic> statistics)
    {
        var inputs = new List<StatisticInput>(statistics.Count);
        foreach (var statistic in statistics)
        {
            if ((statistic.OnStatisticField == "*" || string.Equals(statistic.OnStatisticField, statistic.OutStatisticFieldName, StringComparison.OrdinalIgnoreCase)) && statistic.StatisticType == "count")
            {
                inputs.Add(new StatisticInput(statistic, -1, AttributeKind.Int64, true));
                continue;
            }

            var index = dataset.Schema.IndexOf(statistic.OnStatisticField);
            if (index < 0)
            {
                throw EsriInteropException.Invalid($"Statistic '{statistic.OutStatisticFieldName}' names unknown field '{statistic.OnStatisticField}' in layer '{dataset.Id}'.");
            }

            var kind = dataset.Schema[index].Kind;
            if (kind == AttributeKind.Geometry)
            {
                throw EsriInteropException.Invalid($"Statistic '{statistic.OutStatisticFieldName}' cannot aggregate geometry field '{statistic.OnStatisticField}'.");
            }

            if (statistic.StatisticType is "sum" or "avg" or "stddev" or "var" or "percentile_cont" or "percentile_disc" && kind is not (AttributeKind.Int64 or AttributeKind.Double))
            {
                throw EsriInteropException.Invalid($"Statistic '{statistic.StatisticType}' on field '{statistic.OnStatisticField}' needs a numeric field.");
            }

            inputs.Add(new StatisticInput(statistic, index, kind, false));
        }

        return inputs;
    }

    private static List<KeyValuePair<AttributeValue[], List<MatchedFeature>>> GroupMatches(
        IReadOnlyList<MatchedFeature> matches, IReadOnlyList<GroupField> groupFields)
    {
        var groups = new Dictionary<AttributeValue[], List<MatchedFeature>>(AttributeRowComparer.Instance);
        var order = new List<AttributeValue[]>();
        foreach (var match in matches)
        {
            var key = new AttributeValue[groupFields.Count];
            for (var i = 0; i < groupFields.Count; i++)
            {
                key[i] = match.Feature[groupFields[i].Index];
            }

            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
                order.Add(key);
            }

            list.Add(match);
        }

        return order.Select(key => new KeyValuePair<AttributeValue[], List<MatchedFeature>>(key, groups[key])).ToList();
    }

    private static StatisticRow NullRow(
        IReadOnlyList<GroupField> groupFields,
        IReadOnlyList<EsriOutStatistic> statistics,
        IReadOnlyList<StatisticInput> inputs)
    {
        var groupValues = new AttributeValue[groupFields.Count];
        for (var i = 0; i < groupValues.Length; i++)
        {
            groupValues[i] = AttributeValue.Null;
        }

        var values = new AttributeValue[statistics.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = AttributeValue.Null;
        }

        return new StatisticRow(groupValues, values, StatisticKinds(inputs));
    }

    private static StatisticRow ComputeRow(
        AttributeValue[] key,
        IReadOnlyList<MatchedFeature> members,
        IReadOnlyList<GroupField> groupFields,
        IReadOnlyList<EsriOutStatistic> statistics,
        IReadOnlyList<StatisticInput> inputs)
    {
        var values = new AttributeValue[statistics.Count];
        for (var i = 0; i < statistics.Count; i++)
        {
            values[i] = Aggregate(members, inputs[i]);
        }

        return new StatisticRow(key, values, StatisticKinds(inputs));
    }

    private static AttributeKind[] StatisticKinds(IReadOnlyList<StatisticInput> inputs)
    {
        var kinds = new AttributeKind[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            kinds[i] = ResultKind(inputs[i]);
        }

        return kinds;
    }

    private static AttributeKind ResultKind(StatisticInput input) => input.Spec.StatisticType switch
    {
        "count" => AttributeKind.Int64,
        "sum" when input.Kind == AttributeKind.Int64 => AttributeKind.Int64,
        "sum" or "avg" or "stddev" or "var" or "percentile_cont" or "percentile_disc" => AttributeKind.Double,
        "min" or "max" => input.Kind,
        _ => AttributeKind.Double,
    };

    private static AttributeValue Aggregate(IReadOnlyList<MatchedFeature> members, StatisticInput input)
    {
        var type = input.Spec.StatisticType;
        if (type == "count" && input.CountRows)
        {
            return AttributeValue.FromInt64(members.Count);
        }

        var raw = new List<AttributeValue>();
        foreach (var member in members)
        {
            var value = member.Feature[input.Index];
            if (!value.IsNull)
            {
                raw.Add(value);
            }
        }

        if (raw.Count == 0)
        {
            return AttributeValue.Null;
        }

        if (type == "count")
        {
            return AttributeValue.FromInt64(raw.Count);
        }

        if (type is "percentile_cont" or "percentile_disc")
        {
            return Percentile(raw, input.Spec);
        }

        if (type is "min" or "max")
        {
            var best = raw[0];
            foreach (var candidate in raw.Skip(1))
            {
                var order = AttributeValueComparer.Instance.Compare(candidate, best);
                if ((type == "min" && order < 0) || (type == "max" && order > 0))
                {
                    best = candidate;
                }
            }

            return best;
        }

        var numbers = raw.Select(ToDouble).ToArray();
        return type switch
        {
            "sum" when input.Kind == AttributeKind.Int64 && raw.All(value => value.Kind == AttributeKind.Int64) =>
                AttributeValue.FromInt64(raw.Sum(value => value.Int64Value)),
            "sum" => AttributeValue.FromDouble(numbers.Sum()),
            "avg" => AttributeValue.FromDouble(numbers.Average()),
            "var" => AttributeValue.FromDouble(Variance(numbers)),
            "stddev" => AttributeValue.FromDouble(Math.Sqrt(Variance(numbers))),
            _ => AttributeValue.Null,
        };
    }

    /// <summary>
    /// The S3 percentile statistic over the group's non-null numeric
    /// values, ranked in the requested order: discrete returns the dataset
    /// value at rank <c>ceil(fraction × n)</c>, continuous linearly
    /// interpolates at rank <c>fraction × (n − 1)</c>.
    /// </summary>
    private static AttributeValue Percentile(List<AttributeValue> raw, EsriOutStatistic spec)
    {
        var numbers = raw.Select(ToDouble).ToList();
        numbers.Sort();
        if (spec.PercentileDescending)
        {
            numbers.Reverse();
        }

        var fraction = spec.PercentileValue ?? 0;
        return spec.StatisticType == "percentile_disc"
            ? AttributeValue.FromDouble(DiscretePercentile(numbers, fraction))
            : AttributeValue.FromDouble(ContinuousPercentile(numbers, fraction));
    }

    private static double DiscretePercentile(List<double> sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private static double ContinuousPercentile(List<double> sorted, double fraction)
    {
        var rank = fraction * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return lower == upper
            ? sorted[lower]
            : sorted[lower] + ((rank - lower) * (sorted[upper] - sorted[lower]));
    }

    private static double ToDouble(AttributeValue value) => value.Kind switch
    {
        AttributeKind.Int64 => value.Int64Value,
        AttributeKind.Double => value.DoubleValue,
        _ => throw EsriInteropException.Invalid($"Cannot aggregate non-numeric value of kind {value.Kind}."),
    };

    private static double Variance(double[] numbers)
    {
        if (numbers.Length <= 1)
        {
            return 0;
        }

        var mean = numbers.Average();
        return numbers.Sum(number => (number - mean) * (number - mean)) / (numbers.Length - 1);
    }

    private static bool HavingMatches(
        StatisticRow row,
        IReadOnlyList<GroupField> groupFields,
        IReadOnlyList<EsriOutStatistic> statistics,
        EsriFilterClause having)
    {
        var fields = new List<FieldDefinition>(groupFields.Count + statistics.Count);
        var values = new List<AttributeValue>(fields.Capacity);
        for (var i = 0; i < groupFields.Count; i++)
        {
            fields.Add(new FieldDefinition(groupFields[i].Name, KindForComparison(groupFields[i].Kind)));
            values.Add(row.GroupValues[i]);
        }

        for (var i = 0; i < statistics.Count; i++)
        {
            fields.Add(new FieldDefinition(statistics[i].OutStatisticFieldName, KindForComparison(row.StatKinds[i])));
            values.Add(row.StatValues[i]);
        }

        var schema = new FeatureSchema(fields);
        var feature = new Feature(new FeatureId("having"), schema, values.ToArray());
        return having.Matches(feature);
    }

    private static AttributeKind KindForComparison(AttributeKind kind) => kind == AttributeKind.Null ? AttributeKind.Double : kind;

    private static List<StatisticRow> ApplyStatisticOrder(
        List<StatisticRow> rows,
        IReadOnlyList<GroupField> groupFields,
        IReadOnlyList<EsriOutStatistic> statistics,
        IReadOnlyList<EsriOrderByField>? orderBy,
        DatasetDescription dataset)
    {
        if (orderBy is not { Count: > 0 })
        {
            return rows;
        }

        IOrderedEnumerable<StatisticRow>? ordered = null;
        foreach (var key in orderBy)
        {
            var selector = StatisticSelector(key.Name, groupFields, statistics, dataset);
            ordered = ordered is null
                ? (key.Descending ? rows.OrderByDescending(selector, AttributeValueComparer.Instance) : rows.OrderBy(selector, AttributeValueComparer.Instance))
                : (key.Descending ? ordered.ThenByDescending(selector, AttributeValueComparer.Instance) : ordered.ThenBy(selector, AttributeValueComparer.Instance));
        }

        return ordered!.ToList();
    }

    private static Func<StatisticRow, AttributeValue> StatisticSelector(
        string name,
        IReadOnlyList<GroupField> groupFields,
        IReadOnlyList<EsriOutStatistic> statistics,
        DatasetDescription dataset)
    {
        for (var i = 0; i < groupFields.Count; i++)
        {
            if (string.Equals(groupFields[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                var index = i;
                return row => row.GroupValues[index];
            }
        }

        for (var i = 0; i < statistics.Count; i++)
        {
            if (string.Equals(statistics[i].OutStatisticFieldName, name, StringComparison.OrdinalIgnoreCase))
            {
                var index = i;
                return row => row.StatValues[index];
            }
        }

        throw EsriInteropException.Invalid($"'orderByFields' names unknown statistic or group field '{name}' in layer '{dataset.Id}'.");
    }

    private static IResult WriteStatistics(
        DatasetDescription dataset,
        IReadOnlyList<GroupField> groupFields,
        IReadOnlyList<EsriOutStatistic> statistics,
        IReadOnlyList<StatisticInput> inputs,
        IReadOnlyList<StatisticRow> rows,
        bool exceeded,
        string? nextToken)
    {
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("displayFieldName", groupFields.Count > 0 ? groupFields[0].Name : string.Empty);
            writer.WritePropertyName("fields");
            writer.WriteStartArray();
            foreach (var group in groupFields)
            {
                WriteField(writer, group.Name, EsriFieldType.FromAttributeKind(group.Kind), true, false);
            }

            for (var i = 0; i < statistics.Count; i++)
            {
                WriteField(writer, statistics[i].OutStatisticFieldName, EsriFieldType.FromAttributeKind(ResultKindForWrite(inputs[i], rows)), true, false);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("features");
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("attributes");
                writer.WriteStartObject();
                for (var i = 0; i < groupFields.Count; i++)
                {
                    EsriAttributeCodec.Write(writer, groupFields[i].Name, row.GroupValues[i]);
                }

                for (var i = 0; i < statistics.Count; i++)
                {
                    EsriAttributeCodec.Write(writer, statistics[i].OutStatisticFieldName, row.StatValues[i]);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("exceededTransferLimit", exceeded);
            WritePaginationToken(writer, nextToken);
            writer.WriteEndObject();
        });
    }

    private static AttributeKind ResultKindForWrite(StatisticInput input, IReadOnlyList<StatisticRow> rows)
    {
        var kind = ResultKind(input);
        if (kind is not (AttributeKind.Int64 or AttributeKind.Double or AttributeKind.String or AttributeKind.DateTimeOffset or AttributeKind.Guid or AttributeKind.Boolean))
        {
            return AttributeKind.Double;
        }

        return kind;
    }

    private sealed record GroupField(string Name, int Index, AttributeKind Kind);

    private sealed record StatisticInput(EsriOutStatistic Spec, int Index, AttributeKind Kind, bool CountRows);

    private sealed record StatisticRow(AttributeValue[] GroupValues, AttributeValue[] StatValues, AttributeKind[] StatKinds);

    private static IGeometry TransformGeometry(
        IGeometry geometry,
        CoordinateReference? source,
        CoordinateReference? target,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        if (target is not { } to || source is not { } from || from == to)
        {
            return geometry;
        }

        return transforms.Transform(geometry, from.ToString(), to.ToString(), cancellationToken);
    }

    private static IResult WriteFeatures(
        DatasetDescription dataset,
        CoordinateReference? layerCrs,
        EsriFeatureQuery query,
        IReadOnlyList<MatchedFeature> features,
        bool exceeded,
        string? nextToken)
    {
        var options = new EsriFeatureWriteOptions(EsriLayerModel.ObjectIdField, 0, query.OutFields, query.ReturnGeometry, query.ReturnEnvelope);
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("objectIdFieldName", EsriLayerModel.ObjectIdField);
            writer.WriteString("geometryType", EsriLayerModel.GeometryType(dataset.GeometryType));
            WriteSpatialReference(writer, query.OutSr ?? layerCrs);
            WriteFields(writer, dataset);
            writer.WritePropertyName("features");
            writer.WriteStartArray();
            foreach (var feature in features)
            {
                EsriFeatureCodec.Write(writer, feature.Feature, options with { ObjectId = feature.ObjectId });
            }

            writer.WriteEndArray();
            writer.WriteBoolean("exceededTransferLimit", exceeded);
            WritePaginationToken(writer, nextToken);
            writer.WriteEndObject();
        });
    }

    private static void WriteSpatialReference(Utf8JsonWriter writer, CoordinateReference? coordinateReference)
    {
        if (coordinateReference is { } crs && IsMapped(crs))
        {
            EsriSpatialReference.Write(writer, crs);
            return;
        }

        writer.WriteNull("spatialReference");
    }

    private static bool IsMapped(CoordinateReference crs) =>
        string.Equals(crs.Authority, "EPSG", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(crs.Code, NumberStyles.None, CultureInfo.InvariantCulture, out var epsg)
        && WkidMap.TryFromEpsg(epsg, out _);

    private static void WriteFields(Utf8JsonWriter writer, DatasetDescription dataset)
    {
        writer.WritePropertyName("fields");
        writer.WriteStartArray();
        WriteField(writer, EsriLayerModel.ObjectIdField, EsriFieldType.Oid, false, false);
        foreach (var field in dataset.Schema.Fields)
        {
            // A raster catalog's schema already carries the identity column;
            // the synthetic OBJECTID above is authoritative, so do not repeat it.
            if (field.Name == EsriLayerModel.ObjectIdField)
            {
                continue;
            }

            WriteField(writer, field.Name, EsriFieldType.FromAttributeKind(field.Kind), field.Nullable, field.Kind != AttributeKind.Geometry);
        }

        writer.WriteEndArray();
    }

    private static void WriteField(Utf8JsonWriter writer, string name, string type, bool nullable, bool editable)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("type", type);
        writer.WriteString("alias", name);
        writer.WriteBoolean("nullable", nullable);
        writer.WriteBoolean("editable", editable);
        writer.WriteEndObject();
    }

    internal sealed record MatchedFeature(long ObjectId, Feature Feature);

    /// <summary>One compiled <c>orderByFields</c> key: a schema index and direction, or the synthetic object id.</summary>
    private sealed record OrderKey(int Index, bool Descending, bool ObjectId = false);

    /// <summary>
    /// Orders attribute values of the kinds a schema can declare. Nulls sort
    /// last in ascending order (and therefore first when the key is reversed
    /// for DESC), matching the conventional SQL default. Non-null values of a
    /// key always share one kind because the dataset schema fixes the column
    /// kind; a defensive fallback compares the kinds when they do not.
    /// </summary>
    internal sealed class AttributeValueComparer : IComparer<AttributeValue>
    {
        public static readonly AttributeValueComparer Instance = new();

        public int Compare(AttributeValue left, AttributeValue right)
        {
            if (left.IsNull || right.IsNull)
            {
                return CompareNulls(left, right);
            }

            return CompareValues(left, right);
        }

        private static int CompareNulls(AttributeValue left, AttributeValue right) =>
            left.IsNull ? (right.IsNull ? 0 : 1) : -1;

        private static int CompareValues(AttributeValue left, AttributeValue right)
        {
            if (left.Kind != right.Kind)
            {
                return left.Kind.CompareTo(right.Kind);
            }

            return CompareSameKind(left, right);
        }

        private static int CompareSameKind(AttributeValue left, AttributeValue right) =>
            KindComparers.TryGetValue(left.Kind, out var compare) ? compare(left, right) : 0;

        private static readonly Dictionary<AttributeKind, Func<AttributeValue, AttributeValue, int>> KindComparers = new()
        {
            [AttributeKind.Boolean] = (left, right) => left.BooleanValue.CompareTo(right.BooleanValue),
            [AttributeKind.Int64] = (left, right) => left.Int64Value.CompareTo(right.Int64Value),
            [AttributeKind.Double] = (left, right) => left.DoubleValue.CompareTo(right.DoubleValue),
            [AttributeKind.String] = (left, right) => string.CompareOrdinal(left.StringValue, right.StringValue),
            [AttributeKind.DateTimeOffset] = (left, right) => left.DateTimeOffsetValue.UtcTicks.CompareTo(right.DateTimeOffsetValue.UtcTicks),
            [AttributeKind.Guid] = (left, right) => left.GuidValue.CompareTo(right.GuidValue),
        };
    }

    private sealed record PageResult(IReadOnlyList<MatchedFeature> Items, bool Exceeded, string? NextToken);

    /// <summary>Writes the next-page cursor when the page filled up; the final page carries none.</summary>
    private static void WritePaginationToken(Utf8JsonWriter writer, string? nextToken)
    {
        if (nextToken is not null)
        {
            writer.WriteString("resultPaginationToken", nextToken);
        }
    }

    /// <summary>One projected field of a distinct-values request.</summary>
    private readonly record struct DistinctField(string Name, int Index);

    /// <summary>Structural equality for projected distinct-value rows.</summary>
    private sealed class AttributeRowComparer : IEqualityComparer<AttributeValue[]>
    {
        public static AttributeRowComparer Instance { get; } = new();

        public bool Equals(AttributeValue[]? left, AttributeValue[]? right) =>
            ReferenceEquals(left, right)
            || (left is not null && right is not null && left.AsSpan().SequenceEqual(right));

        public int GetHashCode(AttributeValue[] row)
        {
            var hash = new HashCode();
            foreach (var value in row)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
