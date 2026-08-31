// GENERATED FILE — do not edit by hand.
// Regenerate from the host's OpenAPI description: node scripts/generate.mjs <openapi.json> <out.ts>
// The snapshot lives at scripts/openapi.snapshot.json; scripts/check-generated.mjs fails
// when this file has drifted from it.

export interface CapabilityDetailDto {
  id: string;
  purpose: string;
  inputSchema: string;
  outputSchema: string;
  errors: ErrorVariantDto[];
  requiredPermissions: string[];
  traits: string[];
  providers: ProviderOverviewDto[];
}

export interface CapabilityErrorDto {
  kind: string;
  code: string;
  message: string;
}

export interface CapabilitySummaryDto {
  id: string;
  purpose: string;
  inputSchema: string;
  outputSchema: string;
  traits: string[];
  requiredPermissions: string[];
  providers: string[];
}

export interface ErrorVariantDto {
  code: string;
  description: string;
}

export interface InvocationRequest {
  capability: string;
  permissions?: string[] | null;
  deadline?: string | null;
  provider?: string | null;
  resource?: string | null;
  wait?: boolean | null;
  arguments?: { [key: string]: unknown } | null;
}

export interface InvocationResponse {
  kind: string;
  capability: string;
  ok: boolean;
  result: JsonNode | null;
  error: CapabilityErrorDto | null;
  provenance: ProvenanceDto | null;
  job: JobStartedDto | null;
}

export interface JobEventDto {
  kind: JobEventKind;
  timestamp: string;
  progress: unknown | null;
  message: string | null;
  resource: ResourceDto | null;
  note: string | null;
  error: CapabilityErrorDto | null;
}

export type JobEventKind = "created" | "started" | "progress" | "note" | "resource" | "completed" | "failed" | "cancelled" | "timedOut";

export interface JobEventsResponse {
  id: string;
  state: JobState;
  events: JobEventDto[];
}

export interface JobResponse {
  id: string;
  capability: string;
  state: JobState;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  deadline: string | null;
  provider: string | null;
  step: string | null;
  errorCode: string | null;
}

export interface JobStartedDto {
  jobId: string;
  state: JobState;
  location: string;
}

export type JobState = "pending" | "running" | "completed" | "failed" | "cancelled" | "timedOut";

export type JsonNode = unknown;

export interface PluginCapabilityDto {
  id: string;
  purpose: string;
  inputSchema: string;
  outputSchema: string;
  traits: string[];
  permissions: string[];
  errors: ErrorVariantDto[];
}

export interface PluginDto {
  id: string;
  displayName: string;
  runtime: string;
  state: string;
  restartCount: unknown;
  processId: number | null;
  startedAt: string | null;
  lastHealthyAt: string | null;
  lastError: string | null;
  capabilities: PluginCapabilityDto[];
}

export interface ProblemDetails {
  type?: string | null;
  title?: string | null;
  status?: number | null;
  detail?: string | null;
  instance?: string | null;
}

export interface ProvenanceDto {
  capability: string;
  provider: string | null;
  step: string | null;
  startedAt: string;
  durationMs: unknown;
  deadline: string | null;
  jobId: string | null;
}

export interface ProviderOverviewDto {
  id: string;
  health: string;
}

export interface ResourceDto {
  id: string;
  kind: string;
  owner: string;
  createdAt: string;
  state: ResourceState;
}

export type ResourceState = "open" | "leased" | "closed";
