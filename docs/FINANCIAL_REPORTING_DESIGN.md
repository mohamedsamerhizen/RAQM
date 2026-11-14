# Financial reporting design

The reporting service reads company-scoped posted journal headers and lines. Each report and each drill-down use a PostgreSQL repeatable-read snapshot. The report metadata identifies the company, base currency, report revision, requesting actor, generation time, inclusive reporting dates and highest posted journal sequence observed through the requested cutoff. Report calculations use decimal and the existing ledger precision.

## Definitions and presentation

An organization Owner publishes an immutable definition revision. Account mappings identify posting accounts by ID and must agree with the account class. Asset and liability mappings can specify current and noncurrent groups. Equity mappings can identify capital, retained earnings, distributions and other equity. Income mappings can identify operating revenue, cost of sales, operating expenses, other income and other expenses. Cash rules map exact journal source codes to operating, investing or financing. Mapping and classification use account and source identities.

An unmapped posting account contributes to its class total and appears in an unclassified group. Revision zero uses these fallback groups. The API reports incompatible mappings and exposes integrity differences. A parent account row is a hierarchy subtotal of its posting descendants. Statement totals use each ledger account once. Assets and expenses display debit-positive. Liabilities, equity and revenue display credit-positive. Contra balances therefore reduce their class totals when posted to their normal contra side.

## Statement calculations

| Report | Calculation and checks |
| --- | --- |
| Balance Sheet | Assets, liabilities and permanent equity at the inclusive cutoff. Nominal balances remaining in the current fiscal year and earlier years are displayed separately in equity. The difference is assets less liabilities and total equity. |
| Income Statement | Selected-period and fiscal YTD revenue less expenses. Excludes typed year-end entries and opening conversion entries, including reversals of opening conversion. Gross profit requires operating revenue and cost-of-sales mappings. Operating subtotals require the relevant classifications. |
| Equity Movements | Opening equity includes unclosed nominal balances. Period performance, configured equity account movements and any opening nominal conversion explain closing equity. Typed year-end transfers appear as a memo because they move value within total equity. |
| Cash Movement | Sums GL balances of registered treasury accounts, trading cash and applicable FX bank positions. Groups period cash lines by journal to obtain signed net cash per movement. Separates opening conversion, excludes typed internal transfers and reports unexpected nonzero transfer effects. Classifies external movement by exact source, with an unclassified group for missing rules. |

Select a cutoff or inclusive date range, fiscal year, fiscal quarter within a selected fiscal year, or accounting period. Each comparative cutoff uses its own fiscal calendar for YTD. Percentage change divides the difference by the absolute prior value and is absent for a zero prior value. Ledger drill-down uses the same period and typed income exclusions and can expand an account's descendants. Separate report and drill-down requests each read their own snapshot. The sequence metadata identifies the report snapshot.

## Delivery and routes

The API, PDF, XLSX and CSV use the same report object. The export writer formats existing values and records an export audit event. CSV is UTF-8 and protects formula-like text. XLSX writes numeric cells for amounts within Excel's precision and preserves longer exact decimals as text. PDF uses a standard font with an ASCII text fallback. XLSX and CSV carry international labels. Each export includes the report definition revision and integrity figures.

Routes are relative to `/api/v1/organizations/{orgId}/companies/{companyId}/financial-reporting`.

| Method | Path |
| --- | --- |
| GET, POST | `/definitions` lists and publishes versions |
| POST | `/query` runs a report |
| POST | `/accounts/{accountId}/drill` returns posted evidence, with optional `ytd`, `page` and `pageSize` |
| POST | `/export/{pdf,xlsx,csv}` delivers and audits the report |

The Cash Movement report covers treasury accounts, trading cash mappings and applicable FX bank positions expressed in company currency. A full cash flow statement would require noncash classifications, cash equivalent policy, finer treatment of mixed sources, indirect working capital rules and independent accounting review. This phase uses posted data and explicit mappings rather than estimating those classifications.

## Request and export limits

Interactive statement query and export support 50,000 posted lines through the selected cutoff and four concurrent requests per API instance. Report calculation has a 15-second cancellation deadline. Oversized reports return 422. Use an earlier cutoff or ledger drilldown for larger histories. Capacity under production load remains unmeasured.

PDF accepts printable ASCII. A report with other text returns 422 and can be exported in CSV or XLSX. Authorized report queries and previously issued eligible files remain available during tenant restriction. New human-readable report files require an active commercial operating state and retain a normal export audit event.
