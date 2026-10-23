#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$tools = Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\handoff'
$save = Join-Path $tools 'Save-Checkpoint.ps1'
$status = Join-Path $tools 'Get-CheckpointStatus.ps1'
$verify = Join-Path $tools 'Invoke-LocalVerification.ps1'
$git = (Get-Command git -CommandType Application | Select-Object -First 1).Source
$pwsh = (Get-Process -Id $PID).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "AgentKit.HandoffTests-$([guid]::NewGuid().ToString('N'))"
$passed = 0
$failures = [Collections.Generic.List[string]]::new()

foreach ($script in @($save, $status, $verify)) {
    if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { throw "Required helper missing: '$script'." }
}

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_ }
    Assert ($null -ne $caught) "Expected error matching '$Pattern'."
    Assert ($caught.Exception.Message -like $Pattern) "Unexpected error: $($caught.Exception.Message)"
}

function Test-Case([string]$Name, [scriptblock]$Action) {
    try {
        & $Action
        $script:passed++
        Write-Host "PASS $Name"
    } catch {
        $script:failures.Add("${Name}: $($_.Exception.Message)")
        Write-Warning "FAIL ${Name}: $($_.Exception.Message)"
    }
}

function Invoke-TestGit([string]$Worktree, [string[]]$Arguments) {
    & $git -c core.hooksPath= -c commit.gpgsign=false -c core.autocrlf=false -C $Worktree @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}

function New-Fixture([string]$Name) {
    $root = Join-Path $testRoot $Name
    $worktree = Join-Path $root 'worktree [candidate]'
    $private = Join-Path $root 'private evidence [job]'
    [void][IO.Directory]::CreateDirectory($worktree)
    [void][IO.Directory]::CreateDirectory($private)
    Invoke-TestGit $worktree @('-c', 'init.templateDir=', 'init', '--quiet', '--initial-branch=main')
    [IO.File]::WriteAllText((Join-Path $worktree 'tracked [data].txt'), 'original tracked content')
    [IO.File]::WriteAllText((Join-Path $worktree '.gitignore'), "ignored.txt`n")
    Invoke-TestGit $worktree @('add', '--all')
    Invoke-TestGit $worktree @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
        'commit', '--quiet', '--no-gpg-sign', '-m', 'Synthetic fixture')
    return [pscustomobject]@{
        Root = $root
        Worktree = $worktree
        Private = $private
        Checkpoint = Join-Path $private 'current [checkpoint].json'
    }
}

function Save-Fixture($Fixture, [string]$Note = "# Current checkpoint`nScope: synthetic test`nNext: inspect the result.`n",
    [string]$ExpectedSha256) {
    $options = @{ Path = $Fixture.Checkpoint; Owner = 'owner-one'; Worktree = $Fixture.Worktree; Note = $Note }
    if ($ExpectedSha256) { $options.ExpectedSha256 = $ExpectedSha256 }
    & $save @options
}

function Get-FixtureStatus($Fixture) {
    & $status -Path $Fixture.Checkpoint -Owner owner-one -Worktree $Fixture.Worktree
}

function Invoke-FixtureCheck($Fixture, [string]$Name, [string]$Code) {
    & $verify -Worktree $Fixture.Worktree -Executable $pwsh -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', $Code) `
        -EvidenceDirectory (Join-Path $Fixture.Private $Name)
}

function Read-Evidence($Fixture, [string]$Name) {
    Get-Content -LiteralPath (Join-Path $Fixture.Private "$Name\verification.json") -Raw | ConvertFrom-Json
}

function Remove-TestEntries([string]$Root) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $directories = [Collections.Generic.List[string]]::new()
    $pending.Push($Root)
    while ($pending.Count) {
        $directory = $pending.Pop()
        $item = Get-Item -LiteralPath $directory -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked cleanup parent: '$directory'." }
        $directories.Add($directory)
        foreach ($child in Get-ChildItem -LiteralPath $directory -Force) {
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                if ($child.PSIsContainer) { [IO.Directory]::Delete($child.FullName) } else { [IO.File]::Delete($child.FullName) }
            } elseif ($child.PSIsContainer) {
                $pending.Push($child.FullName)
            } else {
                $child.Attributes = [IO.FileAttributes]::Normal
                [IO.File]::Delete($child.FullName)
            }
        }
    }
    for ($index = $directories.Count - 1; $index -ge 0; $index--) { [IO.Directory]::Delete($directories[$index]) }
}

[void][IO.Directory]::CreateDirectory($testRoot)
try {
    Test-Case 'Missing destinations fail without prompting or creating defaults' {
        $fixture = New-Fixture 'required-destinations'
        Assert-Throws { & $save -Owner owner-one -Worktree $fixture.Worktree -Note 'authored note' } '*Path*required*'
        Assert-Throws { & $verify -Worktree $fixture.Worktree -Executable $pwsh } '*EvidenceDirectory*required*'
        Assert (@(Get-ChildItem -LiteralPath $fixture.Private -Force).Count -eq 0) 'A default destination was created.'
    }

    Test-Case 'Save preserves the complete authored note and verifies JSON readback' {
        $fixture = New-Fixture 'save-readback'
        $note = "# Current checkpoint`r`n`r`nScope: T017`r`nDecision: keep this rationale.`r`nNext: inspect [data].`r`n"
        $result = Save-Fixture $fixture $note
        $record = Get-Content -LiteralPath $fixture.Checkpoint -Raw | ConvertFrom-Json
        Assert ($result.persisted -and $result.state -eq 'current') 'Save did not report current persisted state.'
        Assert ($record.note -ceq $note) 'Authored whitespace or content was lost.'
        Assert ($record.owner -eq 'owner-one' -and $record.worktree -eq $fixture.Worktree) 'Owner or worktree missing.'
        Assert ($result.sha256 -eq (Get-FileHash -LiteralPath $fixture.Checkpoint -Algorithm SHA256).Hash) 'Wrong readback hash.'
        Assert ($result.lastVerifiedUpdateUtc -and $result.candidate.fingerprint) 'Save omitted observed basis or timestamp.'
        Assert ((Get-FixtureStatus $fixture).sha256 -eq $result.sha256) 'Status does not identify the saved record.'
        $dateNote = '2026-10-22T00:00:00Z'
        $dateResult = Save-Fixture $fixture $dateNote $result.sha256
        Assert ($dateResult.note -is [string] -and $dateResult.note -ceq $dateNote) 'JSON date inference changed authored note text.'
    }

    Test-Case 'Candidate fingerprints stay stable in fresh PowerShell processes' {
        $fixture = New-Fixture 'fresh-process-status'
        $first = Save-Fixture $fixture
        $wrapper = Join-Path $fixture.Root 'read status [fresh].ps1'
        [IO.File]::WriteAllText($wrapper, @'
param([string]$Helper, [string]$Path, [string]$Worktree)
& $Helper -Path $Path -Owner owner-one -Worktree $Worktree | ConvertTo-Json -Depth 12 -Compress
'@)
        foreach ($attempt in 1..3) {
            $json = & $pwsh -NoProfile -NonInteractive -File $wrapper -Helper $status -Path $fixture.Checkpoint -Worktree $fixture.Worktree
            Assert ($LASTEXITCODE -eq 0) 'Fresh status process failed.'
            $fresh = $json | ConvertFrom-Json
            Assert ($fresh.state -eq 'current' -and $fresh.currentCandidate.fingerprint -eq $first.candidate.fingerprint) `
                "Unchanged candidate fingerprint changed in fresh process $attempt."
        }
    }

    if ($IsWindows) {
        Test-Case 'Relative paths, trailing separators and Windows path casing identify the same candidate' {
            $fixture = New-Fixture 'equivalent-paths'
            Push-Location -LiteralPath $fixture.Root
            try {
                $first = & $save -Path '.\private evidence [job]\current [checkpoint].json' -Owner owner-one `
                    -Worktree '.\worktree [candidate]\' -Note 'relative path note'
                $equivalent = & $status -Path $fixture.Checkpoint.ToUpperInvariant() -Owner owner-one `
                    -Worktree $fixture.Worktree.ToUpperInvariant()
                Assert ($first.state -eq 'current' -and $equivalent.state -eq 'current') 'Equivalent filesystem paths changed candidate identity.'
                $second = & $save -Path $fixture.Checkpoint -Owner owner-one -Worktree $fixture.Worktree `
                    -Note 'same directory update' -ExpectedSha256 $first.sha256
                Assert ($second.state -eq 'current') 'Trailing separator prevented a same-worktree update.'
            } finally { Pop-Location }
        }
    }

    Test-Case 'Only an inspected same-owner version can be updated' {
        $fixture = New-Fixture 'guarded-update'
        $first = Save-Fixture $fixture
        Assert-Throws { Save-Fixture $fixture 'replacement without hash' } '*ExpectedSha256*required*'
        Assert-Throws {
            & $save -Path $fixture.Checkpoint -Owner another-owner -Worktree $fixture.Worktree -Note 'do not replace' `
                -ExpectedSha256 $first.sha256
        } '*owner*'
        $second = Save-Fixture $fixture 'deliberately updated note' $first.sha256
        Assert ($second.note -eq 'deliberately updated note' -and $second.sha256 -ne $first.sha256) 'Guarded update was not saved.'
        Assert-Throws { Save-Fixture $fixture 'stale writer' $first.sha256 } '*changed*'
        Assert ((Get-FixtureStatus $fixture).note -eq $second.note) 'Stale writer overwrote current state.'
    }

    Test-Case 'User edits and unrelated existing documents are preserved' {
        $fixture = New-Fixture 'user-edit'
        $first = Save-Fixture $fixture
        $record = Get-Content -LiteralPath $fixture.Checkpoint -Raw | ConvertFrom-Json
        $record.note = 'manual user edit to preserve'
        [IO.File]::WriteAllText($fixture.Checkpoint, ($record | ConvertTo-Json -Depth 12))
        $edited = [IO.File]::ReadAllText($fixture.Checkpoint)
        $editedHash = (Get-FileHash -LiteralPath $fixture.Checkpoint).Hash
        Assert ((Get-FixtureStatus $fixture).state -eq 'unknown') 'Edited note was advertised as current.'
        Assert-Throws { Save-Fixture $fixture 'replacement' $first.sha256 } '*'
        Assert-Throws { Save-Fixture $fixture 'replacement' $editedHash } '*edited*'
        Assert ([IO.File]::ReadAllText($fixture.Checkpoint) -ceq $edited) 'Manual edit was lost.'
        [IO.File]::WriteAllText($fixture.Checkpoint, '# A pre-existing handoff, not tool-owned JSON')
        Assert-Throws { Save-Fixture $fixture 'do not replace' } '*'
        Assert ([IO.File]::ReadAllText($fixture.Checkpoint).StartsWith('# A pre-existing')) 'Existing document was replaced.'
    }

    Test-Case 'Missing, wrong-owner, wrong-location and corrupt status stay explicit' {
        $fixture = New-Fixture 'status-states'
        $missing = Get-FixtureStatus $fixture
        Assert ($missing.state -eq 'missing' -and -not $missing.persisted) 'Missing checkpoint was not reported.'
        Save-Fixture $fixture | Out-Null
        $wrongOwner = & $status -Path $fixture.Checkpoint -Owner another-owner -Worktree $fixture.Worktree
        Assert ($wrongOwner.state -eq 'unknown' -and $wrongOwner.reasons.Count -gt 0) 'Owner mismatch was not explained.'
        $other = New-Fixture 'another-clone'
        $wrongLocation = & $status -Path $fixture.Checkpoint -Owner owner-one -Worktree $other.Worktree
        Assert ($wrongLocation.state -eq 'unknown') 'A different physical clone was treated as the same candidate.'
        [IO.File]::WriteAllText($fixture.Checkpoint, '{"schemaVersion":99}')
        $unknown = Get-FixtureStatus $fixture
        Assert ($unknown.state -eq 'unknown' -and $unknown.reasons.Count -gt 0) 'Unsupported metadata became empty/current state.'
    }

    Test-Case 'Tracked content and index-only changes invalidate coverage' {
        $fixture = New-Fixture 'tracked-stale'
        $first = Save-Fixture $fixture
        $file = Join-Path $fixture.Worktree 'tracked [data].txt'
        [IO.File]::WriteAllText($file, 'changed tracked content')
        Assert ((Get-FixtureStatus $fixture).state -eq 'stale') 'Tracked edit was missed.'
        Invoke-TestGit $fixture.Worktree @('add', '--all')
        [IO.File]::WriteAllText($file, 'original tracked content')
        $now = Get-FixtureStatus $fixture
        Assert ($now.state -eq 'stale' -and $now.currentCandidate.fingerprint -ne $first.candidate.fingerprint) 'Index was not covered.'
    }

    Test-Case 'Inherited Git index overrides cannot hide changes in the actual candidate' {
        $fixture = New-Fixture 'inherited-index'
        Save-Fixture $fixture | Out-Null
        $foreignIndex = Join-Path $fixture.Private 'foreign index'
        Copy-Item -LiteralPath (Join-Path $fixture.Worktree '.git\index') -Destination $foreignIndex
        $foreignHash = (Get-FileHash -LiteralPath $foreignIndex).Hash
        $file = Join-Path $fixture.Worktree 'tracked [data].txt'
        [IO.File]::WriteAllText($file, 'staged candidate change')
        Invoke-TestGit $fixture.Worktree @('add', '--all')
        [IO.File]::WriteAllText($file, 'original tracked content')
        $previousIndex = $env:GIT_INDEX_FILE
        try {
            $env:GIT_INDEX_FILE = $foreignIndex
            Assert ((Get-FixtureStatus $fixture).state -eq 'stale') 'An inherited alternate index hid the actual staged change.'
            Assert ((Get-FileHash -LiteralPath $foreignIndex).Hash -eq $foreignHash) 'Observation modified the foreign index.'
        } finally { $env:GIT_INDEX_FILE = $previousIndex }
    }

    Test-Case 'Added, changed and deleted non-ignored untracked content invalidates coverage' {
        $fixture = New-Fixture 'untracked-stale'
        $first = Save-Fixture $fixture
        $file = Join-Path $fixture.Worktree 'untracked [new] file.txt'
        [IO.File]::WriteAllText($file, 'first untracked bytes')
        Assert ((Get-FixtureStatus $fixture).state -eq 'stale') 'Added untracked file was missed.'
        $second = Save-Fixture $fixture 'includes untracked work' $first.sha256
        [IO.File]::WriteAllText($file, 'other untracked bytes')
        Assert ((Get-FixtureStatus $fixture).state -eq 'stale') 'Changed untracked bytes were missed.'
        $third = Save-Fixture $fixture 'includes changed bytes' $second.sha256
        [IO.File]::Delete($file)
        Assert ((Get-FixtureStatus $fixture).state -eq 'stale') 'Deleted untracked file was missed.'
        Assert ($third.candidate.untrackedFileCount -eq 1) 'Untracked coverage count is wrong.'
    }

    Test-Case 'Ignored content is an explicit limitation, never claimed as covered' {
        $fixture = New-Fixture 'ignored-limit'
        Save-Fixture $fixture | Out-Null
        [IO.File]::WriteAllText((Join-Path $fixture.Worktree 'ignored.txt'), 'not included')
        $result = Get-FixtureStatus $fixture
        Assert ($result.state -eq 'current') 'Ignored content unexpectedly changed the documented fingerprint scope.'
        Assert (($result.currentCandidate.limitations -join ' ') -match 'ignored') 'Ignored-content coverage gap was hidden.'
    }

    Test-Case 'Actual replacement failure is surfaced and preserves the previous checkpoint' {
        $fixture = New-Fixture 'write-failure'
        $first = Save-Fixture $fixture
        $before = [IO.File]::ReadAllBytes($fixture.Checkpoint)
        $lock = [IO.File]::Open($fixture.Checkpoint, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            Assert-Throws { Save-Fixture $fixture 'must fail to replace locked destination' $first.sha256 } '*'
        } finally { $lock.Dispose() }
        Assert (([Convert]::ToBase64String([IO.File]::ReadAllBytes($fixture.Checkpoint))) -eq
            [Convert]::ToBase64String($before)) 'Failed save damaged the previous checkpoint.'
        Assert (@(Get-ChildItem -LiteralPath $fixture.Private -Filter '*.tmp' -Force).Count -eq 0) 'Failed save left a temporary note.'
    }

    Test-Case 'Missing parents and in-checkout destinations are refused before writing' {
        $fixture = New-Fixture 'unsafe-destinations'
        Assert-Throws {
            & $save -Path (Join-Path $fixture.Private 'missing\checkpoint.json') -Owner owner-one -Worktree $fixture.Worktree -Note 'note'
        } '*parent*'
        Assert-Throws {
            & $save -Path (Join-Path $fixture.Worktree 'private.json') -Owner owner-one -Worktree $fixture.Worktree -Note 'note'
        } '*outside*'
        Assert-Throws {
            & $verify -Worktree $fixture.Worktree -Executable $pwsh -EvidenceDirectory (Join-Path $fixture.Worktree 'logs')
        } '*outside*'
        Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.Private 'missing'))) 'Missing parent was created.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.Worktree 'logs'))) 'Runtime evidence entered the source checkout.'
        $commandScript = Join-Path $fixture.Root 'not-native.cmd'
        [IO.File]::WriteAllText($commandScript, '@echo must not execute implicitly')
        if ($IsWindows) {
            Assert-Throws {
                & $verify -Worktree $fixture.Worktree -Executable $commandScript -EvidenceDirectory (Join-Path $fixture.Private 'shell-wrapper')
            } '*native*'
            Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.Private 'shell-wrapper'))) 'Script preflight created evidence or ran a shell.'
        }
    }

    Test-Case 'Passing native command preserves stdout/stderr, literal arguments and actual cwd' {
        $fixture = New-Fixture 'passing-command'
        $program = Join-Path $fixture.Root 'check [arguments].ps1'
        [IO.File]::WriteAllText($program, @'
param([string]$First, [string]$Empty, [string]$Last)
[Console]::Out.WriteLine((Get-Location).Path)
[Console]::Out.WriteLine(($First, $Empty, $Last | ConvertTo-Json -Compress))
[Console]::Error.WriteLine('stderr evidence')
exit 0
'@)
        $beforeLocation = (Get-Location).Path
        $result = & $verify -Worktree $fixture.Worktree -Executable $pwsh `
            -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $program, 'spaces [brackets] "quotes"', '', 'last') `
            -EvidenceDirectory (Join-Path $fixture.Private 'pass [logs]')
        Assert (@($result).Count -eq 1) 'Verification must return exactly one evidence record without async task values.'
        $stdout = [IO.File]::ReadAllText($result.stdoutPath)
        $stderr = [IO.File]::ReadAllText($result.stderrPath)
        $record = Read-Evidence $fixture 'pass [logs]'
        Assert ($result.outcome -eq 'passed' -and $record.exitCode -eq 0 -and $record.freshness -eq 'current') 'Passing evidence is wrong.'
        Assert ($stdout.Contains($fixture.Worktree)) 'Command did not run from the specified worktree.'
        Assert ($stdout.Contains('spaces [brackets]') -and $stdout.Contains('\"quotes\"') -and $stdout.Contains(',"",')) 'Argument boundaries changed.'
        Assert ($stderr.Contains('stderr evidence')) 'stderr was lost or mixed with stdout.'
        Assert (-not $stdout.Contains('stderr evidence')) 'stderr leaked into stdout evidence.'
        Assert ((Get-Location).Path -eq $beforeLocation) 'Verification changed the caller location.'
        Assert ($record.startedAtUtc -and $record.completedAtUtc -and $record.candidateBefore.fingerprint) 'Evidence basis or timing missing.'
        Assert ($record.stdoutSha256 -eq (Get-FileHash -LiteralPath $result.stdoutPath).Hash) 'Recorded stdout integrity is wrong.'
    }

    Test-Case 'Failed native command stays failed with its true exit code and both logs' {
        $fixture = New-Fixture 'failing-command'
        Assert-Throws {
            Invoke-FixtureCheck $fixture 'failed [logs]' "[Console]::Out.WriteLine('partial stdout'); [Console]::Error.WriteLine('check failed'); exit 7"
        } '*exit code 7*'
        $record = Read-Evidence $fixture 'failed [logs]'
        Assert ($record.outcome -eq 'failed' -and $record.exitCode -eq 7) 'Failure was turned into a pass.'
        Assert ([IO.File]::ReadAllText($record.stdoutPath).Contains('partial stdout')) 'Failure lost stdout.'
        Assert ([IO.File]::ReadAllText($record.stderrPath).Contains('check failed')) 'Failure lost stderr.'
        Assert ($record.freshness -eq 'current') 'A failed command should still identify its exact candidate.'
    }

    Test-Case 'Failure exits nonzero through the documented PowerShell script entry point' {
        $fixture = New-Fixture 'native-helper-exit'
        $wrapper = Join-Path $fixture.Root 'invoke failed check.ps1'
        [IO.File]::WriteAllText($wrapper, @'
param([string]$Helper, [string]$Worktree, [string]$Executable, [string]$EvidenceDirectory)
& $Helper -Worktree $Worktree -Executable $Executable -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', 'exit 13') `
    -EvidenceDirectory $EvidenceDirectory
'@)
        & $pwsh -NoProfile -NonInteractive -File $wrapper -Helper $verify -Worktree $fixture.Worktree -Executable $pwsh `
            -EvidenceDirectory (Join-Path $fixture.Private 'entrypoint-failure') 2>&1 | Out-Null
        Assert ($LASTEXITCODE -ne 0) 'The helper process exited successfully after a failed check.'
        Assert ((Read-Evidence $fixture 'entrypoint-failure').exitCode -eq 13) 'True child exit was not retained.'
    }

    Test-Case 'Both output pipes are drained without truncation or deadlock' {
        $fixture = New-Fixture 'large-pipes'
        $result = Invoke-FixtureCheck $fixture 'large' "[Console]::Out.Write(('o' * 131072)); [Console]::Error.Write(('e' * 131072)); exit 0"
        Assert ((Get-Item -LiteralPath $result.stdoutPath).Length -eq 131072) 'stdout was truncated.'
        Assert ((Get-Item -LiteralPath $result.stderrPath).Length -eq 131072) 'stderr was truncated.'
    }

    Test-Case 'A zero-exit command changing candidate content leaves stale evidence and fails the helper' {
        $fixture = New-Fixture 'command-changes-candidate'
        Assert-Throws {
            Invoke-FixtureCheck $fixture 'stale' "[IO.File]::WriteAllText('generated [file].txt', 'new candidate'); exit 0"
        } '*candidate*changed*'
        $record = Read-Evidence $fixture 'stale'
        Assert ($record.exitCode -eq 0 -and $record.outcome -eq 'passed' -and $record.freshness -eq 'stale') 'Actual exit and stale basis were conflated.'
        Assert ($record.candidateBefore.fingerprint -ne $record.candidateAfter.fingerprint) 'Changed candidate basis was lost.'
    }

    Test-Case 'Launch failures remain blocked and evidence directories are never reused' {
        $fixture = New-Fixture 'blocked-launch'
        $badExecutable = Join-Path $fixture.Root 'not [an executable].exe'
        [IO.File]::WriteAllText($badExecutable, 'not an executable image')
        Assert-Throws {
            & $verify -Worktree $fixture.Worktree -Executable $badExecutable -EvidenceDirectory (Join-Path $fixture.Private 'blocked')
        } '*'
        $record = Read-Evidence $fixture 'blocked'
        Assert ($record.outcome -eq 'blocked' -and $null -eq $record.exitCode -and $record.error) 'Launch failure was not persisted honestly.'
        Assert-Throws { Invoke-FixtureCheck $fixture 'blocked' "exit 0" } '*already exists*'
        Assert ((Read-Evidence $fixture 'blocked').outcome -eq 'blocked') 'Existing evidence was overwritten.'
    }

    Test-Case 'Unavailable Git persists useful notes and reports unknown candidate evidence' {
        $fixture = New-Fixture 'missing-git'
        $originalPath = $env:PATH
        $env:PATH = $fixture.Private
        try {
            $result = Save-Fixture $fixture
            Assert ($result.persisted -and $result.state -eq 'unknown') 'Unavailable Git made a false current claim or lost the note.'
            Assert ($null -eq $result.candidate.fingerprint -and $result.reasons.Count -gt 0) 'Missing Git was not explained.'
            $check = Invoke-FixtureCheck $fixture 'unknown' "exit 0"
            Assert ($check.outcome -eq 'passed' -and $check.freshness -eq 'unknown') 'Missing candidate data became current verification.'
        } finally { $env:PATH = $originalPath }
    }

    Test-Case 'Submodules, sparse modes and nested repositories are explicit unknowns' {
        $fixture = New-Fixture 'submodule-gap'
        $head = (Invoke-TestGit $fixture.Worktree @('rev-parse', 'HEAD')).Trim()
        Invoke-TestGit $fixture.Worktree @('update-index', '--add', '--cacheinfo', "160000,$head,nested-submodule")
        Assert ((Save-Fixture $fixture).state -eq 'unknown') 'Unsupported submodule was called covered.'
        $sparse = New-Fixture 'sparse-gap'
        Invoke-TestGit $sparse.Worktree @('config', '--local', 'core.sparseCheckout', 'true')
        Assert ((Save-Fixture $sparse).state -eq 'unknown') 'Sparse mode was called covered.'
        $nested = New-Fixture 'nested-gap'
        $nestedPath = Join-Path $nested.Worktree 'nested [repository]'
        [void][IO.Directory]::CreateDirectory($nestedPath)
        Invoke-TestGit $nestedPath @('-c', 'init.templateDir=', 'init', '--quiet')
        [IO.File]::WriteAllText((Join-Path $nestedPath 'work.txt'), 'nested work')
        Assert ((Save-Fixture $nested).state -eq 'unknown') 'Nested repository contents were silently skipped.'
    }

    if ($IsWindows) {
        Test-Case 'Junction aliases cannot redirect runtime writes into a repository' {
            $fixture = New-Fixture 'junction-destination'
            $alias = Join-Path $fixture.Private 'linked [checkout]'
            New-Item -ItemType Junction -Path $alias -Target $fixture.Worktree | Out-Null
            Assert-Throws {
                & $save -Path (Join-Path $alias 'private.json') -Owner owner-one -Worktree $fixture.Worktree -Note 'note'
            } '*linked*'
            Assert-Throws {
                & $verify -Worktree $fixture.Worktree -Executable $pwsh -EvidenceDirectory (Join-Path $alias 'logs')
            } '*linked*'
            Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.Worktree 'private.json'))) 'Write followed a junction.'
        }
    }

    Test-Case 'Read-only capture runs no hooks, changes no Git state, and creates no evidence in source' {
        $fixture = New-Fixture 'no-surprises'
        $config = Join-Path $fixture.Worktree '.git\config'
        $index = Join-Path $fixture.Worktree '.git\index'
        $beforeConfig = (Get-FileHash -LiteralPath $config).Hash
        $beforeIndex = (Get-FileHash -LiteralPath $index).Hash
        $beforeIndexTime = (Get-Item -LiteralPath $index -Force).LastWriteTimeUtc
        $beforeHead = Invoke-TestGit $fixture.Worktree @('rev-parse', 'HEAD')
        $hook = Join-Path $fixture.Worktree '.git\hooks\post-index-change'
        [void][IO.Directory]::CreateDirectory((Split-Path $hook -Parent))
        [IO.File]::WriteAllText($hook, "#!/bin/sh`necho unexpected > hook-was-run.txt`n")
        Save-Fixture $fixture | Out-Null
        Get-FixtureStatus $fixture | Out-Null
        Invoke-FixtureCheck $fixture 'local-only' "exit 0" | Out-Null
        Assert ((Get-FileHash -LiteralPath $config).Hash -eq $beforeConfig) 'Git configuration changed.'
        Assert ((Get-FileHash -LiteralPath $index).Hash -eq $beforeIndex) 'Git index content changed.'
        Assert ((Get-Item -LiteralPath $index -Force).LastWriteTimeUtc -eq $beforeIndexTime) 'Git index was refreshed.'
        Assert ((Invoke-TestGit $fixture.Worktree @('rev-parse', 'HEAD')) -eq $beforeHead) 'Git HEAD changed.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.Worktree 'hook-was-run.txt'))) 'Capture executed a repository hook.'
        Assert ((Invoke-TestGit $fixture.Worktree @('status', '--porcelain')) -join '' -eq '') 'Helpers changed the source checkout.'
    }
} finally {
    Remove-TestEntries -Root $testRoot
}

if ($failures.Count) { throw "$($failures.Count) test(s) failed:`n$($failures -join "`n")" }
Write-Host "$passed checks passed. Temporary repositories and evidence removed."
