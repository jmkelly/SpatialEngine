using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "Spatial.Host",
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown",
}));

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.Run();

/// <summary>Entry point type used by <c>WebApplicationFactory</c> in integration tests.</summary>
public partial class Program;
