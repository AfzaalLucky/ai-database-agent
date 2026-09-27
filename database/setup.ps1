<#
.SYNOPSIS
  Creates (or recreates) the FoundryTrade sample database: schema, deterministic seed data and the
  least-privilege agent_reader identity.

.DESCRIPTION
  DESTRUCTIVE for the FoundryTrade database only — it is dropped and rebuilt. Requires sqlcmd
  (go-sqlcmd or ODBC sqlcmd) and permission to create databases on the target server.

.EXAMPLE
  ./database/setup.ps1
  ./database/setup.ps1 -Server "localhost,1433" -SqlAuth -User sa
#>
param(
    [string] $Server = "localhost",
    [switch] $SqlAuth,
    [string] $User = "sa"
)
$ErrorActionPreference = "Stop"
$scripts = "01-create-database.sql", "02-schema.sql", "03-seed.sql", "04-security.sql"
[string[]] $auth = if ($SqlAuth) { @("-U", $User) } else { @("-E") }

foreach ($script in $scripts) {
    $path = Join-Path $PSScriptRoot $script
    Write-Host "==> $script"
    & sqlcmd -S $Server @auth -C -b -i $path
    if ($LASTEXITCODE -ne 0) { throw "$script failed (exit $LASTEXITCODE)" }
}
Write-Host "FoundryTrade is ready."
