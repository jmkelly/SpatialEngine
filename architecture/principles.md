# Architecture Principles

This is the project's philosophy. The code is the documentation;
this file states the standing shape so a change can be judged against it.

## Principles

Only what the code does not say. Everything else — project layout,
immutability, cancellable `Task`s, contract-only boundaries, the
conformance suite — is read off the code and the architecture tests.

- Contracts outlive implementations.
- Packaging is not architecture.
- Open formats and language-neutral protocols are preferred at boundaries.
- Keep the kernel small and stable.
- Add another language only where profiling or platform integration justifies it.
- Do not introduce Native AOT until compatibility is demonstrated.

## How to change the architecture

- Add a capability → versioned contract, conformance fixtures, SDK updates.

