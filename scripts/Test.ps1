$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if ($IsWindows -and !$env:DOCKER_HOST) {
    $endpoint = docker context inspect --format '{{.Endpoints.docker.Host}}'
    if ($LASTEXITCODE -eq 0 -and $endpoint) {
        $env:DOCKER_HOST = $endpoint -replace '^npipe:/{2,}\./pipe/', 'npipe://./pipe/'
    }
}
dotnet restore RAQM.slnx
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet build RAQM.slnx -c Release --no-restore
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet test RAQM.slnx -c Release --no-build --logger trx
exit $LASTEXITCODE

