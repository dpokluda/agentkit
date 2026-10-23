#Requires -Version 7.0
<#
.SYNOPSIS
Shared path, checkpoint and candidate observations for the manual handoff helpers.
.DESCRIPTION
Checkpoint records hold an authored note and its candidate fingerprint.
Candidate records distinguish observed content from unknown coverage; ignored and external inputs remain outside the fingerprint.
These helpers do not discover jobs, run project checks or authorize writes.
#>

function Resolve-HandoffPath([string]$Path, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Name is required; no default destination or worktree is selected."
    }

    $provider = $null
    $drive = $null
    $fullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path, [ref]$provider, [ref]$drive)
    if ($provider.Name -ne 'FileSystem') {
        throw "$Name must be a filesystem path."
    }

    $fullPath = [IO.Path]::GetFullPath($fullPath)
    if ($fullPath.Length -gt [IO.Path]::GetPathRoot($fullPath).Length) {
        $fullPath = $fullPath.TrimEnd('\', '/')
    }

    return $fullPath
}

function Get-HandoffItem([string]$Path) {
    try {
        return Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    } catch [System.Management.Automation.ItemNotFoundException] {
        return $null
    }
}

function Assert-HandoffUnlinkedPath([string]$Path) {
    $current = $Path
    while ($current) {
        $item = Get-HandoffItem $current
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Linked paths are not supported here: '$current'. Use the direct filesystem path."
        }

        $current = [IO.Path]::GetDirectoryName($current)
    }
}

<#
.SYNOPSIS
Requires an existing private parent; source checkouts and linked ancestry cannot receive runtime notes or logs.
#>
function Assert-HandoffDestination([string]$Path, [string]$Worktree) {
    Assert-HandoffUnlinkedPath $Path
    $parent = [IO.Path]::GetDirectoryName($Path)
    $item = Get-HandoffItem $parent
    if ($null -eq $item -or -not $item.PSIsContainer) {
        throw "Destination parent must already be an existing directory: '$parent'."
    }

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $toolCheckout = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    foreach ($source in @($Worktree, $toolCheckout)) {
        $prefix = $source.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($Path.Equals($source, $comparison) -or $Path.StartsWith($prefix, $comparison)) {
            throw "Runtime checkpoints and evidence must stay outside source checkouts: '$Path'."
        }
    }

    # The destination could be in a third checkout, not just this job or the AgentKit source.
    $current = $parent
    while ($current) {
        if ($null -ne (Get-HandoffItem (Join-Path $current '.git'))) {
            throw "Runtime checkpoints and evidence must stay outside Git checkouts: '$Path'."
        }

        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Get-HandoffTextHash([AllowEmptyString()][string]$Text) {
    $stream = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($Text))
    try {
        return (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash
    } finally {
        $stream.Dispose()
    }
}

<#
.SYNOPSIS
Reads one versioned note, rejecting incomplete records and manual text edits rather than replacing them.
#>
function Read-HandoffCheckpoint([string]$Path) {
    $text = Get-Content -LiteralPath $Path -Raw -Encoding utf8

    # Newer PowerShell versions infer JSON dates unless told to preserve authored strings.
    $jsonOptions = @{ AsHashtable = $true; ErrorAction = 'Stop' }
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) {
        $jsonOptions.DateKind = 'String'
    }

    $record = ConvertFrom-Json -InputObject $text @jsonOptions
    if ($record -isnot [Collections.IDictionary] -or $record.schemaVersion -ne 1) {
        throw "Unsupported checkpoint schema in '$Path'; existing content was not replaced."
    }

    if ([string]::IsNullOrWhiteSpace($record.owner) -or [string]::IsNullOrWhiteSpace($record.worktree) -or
        [string]::IsNullOrWhiteSpace($record.note) -or -not $record.savedAtUtc -or
        $record.candidate -isnot [Collections.IDictionary]) {
        throw "Incomplete checkpoint metadata in '$Path'; existing content was not replaced."
    }

    if ($record.noteSha256 -ne (Get-HandoffTextHash $record.note)) {
        throw "The authored note was edited outside this helper: '$Path'. Preserve it and use a new authorized path."
    }

    return @{ record = $record; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}

<#
.SYNOPSIS
Runs read-only Git with literal arguments and isolated Git settings, preserving separate stdout and error evidence.
#>
function Invoke-HandoffGitRead([string]$Executable, [string]$Worktree, [string[]]$Arguments, [int[]]$AllowedExitCodes = @(0)) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $Worktree
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8

    # An inherited alternate index or repository must not hide the actual job's candidate.
    foreach ($name in @($start.Environment.Keys)) {
        if ($name.StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase)) {
            [void]$start.Environment.Remove($name)
        }
    }

    $start.Environment['GIT_TERMINAL_PROMPT'] = '0'
    $start.Environment['GCM_INTERACTIVE'] = 'Never'
    $start.Environment['GIT_NO_LAZY_FETCH'] = '1'

    foreach ($argument in @('--no-optional-locks', '--no-pager', '-c', 'core.fsmonitor=false',
            '-c', 'core.untrackedCache=false', '-C', $Worktree) + $Arguments) {
        $start.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) {
            throw 'Git did not start.'
        }

        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()

        if ($process.ExitCode -notin $AllowedExitCodes) {
            throw "Git $($Arguments -join ' ') failed with exit code $($process.ExitCode): $($errorText.Trim())"
        }

        return $output
    } finally {
        $process.Dispose()
    }
}

<#
.SYNOPSIS
Fingerprints the exact local Git candidate, including staged changes and non-ignored untracked file bytes.
.DESCRIPTION
Unknown or unsupported observations return reasons, not a clean claim.
This is not an atomic filesystem snapshot, and it does not prove that no other writer is active.
#>
function Get-HandoffCandidate([string]$Worktree) {
    $candidate = [ordered]@{
        state = 'unknown'
        observedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        worktree = $Worktree
        repository = $null
        head = $null
        branch = $null
        fingerprint = $null
        indexSha256 = $null
        trackedFileCount = $null
        untrackedFileCount = $null
        coverage = 'Git HEAD, branch, index entries, tracked file bytes and non-ignored untracked file bytes'
        limitations = @(
            'Ignored files, timestamps, permissions, external dependencies and environment are not fingerprinted.'
            'Snapshots are observations, not a filesystem lock; stop concurrent writers before capture or verification.'
        )
        reasons = @()
    }

    try {
        Assert-HandoffUnlinkedPath $Worktree
        $directory = Get-HandoffItem $Worktree
        if ($null -eq $directory -or -not $directory.PSIsContainer) {
            throw "Worktree directory is unavailable: '$Worktree'."
        }

        $git = Get-Command git -CommandType Application -ErrorAction Ignore | Select-Object -First 1
        if ($null -eq $git) {
            throw 'Git executable is unavailable; candidate freshness is unknown.'
        }

        # Bind coverage to this top-level checkout and physical clone, not just a branch or remote name.
        $options = @{ Executable = $git.Source; Worktree = $Worktree }
        $root = (Invoke-HandoffGitRead @options -Arguments @('rev-parse', '--show-toplevel')).Trim()
        if ($IsWindows) {
            $root = $root.Replace('/', '\')
        }

        $root = [IO.Path]::GetFullPath($root)
        $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        if (-not $root.TrimEnd('\', '/').Equals($Worktree.TrimEnd('\', '/'), $comparison)) {
            throw "Worktree must be the Git top-level directory for candidate coverage; observed '$root'."
        }

        $common = (Invoke-HandoffGitRead @options -Arguments @('rev-parse', '--git-common-dir')).Trim()
        if ($IsWindows) {
            $common = $common.Replace('/', '\')
        }
        if (-not [IO.Path]::IsPathRooted($common)) {
            $common = Join-Path $Worktree $common
        }

        $candidate.repository = [IO.Path]::GetFullPath($common)
        Assert-HandoffUnlinkedPath $candidate.repository

        $candidate.head = (Invoke-HandoffGitRead @options -Arguments @('rev-parse', '--verify', 'HEAD')).Trim()
        $branch = (Invoke-HandoffGitRead @options -Arguments @('symbolic-ref', '--quiet', '--short', 'HEAD') -AllowedExitCodes @(0, 1)).Trim()
        if ($branch) {
            $candidate.branch = $branch
        }

        $sparse = (Invoke-HandoffGitRead @options -Arguments @('config', '--bool', 'core.sparseCheckout') -AllowedExitCodes @(0, 1)).Trim()
        if ($sparse -eq 'true') {
            throw 'Sparse checkouts are not covered by this candidate helper.'
        }

        # Hash index entries, not short-status text: staged content can change while the worktree bytes stay identical.
        $index = Invoke-HandoffGitRead @options -Arguments @('ls-files', '--stage', '-z')
        $untracked = Invoke-HandoffGitRead @options -Arguments @('ls-files', '--others', '--exclude-standard', '-z')
        $files = [Collections.Generic.List[object]]::new()
        $trackedPaths = [Collections.Generic.List[string]]::new()
        foreach ($entry in $index.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
            if ($entry -notmatch '(?s)^(?<mode>[0-9]{6}) [0-9a-f]+ (?<stage>[0-3])\t(?<path>.*)$') {
                throw 'Git returned an unsupported index entry.'
            }

            if ($Matches.mode -in @('160000', '040000') -or $Matches.stage -ne '0') {
                throw 'Submodule, sparse-index and unmerged index entries are not covered by this candidate helper.'
            }

            $trackedPaths.Add($Matches.path)
        }

        # Include file bytes and absence markers so edits, additions and deletions invalidate the same saved basis.
        $untrackedPaths = @($untracked.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
        foreach ($relative in @($trackedPaths.ToArray()) + $untrackedPaths) {
            $local = if ($IsWindows) { $relative.Replace('/', '\') } else { $relative }
            $filePath = Join-Path $Worktree $local
            Assert-HandoffUnlinkedPath $filePath
            $item = Get-HandoffItem $filePath
            if ($null -ne $item -and $item.PSIsContainer) {
                throw "Nested repositories or directory entries are not covered: '$relative'."
            }

            $hash = if ($null -eq $item) { $null } else { (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash }
            $files.Add([ordered]@{ path = $relative; present = $null -ne $item; sha256 = $hash })
        }

        $candidate.indexSha256 = Get-HandoffTextHash $index
        $basis = [ordered]@{
            worktree = if ($IsWindows) { $root.ToUpperInvariant() } else { $root }
            repository = if ($IsWindows) { $candidate.repository.ToUpperInvariant() } else { $candidate.repository }
            head = $candidate.head
            branch = $candidate.branch
            indexSha256 = $candidate.indexSha256
            files = $files.ToArray()
        }

        $candidate.fingerprint = Get-HandoffTextHash (ConvertTo-Json -InputObject $basis -Depth 8 -Compress)
        $candidate.trackedFileCount = $trackedPaths.Count
        $candidate.untrackedFileCount = $untrackedPaths.Count
        $candidate.state = 'observed'
    } catch {
        $candidate.state = 'unknown'
        $candidate.fingerprint = $null
        $candidate.reasons = @($_.Exception.Message)
    }

    return [pscustomobject]$candidate
}
