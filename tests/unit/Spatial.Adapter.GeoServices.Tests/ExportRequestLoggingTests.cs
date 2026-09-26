using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// Export request diagnostics (ADR-0045, T-110): a completed or rejected
/// MapServer export / ImageServer exportImage each logs one structured event
/// carrying the service and the request values, so a blank parity panel is
/// diagnosable from the log alone.
/// </summary>
public sealed class ExportRequestLoggingTests
{
    [Fact]
    public async Task Request_values_are_exposed_for_diagnostics()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?bbox=-125%2C25%2C-66%2C50&size=800%2C600&f=image");

        var parameters = await EsriRequestParameters.ReadAsync(context, CancellationToken.None);

        Assert.Equal("-125,25,-66,50", parameters.Values["bbox"]);
        Assert.Equal("800,600", parameters.Values["size"]);
        Assert.Contains("bbox=-125,25,-66,50", GeoServicesExportLogging.FormatParameters(parameters));
        Assert.Contains("size=800,600", GeoServicesExportLogging.FormatParameters(parameters));
        Assert.Contains("f=image", GeoServicesExportLogging.FormatParameters(parameters));
    }

    [Fact]
    public void Empty_parameters_format_to_none()
    {
        Assert.Equal("(none)", GeoServicesExportLogging.FormatParameters(null));
    }

    [Fact]
    public void Describe_keeps_the_wire_code_status_and_reason()
    {
        var failure = EsriErrorMapper.Describe(EsriInteropException.Invalid("bad bbox"));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.EsriCode);
        Assert.Equal(StatusCodes.Status400BadRequest, failure.HttpStatus);
        Assert.Equal("bad bbox", failure.Message);
    }

    [Fact]
    public void Describe_maps_an_unknown_service_to_not_found()
    {
        var failure = EsriErrorMapper.Describe(new EsriInteropException(EsriErrorCodes.NotFound, "missing"));

        Assert.Equal(EsriErrorCodes.NotFound, failure.EsriCode);
        Assert.Equal(StatusCodes.Status404NotFound, failure.HttpStatus);
    }

    [Fact]
    public void A_completed_map_export_logs_service_size_and_values()
    {
        var factory = new CapturingLoggerFactory();
        var log = factory.CreateLogger("test");

        GeoServicesExportLogging.LogMapExportCompleted(
            log, "GET", "/arcgis/rest/services/WorldReference/MapServer/export",
            "WorldReference", 800, 600, 1234, "image/png",
            "bbox=-125,25,-66,50&bboxSR=4326&imageSR=4326&size=800,600&format=png&f=image");

        var entry = Assert.Single(factory.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("WorldReference", entry.Message);
        Assert.Contains("800x600", entry.Message);
        Assert.Contains("bbox=-125,25,-66,50", entry.Message);
        Assert.Contains("f=image", entry.Message);
    }

    [Fact]
    public void A_failed_map_export_logs_the_code_status_reason_and_values()
    {
        var factory = new CapturingLoggerFactory();
        var log = factory.CreateLogger("test");
        var failure = new EsriInteropException(EsriErrorCodes.NotFound, "Service 'Missing' does not exist.");

        GeoServicesExportLogging.LogMapExportFailed(
            log, failure, "GET", "/arcgis/rest/services/Missing/MapServer/export",
            "Missing", failure.Code, StatusCodes.Status404NotFound, failure.Message,
            "bbox=-125,25,-66,50&size=800,600&f=image");

        var entry = Assert.Single(factory.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Missing", entry.Message);
        Assert.Contains("404", entry.Message);
        Assert.Contains("bbox=-125,25,-66,50", entry.Message);
    }

    [Fact]
    public void A_failed_image_export_logs_the_code_status_reason_and_values()
    {
        var factory = new CapturingLoggerFactory();
        var log = factory.CreateLogger("test");
        var failure = EsriInteropException.Invalid("The 'bbox' parameter is required.");

        GeoServicesExportLogging.LogImageExportFailed(
            log, failure, "GET", "/arcgis/rest/services/WorldReference/ImageServer/exportImage",
            "WorldReference", failure.Code, StatusCodes.Status400BadRequest, failure.Message,
            "size=800,600&f=image");

        var entry = Assert.Single(factory.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("WorldReference", entry.Message);
        Assert.Contains("400", entry.Message);
        Assert.Contains("size=800,600", entry.Message);
    }

    [Fact]
    public void A_completed_image_export_logs_service_and_values()
    {
        var factory = new CapturingLoggerFactory();
        var log = factory.CreateLogger("test");

        GeoServicesExportLogging.LogImageExportCompleted(
            log, "GET", "/arcgis/rest/services/WorldReference/ImageServer/exportImage",
            "WorldReference", "bbox=-125,25,-66,50&size=800,600&f=image");

        var entry = Assert.Single(factory.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("WorldReference", entry.Message);
        Assert.Contains("bbox=-125,25,-66,50", entry.Message);
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
