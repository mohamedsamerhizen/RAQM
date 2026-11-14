# Local development

The [README quick start](../README.md#quick-start) uses Compose with a private PostgreSQL network and a project-scoped named volume. Readiness checks the database and migration state. Liveness checks the API process. Local Development exposes the OpenAPI JSON at `/openapi/v1.json`.

For an example of the API carrying a financial operation through PostgreSQL, see the [captured posting and close](ENGINEERING_TOUR.md#captured-posting-and-close).

## Configuration

The [environment template](../.env.example) leaves `POSTGRES_PASSWORD`, `JWT_SIGNING_KEY`, and `ONBOARDING_KEY` blank. Run [Initialize-LocalEnvironment.ps1](../scripts/Initialize-LocalEnvironment.ps1) once to generate random local values. Existing data lives in the named volume, and the initializer refuses to overwrite existing settings. `docker compose stop` stops the services while retaining data.

## First authenticated request

Use the operator onboarding value from your own local environment. This PowerShell example reads the value privately, creates a synthetic account with a random password, and shows the organization context. It calls the real local routes.

```powershell
$localOnboarding = (Get-Content .env | Where-Object { $_ -like 'ONBOARDING_KEY=*' }) -replace '^ONBOARDING_KEY=', ''
$demoId = [guid]::NewGuid().ToString('N')
$account = @{
    email = "review-$demoId@example.test"
    password = "Ra1!$([guid]::NewGuid().ToString('N'))"
    organizationName = 'RAQM Local Review'
    onboardingKey = $localOnboarding
} | ConvertTo-Json
$session = Invoke-RestMethod http://localhost:5097/api/v1/auth/onboard -Method Post -ContentType 'application/json' -Body $account
Invoke-RestMethod http://localhost:5097/api/v1/me -Headers @{ Authorization = "Bearer $($session.accessToken)" }
```

Use an organization ID from the response for company endpoints under `/api/v1/organizations/{orgId}/companies`. The OpenAPI document describes the request bodies. Refresh tokens rotate on use and reusing a consumed token revokes its session. [Identity and company access](ARCHITECTURE.md#tenant-resolution-and-authorization)

## Test and schema commands

```powershell
dotnet build RAQM.slnx -c Release
dotnet test RAQM.slnx -c Release --no-build --filter "Category!=Integration"

# PostgreSQL integration tests only, with Docker running
dotnet test RAQM.slnx -c Release --no-build --filter "Category=Integration"

dotnet tool restore
dotnet ef migrations has-pending-model-changes --project RAQM.Api --configuration Release --no-build
```

The integration fixtures create disposable PostgreSQL 17 containers. On Windows, [Test.ps1](../scripts/Test.ps1) normalizes the Docker context endpoint and runs the complete suite. If you run the filter directly with Docker Desktop Linux, set `$env:DOCKER_HOST = 'npipe://./pipe/dockerDesktopLinuxEngine'` when needed.

## Run the API on the host

Provide a reachable development PostgreSQL database, independent from the Compose internal endpoint. Set `ConnectionStrings__Database`, `Auth__SigningKey` (at least 32 UTF-8 bytes), and `Auth__OnboardingKey` (at least 32 characters) in the current environment using local secrets. Apply migrations using a database identity with `btree_gist` installation permission.

```powershell
dotnet restore RAQM.slnx
dotnet tool restore
dotnet ef database update --project RAQM.Api
dotnet run --project RAQM.Api --launch-profile http
```

Use forward migrations for retained data and validate restore separately. See the [release verification](RELEASE_VERIFICATION.md) and [deployment boundaries](release-readiness/KNOWN_LIMITATIONS.md) before using a retained environment.
