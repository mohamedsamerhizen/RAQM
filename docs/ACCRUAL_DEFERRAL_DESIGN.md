# Accruals, deferrals and recurring journals

A schedule retains company currency, source reference, immutable terms, optimistic version and numbered occurrences. Financial writers can prepare schedules. An organization Owner approves or cancels them. An approved plan can be posted manually or executed through an explicit due-through run. A run processes occurrences and optional reversals chronologically. Each occurrence commits independently under the company lock and returns its posted journal or a failure. Failed attempts recorded by a run are audited. An authorized caller can retry a failed occurrence without duplicating earlier postings.

| Schedule | Debit | Credit |
| --- | --- | --- |
| Accrued expense, for example USD 120 unpaid rent | Expense 120 | Accrued liability 120 |
| Accrued revenue | Accrued asset | Revenue |
| Prepaid expense recognition, for example USD 100 monthly rent | Expense 100 | Prepaid asset 100 |
| Deferred revenue recognition, for example USD 100 earned service | Deferred revenue liability 100 | Revenue 100 |
| Recurring journal | Approved balanced template | Approved balanced template |

Recognitions use the scheduled accounting dates. The existing open-period, base-currency, account eligibility and authorized backdate rules apply. Occurrence ID is the posting identity. It has one original journal and at most one exact reversing journal. Cancellation stops future postings and retains original and reversing evidence. Occurrences are never silently reposted after reversal. A corrected agreement uses a new source with traceable accounting evidence.

Use one to 120 monthly, quarterly or annual occurrences. The date calculation anchors every occurrence to the first date. Equal allocations floor each regular installment to the currency minor unit and place the remaining amount in the final installment. A recurring template supplies the full balanced lines for every occurrence and the plan total must equal the per-occurrence debit multiplied by the count. Accrual and recurring schedules can specify a reversal delay. Owner-controlled reversal is also available where a correction is required.

A deferral references one unreversed posted basis line, for the full approved amount, on a suitable prepaid asset or deferred liability. The linked line is unique across schedules and remains linked after cancellation. The policy reserves the basis for that schedule. To replace the basis, cancel the schedule, reverse its posted recognition as needed, and use a corrected posted basis. Recognition consumes available balance at the occurrence date. A linked basis cannot reverse while its schedule is active or a recognition remains unreversed. The customer-advance and other protected AR/AP controls retain their Phase 02 posting rules. Revenue recognition needs an approved schedule reflecting earned performance. Recording cash alone creates no recognition schedule.

Schedule reconciliation compares planned, posted, reversed and pending amounts to the linked posted journals and derives remaining basis. The coordinator stages the source and commits the posting and its link together. Deferred database checks verify the source, ordered amounts and reversal linkage. The SQL execution evidence remains pending.

Routes are relative to `/api/v1/organizations/{orgId}/companies/{companyId}/recognition`.

| Method | Path |
| --- | --- |
| GET, POST | `/schedules` lists and creates schedules |
| GET | `/schedules/{id}/preview` and `/schedules/{id}/reconcile` |
| POST | `/schedules/{id}/{approve,cancel,run}` |
| POST | `/schedules/{id}/occurrences/{occurrenceId}/{post,reverse}` |

The run endpoint is the execution boundary for authorized schedulers and operators. This phase does not include a background job service, contract performance tracking, multi-currency recognition or weighted allocation schedules.
