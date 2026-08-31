using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Spatial.PluginSdk.Jobs;
using Spatial.PluginSdk.Resources;

namespace Spatial.PluginSdk.Http;

/// <summary>
/// The JSON shapes of the public HTTP host API (plan §12, ADR-0030,
/// <c>architecture/host-api.md</c>): every request and response the
/// independently executable <c>Spatial.Host</c> serves and the .NET client
/// SDK consumes. The shapes live in the SDK — the public contract assembly —
/// so host, OpenAPI, TypeScript client and .NET client stay in lockstep
/// (deployment-profiles "one public API, contract set" invariant). Values
/// that cross inline use the <see cref="Spatial.PluginSdk.Codec.ValueCodec"/>
/// wire encoding, exactly like the worker protocol; streaming data crosses
/// as streams, never inline (ADR-0020/0023).
/// </summary>
public static class HostApiJson
{
    /// <summary>
    /// The shared JSON options for the host API: camelCase property names and
    /// enum members rendered as their camelCase names. Host and SDK share
    /// this one options instance so the wire shapes cannot drift.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

// ---- Invocation (POST /api/invocations) ----

/// <summary>The request body of <c>POST /api/invocations</c>.</summary>
public sealed record InvocationRequest(
    string Capability,
    IReadOnlyDictionary<string, JsonNode?>? Arguments = null,
    IReadOnlyList<string>? Permissions = null,
    DateTimeOffset? Deadline = null,
    string? Provider = null,
    string? Resource = null,
    bool? Wait = null)
{
    /// <summary>Arguments as the codec already encoded them (lossless wire form).</summary>
    public IReadOnlyDictionary<string, JsonNode?> Arguments { get; init; } = Arguments ?? new Dictionary<string, JsonNode?>();
}

/// <summary>
/// Structured provenance of one invocation: which capability, which provider
/// served it, which deterministic resolution step picked the provider, when
/// it started, its duration and the deadline that bounded it. The job id is
/// present when the invocation ran as a tracked job.
/// </summary>
public sealed record ProvenanceDto(
    string Capability,
    string? Provider,
    string? Step,
    DateTimeOffset StartedAt,
    double DurationMs,
    DateTimeOffset? Deadline,
    string? JobId);

/// <summary>A structured, actionable capability error (the <see cref="Spatial.PluginSdk.Capabilities.CapabilityError"/> wire form).</summary>
public sealed record CapabilityErrorDto(
    string Kind,
    string Code,
    string Message);

/// <summary>A job started for a long-running (or explicitly asynchronous) invocation.</summary>
public sealed record JobStartedDto(
    string JobId,
    JobState State,
    string Location)
{
    /// <summary>Builds the location header value for <c>GET /api/jobs/{id}</c>.</summary>
    public static string LocationFor(JobId jobId) => $"/api/jobs/{jobId}";
}

/// <summary>
/// The response of <c>POST /api/invocations</c>, discriminated on
/// <see cref="Kind"/>: <c>completed</c> carries <c>Ok</c> plus a
/// wire-encoded <see cref="Result"/> (or a structured <see cref="Error"/>)
/// and its <see cref="Provenance"/>; <c>job</c> carries the started
/// <see cref="Job"/> to poll or subscribe. Runtime outcomes are always
/// returned as a 2xx <c>completed</c> body — HTTP 4xx/5xx are reserved for
/// malformed requests and host failures.
/// </summary>
public sealed record InvocationResponse(
    string Kind,
    string Capability,
    bool Ok,
    JsonNode? Result,
    CapabilityErrorDto? Error,
    ProvenanceDto? Provenance,
    JobStartedDto? Job)
{
    public static InvocationResponse Completed(
        string capability, JsonNode? result, ProvenanceDto provenance) =>
        new("completed", capability, true, result, null, provenance, null);

    public static InvocationResponse Failed(
        string capability, CapabilityErrorDto error, ProvenanceDto provenance) =>
        new("completed", capability, false, null, error, provenance, null);

    public static InvocationResponse JobStarted(string capability, JobStartedDto job) =>
        new("job", capability, false, null, null, null, job);
}

// ---- Jobs ----

/// <summary>One tracked job's public state (ADR-0008): identity, lifecycle, serving provider and terminal error.</summary>
public sealed record JobResponse(
    string Id,
    string Capability,
    JobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? Deadline,
    string? Provider,
    string? Step,
    string? ErrorCode);

/// <summary>One append-only job event: lifecycle, progress, published resources and diagnostics.</summary>
public sealed record JobEventDto(
    JobEventKind Kind,
    DateTimeOffset Timestamp,
    double? Progress,
    string? Message,
    ResourceDto? Resource,
    string? Note,
    CapabilityErrorDto? Error);

/// <summary>The <c>GET /api/jobs/{id}/events</c> body: the job snapshot plus its events so far.</summary>
public sealed record JobEventsResponse(
    string Id,
    JobState State,
    IReadOnlyList<JobEventDto> Events);

// ---- Resources ----

/// <summary>A runtime-owned resource handle and its registry state (ADR-0022).</summary>
public sealed record ResourceDto(
    string Id,
    string Kind,
    string Owner,
    DateTimeOffset CreatedAt,
    ResourceState State);

// ---- Capabilities ----

/// <summary>The summary of one registered capability in the <c>GET /api/capabilities</c> listing.</summary>
public sealed record CapabilitySummaryDto(
    string Id,
    string Purpose,
    string InputSchema,
    string OutputSchema,
    IReadOnlyList<string> Traits,
    IReadOnlyList<string> RequiredPermissions,
    IReadOnlyList<string> Providers);

/// <summary>One declared failure mode of a capability contract (stable dotted code + when it occurs).</summary>
public sealed record ErrorVariantDto(string Code, string Description);

/// <summary>The full declaration of one capability (identity, schemas, errors, permissions, traits, serving providers).</summary>
public sealed record CapabilityDetailDto(
    string Id,
    string Purpose,
    string InputSchema,
    string OutputSchema,
    IReadOnlyList<ErrorVariantDto> Errors,
    IReadOnlyList<string> RequiredPermissions,
    IReadOnlyList<string> Traits,
    IReadOnlyList<ProviderOverviewDto> Providers);

/// <summary>One registered provider serving a capability: its id and current health overview.</summary>
public sealed record ProviderOverviewDto(string Id, string Health);

// ---- Plugins ----

/// <summary>The capability contract a loaded plugin package declares (its manifest capability).</summary>
public sealed record PluginCapabilityDto(
    string Id,
    string Purpose,
    string InputSchema,
    string OutputSchema,
    IReadOnlyList<string> Traits,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<ErrorVariantDto> Errors);

/// <summary>One supervised plugin worker package: identity, lifecycle state and its manifest capabilities.</summary>
public sealed record PluginDto(
    string Id,
    string DisplayName,
    string Runtime,
    string State,
    int RestartCount,
    int? ProcessId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastHealthyAt,
    string? LastError,
    IReadOnlyList<PluginCapabilityDto> Capabilities);
