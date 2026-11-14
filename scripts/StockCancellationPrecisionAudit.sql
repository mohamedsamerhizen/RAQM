-- PostgreSQL 17, migration 21 or later. Run on the intended database with a read-only role.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;

WITH cancellations AS (
    SELECT credit."CompanyId" AS company_id, invoice."Id" AS invoice_id,
           sale."Id" AS invoice_line_id, sale."StockItemId" AS item_id,
           credit."Id" AS credit_id, line."Id" AS credit_line_id,
           credit."JournalId" AS credit_journal_id, credit."PostingDate" AS posting_date,
           line."Quantity" AS cancelled_sales_units, sale."StockSalesFactor" AS sales_factor,
           line."Quantity" * sale."StockSalesFactor" AS cancelled_base_units,
           sale."Quantity" * sale."StockSalesFactor" AS invoiced_base_units
    FROM trading."DocumentLines" line
    JOIN trading."Documents" credit ON credit."Id" = line."DocumentId" AND credit."CompanyId" = line."CompanyId"
    JOIN trading."DocumentLines" sale ON sale."Id" = line."OriginalLineId" AND sale."CompanyId" = line."CompanyId"
    JOIN trading."Documents" invoice ON invoice."Id" = sale."DocumentId" AND invoice."CompanyId" = sale."CompanyId"
    WHERE line."CancelUnissuedStock" AND credit."Kind" = 2 AND credit."State" = 2
), affected AS (
    SELECT c.*, sum(cancelled_base_units) OVER (PARTITION BY company_id, invoice_line_id) AS total_cancelled_base_units
    FROM cancellations c
)
SELECT a.*, issued.issued_base_units,
       a.invoiced_base_units - a.total_cancelled_base_units - issued.issued_base_units AS remaining_base_units
FROM affected a
CROSS JOIN LATERAL (
    SELECT coalesce(sum((o."PayloadJson"->>'Quantity')::numeric), 0) AS issued_base_units
    FROM inventory."Operations" o
    WHERE o."CompanyId" = a.company_id AND o."TradeLineId" = a.invoice_line_id AND o."Kind" = 2 AND o."State" = 2
) issued
WHERE a.cancelled_base_units IS NULL OR a.cancelled_base_units NOT BETWEEN 0.000001 AND 999999999999
   OR a.cancelled_base_units <> trunc(a.cancelled_base_units, 6)
ORDER BY a.company_id, a.invoice_id, a.posting_date, a.credit_line_id;

COMMIT;
