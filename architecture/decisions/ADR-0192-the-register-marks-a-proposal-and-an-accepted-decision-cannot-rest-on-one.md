---
status: accepted
date: 2026-10-07
deciders: maintainer + agent
summary: The register marks a `status: proposed` row, the gate refuses an accepted record that amends a proposal, and seven load-bearing proposals are ratified.
amends: ADR-0141
---

# ADR-0192: The register marks a proposal, and an accepted decision may not rest on one

## Context

Seven records sat at `status: proposed` on 2026-10-07 — ADR-0035, ADR-0036,
ADR-0037, ADR-0038, ADR-0071, ADR-0075 and ADR-0079 — while the code each one
decides was landed. ADR-0071 is the sharp one: the contracts
(`IAuthService`), the host composition (`AuthOptions`, `LocalAuthService`),
the CLI (`AuthTokenStore`) and the served `POST /api/auth/login` all exist,
so the decision is de facto made and the record still reads as a draft. Two of
the seven were not merely cited: accepted records already refine them, so four
accepted decisions (ADR-0106, ADR-0140, ADR-0156, ADR-0166) `amends:` a record
whose own front matter says it is not yet decided.

The register in `architecture/distilled/README.md` is the routing surface an
agent reads before opening a record, and ADR-0141's generator renders every row
from `summary` alone. It marks a superseded record with a strikethrough, so it
already knows standing can change what a row has to say, but a proposed record
and an accepted one are the same row. An unratified decision that code
implements is a decision that can be overturned after the fact, with the
overturn paid in code — and the reader routing through the register could not
see it to argue with it.

## Decision

**A record's own `status` decides what its register row says, and an accepted
record may not amend a record that is still `status: proposed`.**

1. `tools/arch-index.py` renders a `status: proposed` record with a
   `**(proposed)**` marker, the same way a superseded record already carries its
   strikethrough. The marker is generated from the record, so it cannot drift
   from the front matter, and it is the standing the routing reader needs: a
   proposal is not a ratified decision.
2. `arch-index.py --check` — every lane of `eng/verify.sh` — fails when a record
   at `status: accepted` carries an `amends:` link to a record still at
   `status: proposed` (`load_bearing_proposals`). The finding is the case where
   overturning the proposal is paid for in the accepted record's code, so the
   edge may not sit in the corpus unremarked. The remedy is to ratify the
   proposal on its own record, not to drop the link: the link is the evidence
   the decision was made.
3. The seven records above are ratified to `status: accepted` under this
   record. Each one's code is landed, accepted decisions already rest on two of
   them, and the decision to implement them was taken when the implementation
   merged rather than here. ADR-0071's `summary` loses the `(proposed)` suffix it
   carried, because the register renders the summary and a ratified row may not
   say otherwise.

## Alternatives

- **Mark the proposal in the register only.** Rejected: a marker is a reader's
  signal, and the four accepted refiners show a proposal can accumulate
  accepted dependents without anyone reading the row. The gate is what makes
  the next such edge a failure rather than a footnote.
- **Ratify without a marker or a gate.** Rejected: it fixes today's seven and
  leaves the next proposal invisible, which is the defect this record is about.
- **Leave the seven proposed and record what is open.** Rejected on the
  evidence: their code is landed and their contract surface is served. There is
  no open decision to record — only an unflipped status.
- **Make `proposed` impossible in the corpus.** Rejected: a proposal is a
  legitimate state while a decision is being written, which is why `TEMPLATE.md`
  opens with it. This record makes a proposal visible and stops it carrying an
  accepted decision; it does not forbid proposals.

## Consequences

- An agent routing through the distilled digest can tell a draft from a
  ratified decision without opening `architecture/decisions/README.md`, which
  already carried a status column the register did not.
- A proposed record with an accepted refiner is now a red gate, so the
  decision to ratify it is taken explicitly instead of by silence.
- Cost: the seven status flips are a one-off corpus edit in this record's
  commit, and the register grows a second rendering branch beside the
  superseded one. Both are visible in the generated diff.

## References

- Amends ADR-0141, which generates the register and the index and gates them on
  every lane.
- ADR-0145, which made the register generated wholesale from the record's own
  `summary`.
- `tools/arch-index.py` (`register_row`, `load_bearing_proposals`),
  `tools/test_arch_index.py` (`ProposedRecordTests`).
- SpatialEngine-tp1, the audit that found the seven.
