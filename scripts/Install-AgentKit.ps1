#Requires -Version 7.0
<#
.SYNOPSIS
Links the public AgentKit into a home directory and both agent profiles.
.DESCRIPTION
Creates agentkit first, then profile links that point through that home alias.
Any occupied link location, including an already-correct or broken link, stops preflight
without changes. Nothing is overwritten, backed up, or automatically replaced.
Later link-creation errors stop the script; earlier links remain because there is no rollback.
WhatIf previews link creation without changing files or creating profile folders.

OPINIONS.md and VOICE.md live in each agent's root and are consulted through GLOBAL's
directions. They are not installed as automatically loaded modular rules.
.PARAMETER ContentRoot
AgentKit content root. Defaults to the parent of this script's directory.
An explicit relative path is resolved against the caller before navigating home.
.PARAMETER HomeRoot
Existing home directory. Defaults to HOME; use a temporary directory for isolated tests.
.EXAMPLE
.\Install-AgentKit.ps1 -HomeRoot $temporaryHome -WhatIf
Previews the links without changing the temporary home.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateNotNullOrEmpty()]
    [string]$ContentRoot = (Split-Path -Path $PSScriptRoot -Parent),

    [ValidateNotNullOrEmpty()]
    [string]$HomeRoot = $HOME
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ExistingItem {
    param([string]$Path)

    try {
        # Read hidden entries and broken links as occupied; this never forces a write.
        return Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    } catch [System.Management.Automation.ItemNotFoundException] {
        return $null
    }
}

$contentPath = (Resolve-Path -LiteralPath $ContentRoot -ErrorAction Stop).ProviderPath
$homePath = (Resolve-Path -LiteralPath $HomeRoot -ErrorAction Stop).ProviderPath
if (-not (Test-Path -LiteralPath $homePath -PathType Container)) {
    throw "HomeRoot must be an existing directory: '$homePath'."
}
$aliasPath = Join-Path $homePath 'agentkit'
$mappings = @(
    @{ Location = 'agentkit'; Artifact = ''; Directory = $true },
    @{ Location = '.copilot\agents'; Artifact = 'agents'; Directory = $true },
    @{ Location = '.claude\agents'; Artifact = 'agents'; Directory = $true },
    @{ Location = '.copilot\skills'; Artifact = 'skills'; Directory = $true },
    @{ Location = '.claude\skills'; Artifact = 'skills'; Directory = $true },
    @{ Location = '.copilot\copilot-instructions.md'; Artifact = 'instructions\GLOBAL.md'; Directory = $false },
    @{ Location = '.copilot\OPINIONS.md'; Artifact = 'instructions\OPINIONS.md'; Directory = $false },
    @{ Location = '.copilot\VOICE.md'; Artifact = 'instructions\VOICE.md'; Directory = $false },
    @{ Location = '.claude\CLAUDE.md'; Artifact = 'instructions\GLOBAL.md'; Directory = $false },
    @{ Location = '.claude\OPINIONS.md'; Artifact = 'instructions\OPINIONS.md'; Directory = $false },
    @{ Location = '.claude\VOICE.md'; Artifact = 'instructions\VOICE.md'; Directory = $false }
)

foreach ($mapping in $mappings) {
    $required = if ($mapping.Artifact) { Join-Path $contentPath $mapping.Artifact } else { $contentPath }
    $type = if ($mapping.Directory) { 'Container' } else { 'Leaf' }
    if (-not (Test-Path -LiteralPath $required -PathType $type)) {
        throw "Required AgentKit file or directory not found: '$required'. No links were created."
    }
    $location = Join-Path $homePath $mapping.Location
    $parent = Get-ExistingItem -Path (Split-Path -Path $location -Parent)
    if ($null -ne $parent -and (-not $parent.PSIsContainer -or
            ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint))) {
        throw "Link location '$location' has a linked or non-directory parent. No links were created."
    }
    if ($null -ne (Get-ExistingItem -Path $location)) {
        throw "Link location '$location' is already occupied. No links were created; move it manually before running again."
    }
}

Push-Location -LiteralPath $homePath
try {
    foreach ($mapping in $mappings) {
        $pointsTo = if ($mapping.Artifact) { Join-Path $aliasPath $mapping.Artifact } else { $contentPath }
        $location = Join-Path $homePath $mapping.Location
        if (-not $PSCmdlet.ShouldProcess($location, "Create link that points to '$pointsTo'")) {
            if ($WhatIfPreference) { continue }
            throw "Link creation declined at '$location'. The script stopped; earlier links remain."
        }
        $parent = Split-Path -Path $mapping.Location -Parent
        if ($parent -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
            New-Item -ItemType Directory -Path $parent -Confirm:$false | Out-Null
        }
        New-Item -ItemType SymbolicLink -Path $mapping.Location -Target $pointsTo -Confirm:$false
    }
} finally {
    Pop-Location
}
