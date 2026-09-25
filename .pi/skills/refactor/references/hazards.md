# Hazards

Moves that only look behaviour-preserving. Each entry: why the move changes behaviour, and what to do instead.

## Build a net first

When the behaviour you are about to move has no test, write a **characterisation test** before the move: call the current code, assert what it actually returns today, including the ugly cases. That test is the net, and it is allowed to be ugly: it pins behaviour, not intent. It becomes a real assertion when the behaviour it pinned becomes intended.

## Names that reach outside the process

- **Serialised payloads.** A property name, casing, or numeric format in a DTO crosses the wire. A rename changes the API for every client reading it. Keep the wire name, and add the new one as an alias if the rename is wanted.
- **Contract interfaces.** A signature on `Spatial.Contracts` is a contract change: it needs the contract, SDK, test, and ADR updates together, not a silent tidy in a refactor step.
- **Exception codes.** `SpatialException` codes are a client contract. Renaming or re-mapping one is a behaviour change, and the failure tests are the net that catches it.
- **Configuration keys, env vars, CLI flags.** They are the environment's source of truth. Read them from the same key the config already names; a tidy that renames a key breaks a deployment nobody here can see.

## Arithmetic and formatting

- **Doubles.** Reassociating a sum, replacing a loop with a closed form, hoisting a subexpression out, or caching an intermediate changes the last bits. Geometry code moves by relocating whole operations, never by rewriting the arithmetic. Assert with the tolerance the real code already uses.
- **Culture and parsing.** `InvariantGlobalization` and per-call culture providers mean a parse or format change moves a boundary. Keep the same culture and format string the line already used; a "clearer" numeric parse is a new behaviour.
- **Precision and rounding.** `decimal` to `double`, rounding mode, and truncation order are all observable. Treat a change of type as a change of behaviour.

## Time, ordering, and identity

- **Time source.** `DateTime.Now` versus `UtcNow`, an injected clock versus a real one, and a value hoisted into a field all change results across a boundary. Keep the source, keep the moment.
- **Randomness.** Seeded and unseeded generators are not interchangeable, and a generator moved into a field is now shared. Preserve the seeding, or the tests stop being repeatable and you will read that as a flaky failure.
- **Ordering.** `HashSet` and dictionary iteration, `Task.WhenAll` completions, and `IEnumerable` laziness all promise less than they deliver. Sorting the collection is a behaviour change; moving code that produced it is not.

## Concurrency and lifetime

- **Lock scope and shared state.** Moving code in or out of a `lock`, or making a `static` cache that was per-instance, changes interleaving. These need a test that exercises concurrency, or they wait for one.
- **Async and cancellation.** Sync to `Task<T>`, `Task<T>` to `ValueTask<T>`, dropping `ConfigureAwait`, or moving where a `CancellationToken` is observed all change timing and what happens under cancellation. A cancellable path gets a cancellation test before the move, and the observation point stays put.
- **DI lifetimes and registration order.** Changing a registration from scoped to singleton, or the order two registrations resolve in, changes which instance a caller sees. That is a composition change: name it, and let it be its own step.

## Failures and diagnostics

- **`try` boundaries.** Moving code across a `try` changes what is caught, what is logged, and what the caller sees. Keep the boundary where it is, or move the boundary as its own step with a failure test on both sides.
- **Fakes that reimplemented the logic.** A hand-written fake carrying a copy of the logic the step deletes now tests the fake, and it keeps passing while the real code rots. Tests cross the seam: assert through the interface, and let the fake return canned data.

## Files that are not yours to edit

- Generated and derived output: anything under `obj/` and `bin/`, generated sources, and anything a tool rewrites on build. Edit the generator's input.
- Pinned dependencies: `Directory.Packages.props` and lock files. A version bump is a deliberate act, with its own verification.
- Migration history: applied schema changes stay as they were; a new change is a new migration.
