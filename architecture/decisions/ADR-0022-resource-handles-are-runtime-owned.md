# ADR-0022: Resource handles are runtime-owned with leases

Status: Accepted

## Context

Expensive or long-lived values — datasets, transactions, intermediate
results — must cross invoke boundaries without copying. Opaque handles
(plan §8) give clients a token to pass around, but a token alone grants
nothing: without runtime ownership, handles can be forged, leaked or used
after disposal.

## Decision

Resources are runtime-owned. The runtime mints an opaque `ResourceId`
(never reused, meaningless to clients), registers the handle with its
owning provider, and tracks lifecycle state (open, leased, closed) behind
the id in a `ResourceRegistry`. Access is gated by leases: a lease is a
time-bounded right to use the resource, issued and renewed by the registry,
honoured on stream reads, and reclaimed when a client never releases it
(owner teardown reports the leak count). Providers mint handles through the
invocation facilities (`facilities.Resources.Create(kind)`); the owning
provider is derived from the handle during resolution (resolution step 2 —
the compatible resource-local provider).

## Consequences

- Clients cannot forge usable handles; unknown ids resolve to nothing.
- Resource-local provider resolution becomes real: passing a handle in
  `InvocationOptions.Resource` lets the runtime prefer the handle's owner.
- Leaks are detectable and reported (a client failing to release a lease is
  a leak test case).
- Hosts reclaim resources of a provider when it is unregistered or replaced
  (`DisposeOwner`), so plugin replacement does not leak state.
- Phase 5 workers carry resource ids across language-neutral boundaries; the
  registry stays host-side.