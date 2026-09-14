namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Map Service wire shapes (spec §4). Esri's MapServer JSON is written
/// explicitly so the adapter owns its shape independently of the Feature
/// Server records; nullable members are omitted by the facade's serializer.
/// </summary>
internal sealed record EsriMapServerRoot(
    double CurrentVersion,
    string ServiceDescription,
    string MapName,
    string? Description,
    string? CopyrightText,
    EsriSpatialReferenceDto? SpatialReference,
    bool SingleFusedMapCache,
    EsriTileInfo? TileInfo,
    EsriExtent? InitialExtent,
    EsriExtent? FullExtent,
    string Units,
    string Capabilities,
    string SupportedImageFormatTypes,
    IReadOnlyList<EsriMapLayerRef> Layers,
    IReadOnlyList<EsriMapLayerRef> Tables,
    bool SupportsDynamicLayers,
    bool SupportsTimeRelation,
    bool ExportTilesAllowed);

/// <summary>One layer reference under the MapServer root or the all-layers resource.</summary>
internal sealed record EsriMapLayerRef(
    int Id,
    string Name,
    int ParentLayerId,
    bool DefaultVisibility,
    IReadOnlyList<int>? SubLayerIds,
    double MinScale,
    double MaxScale);

/// <summary>One MapServer layer's metadata (spec §4.2).</summary>
internal sealed record EsriMapLayer(
    double CurrentVersion,
    int Id,
    string Name,
    string Type,
    string GeometryType,
    string ObjectIdField,
    string DisplayField,
    IReadOnlyList<EsriField> Fields,
    string Capabilities,
    int MaxRecordCount,
    EsriSpatialReferenceDto? SpatialReference,
    EsriExtent? Extent,
    EsriDrawingInfo? DrawingInfo,
    int ParentLayerId,
    bool DefaultVisibility,
    bool HasAttachments,
    string HtmlPopupType,
    IReadOnlyDictionary<string, EsriDomain>? Domains = null);

/// <summary>The <c>layers</c> resource (spec §4.8): every layer reference.</summary>
internal sealed record EsriMapLayersResponse(
    double CurrentVersion,
    IReadOnlyList<EsriMapLayerRef> Layers,
    IReadOnlyList<EsriMapLayerRef> Tables);

/// <summary>An Esri extent object.</summary>
internal sealed record EsriExtent(double Xmin, double Ymin, double Xmax, double Ymax, EsriSpatialReferenceDto? SpatialReference);

/// <summary>The MapServer tiling scheme (spec §4.0).</summary>
internal sealed record EsriTileInfo(
    int Rows,
    int Cols,
    double Dpi,
    EsriPoint Origin,
    EsriSpatialReferenceDto? SpatialReference,
    IReadOnlyList<EsriLod> Lods);

/// <summary>One level of detail in a MapServer <c>tileInfo</c>.</summary>
internal sealed record EsriLod(int Level, double Resolution, double Scale);

/// <summary>An Esri point.</summary>
internal sealed record EsriPoint(double X, double Y);

/// <summary>The MapServer <c>export</c> JSON response (spec §4.0.4); the image itself is at <c>href</c>.</summary>
internal sealed record EsriMapExportResponse(string? Href, int Width, int Height, EsriExtent Extent, double Scale);

/// <summary>One layer's persisted-style projection (spec §12–15): a renderer and optional label classes.</summary>
internal sealed record EsriDrawingInfo(EsriRenderer Renderer, IReadOnlyList<EsriLabelClass>? LabelingInfo = null);

/// <summary>
/// An Esri renderer (spec §15). Only the members the projected renderer type
/// needs are emitted; the serializer drops nulls. The adapter produces
/// <c>simple</c>, <c>uniqueValue</c> and <c>classBreaks</c>.
/// </summary>
internal sealed record EsriRenderer(
    string Type,
    EsriSymbol? Symbol = null,
    string? Field = null,
    double? MinValue = null,
    IReadOnlyList<EsriClassBreakInfo>? ClassBreakInfos = null,
    string? Field1 = null,
    string? Field2 = null,
    string? Field3 = null,
    string? FieldDelimiter = null,
    EsriSymbol? DefaultSymbol = null,
    string? DefaultLabel = null,
    IReadOnlyList<EsriUniqueValueInfo>? UniqueValueInfos = null);

/// <summary>One class-break entry (spec §15.3).</summary>
internal sealed record EsriClassBreakInfo(double ClassMaxValue, EsriSymbol Symbol, string? Label = null, string? Description = null);

/// <summary>One unique-value entry (spec §15.2).</summary>
internal sealed record EsriUniqueValueInfo(string Value, EsriSymbol Symbol, string? Label = null, string? Description = null);

/// <summary>
/// An Esri simple-fill / simple-line / simple-marker symbol. Only the members
/// the symbol type needs are emitted (the serializer drops nulls).
/// </summary>
internal sealed record EsriSymbol(
    string Type,
    string Style,
    IReadOnlyList<int> Color,
    double? Size = null,
    double? Width = null,
    EsriSymbolOutline? Outline = null);

/// <summary>A symbol outline (Esri simple line symbol) for fill and marker symbols.</summary>
internal sealed record EsriSymbolOutline(string Type, string Style, IReadOnlyList<int> Color, double Width);

/// <summary>A domain object (spec §13): a coded value set or a numeric range.</summary>
internal sealed record EsriDomain(
    string Type,
    string? Name = null,
    IReadOnlyList<EsriCodedValue>? CodedValues = null,
    IReadOnlyList<double>? Range = null);

/// <summary>One name/code pair in a coded-value domain (spec §13.2).</summary>
internal sealed record EsriCodedValue(string Name, object Code);

/// <summary>One label class (spec §14.2).</summary>
internal sealed record EsriLabelClass(
    string LabelPlacement,
    string LabelExpression,
    bool UseCodedValues,
    EsriTextSymbol Symbol,
    double MinScale,
    double MaxScale);

/// <summary>A text symbol used by a label class (spec §12.7).</summary>
internal sealed record EsriTextSymbol(
    string Type,
    IReadOnlyList<int>? Color,
    IReadOnlyList<int>? BackgroundColor,
    IReadOnlyList<int>? BorderLineColor,
    string VerticalAlignment,
    string HorizontalAlignment,
    bool RightToLeft,
    double Angle,
    double XOffset,
    double YOffset,
    EsriFont Font);

/// <summary>The font of a text symbol (spec §12.7).</summary>
internal sealed record EsriFont(string Family, double Size, string Style, string Weight, string Decoration);

/// <summary>The MapServer <c>legend</c> resource (S4 legend-map-service/): one entry per layer.</summary>
internal sealed record EsriMapLegendResponse(IReadOnlyList<EsriLegendLayer> Layers);

/// <summary>One layer's legend: its swatches plus the group headings they belong to (G1 map-legend.Census.json).</summary>
internal sealed record EsriLegendLayer(
    int LayerId,
    string LayerName,
    string LayerType,
    double MinScale,
    double MaxScale,
    IReadOnlyList<EsriLegendEntry> Legend,
    IReadOnlyList<EsriLegendGroup> LegendGroups);

/// <summary>One legend swatch: its label, swatch bytes and optional data values.</summary>
internal sealed record EsriLegendEntry(
    string Label,
    string Url,
    string ImageData,
    string ContentType,
    int Height,
    int Width,
    string? GroupId = null,
    IReadOnlyList<object>? Values = null);

/// <summary>One legend group heading (the classified field, or empty for a simple renderer).</summary>
internal sealed record EsriLegendGroup(string Id, string Heading);

/// <summary>The MapServer <c>queryDomains</c> response (S4): the projected domains of each selected layer.</summary>
internal sealed record EsriMapQueryDomainsResponse(IReadOnlyList<EsriLayerDomains> Domains);

/// <summary>One layer's projected domains, keyed by field name (same shape as the layer metadata <c>domains</c>).</summary>
internal sealed record EsriLayerDomains(int LayerId, IReadOnlyDictionary<string, EsriDomain> Domains);

/// <summary>The MapServer <c>queryLegends</c> response (S4): the legend layers of each selected layer.</summary>
internal sealed record EsriMapQueryLegendsResponse(IReadOnlyList<EsriLegendLayer> Layers);

/// <summary>The per-layer <c>generateRenderer</c> response (S4): the classified renderer.</summary>
internal sealed record EsriGenerateRendererResponse(EsriRenderer Renderer);
