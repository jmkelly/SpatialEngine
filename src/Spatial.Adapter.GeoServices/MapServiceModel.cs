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
    IReadOnlyList<EsriMapLayerRef> Tables);

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
    string HtmlPopupType);

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

/// <summary>One layer's persisted-style projection (spec §12–15): a simple renderer only.</summary>
internal sealed record EsriDrawingInfo(EsriRenderer Renderer);

/// <summary>An Esri renderer; the facade produces only the <c>simple</c> type.</summary>
internal sealed record EsriRenderer(string Type, EsriSymbol Symbol);

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
