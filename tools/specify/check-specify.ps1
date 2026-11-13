<#
.SYNOPSIS
Check Spec Kit versions without changing anything. Defaults to -Repo.
.PARAMETER Global
Check the installed CLI against the latest GitHub release.
.PARAMETER Repo
Check the current repository root against the installed CLI.
.EXAMPLE
check-specify.ps1 -Global -Repo
Check both the CLI and repository.
#>

[CmdletBinding()]
param(
    [switch]$Global,
    [switch]$Repo
)

$ErrorActionPreference = 'Stop'

if (-not $Global -and -not $Repo)
{
    $Repo = $true
}

if ($Repo)
{
    if (-not (Test-Path -LiteralPath '.specify\integration.json'))
    {
        throw 'Run from a repository root initialized with Spec Kit, or use -Global only.'
    }

    $metadata = Get-Content -LiteralPath '.specify\integration.json' -Raw | ConvertFrom-Json
}

if ($Global)
{
    & specify self check
    if ($LASTEXITCODE -ne 0) { throw 'Specify CLI update check failed.' }
}

if ($Repo)
{
    $cli = & specify version --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the installed CLI version.' }
    if (-not $metadata.version -or -not $cli.cli_version) { throw 'Missing Spec Kit version information.' }

    Write-Host "Repository Spec Kit: $($metadata.version)"
    Write-Host "Installed CLI:       $($cli.cli_version)"

    if ($metadata.version -ne $cli.cli_version)
    {
        Write-Warning 'Repository and CLI versions differ. Review before running update-specify.ps1 -Repo.'
    }

    & specify integration status
    if ($LASTEXITCODE -ne 0) { throw 'Repository integration check failed.' }
}
