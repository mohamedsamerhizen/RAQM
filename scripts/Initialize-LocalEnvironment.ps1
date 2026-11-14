$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (Test-Path -LiteralPath '.env') { throw '.env already exists; preserve existing secrets.' }
function New-Secret { [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
@(
    "POSTGRES_PASSWORD=$(New-Secret)"
    "JWT_SIGNING_KEY=$(New-Secret)"
    "ONBOARDING_KEY=$(New-Secret)"
) | Set-Content -LiteralPath '.env'
Write-Host 'Created local-only .env. Do not commit or share it.'
