using System.Reflection;
using Microsoft.Extensions.Logging;
using Spatial.Host.Api;
using Spatial.Provider.PostGIS;

namespace Spatial.Host;

/// <summary>
/// One structured startup summary (ADR-0045) so a Seq query can answer "what
/// profile started, and was it configured?" without scraping stdout. Messages
/// carry configuration <em>state</em> only — never a connection string, token
/// or other secret.
/// </summary>
internal static partial class StartupLogging
{
    public static void Log(WebApplication app)
    {
        var logger = app.Logger;
        var postgisConfigured = !string.IsNullOrWhiteSpace(app.Services.GetRequiredService<PostgisOptions>().ConnectionString);
        var adminEnabled = AdminOptions.FromConfiguration(app.Configuration).Enabled;
        var seqEnabled = app.Services.GetRequiredService<LoggingSettings>().SeqEnabled;
        var webRoot = app.Configuration["Spatial:WebRoot"];
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        var environmentName = app.Environment.EnvironmentName;
        var postgisState = postgisConfigured ? "configured" : "unconfigured";
        var adminState = adminEnabled ? "enabled" : "disabled";
        var seqState = seqEnabled ? "enabled" : "disabled";

        LogStarted(logger, version, environmentName, postgisState, adminState, seqState);

        if (!postgisConfigured)
        {
            LogPostgisUnconfigured(logger, "Spatial:Postgis:ConnectionString", PostgisOptions.EnvironmentVariable);
        }

        if (!adminEnabled)
        {
            LogAdminDisabled(logger, "Spatial:Admin:Token", AdminOptions.EnvironmentVariable);
        }

        if (!string.IsNullOrWhiteSpace(webRoot))
        {
            LogWorkbenchRoot(logger, webRoot);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Spatial.Host {Version} starting in {EnvironmentName}; PostGIS {PostgisState}, admin routes {AdminState}, Seq sink {SeqState}")]
    private static partial void LogStarted(
        ILogger logger, string version, string environmentName, string postgisState, string adminState, string seqState);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "The PostGIS store is not configured; requests to the 'postgis' store will fail with store.unavailable. Set {Setting} or {EnvironmentVariable}.")]
    private static partial void LogPostgisUnconfigured(ILogger logger, string setting, string environmentVariable);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Admin mutation routes are disabled; set {Setting} or {EnvironmentVariable} to enable maps and ingest.")]
    private static partial void LogAdminDisabled(ILogger logger, string setting, string environmentVariable);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Serving the browser workbench from {WebRoot}.")]
    private static partial void LogWorkbenchRoot(ILogger logger, string webRoot);
}
