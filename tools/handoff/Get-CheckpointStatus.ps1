#Requires -Version 7.0
<#
.SYNOPSIS
Inspects one explicitly named checkpoint and compares its saved candidate with the current worktree.
.DESCRIPTION
Returns current, missing, stale or unknown with reasons. This does not run validation,
discover jobs, scrape history, or prove an idle writer or human acceptance.
#>
[CmdletBinding()]
param([string]$Path, [string]$Owner, [string]$Worktree)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Handoff.Common.ps1')

$checkpointPath = Resolve-HandoffPath $Path 'Path'
$worktreePath = Resolve-HandoffPath $Worktree 'Worktree'
if ([string]::IsNullOrWhiteSpace($Owner)) {
    throw 'Owner is required.'
}

# Persistence, ownership and candidate freshness are separate observations, not one success flag.
$result = [ordered]@{
    schemaVersion = 1
    path = $checkpointPath
    state = 'unknown'
    persisted = $null
    observedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    expectedOwner = $Owner
    expectedWorktree = $worktreePath
    owner = $null
    worktree = $null
    lastVerifiedUpdateUtc = $null
    sha256 = $null
    note = $null
    candidate = $null
    currentCandidate = $null
    reasons = @()
}

try {
    Assert-HandoffUnlinkedPath $checkpointPath
    $item = Get-HandoffItem $checkpointPath
    if ($null -eq $item) {
        $result.state = 'missing'
        $result.persisted = $false
        $result.reasons = @('No checkpoint exists at the requested path.')
        return [pscustomobject]$result
    }

    $result.persisted = $true
    if ($item.PSIsContainer) {
        throw 'Checkpoint path is a directory, not a saved record.'
    }

    $saved = Read-HandoffCheckpoint $checkpointPath
    $record = $saved.record
    $result.owner = $record.owner
    $result.worktree = $record.worktree
    $result.lastVerifiedUpdateUtc = $record.savedAtUtc
    $result.sha256 = $saved.sha256
    $result.note = $record.note
    $result.candidate = [pscustomobject]$record.candidate

    if ($record.owner -cne $Owner) {
        throw "Expected owner '$Owner', but saved owner is '$($record.owner)'."
    }

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $record.worktree.Equals($worktreePath, $comparison)) {
        throw 'Saved checkpoint belongs to a different worktree location.'
    }

    # A readable note can still be stale or have unknown coverage; neither becomes candidate-ready evidence.
    $current = Get-HandoffCandidate $worktreePath
    $result.currentCandidate = $current
    if ($record.candidate.state -ne 'observed' -or -not $record.candidate.fingerprint -or $current.state -ne 'observed') {
        $result.reasons = @($record.candidate.reasons) + @($current.reasons)
        if (-not $result.reasons.Count) {
            $result.reasons = @('Candidate coverage is unavailable.')
        }
    } elseif ($record.candidate.fingerprint -ne $current.fingerprint) {
        $result.state = 'stale'
        $result.reasons = @('Covered candidate content changed since the checkpoint was saved.')
    } else {
        $result.state = 'current'
    }
} catch {
    $result.state = 'unknown'
    $result.reasons = @($_.Exception.Message)
}

[pscustomobject]$result
