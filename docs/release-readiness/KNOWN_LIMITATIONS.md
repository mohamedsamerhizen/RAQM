# Supported limits and boundaries

- FX close accepts at most 500 open positions at one cutoff. Larger populations need a separately designed multi-batch workflow; they cannot be silently skipped.
- Year-end readiness exposes the single-journal 1000-line envelope. Larger charts cannot complete through that path.
- Stock matching supports receipt shares across warehouses for one source line. Combined multi-clearing-line bills require distinct segments. Claimed asset-basis credits after capitalization require another coordinated correction workflow.
- Ambiguous legacy stocked sales and withholding payments require explicit authorized classification or supported reversal/reposting. Posted financial history is not silently rewritten.
- Historical GL report values can be pinned by sequence and definition revision; not every mutable source configuration is frozen. General historical restatement is not implemented.
- External bank statement origin and reciprocal cash settlement confirmation need independent evidence. Local reconciliations alone cannot establish them.
- Webhook workers provide database lease fencing and idempotent local state changes; exactly-once external receipt is not claimed. Live failover, alerting, restore, TLS/egress and load remain untested on an approved deployed target.
- Historical Tax and Integration migration `Down`/re-up paths can remove earlier foreign keys (R04-004). Retained-data downgrade is unsupported. Use forward migrations and separately validated restore/repair under change control.
- A general legal-hold release workflow and high-volume trading commit performance target are not established by the present evidence.

Production acceptance also requires provisioning the shared encrypted webhook key ring and configuring the exact trusted ingress peer. Interactive report bounds and PDF text support are described in the financial reporting documentation.

- Stock conversions for invoices, unissued cancellation credits and physical returns require exact base quantities at six decimal places. Existing incompatible posted cancellations are preserved. Use the [read-only precision report](../../scripts/StockCancellationPrecisionAudit.sql) and arrange an approved correction before closing an affected period.
