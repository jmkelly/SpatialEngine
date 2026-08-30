| Code | Location | Message |
| ---  | ---      | ---     |

**Gate**: 0 warning(s) found — 0 required (clean build). Full data in warnings-report.json.

Suppress (repo policy): a *specific* `<NoWarn>` entry in the csproj with a documented reason. Blanket suppression to dodge the gate is rejected (anti-gaming rules).

Diagnosis (verifier): read the source at each location and add a fix recommendation.
Fix (implementor): fix the warning — remove the unused code, apply the analyzer's suggested API — or add the specific NoWarn with a reason; then re-run this audit.
