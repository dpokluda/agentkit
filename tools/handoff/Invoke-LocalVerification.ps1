#Requires -Version 7.0
<#
.SYNOPSIS
Runs one approved native executable and retains candidate-bound local evidence.
.DESCRIPTION
Requires explicit job approval for the executable, arguments and their side effects.
Creates a new evidence directory beneath an existing private parent. Does not discover
checks, invoke a shell implicitly, install dependencies, or grant execution permission.
Nonzero child exits, launch/capture failures and changed candidates throw after capture.
#>
[CmdletBinding()]
param(
    [string]$Worktree,
    [string]$Executable,
    [AllowEmptyCollection()][AllowEmptyString()][string[]]$ArgumentList = @(),
    [string]$EvidenceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Handoff.Common.ps1')

$evidence = Resolve-HandoffPath $EvidenceDirectory 'EvidenceDirectory'
$worktreePath = Resolve-HandoffPath $Worktree 'Worktree'
if ([string]::IsNullOrWhiteSpace($Executable) -or -not [IO.Path]::IsPathFullyQualified($Executable)) {
    throw 'Executable is required and must be an explicitly approved absolute native executable path.'
}

$executablePath = Resolve-HandoffPath $Executable 'Executable'
$program = Get-HandoffItem $executablePath
if ($null -eq $program -or $program.PSIsContainer) {
    throw "Executable does not exist as a file: '$executablePath'."
}

if ($IsWindows -and [IO.Path]::GetExtension($executablePath) -notin @('.exe', '.com')) {
    throw 'Executable must be a native .exe/.com; supply an explicitly approved interpreter for scripts.'
}

if ($null -eq $ArgumentList -or @($ArgumentList | Where-Object { $null -eq $_ }).Count) {
    throw 'ArgumentList cannot contain null; supply an empty array for no arguments.'
}

$directory = Get-HandoffItem $worktreePath
if ($null -eq $directory -or -not $directory.PSIsContainer) {
    throw "Worktree must be an existing directory: '$worktreePath'."
}

Assert-HandoffDestination $evidence $worktreePath
if ($null -ne (Get-HandoffItem $evidence)) {
    throw "EvidenceDirectory already exists; choose a new explicit directory: '$evidence'."
}

# Preflight completes before allocating evidence or starting the caller-approved command.
New-Item -ItemType Directory -Path $evidence -Confirm:$false | Out-Null
$record = [ordered]@{
    schemaVersion = 1
    worktree = $worktreePath
    executable = $executablePath
    argumentList = @($ArgumentList)
    attemptedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    startedAtUtc = $null
    completedAtUtc = $null
    exitCode = $null
    outcome = 'blocked'
    candidateBefore = Get-HandoffCandidate $worktreePath
    candidateAfter = $null
    freshness = 'unknown'
    stdoutPath = Join-Path $evidence 'stdout.log'
    stderrPath = Join-Path $evidence 'stderr.log'
    stdoutSha256 = $null
    stderrSha256 = $null
    evidencePath = Join-Path $evidence 'verification.json'
    error = $null
    reasons = @()
}

$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $executablePath
$start.WorkingDirectory = $worktreePath
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in $ArgumentList) {
    $start.ArgumentList.Add($argument)
}

$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$stdoutFile = $null
$stderrFile = $null

try {
    $stdoutFile = [IO.File]::Open($record.stdoutPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $stderrFile = [IO.File]::Open($record.stderrPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    if (-not $process.Start()) {
        throw 'Approved executable did not start.'
    }

    $record.startedAtUtc = $process.StartTime.ToUniversalTime().ToString('o')

    # Drain both raw streams concurrently so a full stderr pipe cannot block stdout.
    $stdoutCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
    $stderrCopy = $process.StandardError.BaseStream.CopyToAsync($stderrFile)
    $process.WaitForExit()
    $record.exitCode = $process.ExitCode
    [void]$stdoutCopy.GetAwaiter().GetResult()
    [void]$stderrCopy.GetAwaiter().GetResult()

    $stdoutFile.Flush($true)
    $stderrFile.Flush($true)
    $record.outcome = if ($record.exitCode -eq 0) { 'passed' } else { 'failed' }
} catch {
    $record.error = $_.Exception.Message
    $record.outcome = 'blocked'
} finally {
    $record.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    if ($null -ne $stdoutFile) {
        $stdoutFile.Dispose()
    }
    if ($null -ne $stderrFile) {
        $stderrFile.Dispose()
    }

    $process.Dispose()
}

# Command success and candidate coverage are independent: a passing command can change the code it was checking.
$record.candidateAfter = Get-HandoffCandidate $worktreePath
if ($record.candidateBefore.state -eq 'observed' -and $record.candidateAfter.state -eq 'observed') {
    $record.freshness = if ($record.candidateBefore.fingerprint -eq $record.candidateAfter.fingerprint) { 'current' } else { 'stale' }
    if ($record.freshness -eq 'stale') {
        $record.reasons = @('Covered candidate changed while the command ran; evidence does not verify the resulting candidate.')
    }
} else {
    $record.reasons = @($record.candidateBefore.reasons) + @($record.candidateAfter.reasons)
}

if (Test-Path -LiteralPath $record.stdoutPath -PathType Leaf) {
    $record.stdoutSha256 = (Get-FileHash -LiteralPath $record.stdoutPath).Hash
}
if (Test-Path -LiteralPath $record.stderrPath -PathType Leaf) {
    $record.stderrSha256 = (Get-FileHash -LiteralPath $record.stderrPath).Hash
}

# Preserve and verify failed/stale evidence too, before surfacing the terminating error to the caller.
$json = ConvertTo-Json -InputObject $record -Depth 12
$stream = [IO.File]::Open($record.evidencePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush($true)
} finally {
    $stream.Dispose()
}

if ((Get-Content -LiteralPath $record.evidencePath -Raw -Encoding utf8) -cne $json) {
    throw "Verification evidence readback failed: '$($record.evidencePath)'."
}

[pscustomobject]$record
if ($record.outcome -eq 'blocked') {
    throw "Local verification was blocked: $($record.error) Evidence: '$($record.evidencePath)'."
}
if ($record.outcome -eq 'failed') {
    throw "Local verification failed with exit code $($record.exitCode). Evidence: '$($record.evidencePath)'."
}
if ($record.freshness -eq 'stale') {
    throw "Covered candidate changed during verification. Evidence: '$($record.evidencePath)'."
}
