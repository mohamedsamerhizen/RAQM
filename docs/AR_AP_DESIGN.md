# Accounts receivable and payable

The API uses Trading subfolders under Controllers, Services, Interfaces, Entities, DTOs, Data/Configurations and Helpers. Trading owns party profiles, documents and settlement events. Accounting owns the general ledger and its posting rules.

## Setup and scope

An organization Owner configures distinct, active, non-contra posting accounts. Receivables, input tax and supplier advances are assets. Payables, output tax and customer advances are liabilities. Default revenue, default expense, opening equity and at least one cash or bank asset are required. AR, AP, both tax controls and both advance controls are protected. Manual journals and general opening batches cannot post to them after activation. The shared Posting Engine validates the source and exact account amounts. The migration adds database guards for source and ledger contracts.

Mappings freeze when a party or tax code references the setup. Company currency freezes after setup. Control mappings over existing posted activity require explicit acknowledgement. Legacy entries remain in the GL and reconciliation exposes differences. An Owner can post an exact reversal with a reason for eligible activity posted before protection began. New posted trading documents are corrected through credit notes.

The authenticated principal supplies the actor. The financial access service resolves tenant, organization, company and current membership from persistence. Readers can query authorized companies. Owners and company Managers can perform ordinary financial writes. Setup, tax changes, refunds, payment reversals and backdated activity require an organization Owner. The company row lock serializes writes and protects the authorization recheck. Composite foreign keys enforce company and scope relationships.

## Party and document model

| Record | Purpose |
| --- | --- |
| Party | Customer or supplier profile, company/kind/code identity, contact details, billing address, informational tax ID, default account, terms, activity and optional credit limit |
| Document | Invoice, bill, credit note or opening amount, with party snapshots, document/due/posting dates, optional scheduled payment date, totals, version and journal link |
| Document line | Quantity, price, discount, tax rate version, selected accounts and computed amounts. Credits link to an original immutable line |
| Tax code and rate | A company tax identity tied to input and output accounts, with immutable, inclusive date windows and nonoverlapping versions |

Use one opening receivable or payable per party to import an opening obligation in an open period. It posts against opening equity and participates in aging and settlement. Reconcile the party imports against the legacy GL. Importing both a legacy GL balance and an opening document for the same obligation would duplicate it. A deactivated party retains history and can receive settlements and correcting credits.

Draft documents can be edited, approved or cancelled. Approval records the actor and freezes the financial payload. An approved document can be reopened, cancelled or posted with the current version. Posted and cancelled documents are immutable. Posting assigns a company/kind sequence and links the posted journal. Bill references are trimmed and normalized for uniqueness per company and supplier across active documents. Due dates default to document date plus party terms. Scheduled payment dates are informational.

Customer credit limits apply to net AR exposure when a receivable is posted, after any funding applied within that transaction. A null limit is unlimited and zero permits a fully prepaid transaction. A source posting retry with the same key and intent returns the same journal. A different request for an already posted source conflicts. Settlement status is calculated separately from posting state using the remaining balance and effective allocation events.

## Financial effects

| Document | Debits | Credits |
| --- | --- | --- |
| Sales invoice | Receivables | Revenue and output tax |
| Purchase bill | Expense or suitable non-inventory asset, plus input tax | Payables |
| Customer credit | Original revenue and output tax | Receivables |
| Supplier credit | Payables | Original expense/asset and input tax |
| Opening receivable | Receivables | Opening equity |
| Opening payable | Opening equity | Payables |

Credit quantities and cumulative net, tax and discount amounts are capped by the original line. Credits reuse its rate, price, tax mode and accounts. The last credited quantity takes the remaining monetary residual. Stale credit drafts must be reopened and recalculated when intervening credits change the residual. Credits default to allocating against the original outstanding amount and retain any excess as available funding. They cannot post before the original accounting date.

## Tax and decimal policy

Phase 02 uses the existing versioned company base currency policy. Quantity and percentage support six decimal places, unit price four, and stored money uses numeric(20,4) with the applicable currency minor units. Rates and discounts range from zero to 100 percent. Each line rounds gross first, then rounds the discount on gross, using midpoint-to-even. For exclusive tax, the resulting amount is the net and tax is rounded on that base. For inclusive tax, net is the rounded discounted gross divided by one plus the rate, and tax is the residual. Tax versions are selected for the document date. Totals are the sum of line snapshots. Jurisdiction-specific rules, tax filing and recoverability percentages remain outside this phase.

## HTTP workflow

All paths are relative to `/api/v1/organizations/{orgId}/companies/{companyId}/trading`.

| Path | Methods and operations |
| --- | --- |
| `/configuration` | GET mappings, PUT setup |
| `/parties`, `/parties/{id}` | GET search/get, POST create, PUT update |
| `/tax-codes`, `/tax-codes/{id}` | GET list, POST create, PUT update |
| `/tax-codes/{id}/rates` | GET list, POST immutable rate version |
| `/documents`, `/documents/{id}` | GET search/get, POST draft, PUT versioned draft edit |
| `/documents/{id}/{approve,reopen,cancel,post}` | POST lifecycle action |
| `/parties/{id}/statement`, `/aging`, `/reconciliation`, `/audit/{id}` | GET reporting and audit history |

Create the accounts and fiscal periods, configure trading, then create the party. Draft and approve an invoice or bill, post it, record and allocate payment, and compare the statement, aging and reconciliation for the required cutoff. Party search accepts kind, active and text filters. Document search accepts party, kind, financial state, due cutoff and text. Both are paginated.

See [Financial settlement design](FINANCIAL_SETTLEMENT_DESIGN.md) for payment effects and [release verification](RELEASE_VERIFICATION.md) for verification.

## Unissued stock cancellation

Create a customer credit referencing the original invoice and line. Set cancelUnissuedStock to true on each explicitly stocked line being cancelled, and use its sales-unit quantity. A different Owner must approve the draft. Post through the existing credit endpoint with an idempotency key and an eligible period. The credit offsets the original revenue, tax and receivable as applicable. Cancellation reduces required fulfillment at the posting cutoff and creates zero stock or COGS movements. Use the existing stock-return workflow for units already issued. Split a cancellation and physical return on the same invoice line into separate credits. Review or cancel any unposted issue that no longer reflects the shipping plan.

### Stock quantity precision

Sales-unit quantities and conversion factors accept up to six decimal places. The stock ledger uses base-unit quantities with up to six decimal places. Multiplication uses exact decimal arithmetic. The product for a stocked invoice, unissued cancellation or physical return must be exactly representable at stock precision. Halfway values are rejected in either direction. Currency calculations retain their existing rounding and do not determine stock quantity. The cancellation posting path and PostgreSQL both reject incompatible conversions before the financial transaction can commit.

For example, at a sales factor of 1.5, a cancellation of 0.333333 sales units produces 0.4999995 base units and is rejected. A cancellation of 0.333334 sales units produces exactly 0.500001 base units. Supply a representable quantity that reflects the authorized business intent.

To assess previously posted cancellations, a database operator can run [the read-only precision report](../scripts/StockCancellationPrecisionAudit.sql) on the intended database. It returns scoped invoice, credit and journal identifiers and exact quantities. Preserve the output privately. A flagged credit needs governed review before the affected workflow proceeds.
