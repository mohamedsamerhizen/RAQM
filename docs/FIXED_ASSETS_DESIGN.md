# Fixed assets design

Phase 05 uses a company base-currency cost model. Acquisition records the economic purchase date. An approved operation records recognition at its accounting date. A separate approved availability operation records when depreciation can begin. A disposal records the accounting date and can retain a separate physical date within that fiscal period. IAS 16 guides initial cost, component accounting, availability and derecognition. IAS 36 guides recoverability evidence and reversal ceilings. Formal compliance requires independent review. [IAS 16](https://www.ifrs.org/issued-standards/list-of-standards/ias-16-property-plant-and-equipment/) · [IAS 36](https://www.ifrs.org/issued-standards/list-of-standards/ias-36-impairment-of-assets/)

## Records and lifecycle

| Record | Role |
| --- | --- |
| Asset category and control mappings | Dedicated cost, accumulated depreciation, accumulated impairment and optional acquisition clearing accounts. The category also identifies depreciation expense, impairment loss and disposal gain/loss accounts, the capitalization threshold and the mandatory close policy. |
| Asset and components | Company-unique code, acquisition and supplier evidence, initial physical assignment, component amounts, residual values and useful lives. Components are the units of recognition and depreciation. |
| Operation | Dated request, approval, version, posted sequence, immutable plan, posted journal reference and actor evidence. |
| Movement | Signed changes in cost, accumulated depreciation and impairment, consumed life and prospective overrides. These are retained register events. |
| Source claim | A unique company reference to a whole posted journal line consumed by an asset operation. |

Register a Draft asset and prepare acquisition or opening conversion. Owner approval advances the asset to Approved. Posting advances it to Capitalized. Posting availability advances it to In Service. An opening conversion can supply evidenced historical availability and enter In Service at the cutover. Sale and write-off end the lifecycle. The register derives carrying amounts from posted movements and compares them with posted GL lines.

Owners publish immutable category mappings. Control accounts are distinct active posting assets, with contra normal sides for depreciation and impairment. Expense and gain accounts use their corresponding classes. The fixed asset controls cannot be reused as treasury accounts or trading controls. Their eligible postings include asset journals, opening conversion journals and properly sourced AP bill/credit activity on cost or clearing. Posted claims restrict later credit and reversal of source bills. Earlier GL history remains visible in reconciliation.

Administrative transfers require approval and evidence of the new location, branch, department, custodian, cost center and physical status. Branch, department and cost center are descriptive references in this phase. The custodian must be a company member. The effective date controls historical assignment. Idle and maintenance status retain ordinary straight-line depreciation. Administrative transfers remain within the company and have no GL effect.

## Transactions, reports and close

Financial writers prepare operations. Owners approve, cancel and post using a current version. Posting recomputes the plan under the company lock. The approved plan, shared-engine journal, asset state, source claims and register movements commit together. The same request key returns the original operation. A changed payload conflicts. The same posted operation returns its original result. Register and history reads require company access.

Reports provide paginated register search, an as-of position, depreciation forecasts, recorded movement detail, period acquisitions, improvements, repairs, disposals and impairment, category and assignment summaries, useful-life flags and dedicated control-account reconciliation. Operation history retains approved source and journal IDs for drill-down. Phase 04 financial statements read the resulting posted GL lines.

Close readiness includes asset reconciliation, due depreciation and pending acquisition, disposal, improvement and impairment evidence. A category's mandatory policy controls whether its findings block or require review. Its evidence contributes to the approval fingerprint. The existing period transition service checks mandatory findings when closing or locking an open period, including through the legacy route. Closed and finalized fiscal dates retain their existing restrictions.

All routes are relative to `/api/v1/organizations/{orgId}/companies/{companyId}/fixed-assets`.

| Methods | Path |
| --- | --- |
| GET, POST | `/categories` and `/register` |
| GET | `/register/{assetId}/movements` and `/register/{assetId}/schedule?at=...&through=...` |
| GET, POST | `/operations` lists paginated operations or prepares an approved-workflow request |
| GET | `/operations/{id}/preview` |
| POST | `/operations/{id}/{approve,post,cancel}` |
| GET | `/report` accepts `period` or `from` and `to` |
| GET | `/audit/{entityId}` |
