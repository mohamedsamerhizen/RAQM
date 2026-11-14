# Architecture

## Modular monolith

One deployable ASP.NET Core API and one PostgreSQL database. Thin ASP.NET Core controllers bind requests and select responses; explicit application services implement workflows behind domain-specific interfaces. Identity owns credentials and sessions. Organizations owns tenancy, membership, companies and settings. Accounting owns chart setup, fiscal periods, draft/posted journals, posting controls, opening conversion, reversals and trusted ledger queries. Data owns DbContext, design-time factory, migrations and domain-grouped EF configurations. Entities and DTOs are separate. Configurations owns validated options and the OpenAPI compatibility adapter; Middleware owns correlation and error handling; Filters preserves request binding behavior; Helpers contains identity/tenant context utilities. Database health lives under Services/Health.

Identity, Organizations and Accounting boundaries remain explicit in domain subfolders under Services, Interfaces, Entities, DTOs and Data/Configurations. A single RaqmDbContext coordinates transactions across these domains. EF Core is used directly, without repository wrappers, MediatR, CQRS scaffolding or distributed transactions. IPostingEngine resolves to PostingEngine; accounting services use explicit interfaces and the existing DbContext transaction boundary.

## Tenant resolution and authorization

Tenant is the SaaS isolation key; Organization is the customer's ownership and membership boundary; Company is a legal accounting entity. Phase 0 enforces exactly one organization per tenant. One user can belong to several independent organizations, and each organization can own many companies.

The organization route parameter selects a candidate context. OrganizationService.Resolve queries authenticated user membership and obtains the TenantId from that persisted row. A client-supplied tenant ID never establishes access. There is no default tenant, fallback to the first organization or ambient company selection. Missing membership returns 404. Company queries include resolved tenant and organization and require ownership or an explicit company grant. An ID from another organization never widens a query.

Authorization is application scoped, with composite database foreign keys as an additional integrity boundary. There is no PostgreSQL row-level security or global EF query filter. Future services MUST use resolved context and scoped queries; direct unscoped business queries require review. The initial negative tests exercise both organization and company ID substitution.

| Role | Permissions |
| --- | --- |
| Organization Owner | Read organization, manage members, create/read/update companies, grant/revoke company access |
| Organization Member | Read organization; no company access until granted |
| Company Manager | Read company, update settings |
| Company Reader | Read company |

Platform Identity roles are persisted through ASP.NET Core Identity but are not public assignment endpoints. Tenant permissions live in memberships, not JWT role claims. Owner promotion, ownership transfer, organization-member deletion, tenant closure and company deletion are deliberately absent: no endpoint can remove the sole owner. Adding a member never grants ownership. Company grant revocation is immediate on the next authorized query.

## Identity and security

Operator-secret controlled onboarding is the initial enrollment gate. User, tenant and organization creation occur in one database transaction. Existing authenticated users may create additional organizations; each gets a separate tenant. Shared onboarding is intentionally an initial control, to be replaced with expiring single-use invitations and operational onboarding approval before broad release.

Identity hashes passwords; unique email and normalized username prevent duplicates. Login lockout is five failed attempts / 15 minutes; authentication routes share a per-IP fixed window limiter (10/minute default, configurable 1..1000). IP headers are not trusted. A reverse proxy needs an explicitly trusted forwarding configuration before using forwarded IPs. No CORS wildcard or browser cookie auth is configured.

JWTs use issuer/audience/signature/algorithm/lifetime validation, a ten-minute default and fifteen-second skew. Every authenticated request checks the persisted session, so revocation does not wait for JWT expiry. Refresh tokens are 512-bit random values; only SHA-256 hashes are stored. Rotation uses a PostgreSQL row lock on the session and a transaction. Concurrent refreshes serialize; reuse revokes the family, so clients must serialize refresh requests. Old hashes remain to detect replay. Sessions have an absolute fourteen-day default expiry; refresh does not extend it.

Logout and revoke-all update the same session rows that rotation locks. No secrets, bodies, passwords or tokens are written by request logging. Token responses and business responses set Cache-Control: no-store. Clients are responsible for secure token storage. A database unavailable during authentication fails closed.

## Platform

/api/v1 is the explicit URL major-version strategy. Breaking changes receive a new prefix; no silent unversioned alias. Development OpenAPI is /openapi/v1.json; it is JSON, not a Swagger UI. HTTPS is supplied by deployment ingress; production HSTS is enabled. AllowedHosts must be configured for the deployment.

Validated options fail startup on missing/invalid database configuration, insufficient authentication keys or invalid token/rate limits. The database itself can be unavailable without failing liveness. /health/live checks process responsiveness. /health/ready verifies PostgreSQL connectivity and no pending migrations. A migration that has not been applied keeps readiness unhealthy.

JSON console logging has request correlation scopes. A supplied X-Correlation-ID is accepted only for 1..64 ASCII alphanumeric, dash or underscore characters; otherwise a random ID is generated. It is echoed on the response and included in ProblemDetails. Errors map to 400/401/403/404/409/429 as appropriate, with generic 500 responses. Authentication middleware failures receive ProblemDetails through status code pages.

Migration application is explicit: dotnet ef database update, or the --migrate one-shot process used by Compose. Normal API startup does not migrate or reset the database. Production deployments should review SQL and run a single privileged migration job before starting restricted API instances.

## Controller compatibility

Controllers depend on IIdentityService / IOrganizationService, never directly on DbContext. Existing HTTP paths, JSON field names, statuses and permission checks are preserved. The current-user database query moved from the former endpoint into IdentityService. Organization/company responses use explicit DTOs with the exact previous entity JSON shape.

MVC implicit required validation is suppressed so it does not introduce new validation rules ahead of the existing services. RequestValidationFilter preserves the prior 400 ProblemDetails behavior for malformed/missing JSON. Authentication/authorization and per-IP rate limits remain endpoint middleware concerns. Health and OpenAPI remain platform endpoints.

The OpenAPI transformer retains the previous application/json documentation while runtime binding continues to accept JSON suffix media types. Recorded HTTP and OpenAPI snapshots, response serialization checks and endpoint authorization metadata tests protect the controller contract. See [release verification](RELEASE_VERIFICATION.md) for the local PostgreSQL results.

Financial writes serialize on the company row. Company setting and permission changes use the same lock. Trading uses the scoped financial transaction to compose source state, settlement events and journals. Accounting and reconciliation reports read repeatable snapshots. See [financial settlement design](FINANCIAL_SETTLEMENT_DESIGN.md) for transaction ownership and [release verification](RELEASE_VERIFICATION.md) for the PostgreSQL execution boundary.
