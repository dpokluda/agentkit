#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installer = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Install-AgentKit.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "AgentKit.SimpleTests-$([guid]::NewGuid().ToString('N'))"
$passed = 0
$skipped = 0
$failures = [Collections.Generic.List[string]]::new()
$canCreateLinks = $false

function New-Fixture {
    param([string]$Name)

    $root = Join-Path $testRoot $Name
    $content = Join-Path $root 'content'
    $homePath = Join-Path $root 'home'
    [void][IO.Directory]::CreateDirectory($homePath)
    foreach ($relative in @('agents', 'skills', 'instructions', 'scripts')) {
        [void][IO.Directory]::CreateDirectory((Join-Path $content $relative))
    }
    foreach ($file in @('GLOBAL.md', 'OPINIONS.md', 'VOICE.md')) {
        [IO.File]::WriteAllText((Join-Path $content "instructions\$file"), "# Synthetic $file")
    }
    $script = Join-Path $content 'scripts\Install-AgentKit.ps1'
    Copy-Item -LiteralPath $installer -Destination $script
    return [pscustomobject]@{ Root = $root; Content = $content; HomeRoot = $homePath; Installer = $script }
}

function Invoke-Fixture {
    param($Fixture, [hashtable]$Options = @{})

    & $Fixture.Installer -HomeRoot $Fixture.HomeRoot -Confirm:$false @Options
}

function Assert {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern)

    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_ }
    Assert ($null -ne $caught) "Expected error matching '$Pattern'."
    Assert ($caught.Exception.Message -like $Pattern) "Unexpected error: $($caught.Exception.Message)"
}

function Test-Case {
    param([string]$Name, [scriptblock]$Action, [switch]$NeedsLinks)

    if ($NeedsLinks -and -not $canCreateLinks) {
        $script:skipped++
        Write-Host "SKIP $Name (symbolic-link privilege unavailable)"
        return
    }
    try {
        & $Action
        $script:passed++
        Write-Host "PASS $Name"
    } catch {
        $script:failures.Add("${Name}: $($_.Exception.Message)")
        Write-Warning "FAIL ${Name}: $($_.Exception.Message)"
    }
}

function Remove-TestEntries {
    param([string]$Root)

    # Unlink test entries without traversing their targets; remove only empty real directories afterward.
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
                if ($child.Attributes -band [IO.FileAttributes]::Directory) {
                    [IO.Directory]::Delete($child.FullName)
                } else { [IO.File]::Delete($child.FullName) }
            } elseif ($child.PSIsContainer) {
                $pending.Push($child.FullName)
            } else { [IO.File]::Delete($child.FullName) }
        }
    }
    for ($index = $directories.Count - 1; $index -ge 0; $index--) {
        [IO.Directory]::Delete($directories[$index])
    }
}

$expected = [ordered]@{
    'agentkit' = ''
    '.copilot\agents' = 'agents'
    '.claude\agents' = 'agents'
    '.copilot\skills' = 'skills'
    '.claude\skills' = 'skills'
    '.copilot\copilot-instructions.md' = 'instructions\GLOBAL.md'
    '.copilot\OPINIONS.md' = 'instructions\OPINIONS.md'
    '.copilot\VOICE.md' = 'instructions\VOICE.md'
    '.claude\CLAUDE.md' = 'instructions\GLOBAL.md'
    '.claude\OPINIONS.md' = 'instructions\OPINIONS.md'
    '.claude\VOICE.md' = 'instructions\VOICE.md'
}

[void][IO.Directory]::CreateDirectory($testRoot)
try {
    Test-Case 'Only ContentRoot/HomeRoot and ordinary ShouldProcess options remain' {
        $command = Get-Command -Name $installer -CommandType ExternalScript
        foreach ($name in @('ContentRoot', 'HomeRoot', 'WhatIf', 'Confirm')) {
            Assert ($command.Parameters.ContainsKey($name)) "Missing parameter '$name'."
        }
        foreach ($name in @('BackupExisting', 'SwitchRoot', 'PrivateRoot', 'PublicRoot')) {
            Assert (-not $command.Parameters.ContainsKey($name)) "Obsolete parameter '$name' remains."
        }
    }

    Test-Case 'WhatIf uses the script-parent root, creates nothing, and restores cwd' {
        $fixture = New-Fixture 'preview'
        Push-Location $testRoot
        try {
            Invoke-Fixture $fixture @{ WhatIf = $true } | Out-Null
            Assert ((Get-Location).Path -eq $testRoot) 'Preview did not restore caller cwd.'
        } finally { Pop-Location }
        Assert (@(Get-ChildItem -LiteralPath $fixture.HomeRoot -Force).Count -eq 0) 'WhatIf created profile entries.'
    }

    foreach ($relative in @('instructions\GLOBAL.md', 'instructions\OPINIONS.md', 'instructions\VOICE.md', 'agents', 'skills')) {
        Test-Case "Missing required content stops before any links: $relative" {
            $fixture = New-Fixture ([guid]::NewGuid().ToString('N'))
            $path = Join-Path $fixture.Content $relative
            if ($relative.EndsWith('.md')) { [IO.File]::Delete($path) } else { [IO.Directory]::Delete($path) }
            Assert-Throws { Invoke-Fixture $fixture @{ WhatIf = $true } } '*Required AgentKit*not found*'
            Assert (@(Get-ChildItem -LiteralPath $fixture.HomeRoot -Force).Count -eq 0) 'Missing content caused changes.'
        }
    }

    foreach ($relative in @('agentkit', '.copilot\OPINIONS.md', '.claude\VOICE.md', '.copilot\agents')) {
        Test-Case "Any occupied file link location stops without overwrite: $relative" {
            $fixture = New-Fixture ([guid]::NewGuid().ToString('N'))
            $path = Join-Path $fixture.HomeRoot $relative
            [void][IO.Directory]::CreateDirectory((Split-Path $path -Parent))
            [IO.File]::WriteAllText($path, 'keep this content')
            Assert-Throws { Invoke-Fixture $fixture @{ WhatIf = $true } } '*Link location*already occupied*'
            Assert ([IO.File]::ReadAllText($path) -eq 'keep this content') 'Occupied file changed.'
            Assert (@(Get-ChildItem -LiteralPath $fixture.HomeRoot -Force).Count -eq 1) 'Preflight created other entries.'
        }
    }

    Test-Case 'An occupied directory is preserved' {
        $fixture = New-Fixture 'occupied-directory'
        $path = Join-Path $fixture.HomeRoot 'agentkit'
        [void][IO.Directory]::CreateDirectory($path)
        Assert-Throws { Invoke-Fixture $fixture } '*Link location*already occupied*'
        Assert (Test-Path -LiteralPath $path -PathType Container) 'Occupied directory was replaced.'
    }

    Test-Case 'Recorded link calls are alias-first, root-level, and resolve relative content before changing cwd' {
        $fixture = New-Fixture 'recorded-calls'
        $selected = New-Fixture 'selected-content'
        $calls = [Collections.Generic.List[object]]::new()
        function New-Item {
            [CmdletBinding(SupportsShouldProcess = $true)]
            param([string]$ItemType, [string]$Path, [string]$Target)
            if ($ItemType -eq 'SymbolicLink') {
                $calls.Add([pscustomobject]@{ Location = $Path; PointsTo = $Target; Cwd = (Get-Location).Path })
            } else { Microsoft.PowerShell.Management\New-Item @PSBoundParameters }
        }
        Push-Location $fixture.Root
        try {
            $relative = [IO.Path]::GetRelativePath($fixture.Root, $selected.Content)
            Invoke-Fixture $fixture @{ ContentRoot = $relative } | Out-Null
            Assert ((Get-Location).Path -eq $fixture.Root) 'Successful call did not restore caller cwd.'
        } finally { Pop-Location }
        Assert ($calls.Count -eq $expected.Count) 'Incorrect mapping count.'
        $index = 0
        foreach ($mapping in $expected.GetEnumerator()) {
            $pointsTo = if ($mapping.Value) { Join-Path (Join-Path $fixture.HomeRoot 'agentkit') $mapping.Value } else { $selected.Content }
            Assert ($calls[$index].Location -eq $mapping.Key) "Wrong link location/order: $($mapping.Key)."
            Assert ($calls[$index].PointsTo -eq $pointsTo) "Wrong points-to path: $($mapping.Key)."
            Assert ($calls[$index].Cwd -eq $fixture.HomeRoot) 'Links were not created from HomeRoot.'
            $index++
        }
    }

    Test-Case 'A New-Item failure stops and restores caller cwd' {
        $fixture = New-Fixture 'creation-error'
        function New-Item {
            [CmdletBinding(SupportsShouldProcess = $true)]
            param([string]$ItemType, [string]$Path, [string]$Target)
            throw 'Synthetic New-Item failure'
        }
        $before = (Get-Location).Path
        Assert-Throws { Invoke-Fixture $fixture } '*Synthetic New-Item failure*'
        Assert ((Get-Location).Path -eq $before) 'Failure did not restore caller cwd.'
    }

    Test-Case 'HomeRoot must already exist' {
        $fixture = New-Fixture 'missing-home'
        [IO.Directory]::Delete($fixture.HomeRoot)
        Assert-Throws { Invoke-Fixture $fixture @{ WhatIf = $true } } '*Cannot find path*because it does not exist*'
        Assert (-not (Test-Path -LiteralPath $fixture.HomeRoot)) 'Installer created a missing HomeRoot.'
    }

    if ($IsWindows) {
        Test-Case 'Even an already-correct directory junction is an occupied link location' {
            $fixture = New-Fixture 'occupied-junction'
            New-Item -ItemType Junction -Path (Join-Path $fixture.HomeRoot 'agentkit') -Target $fixture.Content | Out-Null
            Assert-Throws { Invoke-Fixture $fixture @{ WhatIf = $true } } '*Link location*already occupied*'
            Assert ((Get-Item -LiteralPath (Join-Path $fixture.HomeRoot 'agentkit') -Force).LinkType -eq 'Junction') 'Junction changed.'
        }

        Test-Case 'A broken directory junction is also occupied' {
            $fixture = New-Fixture 'broken-junction'
            $missing = Join-Path $fixture.Root 'removed-directory'
            [void][IO.Directory]::CreateDirectory($missing)
            $path = Join-Path $fixture.HomeRoot 'agentkit'
            New-Item -ItemType Junction -Path $path -Target $missing | Out-Null
            [IO.Directory]::Delete($missing)
            Assert-Throws { Invoke-Fixture $fixture @{ WhatIf = $true } } '*Link location*already occupied*'
            Assert ((Get-Item -LiteralPath $path -Force).LinkType -eq 'Junction') 'Broken junction changed.'
        }

        Test-Case 'A linked profile parent is not traversed to create links' {
            $fixture = New-Fixture 'linked-profile'
            $outside = Join-Path $fixture.Root 'outside'
            [void][IO.Directory]::CreateDirectory($outside)
            New-Item -ItemType Junction -Path (Join-Path $fixture.HomeRoot '.copilot') -Target $outside | Out-Null
            Assert-Throws { Invoke-Fixture $fixture @{ WhatIf = $true } } '*Link location*linked*parent*'
            Assert (@(Get-ChildItem -LiteralPath $outside -Force).Count -eq 0) 'Installer wrote through a profile parent link.'
        }
    }

    $probeSource = Join-Path $testRoot 'probe-source'
    [IO.File]::WriteAllText($probeSource, 'probe')
    try {
        New-Item -ItemType SymbolicLink -Path (Join-Path $testRoot 'probe-link') -Target $probeSource | Out-Null
        $canCreateLinks = $true
    } catch {
        if ($_.Exception.Message -notmatch '(?i)privilege|not permitted|permission denied|access.*denied') { throw }
        Write-Warning "Real symbolic-link cases will be skipped: $($_.Exception.Message) No elevation requested."
    }

    Test-Case 'Real links use the home alias and root instructions; rerunning stops' -NeedsLinks {
        $fixture = New-Fixture 'real-links'
        Invoke-Fixture $fixture | Out-Null
        foreach ($mapping in $expected.GetEnumerator()) {
            $pointsTo = if ($mapping.Value) { Join-Path (Join-Path $fixture.HomeRoot 'agentkit') $mapping.Value } else { $fixture.Content }
            $item = Get-Item -LiteralPath (Join-Path $fixture.HomeRoot $mapping.Key) -Force
            Assert ($item.LinkType -eq 'SymbolicLink' -and [string]$item.Target -eq $pointsTo) "Incorrect real link: $($mapping.Key)."
        }
        foreach ($unwanted in @('.copilot\instructions', '.claude\rules', '.copilot\AGENTS.md')) {
            Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.HomeRoot $unwanted))) "Unwanted entry: $unwanted."
        }
        Assert-Throws { Invoke-Fixture $fixture } '*Link location*already occupied*'
    }

    Test-Case 'Broken symbolic links are occupied and preserved' -NeedsLinks {
        $fixture = New-Fixture 'broken-link'
        $missing = Join-Path $fixture.Root 'removed-file'
        [IO.File]::WriteAllText($missing, 'temporary')
        [void][IO.Directory]::CreateDirectory((Join-Path $fixture.HomeRoot '.copilot'))
        $path = Join-Path $fixture.HomeRoot '.copilot\OPINIONS.md'
        New-Item -ItemType SymbolicLink -Path $path -Target $missing | Out-Null
        [IO.File]::Delete($missing)
        Assert-Throws { Invoke-Fixture $fixture } '*Link location*already occupied*'
        Assert ((Get-Item -LiteralPath $path -Force).LinkType -eq 'SymbolicLink') 'Broken link was removed.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.HomeRoot 'agentkit'))) 'Preflight created an alias.'
    }

    Test-Case 'A later runtime error leaves earlier links and restores cwd without rollback' -NeedsLinks {
        $fixture = New-Fixture 'partial-links'
        function New-Item {
            [CmdletBinding(SupportsShouldProcess = $true)]
            param([string]$ItemType, [string]$Path, [string]$Target)
            if ($ItemType -eq 'SymbolicLink' -and $Path -eq '.copilot\agents') { throw 'Synthetic later failure' }
            Microsoft.PowerShell.Management\New-Item @PSBoundParameters
        }
        $before = (Get-Location).Path
        Assert-Throws { Invoke-Fixture $fixture } '*Synthetic later failure*'
        Assert ((Get-Location).Path -eq $before) 'Partial failure did not restore cwd.'
        Assert ((Get-Item -LiteralPath (Join-Path $fixture.HomeRoot 'agentkit') -Force).LinkType -eq 'SymbolicLink') 'Earlier link was rolled back.'
    }
} finally {
    Remove-TestEntries -Root $testRoot
}

if ($failures.Count) { throw "$($failures.Count) test(s) failed:`n$($failures -join "`n")" }
Write-Host "$passed checks passed; $skipped real symbolic-link cases skipped. Temporary fixtures removed."
