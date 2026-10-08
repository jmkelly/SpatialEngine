# clients/typescript

`@spatial/client`, the TypeScript SDK for the public host API. Two records
bind it: **ADR-0033** (contracts outlive implementations; the SDK speaks only
the public HTTP surface) and **ADR-0014** (React, TypeScript and MapLibre are
the first frontend, which is why this SDK is the delivered client's only
channel to the host). Route by task:
`architecture/principles.md`.

## Never

- No hand-edit of `src/generated-types.ts`. It is generated from the OpenAPI
  snapshot and `npm run check:generated` fails on any diff; regenerate with
  `npm run generate`.
- No spatial algorithm in this package. It talks to the host; it does not
  compute geometry, and a need for a verb is a verb the host does not serve
  yet (ADR-0033).
- No dependency on a renderer, and no renderer type on an SDK signature
  (ADR-0013's boundary, under ADR-0033).
- No fetch of a host path from a client: the request shape comes from the
  generated contract, so a member the host refuses is a bug here rather than a
  server fallback to write.
- No wire-format decision made here; the canonical encoding is the engine's
  (ADR-0020).

## Commands

- `npm test` — typecheck, the generated-types drift check, then
  `node --test test/*.test.ts`.
- `npm run test:e2e` — against a running host.
- `eng/e2e-web.sh` — a real host plus the delivered client.
