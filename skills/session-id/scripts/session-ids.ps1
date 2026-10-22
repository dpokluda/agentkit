#!/usr/bin/env pwsh
# Print the identifiers of the running Copilot session.
# No arguments, no network. Reads only the agent's own environment.

$ErrorActionPreference = 'Stop'

$sessionId = $env:COPILOT_AGENT_SESSION_ID
$copilotHome = if ($env:COPILOT_HOME) { $env:COPILOT_HOME } else { Join-Path $HOME '.copilot' }

if (-not $sessionId) {
    Write-Output 'COPILOT_AGENT_SESSION_ID is not set - not running inside a Copilot session.'
    exit 1
}

$stateDir = Join-Path (Join-Path $copilotHome 'session-state') $sessionId

Write-Output "Session ID   : $sessionId"
Write-Output "State dir    : $stateDir"
if ($env:AGENCY_REPO_DIR) { Write-Output "Repo         : $($env:AGENCY_REPO_DIR)" }

# Other identifiers, reported only when present so plain CLI output stays short.
$others = [ordered]@{
    'AGENCY_SESSION_ID'      = $env:AGENCY_SESSION_ID
    'AGENCY_LOG_SESSION_DIR' = $env:AGENCY_LOG_SESSION_DIR
}
$present = $others.GetEnumerator() | Where-Object { $_.Value }
if ($present) {
    Write-Output ''
    Write-Output 'Other IDs in scope (NOT usable to reference the conversation):'
    foreach ($kv in $present) { Write-Output "  $($kv.Key) = $($kv.Value)" }
}

# Any live agent attached to this session, including the one running this script.
if (Test-Path $stateDir) {
    $locks = Get-ChildItem $stateDir -Filter 'inuse.*.lock' -ErrorAction SilentlyContinue
    if ($locks) {
        Write-Output ''
        Write-Output 'Attached agents:'
        foreach ($lock in $locks) {
            if ($lock.Name -match 'inuse\.(\d+)\.lock') {
                $procId = [int]$Matches[1]
                $alive = $null -ne (Get-Process -Id $procId -ErrorAction SilentlyContinue)
                $state = if ($alive) { 'running' } else { 'stale (process gone)' }
                Write-Output "  pid $procId - $state"
            }
        }
    }
}

Write-Output ''
Write-Output 'Reference this session:'
Write-Output "  another session : send_session_message  session_id: $sessionId"
Write-Output "  terminal        : copilot --resume=$sessionId"
Write-Output "  app UI          : ghapp://sessions/$sessionId"
Write-Output ''
Write-Output 'Do not --resume this id while the session above is still running.'
