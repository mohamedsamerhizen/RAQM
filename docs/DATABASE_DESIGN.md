# Database design

PostgreSQL 17 is the development/test baseline. EF Core 10 and Npgsql 10 use a single RaqmDbContext. Domain-specific IEntityTypeConfiguration implementations live under Data/Configurations/Identity and Data/Configurations/Organizations; DbContext, design-time factory and migrations live under Data, including Data/Migrations.

## Implemented entities

| Schema | Table | Purpose / integrity |
| --- | --- | --- |
| identity | AspNetUsers | Identity user with UUID key; required application-validated email, normalized username uniqueness |
| identity | AspNetRoles, AspNetUserRoles, AspNetUserClaims, AspNetRoleClaims, AspNetUserLogins, AspNetUserTokens | Standard ASP.NET Core Identity persistence; no role elevation API |
| identity | Sessions | User FK, absolute UTC expiry and optional revocation timestamp; user index |
| identity | RefreshTokens | Session FK, globally unique 64-character SHA-256 hex hash, expiry and consumed timestamp |
| organizations | Tenants | SaaS isolation key, UUID and creation timestamp |
| organizations | Organizations | Tenant FK, unique TenantId (one organization per tenant), name and creator metadata |
| organizations | Members | PK organization/user; composite alternate key tenant/organization/user; role |
| organizations | Companies | Organization and tenant composite FK; unique organization/normalized registration number; currency/fiscal settings; creator/editor timestamps and actors; optimistic Version UUID |
| organizations | CompanyMembers | PK company/user; composite FKs to both company and organization membership; Manager/Reader role |

Every domain FK uses Restrict to avoid cascade deletion of organizational/security data. CompanyMembers cannot reference a user outside the organization's membership or a company from another tenant. Organization membership cannot claim a tenant different from its organization's tenant. Direct database checks constrain uppercase three-letter currency format and valid recurring fiscal start dates, including rejecting February 29. Application validation further restricts currency to the runtime's supported ISO currency set.

Identity's username index provides atomic normalized username uniqueness; all onboarding usernames equal the email address. RequireUniqueEmail is additionally enabled. There is no public username-change workflow that could break this relationship. Passwords use Identity hashing; refresh token plaintext is never persisted.

Tenant and organization IDs are deliberately separate UUIDs. Globally unique company IDs simplify lookup, but do not replace tenant/organization predicates. Owner is a membership role; company grants are optional for owners, mandatory for ordinary members. No deletion or ownership-transfer workflow exists in Phase 0.

## Consistency

Owner onboarding explicitly wraps Identity, tenant, organization, membership and session writes in one transaction. Organization/company creation uses a single atomic SaveChanges. Refresh rotation serializes on a session row with SELECT FOR UPDATE; a replay commits revocation before returning 401. Logout and revoke-all use database UPDATE statements on the same session rows. Company Version is an EF concurrency token; PUT requires the retrieved version and returns 409 for stale/concurrent updates.

All timestamps use DateTimeOffset.UtcNow and PostgreSQL timestamp with time zone; offsets are normalized to UTC. UUIDs are generated in application code. Name lengths are 160, registration number 80, currency 3. There are no monetary persisted columns yet. DbContext decimal conventions explicitly default future decimal fields to numeric(20,4); rates must override to numeric(20,10), with domain rounding validation before persistence.

## Migration lifecycle

InitialFoundation creates only Identity and implemented organizational/session entities. The generated designer and snapshot are committed with the migration. tools/InitialFoundation.sql is the generated idempotent review script.

    dotnet tool restore
    dotnet ef migrations has-pending-model-changes --project RAQM.Api --configuration Release
    dotnet ef migrations script --idempotent --project RAQM.Api --output tools/InitialFoundation.sql
    dotnet ef database update --project RAQM.Api

Set ConnectionStrings__Database for update. The design-time factory has a secret-free localhost connection only to generate/review migrations; it does not substitute real credentials. Compose uses the --migrate process once before API startup. Normal API startup performs no automatic schema updates. Readiness checks database connectivity and unapplied migrations.

No EnsureCreated, EnsureDeleted, EF InMemory, destructive resets or unused accounting tables are used. Testcontainers provisions an isolated PostgreSQL instance, applies migrations, tests actual constraints and disposes only its own resources.

## Before production

Use restricted application credentials, a separate schema-migration identity, encrypted transport, tested backup/restore, monitored storage, retention and secret rotation procedures. The current application enforces isolation in services; PostgreSQL row-level security is a possible later defense, not a current claim. Session/hash cleanup must preserve consumed tokens until their session can no longer authenticate, otherwise replay detection would be weakened.

An earlier structural refactor relocated CLR namespaces and migration snapshot and designer type references while preserving migration 20261106064719_InitialFoundation and its SQL. See [release verification](RELEASE_VERIFICATION.md) for the current forward chain and EF model check.

## Phase 1 additive accounting model

AccountingCoreV1 adds Accounts, FiscalYears, Periods, Journals, JournalLines, OpeningBatches and AuditEvents under accounting. InitialFoundation files/ID remain unchanged. All financial references are company-scoped, with tenant/organization/company FKs to the existing company. Monetary columns are numeric(20,4); posted uniqueness, GiST calendar exclusions (btree_gist) and immutable financial guards are part of the additive migration. No balances cache, destructive reset or Phase 2 tables were added.

Use a migration identity permitted to install btree_gist. Keep schema and trigger ownership separate from the runtime application role. For retained data, upgrade forward and recover with a validated restore or forward repair. See [release verification](RELEASE_VERIFICATION.md) for PostgreSQL execution evidence.

## Phase 2 additive trading model

TradingSubledgerV1 adds Parties, AccountMappings, TaxCodes, TaxRates, Documents, DocumentLines, Payments and Allocations in the trading schema. Composite relationships constrain financial references to the company. Unique indexes cover document numbering, supplier references, opening obligations, idempotency and reversal links. GiST prevents tax date overlap. Retention and deferred ledger contract triggers are described in [financial settlement design](FINANCIAL_SETTLEMENT_DESIGN.md). PostgreSQL execution status is in [release verification](RELEASE_VERIFICATION.md).
