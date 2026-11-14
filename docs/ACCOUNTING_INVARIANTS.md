# Accounting invariants — mandatory Phase 1 acceptance criteria

These invariants apply to the current implementation. See [release verification](RELEASE_VERIFICATION.md) for disposable PostgreSQL evidence and deployment boundaries.

1. Every posted journal balances exactly in company base currency. Total debit equals total credit using decimal arithmetic at the defined currency scale. Require at least two valid lines, non-negative amounts, exactly one debit or credit per line, active postable accounts and appropriate currency.
2. Posted headers and lines are immutable. Corrections require an explicit reversing journal and, when needed, a replacement. No silent edits, destructive cascades or deletes of posted data.
3. A reversal references its original journal, actor, reason, source and accounting date. Reversal rules must prevent accidental repeated full reversals. Both original and correction remain reportable.
4. Posting validation, journal numbering, header/line persistence, source-document linkage and idempotency registration commit together in one PostgreSQL transaction. A failure leaves no partial accounting effect.
5. The unique company-scoped source identity and idempotency key prevent duplicate effects under retries and concurrent workers. Reusing a key with a different payload is a conflict; an identical retry returns the original receipt.
6. Monetary values use C# decimal and explicit PostgreSQL numeric precision, never float/double/real. Proposed defaults: numeric(20,4) for base monetary amounts and numeric(20,10) for rates; transaction-currency scale and rounding are explicit. IQD needs three decimal minor units; do not assume every currency has two. No implicit rounding at the database boundary.
7. Fiscal-period validity and locks are checked and serialized with posting. Closing a period racing a posting must not allow a journal into a closed period. Backdating, reopening and year-end close require explicit authority and audit.
8. The server resolves tenant, organization and company from authenticated membership. Every journal, line, account, period and document reference must belong to the same authorized company. Composite foreign keys and company-scoped unique constraints back up application checks.
9. Financial documents have stable source IDs and traceable links to their accounting effects. A posted document cannot silently return to a mutable draft. Side effects outside PostgreSQL require a transactional outbox.
10. Trial balance, statements and subledger reconciliations derive from trusted posted data, with explicit company, date, fiscal basis and currency. Draft or failed postings never affect financial statements.
11. Currency, fiscal-year definitions and account control rules become restricted after accounting activity exists. Phase 1 freezes fiscal defaults after calendar creation and currency/fiscal defaults after posting, in both service and database guards.
12. Company sequence numbers are unique and allocated transactionally. Requirements for gapless legal numbering must be established per jurisdiction; do not promise gapless identity sequences.

## Implemented Phase 1 boundary

DTOs/Accounting/PostingContracts.cs and Interfaces/Accounting/IPostingEngine.cs are implemented by Services/Accounting/PostingEngine.cs. Actor and scope are independently authorized from membership; source identities and canonical payload hashes prevent duplicate effects. All financial amounts use decimal/numeric(20,4); the versioned currency subset and explicit scale rejection are documented in ACCOUNTING_CORE_DESIGN.md.

Manual drafts, opening conversion batches and exact linked reversals use this engine. General ledger/trial balance derive only from posted lines. The additive migration contains posting/immutability/source/period/account guards, GiST exclusions and append-only audit protections. Their execution is not claimed from compilation or model consistency checks: run the real PostgreSQL integration suite before acceptance.

AR/AP source composition, transactional outbox and supported year-end use the common transaction and posting boundaries. Currency and operating limits are documented in the release notes.
