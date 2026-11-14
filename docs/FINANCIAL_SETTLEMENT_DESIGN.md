# Financial settlements and posting composition

One settlement service handles receipts and supplier payments using the party side. A payment records cash, a control portion and an advance portion. An allocation records which posted debit document consumes that funding. A refund consumes unused payment or credit funding. Payment, refund and allocation evidence retains links, dates, actor, reason and request identity. Reports derive balances from posted documents and effective events.

## Payment and allocation policy

Normal payments apply to control up to the lesser of current and posting-date net party exposure, bounded below by zero. Excess goes to the advance account. The caller can explicitly classify the full payment as an advance. A payment may be unallocated, allocated to multiple documents, or combined with other payments against one document. Allocations consume control before advance. Credit notes can supply control funding as well.

| Event | Customer | Supplier |
| --- | --- | --- |
| Payment on account | Debit cash, credit AR | Debit AP, credit cash |
| Advance | Debit cash, credit customer advance liability | Debit supplier advance asset, credit cash |
| Allocate on-account payment or credit | Settlement event only | Settlement event only |
| Allocate advance | Debit advance liability, credit AR | Debit AP, credit advance asset |
| Refund control funding | Debit AR, credit cash | Debit cash, credit AP |
| Refund advance funding | Debit advance liability, credit cash | Debit cash, credit advance asset |
| Reverse allocation or payment | Append inverse event and exact opposite journal when applicable | Append inverse event and exact opposite journal when applicable |

Source and target must share the company, party, currency and control account. Allocations cannot exceed either remaining funding or target outstanding. Effective dates must be in an open period and cannot precede the source, target or later applicable settlement history. Backdating needs explicit Owner approval and a reason. Financial writes lock the same company before deriving available amounts, so a competing request must re-evaluate availability.

Payments begin as Prepared inside the transaction and become Posted with their journal. An Owner can reverse a payment or refund with a reason. Related refunds must be reversed first. The reversal appends deallocations for active payment allocations, reverses advance transfer journals, then reverses the payment journal and finalizes its Reversed state. A deallocation points to the original allocation and has the same amounts with the opposite effect. An allocation's original row stays Posted. Idempotency and unique reversal links prevent duplicate effects.

## Transaction boundaries

The scoped FinancialTransaction coordinator owns begin, company row lock, save and commit. An outer trading operation can stage the source and call IPostingEngine in the same scope. The engine joins for the same actor, organization and company, validates accounting and source rules, and flushes the journal. The caller can then finalize the source, apply optional allocations and check business rules before the single commit. A nested exception marks the outer transaction failed even if the caller catches it. The coordinator rejects an independent ambient transaction.

Manual journal posting uses the same coordinator as a standalone operation. Accounting retains source uniqueness, version checks, idempotency fingerprint, period checks and the company-level posting sequence. Trading sends journal draft DTOs through the engine. The engine owns journal persistence.

PostgreSQL constraints and triggers in TradingSubledgerV1 enforce retained posted evidence, mapped roles, currency precision, tax date exclusion and source identity. Deferred checks require finalized source state and its matching journal at commit, and compare grouped debit and credit amounts with source snapshots. The database checks net allocations and refunds at event dates so a later reversal cannot hide historic overconsumption. The custom DDL awaits PostgreSQL execution.

## Reporting and reconciliation

Reports read a repeatable snapshot at an inclusive accounting-date cutoff. Statements show opening and closing control balances, original and reversing events, outstanding documents, unapplied credits, unapplied control payments and separate advances. Aging uses due dates and the remaining obligation in Current, 1–30, 31–60, 61–90 and 90+ buckets.

| Comparison | Subledger basis |
| --- | --- |
| AR and AP controls | Open debit documents less unapplied credit notes and unapplied on-account payments |
| Customer and supplier advance controls | Remaining unapplied advances after effective allocations and refunds |
| Payment and credit conservation | Total funding less net allocations, active refunds and unapplied amount |
| Settlement cash journals | Signed payment/refund cash effects with effective reversals, compared with posted GL settlement cash activity |

Reconciliation exposes differences. The reports currently load company trading events into a read snapshot. Database commit checks scan company financial history and cutoff dates. Measure reporting and posting performance with representative company history before production rollout.

## HTTP workflow

Relative to `/api/v1/organizations/{orgId}/companies/{companyId}/trading`, use `GET /payments`, `GET /payments/{id}`, `POST /payments`, `POST /refunds`, `POST /payments/{id}/reverse`, `GET /allocations`, `POST /allocations` and `POST /allocations/{id}/reverse`. Posting a document can include funding instructions. Recording a payment can include its allocations. These composed operations commit together.
