# Domain map

The phase column distinguishes implemented source from future scope. PostgreSQL execution remains unverified for Phases 1 through 9 in the recorded offline runs.

| Module | Ownership and dependencies | Phase |
| --- | --- | --- |
| Identity | Credentials, users, session lifecycle; no ownership of legal entities | 0 implemented |
| Organizations | Tenant isolation, organizations, legal companies, memberships, currency and fiscal defaults | 0 implemented |
| Chart of Accounts | Company-specific accounts, classifications, hierarchy, normal/contra balances and activation | 1 implemented; PG unverified |
| General Ledger | Immutable posted journal headers/lines, company sequence, source links, reversals | 1 implemented; PG unverified |
| Posting Engine | Single atomic validation/posting entry point, period locks, balance checks, idempotency | 1 implemented; PG unverified |
| Fiscal Periods | Company fiscal calendar, open/closed periods, controlled lock/reopen workflows | 1 implemented; PG unverified |
| Accounts Receivable | Customers, invoices, credits, receipts, allocations and aging through the shared Posting Engine | 2 implemented, PG unverified |
| Accounts Payable | Suppliers, bills, credits, payments, allocations and aging through the shared Posting Engine | 2 implemented, PG unverified |
| Banking | Dedicated bank GL mappings, bank-attributed settlements, immutable statement imports and reconciliation | 3 implemented, PG unverified |
| Cash management | Internal transfers, ordinary cash receipts and payments, petty cash disbursements, replenishment and counts | 3 implemented, PG unverified |
| Treasury | Posted cash position, bank/cash activity, outstanding movements and reconciliation cutoffs | 3 foundation, PG unverified |
| Reporting | Trial balance, account ledger, subledger and treasury inquiries, financial statements, Cash Movement, drill-down and exports | 1–4 implemented, PG unverified |
| Financial close | Period checklist, assigned evidence, readiness, Owner approval, final recheck and accounting state transition | 4 implemented, PG unverified |
| Accruals and deferrals | Approved schedules, posted basis links, recurring recognition and exact reversals | 4 implemented, PG unverified |
| Fiscal year-end | Final-period approval, typed nominal close, retained earnings and next-year continuity | 4 implemented, PG unverified |
| Tax | Company tax codes, effective rate versions and line calculations | 2 foundation, PG unverified |
| Multi-currency | Approved direct quotes, dedicated foreign subledgers and banks, monetary revaluation, carrying-value settlement and exposure reconciliation | 8 source, PG unverified |
| Fixed Assets and Capital Expenditure | Company register, component accounting, AP cost claims, depreciation, impairment, disposal, GL reconciliation and fiscal close controls | 5 implemented, PG unverified |
| Procurement | Requisition and purchase order approvals, commitments, receipt sources and three-way match handoff to AP | 6 source, PG unverified |
| Inventory and Warehousing | Item and warehouse registers, reservations, immutable stock movements, transfers, counts and source evidence | 6 source, PG unverified |
| Cost Accounting | Perpetual FIFO or moving average, GRNI, landed cost, NRV, invoice-linked cost of sales and GL reconciliation | 6 source, PG unverified |
| Budgeting and Control | Fiscal allocations, versioned approvals, procurement and capital commitments, account limits and posted-ledger variance | 7 source, PG unverified |
| Forecasting and Scenarios | Deterministic drivers, immutable rolling snapshots, cash timing projections and approved alternatives | 7 source, PG unverified |
| Financial Dimensions | Company values and hierarchies, account rules, source bindings and immutable posted-line evidence | 9 source, PG unverified |
| Project Accounting | Project lifecycle, procurement exposure, scoped budget and GL financials with completion snapshots | 9 source, PG unverified |
| Cost Allocation | Retained shared-cost allocations, management redistribution and same-account financial reclassification | 9 source, PG unverified |
| Corporate Groups | Scoped company register, effective ownership and control, approved group chart mappings, reporting grants | 10 source, PG unverified |
| Intercompany | Atomic legal-ledger pairs, supported service and funding flows, reciprocal cutoff evidence | 10 source, PG unverified |
| Consolidation | Closed-period snapshots, worksheet, supported investment and reciprocal eliminations, translation and published reporting | 10 source, PG unverified |
| Tax Accounting | Company registration, approved commercial pricing evidence, coordinated tax postings, reconciled return versions and structured invoice evidence | 11 source, PG unverified |
| Governance and Internal Audit | Effective financial policies, transaction-bound approvals, scoped access, audit chain, control testing, risks and remediation | 12 source, PG unverified |
| Integrations | Scoped machine access, atomic event capture, leased webhook delivery, approved customer imports and ledger exports | 13 source, PG unverified |
| Governance | Approval workflows, segregation of duties, immutable audit, attachments, retention | incremental |
| SaaS Commercial Platform | Tenant lifecycle, plan versions, entitlements, quotas, operator billing, support and portability | 14 source, PG and operations unverified |
| Platform Operations | Invitations, notifications, API integrations, import/export and backups | incremental |

Financial modules own their documents and lifecycle. Only the posting engine writes trusted posted ledger data. Reporting reads trusted posted data; it must not independently reconstruct balances from mutable invoices. Documents link to journals through stable source identity and explicit reversal relationships. Cross-module changes initially share the DbContext transaction; future external side effects require an outbox, not a network call within posting.

Accounting, Trading, Banking, CashManagement and Treasury use responsibility-based folders within the API. See the [AR/AP design](AR_AP_DESIGN.md) and [banking design](BANKING_DESIGN.md) for source document and ledger boundaries.


