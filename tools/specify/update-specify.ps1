<#
.SYNOPSIS
Update Spec Kit. Defaults to -Repo; with both switches, update the CLI first.
.DESCRIPTION
Global updates require an existing uv-managed install. Stop other Specify processes first.
Repo updates refresh integrations and shared assets, not authored specs or optional extensions.
.PARAMETER Global
Install the latest stable GitHub release using uv.
.PARAMETER Repo
Update the current repository root using the installed CLI.
.PARAMETER Force
Overwrite modified repository-managed files. Review them first.
.EXAMPLE
update-specify.ps1 -Repo -Force
Update repository assets after reviewing local customizations.
.EXAMPLE
update-specify.ps1 -Global -Repo -WhatIf
Preview both updates without changing anything.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Global,
    [switch]$Repo,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if (-not $Global -and -not $Repo)
{
    $Repo = $true
}

if ($Force -and -not $Repo)
{
    throw '-Force applies only to repository updates. Include -Repo or omit -Force.'
}

if ($Repo)
{
    if (-not (Test-Path -LiteralPath '.specify\integration.json'))
    {
        throw 'Run from a repository root initialized with Spec Kit, or use -Global only.'
    }

    $metadata = Get-Content -LiteralPath '.specify\integration.json' -Raw | ConvertFrom-Json
    if (-not $metadata.installed_integrations) { throw 'No installed integrations are recorded in this repository.' }
}

if ($Global)
{
    $null = Get-Command uv -CommandType Application -ErrorAction Stop
    $cli = & specify version --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the installed CLI version.' }
    if (-not $cli.cli_version) { throw 'Specify did not report its CLI version.' }

    $installedVersion = [version]$cli.cli_version
    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/github/spec-kit/releases/latest' -TimeoutSec 30
    $tag = [string]$release.tag_name

    if ($tag -notmatch '\Av\d+\.\d+\.\d+\z')
    {
        throw "Unexpected stable release tag '$tag'. Inspect the release before installing it."
    }

    $targetVersion = [version]$tag.Substring(1)

    if ($installedVersion -ge $targetVersion)
    {
        Write-Host "CLI $installedVersion is already at or above the latest stable release ($tag)."
    }
    elseif ($PSCmdlet.ShouldProcess("Specify CLI $installedVersion", "Install $tag from github/spec-kit using uv"))
    {
        & uv tool install specify-cli --force --from "git+https://github.com/github/spec-kit.git@$tag"

        if ($LASTEXITCODE -ne 0)
        {
            throw "uv failed with exit code $LASTEXITCODE. Check its output and ensure no other Specify processes are running."
        }

        $updated = & specify version --json | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Could not verify the upgraded CLI.' }
        if ([version]$updated.cli_version -ne $targetVersion) { throw "Specify on PATH is not version $targetVersion. Check for another installation." }
        Write-Host "CLI upgraded to $($updated.cli_version)."
    }
}

if ($Repo)
{
    $arguments = @('integration', 'upgrade')
    if ($Force) { $arguments += '--force' }

    foreach ($integration in $metadata.installed_integrations)
    {
        if ($PSCmdlet.ShouldProcess("$PWD ($integration)", 'Upgrade Spec Kit repository assets'))
        {
            & specify @arguments -- $integration
            if ($LASTEXITCODE -ne 0) { throw "Repository upgrade failed for '$integration'. Review the output before retrying." }
        }
    }

    & specify integration status
    if ($LASTEXITCODE -ne 0) { throw 'Repository integration check failed.' }
}
