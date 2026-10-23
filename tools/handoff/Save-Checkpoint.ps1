#Requires -Version 7.0
<#
.SYNOPSIS
Persists an authored note and its observed candidate at an explicitly authorized private path.
.DESCRIPTION
Requires an existing parent outside source checkouts. Updates require the same owner and
the previous readback SHA256. User-edited notes and unsupported existing records are refused.
Returns the same current/missing/stale/unknown status object as Get-CheckpointStatus.ps1.
.PARAMETER ExpectedSha256
File hash from the previously inspected save/status result. Required only when updating.
#>
[CmdletBinding()]
param(
    [string]$Path,
    [string]$Owner,
    [string]$Worktree,
    [string]$Note,
    [string]$ExpectedSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Handoff.Common.ps1')

$checkpointPath = Resolve-HandoffPath $Path 'Path'
$worktreePath = Resolve-HandoffPath $Worktree 'Worktree'
if ([string]::IsNullOrWhiteSpace($Owner)) {
    throw 'Owner is required.'
}
if ([string]::IsNullOrWhiteSpace($Note)) {
    throw 'Note is required and must contain authored content.'
}

$directory = Get-HandoffItem $worktreePath
if ($null -eq $directory -or -not $directory.PSIsContainer) {
    throw "Worktree must be an existing directory: '$worktreePath'."
}

Assert-HandoffDestination $checkpointPath $worktreePath

# Guard the current note before capturing or replacing anything; locks coordinate these helpers, not arbitrary editors.
$lockPath = "$checkpointPath.lock"
Assert-HandoffUnlinkedPath $lockPath
$lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$temporary = $null
try {
    $existing = Get-HandoffItem $checkpointPath
    if ($null -ne $existing) {
        if ($existing.PSIsContainer) {
            throw "Checkpoint path is occupied by a directory: '$checkpointPath'."
        }

        $previous = Read-HandoffCheckpoint $checkpointPath
        if ($previous.record.owner -cne $Owner) {
            throw "Checkpoint belongs to another owner: '$($previous.record.owner)'."
        }
        if (-not $ExpectedSha256) {
            throw 'ExpectedSha256 is required to update an existing checkpoint; inspect it first.'
        }
        if ($previous.sha256 -ne $ExpectedSha256) {
            throw 'Checkpoint changed since it was inspected; nothing was overwritten.'
        }

        $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        if (-not $previous.record.worktree.Equals($worktreePath, $comparison)) {
            throw 'Checkpoint belongs to a different worktree; use its original location or a new authorized path.'
        }
    } elseif ($ExpectedSha256) {
        throw 'Checkpoint disappeared since it was inspected; refusing to recreate it with a stale ExpectedSha256.'
    }

    $record = [ordered]@{
        schemaVersion = 1
        owner = $Owner
        worktree = $worktreePath
        savedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        note = $Note
        noteSha256 = Get-HandoffTextHash $Note
        candidate = Get-HandoffCandidate $worktreePath
    }
    $json = ConvertTo-Json -InputObject $record -Depth 12

    # Flush a complete sibling file before replacement so a failed save cannot leave a half-written note.
    $temporary = "$checkpointPath.$([guid]::NewGuid().ToString('N')).tmp"
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally {
        $stream.Dispose()
    }

    if ($null -ne $existing -and (Get-FileHash -LiteralPath $checkpointPath -Algorithm SHA256).Hash -ne $ExpectedSha256) {
        throw 'Checkpoint changed during capture; nothing was overwritten.'
    }

    [IO.File]::Move($temporary, $checkpointPath, $null -ne $existing)
    $temporary = $null

    $readback = Read-HandoffCheckpoint $checkpointPath
    if ($readback.record.note -cne $Note -or (Get-Content -LiteralPath $checkpointPath -Raw -Encoding utf8) -cne $json) {
        throw "Checkpoint readback did not match the authored record: '$checkpointPath'. Persistence is not verified."
    }
} finally {
    if ($temporary -and [IO.File]::Exists($temporary)) {
        [IO.File]::Delete($temporary)
    }

    $lock.Dispose()
}

& (Join-Path $PSScriptRoot 'Get-CheckpointStatus.ps1') -Path $checkpointPath -Owner $Owner -Worktree $worktreePath
