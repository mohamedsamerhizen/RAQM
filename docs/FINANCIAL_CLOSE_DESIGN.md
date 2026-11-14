# Period close design

The financial close session is a review workflow. AccountingPeriod remains the source of the Open, Closed and Locked accounting state. An open period can have one active session. Sessions move from Preparing to Approved to Completed. An Owner can send an Approved session back to preparation or supersede an active session with a reason. History retains completed and superseded evidence and separately shows the current accounting period state.

Preparation creates a required GL review task. Financial writers can add required or optional tasks and assign a current company financial writer. An assigned reviewer or Owner records task evidence. Writers can raise exceptions with a blocking flag, and Owners resolve them with evidence. Evidence is retained after approval and close. The Owner's approval reason acknowledges review findings.

## Readiness rules

| Finding | Treatment |
| --- | --- |
| Period outside the close calendar or current state | Block until the period is open and the ending date has arrived. |
| Invalid draft, incomplete required task, or unresolved blocking exception | Block. Balanced unposted drafts, unposted trading documents and optional checklist exceptions require review. |
| AR/AP configured | Run the existing control and payment reconciliation at the cutoff. Block discrepancies or missing control mappings. A company without configured AR/AP receives an informational finding. |
| Required bank evidence | Require continuous finalized statements through period end for registered banks with a balance, period book activity or an overlapping statement. A dormant bank without that evidence requirement does not block. |
| Recognition | Block due unposted occurrences, missing schedule approval and overdue required reversals. Review reversals posted after cutoff. Future approved occurrences are informational. |
| Report integrity | Block a Balance Sheet difference or incompatible account mapping. Unclassified balances require review. |
| Final fiscal period | Require an eligible retained earnings mapping, closed or locked earlier periods, no earlier nominal residual, and nominal accounts eligible for posting. |

The approval fingerprint includes the current period version, relevant posted journal and draft identities, current chart and definition versions, reconciliation results and bank evidence, applicable recognition links, checklist and exceptions. The final operation reacquires the company lock and recomputes readiness. Any changed fingerprint requires revalidation and approval. The existing period service transitions the period after the approved session completion has been staged. Both changes commit in the same transaction. An active session prevents bypass through the legacy period state route. Periods without an active session retain the existing authorized state route. Locked periods stay terminal. A completed ordinary close can have a new review session after an authorized reopen. A finalized fiscal cutoff has the stronger year-end restriction.

Successful finalization records the approving and closing actors, reasons, fingerprint, findings and linked year-end journal if applicable. Readiness displays current blockers. Rejected business close attempts are retained in accounting audit after rollback. The database guards prevent mutation of completed evidence and require the actual Closed state when completion commits.

## Routes

All paths are relative to `/api/v1/organizations/{orgId}/companies/{companyId}/financial-close`.

| Method | Path |
| --- | --- |
| GET, POST | `/periods/{periodId}/sessions` lists history and starts preparation |
| GET | `/sessions/{id}/readiness` reads findings |
| POST | `/sessions/{id}/tasks` and `/sessions/{id}/tasks/{taskId}/complete` |
| POST | `/sessions/{id}/exceptions` and `/sessions/{id}/exceptions/{exceptionId}/resolve` |
| POST | `/sessions/{id}/state/{approve,revalidate,supersede,close}` |
| GET, POST | `/years` lists retained records and `/years/{yearId}/close` executes year-end |

See [year-end design](YEAR_END_CLOSING_DESIGN.md) for the final fiscal period.
