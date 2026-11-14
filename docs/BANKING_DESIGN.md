# Banking account design

Phase 03 adds company bank profiles and operational treasury accounts in the existing API. A bank account references a bank profile and one dedicated, non-contra posting asset in the company's chart of accounts. Cash and petty cash use the same one-to-one GL mapping. A mapped GL cannot be registered again, assigned a conflicting trading role or changed to another classification. The account's currency comes from the company. Setup freezes the company currency.

Bank profile codes and treasury account codes are unique within the company. Domestic account identifiers are unique within a company and bank profile, while IBANs are unique within the company. The API checks IBAN format and checksum and accepts an optional SWIFT/BIC on the bank profile. It stores masked account references and normalized identifier digests for duplicate detection. Operators supply the original identifier at registration. The API returns only masks.

Account identity and GL mapping are fixed at registration. Owners can change display names, activity status and the petty cash custodian with a current version and a reason. Accounts with existing posted GL activity require explicit acknowledgement at registration. For opening balances use the Phase 01 opening balance process and the dedicated asset. Use the opening-equity offset for a conversion balance. Registration itself creates no journal. A transfer from an existing company treasury account can fund a newly opened account.

## Trading integration

The existing receipt, payment and refund requests accept an optional `bankAccountId`. When the selected `cashAccountId` belongs to a registered bank, the ID must identify that active bank account in the same company. The amount, control account, advance account and shared posting rules still come from Phase 02. Ordinary legacy cash requests retain their existing shape and fingerprint.

Existing payment rows keep a null bank account attribution. Ledger activity can show an older posted movement on a bank's dedicated GL without attributing the payment record to that bank. Reconciliation can match the original posted GL line. Registration and matching leave payment history intact. A payment reversal is a separate accounting movement and retains the original attribution. A reversal dated after a finalized bank cutoff can appear in a later reconciliation.

## Access and relationships

The authenticated principal supplies the actor. Financial access resolves current organization and company permissions from persistence. Company readers can query accounts and evidence. Owners configure bank profiles and treasury mappings. Company financial writers can import statements and confirm matches. The additive migration uses company-scoped foreign keys for bank profiles, dedicated GL accounts and optional payment attribution.

## HTTP endpoints

Paths below are relative to `/api/v1/organizations/{orgId}/companies/{companyId}`.

| Path | Methods |
| --- | --- |
| `/banking/banks` | GET list, POST create |
| `/banking/banks/{id}` | PUT profile name and status |
| `/banking/accounts` | GET paginated search by kind, status and text, POST register |
| `/banking/accounts/{id}` | PUT account name, status and custodian |
| `/banking/audit/{id}` | GET scoped account, movement or reconciliation audit events |
| `/trading/payments` and `/trading/refunds` | Existing POST contracts with optional bank attribution |

See [reconciliation](BANK_RECONCILIATION_DESIGN.md) and [cash and treasury](CASH_TREASURY_DESIGN.md) for workflows.
