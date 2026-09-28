---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
amends: ADR-0041
---

# ADR-0089: An upload is staged, then loaded — resumable ingest

## Context

ADR-0082 made a large upload bounded in memory: the decode streams, the store
loads the stream, and the feature cap is checked while reading. What it did not
make was *resumable*. An upload that fails at 90% — a dropped connection, a
proxy timeout, a process restart — starts over, and the streaming decode makes
that worse rather than better, because the work already done is now work in a
transaction that rolls back.

ADR-0041 §4 left this open by name: *"streaming landed in ADR-0082; resumable
upload is still open"*, and §6 sized the caps (`MaxBytes`, `MaxFeatures`) for
**one request**. Resumability is not a parameter of that pair; it is a
persisted state the host has to hold between requests, and the decision of what
that state *is* decides whether the ingest path stays atomic.

Two shapes were on the table.

**(a) Resumable offsets in a vendor multipart protocol.** Chunked transfer is
what HTTP and Esri both already speak, so this is the smallest change to make
and the most familiar to a client. It also makes a vendor protocol the domain
model: `Content-Range`, the Esri `add`/`combine`/`import` vocabulary, and their
incompatible ideas about what a chunk *is*, all become things the engine's own
ingest path has to understand. ADR-0041 §5 is explicit that the Esri surface is
a projection of a neutral one, not the neutral one.

**(b) A staged upload, appended to by separate requests, with the ingest
request naming it.** The engine holds bytes it has been given and a length it
can report. Nothing about it is a feature, a dataset or a table until the load
runs.

There is a third question hiding inside the choice, and it is the one that
matters: **what is chunked?** If chunks are *features*, a resumable upload is a
half-populated table the engine has to be able to finish, extend, and either
commit or abandon — the atomicity ADR-0041 §3 buys with "the dataset exists only
if every page lands" is gone, and a malformed row at the end means deciding
what to do with the 99% already written. If chunks are *bytes*, none of that
arises: the staged document is still one document, and the load is still the
same single transaction it is today over the same bytes.

## Decision

**1. Chunks are bytes, not features.** A staged upload is opaque content plus a
length. It never names a dataset, never creates one, and is never visible to a
query, a catalogue or a service. `POST /api/ingest?…&upload=<id>` decodes and
loads the staged bytes exactly as a request body would be, through the same
`IngestPipeline.LoadAsync`, into the same one-transaction store load.

**What this buys, stated plainly.** A malformed row at the end of a resumed
upload fails the whole load and leaves no dataset, precisely as it does for a
single-request upload. That is unchanged behaviour, not a new compromise: the
resumable path *is* the single-request path with a longer pipe. What is new is
that the failing bytes survive the failure, staged and complete, so the caller
can see them, fetch them, fix them and re-stage without re-sending 99% of a
file to find out where it broke.

**What it does not buy.** There is no partial load, no resumable *load*, and no
progress percentage of loaded features. A store that cannot stream pages
(`IDatasetIngestStream` absent) still loads a staged upload from one buffered
`IDatasetIngest` call, so a very large staged upload costs memory on such a
store exactly as a large body does today. The chunking is transport-level and
does not change that.

**2. The staged upload is a contract face, and the bytes are content-addressed
by digest.** `IUploadStaging` (`Spatial.Contracts.Providers`) is an optional
face in the ADR-0033 sense: a store or host that cannot stage bytes does not
implement it and the route is not mounted. It carries `UploadState`,
`UploadAppend` and the verbs start/append/describe/list/open/discard. The id is
opaque and caller-chosen or server-issued; the **digest is what addresses the
content**, and it is verified when the last declared byte arrives. A resumed
upload is therefore provably the same document, not merely one that claims to
be — which is the failure a resume protocol exists to prevent.

**3. The protocol is the smallest one that can resume.** An offset, a declared
total, a digest, and a length the host reports. Four rules make it safe:

- An append above the staged length is `invalid.arguments`, **naming the offset
  to resume from**. A client that is behind is told how far behind.
- An append below it is accepted only when the re-sent bytes are identical to
  what is staged. This is the lost-acknowledgement case, and it is the same
  document or it is a refusal. **The cap on such a chunk is measured from the
  offset it was addressed at, not from the staged length** — otherwise the
  re-sent overlap is counted as though it were new content and a client
  answering the question this protocol exists to make it answerable is refused
  for a limit it cannot breach. The bytes an append actually *adds* are still
  held to the declared total and to `MaxBytes`.
- A refused append changes nothing. The chunk is read to a scratch file within
  the caps and matched in full before a single byte is appended, and the
  staged length is the length of the file, not a number the state file claims —
  so an append interrupted after the bytes were written is not replayed over
  them.
- A digest that does not match faults the upload. It stays staged and
  incomplete, and appending more cannot repair it, because the bytes are not the
  declared document.

**4. A partial upload cannot be mistaken for a complete one.** `Complete` is
true only when a declared total has been reached, and `POST /api/ingest?upload=`
loads only a complete upload, refusing a partial one with its current offset in
the message. The staging is discarded **only after the load has committed**, so
a failed load leaves the bytes for a retry and a committed load cannot be
replayed. Staged uploads are admin-gated like every other mutation — including
the reads, because a staged upload is caller-supplied content, not a dataset.
Un-ingested uploads are pruned after `Spatial:Uploads:MaxAgeHours` (24 by
default), so an abandoned upload is not a directory that grows for ever.

**5. The caps stay sized for the document, not the request.** A staged upload is
bounded by the same `Spatial:Ingest:MaxBytes` as a single-request upload,
because it *is* the same document arriving in pieces; `MaxFeatures` is enforced
once, during the load. Nothing in the caps has to be re-derived per chunk.

**6. One cap helper, because there are now three callers.** The feature-cap
stream wrapper duplicated between the neutral and Esri upload paths is
`IngestPageCap` in `Spatial.Ingest.Codec`, parameterised by the caller's error
factory so each surface still answers in its own error envelope while the
counting and the wording are one thing.

## Consequences

- `Spatial.Contracts` gains one optional interface and two small records, and
  takes no package and no new project reference. `Spatial.Host` gains the
  file-backed implementation, the routes and the `upload=` ingest parameter.
- The neutral surface grows five admin routes (`POST/GET /api/uploads`,
  `GET/PUT/DELETE /api/uploads/{id}`). They are mounted only when an admin
  token or auth is configured, like the rest of the mutation surface, and they
  are not part of the Esri projection: `/arcgis/admin/uploads` keeps its
  multipart-only shape, and projecting chunked Esri `add`/`combine` onto this
  staging is later work (ADR-0041 §5, not decided here).
- Staged bytes live on the host's disk under `Spatial:Uploads:Path` (a
  per-process temp directory by default), not in a store. A store that has to
  survive its own failure is not the place to keep a transient document, and
  nothing queries these bytes; a store-backed staging would be a new persisted
  state per provider for no reader.
- The .NET and TypeScript SDKs both gain the staging verbs and a driver
  (`ResumableIngest.UploadAsync`, `ingestResumable`) that asks the host for the
  offset rather than trusting its own count, retries a chunk that failed in
  transit from the reported offset, and ingests only once complete. Both SDKs
  require a re-readable source (a seekable `Stream`, a `Blob`) — that is what
  resuming *means*, and a forward-only stream is rejected before anything is
  staged.
- A caller that used to post one large body sees the same result. The
  observable changes are additive: a new `upload=` parameter, new routes, and a
  shared cap message on the Esri projection's non-streaming path (which no
  longer names the feature count it saw).

## References

- ADR-0041 (ingest and publications; §3 atomicity, §4 decode, §5 the Esri
  projection, §6 the caps), ADR-0082 (streaming decode and the load face this
  stages into), ADR-0023 (bounded streams), ADR-0033 (in-process interfaces),
  ADR-0045 (observability of the mutation routes)
- `architecture/distilled/host-and-clients.md` — the route table this adds to
- Follow-up: the Esri chunked-upload projection and a CLI flag
