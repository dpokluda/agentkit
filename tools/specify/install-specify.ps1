#Requires -Version 7.0
<#
.SYNOPSIS
Install the latest stable Specify CLI from GitHub using uv.
.DESCRIPTION
Requires PowerShell 7, Git, uv, and Python 3.11 or newer.
Missing prerequisites are reported with installation commands, not installed automatically.
Does not initialize a repository, change PATH, or replace an existing Specify installation.
.EXAMPLE
install-specify.ps1 -WhatIf
Check prerequisites and preview the installation without changing anything.
.EXAMPLE
install-specify.ps1
Install the CLI on a machine where Specify is not yet installed.
#>

[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'

if (-not (Get-Command uv -CommandType Application -ErrorAction SilentlyContinue))
{
    throw 'Install uv first: winget install --id astral-sh.uv --exact. Then reopen PowerShell.'
}

if (-not (Get-Command git -CommandType Application -ErrorAction SilentlyContinue))
{
    throw 'Install Git first: winget install --id Git.Git --exact. Then reopen PowerShell.'
}

$bin = & uv tool dir --bin
if ($LASTEXITCODE -ne 0) { throw 'Could not locate the uv executable directory.' }
$executable = if ($IsWindows) { 'specify.exe' } else { 'specify' }
$specifyPath = Join-Path $bin $executable

if ((Get-Command specify -CommandType Application -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $specifyPath))
{
    throw 'Specify is already installed. Use update-specify.ps1 -Global instead.'
}

$python = & uv python find --system --no-python-downloads '>=3.11'
if ($LASTEXITCODE -ne 0)
{
    throw 'Python 3.11 or newer is required. Run uv python install, then rerun this script.'
}

$release = Invoke-RestMethod -Uri 'https://api.github.com/repos/github/spec-kit/releases/latest' -TimeoutSec 30
$tag = [string]$release.tag_name

if ($tag -notmatch '\Av\d+\.\d+\.\d+\z')
{
    throw "Unexpected stable release tag '$tag'. Inspect the release before installing it."
}

if ($PSCmdlet.ShouldProcess($specifyPath, "Install Specify CLI $tag from github/spec-kit"))
{
    & uv tool install specify-cli --from "git+https://github.com/github/spec-kit.git@$tag" --python $python --no-python-downloads
    if ($LASTEXITCODE -ne 0) { throw 'Specify installation failed. Review the uv output before retrying.' }

    $installed = & $specifyPath version --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not verify the installed CLI.' }
    if ($installed.cli_version -ne $tag.Substring(1)) { throw "The installed CLI does not report version $tag." }

    Write-Host "Installed Specify CLI $($installed.cli_version)."

    if (-not (Get-Command specify -CommandType Application -ErrorAction SilentlyContinue))
    {
        Write-Host 'To add Specify to PATH, run uv tool update-shell, then reopen PowerShell.'
    }
}
