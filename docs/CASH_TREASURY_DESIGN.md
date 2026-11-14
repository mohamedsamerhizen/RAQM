# Cash management and treasury design

Register ordinary cash and petty cash against dedicated asset GL accounts. An organization Owner assigns the petty cash custodian from the company's current financial writers. The custodian or an Owner may record petty expenses and cash counts. Owners approve transfers, post ordinary cash movements, approve variance corrections and reverse posted treasury movements. A company financial writer can prepare a transfer. Mandatory distinct preparer and approver, limits by approval tier and external payment execution remain future controls.

## Financial effects

| Movement | Debit | Credit |
| --- | --- | --- |
| Internal bank, cash or petty cash transfer | Destination asset | Source asset |
| Ordinary cash receipt | Cash | Revenue |
| Ordinary cash payment or petty cash expense | Expense | Cash or petty cash |
| Cash-count shortage | Expense | Petty cash |
| Cash-count excess | Petty cash | Revenue |
| Bank fee | Expense | Bank |
| Bank interest | Bank | Revenue |

A USD 5,000 transfer from Bank A to Bank B posts one journal with a USD 5,000 debit to Bank B and a USD 5,000 credit to Bank A. Both accounts belong to the same company. A transfer remains pending until an Owner approves the current version for the requested accounting date. The request and approval fingerprint prevent duplicate effects. Reversal creates a linked journal with exactly opposite lines. Bank balances can be negative where the posted GL records an overdraft. Petty cash checks that an outflow leaves a nonnegative book balance at the posting date and at subsequent affected daily cutoffs.

Replenish petty cash by transferring funds to it. Expenses require an evidence reference. A cash count captures the physical and posted book amounts at the count date. A correction records the exact variance and requires the count to remain current and its stored book amount to agree with the ledger. Counting creates no journal. Replenishment changes the two asset balances and creates no expense.

## Transaction and migration boundaries

Phase 03 uses the existing FinancialTransaction coordinator and shared Posting Engine. The coordinator locks the company and owns the commit. Treasury services stage their source, call the engine, then attach the posting receipt and finalize state in the same transaction. Failure rolls back the source change and journal. Approval, reversal, adjustments and variance corrections use the same base-currency and fiscal-period policy as other financial postings.

`BankingTreasuryV1` adds nine tables in the `banking` schema for bank profiles, treasury accounts, statements and lines, reconciliation sessions, matches and items, movements and cash counts. It adds a nullable company-scoped bank FK to trading payments. It also adds immutable evidence guards, statement continuity and balance checks, active match reservations and deferred treasury source/journal contracts. The database also checks reconciliation finalization amounts and protects finalized cutoffs. PostgreSQL execution evidence is tracked separately in the [release verification](RELEASE_VERIFICATION.md).

## Inquiries and endpoints

Treasury reports read posted GL lines at a repeatable snapshot. They show opening and closing book balances, gross incoming and outgoing amounts, attributed trading flows, bank charges and the latest finalized reconciliation cutoff. Individual journals, payment IDs and available bank attribution remain traceable. Gross amounts include transfers between treasury accounts and can be used to explain account activity. These inquiries provide basic liquidity visibility. Cash forecasting, investment treasury, intercompany and statutory cash-flow reporting are later work.

Relative to `/api/v1/organizations/{orgId}/companies/{companyId}`:

| Path | Methods |
| --- | --- |
| `/cash-management/transfers` | POST prepare |
| `/cash-management/transfers/{id}/approve` | POST approve and post |
| `/cash-management/movements` | GET history, POST cash or petty expense |
| `/cash-management/movements/{id}/reverse` | POST exact reversal |
| `/cash-management/counts` | POST petty cash count |
| `/cash-management/accounts/{id}/counts` | GET count history |
| `/cash-management/counts/{id}/correct` | POST authorized variance |
| `/treasury/position` | GET positions across company accounts for a date range |
| `/treasury/accounts/{id}/activity` | GET dated and paginated GL activity |
