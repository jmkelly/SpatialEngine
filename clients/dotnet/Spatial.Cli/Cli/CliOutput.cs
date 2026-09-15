using System.Text.Json;
using System.Text.Json.Serialization;
using Spatial.Contracts.Http;

namespace Spatial.Cli;

/// <summary>The success/error envelope <c>--json</c> emits (ADR-0052).</summary>
public sealed record CliEnvelope(bool Ok, string Command, object? Data, CliErrorInfo? Error);

/// <summary>The structured error half of <see cref="CliEnvelope"/>.</summary>
public sealed record CliErrorInfo(string Code, string Message);

/// <summary>
/// The CLI's output seam: human lines by default, a stable
/// <c>{ok, command, data}</c> envelope under <c>--json</c> (ADR-0052).
/// Commands describe their result once and this type decides how to render
/// it.
/// </summary>
public sealed class CliOutput
{
    private static readonly JsonSerializerOptions Json = new(HostApiJson.Options)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ICliConsole _console;
    private readonly bool _json;
    private readonly bool _quiet;
    private readonly bool _verbose;

    /// <summary>Creates the output for one invocation.</summary>
    public CliOutput(ICliConsole console, bool json, bool quiet, bool verbose)
    {
        _console = console;
        _json = json;
        _quiet = quiet;
        _verbose = verbose;
    }

    /// <summary>Whether the invocation asked for machine-readable output.</summary>
    public bool IsJson => _json;

    /// <summary>Writes a successful result: the envelope under <c>--json</c>, otherwise the human form.</summary>
    public void Result(string command, object data, string? human = null)
    {
        if (_json)
        {
            Write(new CliEnvelope(true, command, data, null));
            return;
        }

        if (_quiet)
        {
            return;
        }

        _console.Out.WriteLine(human ?? data.ToString());
    }

    /// <summary>Writes a structured failure to the error stream.</summary>
    public void Error(string command, string code, string message)
    {
        if (_json)
        {
            Write(new CliEnvelope(false, command, null, new CliErrorInfo(code, message)));
            return;
        }

        _console.ErrorWriter.WriteLine($"error: {code}: {message}");
    }

    /// <summary>Writes a diagnostic line when <c>--verbose</c> is set.</summary>
    public void Verbose(string message)
    {
        if (_verbose)
        {
            _console.ErrorWriter.WriteLine(message);
        }
    }

    private void Write(CliEnvelope envelope) =>
        _console.Out.WriteLine(JsonSerializer.Serialize(envelope, Json));
}
