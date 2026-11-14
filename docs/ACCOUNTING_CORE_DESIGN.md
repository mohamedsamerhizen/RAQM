# Accounting core design — Phase 01

Implementation lives in the existing Controllers / Services / Interfaces / Entities / DTOs / Data / Helpers folders, grouped under Accounting. There are no additional projects, repositories or message buses. The API remains a .NET 10 / PostgreSQL modular monolith.

See [release verification](RELEASE_VERIFICATION.md) for build, migration and PostgreSQL test evidence, and for deployment checks still pending.

## Persisted entities

All accounting entities carry Id, TenantId, OrganizationId and CompanyId. Each references the existing company through its tenant/organization/company composite key. Alternate keys (CompanyId, Id) back all financial cross-references. Actor references point to Identity users; timestamps are UTC.

| Entity / accounting table | Responsibility |
| --- | --- |
| LedgerAccount / Accounts | Code, name/description, class, normal side, contra flag, parent, posting/active flags, version, creator/editor metadata |
| FiscalYear / FiscalYears | Immutable company fiscal calendar and inclusive start/end dates |
| AccountingPeriod / Periods | Fiscal-year/number, inclusive dates, Open/Closed/Locked, version and editor |
| JournalEntry / Journals | Draft/Posted, document/posting dates, description/reference, currency/policy, posting period, sequence/number, source identity, idempotency hash/key, reversal link, actors/times |
| JournalLine / JournalLines | Ordered account debit/credit lines and descriptions; authoritative financial amounts |
| OpeningBalanceBatch / OpeningBatches | One initial conversion batch per company, immutable batch key/name and draft/posted journal link |
| AccountingAuditEvent / AuditEvents | Append-only actor/time/action/reason evidence for account, period, draft, posting, reversal and opening operations |

No account-balance table, mutable running-balance cache or unused AR/AP/inventory table exists. Database roles with schema-owner/superuser powers can disable triggers; production application roles must not have those powers.

## Accounts and hierarchy

Account codes are trimmed, uppercased ASCII letters/digits with optional dot, underscore or dash, 1..32 characters; unique within a company. Names are 1..160; descriptions at most 1000. Classes are Asset=0, Liability=1, Equity=2, Revenue=3, Expense=4.

Normal sides: Asset/Expense are Debit=0; Liability/Equity/Revenue are Credit=1. IsContra reverses the normal side. Only active posting accounts can receive entries. Parent accounts must be active, non-posting, same-company and same-class; cycles and posting-enabled parents with children are rejected. Active children prevent parent deactivation.

Once an account or any descendant has posted activity, its code, classification, contra/normal side, posting flag and parent cannot change. Names/descriptions may change. Deactivation remains possible for posting leaves and prevents future posting without removing history. No account-delete endpoint exists; the database rejects deletion.

The explicit basic-v1 initialization template creates five non-posting roots and five posting children: assets/cash (1000/1100), liabilities/payables (2000/2100), equity/opening equity (3000/3100), revenue/sales (4000/4100), expenses/operating expenses (5000/5100). Initialization requires an empty company chart and is atomic. This is a small usable starting chart, not jurisdiction-specific statutory accounting setup.

## Monetary policy v1

.NET decimal and PostgreSQL numeric(20,4) are used for all financial amounts. The largest supported line or journal-side total is 9999999999999999.9999. API validation rejects negatives, excessive magnitude and fractional units finer than the selected company's currency. Each line has exactly one positive debit or credit.

Supported ISO 4217 subset:
- 3 units: IQD, KWD, BHD, JOD, OMR, TND.
- 2 units: USD, EUR, GBP, AED, SAR, CAD, AUD, CHF, CNY.
- 0 units: JPY, KRW.

Units were checked against the [ISO 4217 maintenance agency SIX current currency list](https://www.six-group.com/dam/download/financial-information/data-center/iso-currrency/lists/list-one.xml); the extracted values are tools/verification/phase01/iso4217-units.json. Runtime behavior uses the frozen v1 dictionary, not live downloads or the host's region data.

Posting timestamps are normalized to PostgreSQL microsecond precision before the initial receipt is returned, so a DB-loaded idempotent retry retains exactly the same timestamp. Every journal captures CurrencyPolicyVersion=1 and MinorUnits. Only the company's base currency is supported. Unsupported Phase 0 currencies fail closed on accounting setup/posting; adding another supported currency requires a reviewed policy/database migration. No FX rate, revaluation, settlement or consolidation feature is claimed.

Amounts are **never silently rounded by application code**. Decimal.Round(..., minorUnits, ToEven) is a validation comparison; a mismatch is rejected. Callers must round explicitly before submission. Numerically equal trailing zeros are accepted. Stored-line triggers check minor-unit precision and posting repeats it. PostgreSQL coerces values to numeric(20,4) before row triggers: direct privileged SQL inputs beyond scale 4 can be rounded by PostgreSQL before a trigger can observe the original input. Application validation is therefore essential; do not grant clients direct SQL write access.

## Fiscal calendar and controls

A fiscal year starts on the company's configured recurring month/day in a supplied start year (1900..9998), ends the day before the following anniversary, and generates twelve contiguous monthly periods. Month boundaries are anchored to the fiscal start day using DateOnly.AddMonths. Day 1 gives calendar-month periods; a day-31 anchor clamps short months without gaps. February 29 is excluded from recurring company settings by Phase 0 validation.

Year and period ranges cannot overlap within a company. PostgreSQL GiST exclusion constraints use inclusive daterange ranges and btree_gist UUID equality. Periods must lie within their fiscal year. Calendar rows are immutable: there is no edit/delete calendar endpoint. Period transitions require organization ownership, a current version and a reason. Open can close or lock; Closed can reopen or lock; Locked is terminal.

Company fiscal defaults freeze as soon as a fiscal year exists. Currency and fiscal changes freeze after any posted journal. Permitted name changes remain available. Both application checks and a company-update trigger enforce these rules.

Only open periods accept postings or reversals. Future posting/document dates and document dates before 1900 are rejected on validation/posting. Drafts may be prepared ahead of time but cannot create future financial effects. Backdating is explicit: AllowBackdated=true, a nonempty reason, and an organization Owner are required. Company Managers can post current-date entries, not approve backdating or period operations.

## Journal lifecycle

Draft -> Posted is the only financial transition. Drafts have source identity and currency metadata but no posted sequence/number. Draft creation/update validates line amounts, scope and accounts; balance/eligibility/period checks run at explicit validation and posting. Unbalanced drafts can be prepared but cannot post.

Draft edits require the retrieved Version. Line replacement and header edits share a transaction. Draft deletion removes its lines/header and retains an audit event. Opening source drafts are retained and edited instead of deleted. Posted journals/lines cannot be edited or deleted by the API; database triggers also reject mutations, new lines on posted headers and TRUNCATE of financial tables.

Posted headers keep their immutable original state even after reversal. A reversing journal points to ReversesJournalId and SourceDocumentId=original ID; audit records are appended to both original and reversal. Journal reference/source fields are durable traceability contracts. Binary attachments, approval queues and signed archival documents are not implemented.

## Posting engine contract and transaction

IPostingEngine.PostAsync(actorId, PostingRequest, CancellationToken) is the single implemented posting boundary. Controllers do not bind a trusted TenantId or actor from JSON. They obtain actor from authentication and construct scope/source from persisted authorized drafts. Future trusted in-process modules supply their stable source identity and actor; the engine independently resolves membership/company permissions and checks the tenant against the persisted company.

The engine owns a transaction:
1. Validate key/text/decimal shape; resolve write permission.
2. Lock the authorized company row FOR UPDATE and recheck membership permissions.
3. Compare existing company-scoped idempotency key and canonical SHA-256 financial payload. Identical same-key retries return the original receipt; changed payload is 409.
4. Reject a second journal for the same company/source module/document under a new key.
5. Validate balance, base currency, dates/backdate authority, open period, eligible accounts/ancestors and any opening/reversal source.
6. Recheck persisted draft/version/payload, or stage a new in-process-source journal as Draft with its lines.
7. Allocate the next company-wide committed sequence and GL-00000001-style number, transition to Posted, and append audit evidence.
8. Save and commit all financial effects together.

The payload hash covers scope, source, currency, dates, descriptions/reference, ordered account/amount/description lines and reversal identity. Decimal amounts use invariant canonical strings so 10.0 and 10.00 represent the same intent. Idempotency key, mutable retry flags and actor are not financial payload fields. Even retries require current company write permission. A same-key retry of a historically posted entry returns its receipt without revalidating today's account/period state; it creates no new financial effect.

Unique company-scoped posted indexes protect sequence, idempotency key and source. A filtered unique reversal index protects the original link. Invalid/failing writes roll back; the tests include an injected failure after staging draft/lines but before the posted save.

The engine currently owns its complete transaction. Future AR/AP coordinators must design source-document-state/outbox transaction composition through this boundary rather than writing ledger tables themselves or claiming an external document was atomically updated by the current API. No AR/AP document implementation exists in Phase 1.

## Concurrency and database defense

All accounting writes, period state changes, account updates and company settings/grant/revocation updates serialize on the company row. This is intentionally coarse, predictable correctness at the initial scale. Different companies remain independent; high-volume per-company lock refinement needs new race tests.

Database financial triggers acquire the same company lock. Additional guards enforce immutable scope, account hierarchy/used structure, period calendar, posted header/line immutability, balanced/eligible posting, opening sources, exact reversal negation and append-only audit. Before transitioning a header to Posted the database sees its already-staged lines; the service flushes Draft/lines inside the same uncommitted transaction first.

GiST exclusions prevent range races even outside the application. Definitions and transactional locks follow PostgreSQL's [trigger documentation](https://www.postgresql.org/docs/current/sql-createtrigger.html) and [explicit locking semantics](https://www.postgresql.org/docs/current/explicit-locking.html). The actual custom DDL is in the immutable AccountingCoreV1Sql migration helper. These guards have NOT been executed against PostgreSQL in this phase's environment.

## Opening balances and reversals

One initial opening batch per company prevents duplicate conversion balances under new batch keys. It must use the first configured fiscal year's start date. Once a batch exists, service/database guards prohibit adding an earlier fiscal year that would move this anchor, including while the batch is still a draft. Supplied debit/credit account balances must already balance; no hidden suspense plug is generated. Creation makes a linked draft; posting uses the same engine, eligibility, backdating and period controls. Amounts are never written directly to account balances. Corrections use the linked draft before posting or reversal/correction journals after posting.

A reversal must exactly negate the original ordered lines in the same company. It has its own valid posting date/open period, mandatory reason, posting actor and idempotency key. Current account eligibility still applies: if an account was deactivated, it must be explicitly reactivated before reversal. The original remains unchanged. Duplicate reversal under a new key and reversal-of-reversal are rejected; same-key retries return the existing reversal receipt. Correction workflow is reversal plus a new correct journal, not mutation.

## Trusted ledger and trial balance

Only State=Posted journal lines contribute. Each report uses a PostgreSQL repeatable-read transaction so headers, totals, page counts and rows share one snapshot.

Signed balances use debit-positive / credit-negative consistently, independent of the account's normal side. Opening balance is all posted activity before From. Movement is inclusive From..To. Closing = Opening + Debits - Credits. ClosingDebit=max(Closing,0); ClosingCredit=max(-Closing,0).

Account ledger order is posting date, company journal sequence, line number. Page carries include all earlier page movements, with stable underlying journal/line/source/account drill-down IDs. Non-posting parent ledgers include descendant account lines, matching optional trial-balance hierarchy rollups. Trial-balance grand totals include posting accounts only, avoiding double-counting parent summaries. Inactive accounts remain reportable.

No statement of profit/loss, balance sheet presentation, cash-flow statement, FX report or automatic year-end retained-earnings transfer is claimed; those require additional reporting/accounting policy work.

## APIs

All paths below are appended to /api/v1/organizations/{orgId}/companies/{companyId}/accounting and require authentication plus server-side company access.

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET/POST | accounts | Search/filter/page accounts; create |
| GET/PUT | accounts/{id} | Read/update account with version |
| POST | accounts/initialize | Initialize empty chart with basic-v1 |
| GET | accounts/currency-policy | Versioned supported currency catalog |
| GET/POST | fiscal-years | List years including periods; generate a year and 12 periods |
| PUT | periods/{id}/state | Owner-controlled close/reopen/lock with version/reason |
| GET/POST | journals | Search/page history; create manual draft |
| GET/PUT/DELETE | journals/{id} | Read/update/delete draft; posted mutation rejected |
| POST | journals/{id}/validate | Validate draft against posting controls |
| POST | journals/{id}/post | Idempotently post persisted source |
| POST | journals/{id}/reverse | Post linked reversal |
| GET | journals/{id}/audit | Actor/time/reason trace |
| GET/POST | opening-balances | Read/create initial batch and linked journal |
| POST | opening-balances/{id}/post | Post through the engine |
| GET | accounts/{id}/ledger | Date-filtered, paginated ledger including parent descendants |
| GET | trial-balance | Date-filtered trusted totals; optional hierarchy |

Account filters: search, classification, active, posting, page and pageSize. Journal filters: from, to, state, search, page and pageSize. Pagination is 1..200 rows, page 1..100000. Ledger/trial require from/to; reports begin in/after 1900. Reader grants can read reports; Manager grants can perform ordinary financial writes. Organization Owners additionally control fiscal periods and backdating. No new publicly assignable privileged role exists.

Created drafts/accounts/years/batches return 201; posting/reversal/validation return 200; draft deletion returns 204. Domain errors use existing ProblemDetails with an additional stable code only for new accounting errors (e.g. journal_unbalanced, period_closed, amount_precision, idempotency_conflict). Existing Phase 0 responses keep their prior shape.

## Example workflow (not an executed database verification)

After company creation with USD and authenticated owner membership:
1. POST accounts/initialize with {"template":"basic-v1"}; retain cash 1100 and sales 4100 IDs.
2. POST fiscal-years with {"startYear":2026,"name":"FY 2026"}; use a year containing the actual current UTC posting date.
3. POST journals with the body below, replacing date/IDs with valid current values:

~~~json
{
  "postingDate": "2026-11-06",
  "documentDate": "2026-11-06",
  "description": "Cash sale",
  "reference": "SALE-001",
  "lines": [
    {"accountId":"<cash-id>","debit":100.00,"credit":0,"description":"Cash received"},
    {"accountId":"<sales-id>","debit":0,"credit":100.00,"description":"Revenue"}
  ]
}
~~~

4. POST journals/{id}/validate then /post with {"idempotencyKey":"sale-001","version":"<draft-version>"}.
5. GET the cash ledger and trial balance for that date: expected cash debit 100, sales credit 100, balanced totals.
6. POST journals/{id}/reverse with current postingDate, reason and a new idempotencyKey. The reversal offsets the original; it does not remove it.

Full request records are under DTOs/Accounting; examples must not be mistaken for completed PostgreSQL test evidence.

## Phase 2 posting composition

The scoped FinancialTransaction coordinator owns the company lock and transaction commit for standalone journals and trusted source documents. Trading joins it to commit source state, settlement events and journal effects together. AR/AP, tax and advance controls require validated source posting. See [financial settlement design](FINANCIAL_SETTLEMENT_DESIGN.md) for the transaction boundary and [AR/AP design](AR_AP_DESIGN.md) for legacy mapping policy.


