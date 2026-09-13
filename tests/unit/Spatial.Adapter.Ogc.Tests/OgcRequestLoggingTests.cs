using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// OGC request diagnostics (ADR-0045): a completed operation and a rejected
/// one each log one structured event carrying the operation and the request
/// parameters, so a blank or rejected interop client is diagnosable from the
/// log alone.
/// </summary>
public sealed class OgcRequestLoggingTests
{
    [Fact]
    public async Task A_rejected_operation_logs_the_code_reason_and_parameters()
    {
        var logger = new CapturingLoggerFactory();
        var context = Request("?service=WMS&request=GetMap&crs=EPSG:2154");

        var result = await OgcEndpoints.Dispatch(
            context, logger, _ => throw OgcServiceException.Invalid("Unsupported CRS 'EPSG:2154'."), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, await StatusAsync(result));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("GetMap", entry.Message);
        Assert.Contains("InvalidParameterValue", entry.Message);
        Assert.Contains("EPSG:2154", entry.Message);
    }

    [Fact]
    public async Task A_completed_operation_logs_the_parameters()
    {
        var logger = new CapturingLoggerFactory();
        var context = Request("?service=WMS&request=GetCapabilities");

        await OgcEndpoints.Dispatch(context, logger, _ => Task.FromResult(Results.Ok()), CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("GetCapabilities", entry.Message);
        Assert.Contains("service=WMS", entry.Message);
    }

    private static DefaultHttpContext Request(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/ogc/world/wms";
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    private static async Task<int> StatusAsync(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
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
