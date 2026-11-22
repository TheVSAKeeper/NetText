if (-not ('ComparisonWindowsProbe' -as [type]) -or -not ('ComparisonStreamCapture' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'WindowsProbe.cs')
}

function New-GuiFixture([string]$Path, [long]$TargetBytes, [string]$Prefix, [string]$Record, [string]$Suffix) {
    $encoding = [Text.UTF8Encoding]::new($false)
    $start = $encoding.GetBytes($Prefix)
    $end = $encoding.GetBytes($Suffix)
    $unit = $encoding.GetBytes($Record)
    $unitsPerBlock = [int][Math]::Max(1, [Math]::Floor(1MB / $unit.Length))
    $block = $encoding.GetBytes($Record * $unitsPerBlock)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try {
        $stream.Write($start)
        $total = [long][Math]::Floor(($TargetBytes - $start.Length - $end.Length) / $unit.Length)
        while ($total -gt 0) {
            $units = [int][Math]::Min($unitsPerBlock, $total)
            $stream.Write($block, 0, $units * $unit.Length)
            $total -= $units
        }
        $stream.Write($end)
        $stream.Flush($true)
    } finally {
        $stream.Dispose()
    }
    [ordered]@{
        name = [IO.Path]::GetFileName($Path)
        bytes = (Get-Item -LiteralPath $Path).Length
        sha256 = (Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant()
    }
}

function Get-RustTextOpenedMode([string]$RecentPath, [string]$ExpectedPath) {
    if (-not [IO.File]::Exists($RecentPath)) { return $null }
    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    try {
        $stream = [IO.File]::Open($RecentPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
        try {
            $reader = [IO.StreamReader]::new($stream)
            try { $content = $reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally { $stream.Dispose() }
    } catch [IO.IOException] {
        return $null
    }
    if ([string]::IsNullOrWhiteSpace($content)) { return $null }
    try {
        $entries = ConvertFrom-Json -InputObject $content -AsHashtable -NoEnumerate
    } catch {
        return $null
    }
    foreach ($entry in $entries) {
        if ($entry -isnot [Collections.IDictionary]) { continue }
        if (-not [string]::Equals([string]$entry.path, $ExpectedPath, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($entry.log -is [Collections.IDictionary]) {
            if ($entry.log.edit_pages -isnot [bool]) { return $null }
            if ($entry.log.edit_pages) { return 'paged-editor' }
            if ($entry.view_as_log -ceq $true) { return 'log' }
        } elseif ($entry.view_as_log -ceq $false -and $null -ne $entry.line_count -and $entry.line_count -is [ValueType] -and [long]$entry.line_count -gt 0) {
            return 'editor'
        }
        return $null
    }
    return $null
}

function Invoke-ComparisonGuiSample($Case, [string]$App, [string]$Exe, [int]$Iteration, [int]$Order, [int]$SampleMs, [string]$RunRoot, [string]$WarmNative) {
    $id = "$($Case.Name)-$Iteration-$App"
    $profile = Join-Path $RunRoot "profiles/$id"
    $isRust = $App -eq 'RustText'
    $settingsDir = Join-Path $profile $(if ($isRust) { 'dotnet-demo' } else { 'NetText' })
    New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
    $settings = if ($isRust) {
        '{"open_large_files_as_log":true,"auto_check_updates":false,"auto_check_updates_choice_made":true}'
    } else {
        '{"OpenLargeFilesAsLog":true,"AutoCheckUpdates":false,"AutoCheckUpdatesChoiceMade":true}'
    }
    [IO.File]::WriteAllText((Join-Path $settingsDir 'settings.json'), $settings)
    $recentPath = if ($isRust) { Join-Path $settingsDir 'recent-files.json' } else { '' }
    if ($isRust -and $Case.Cache -eq 'warm' -and $WarmNative) {
        Copy-Item -LiteralPath $WarmNative -Destination (Join-Path $profile 'native') -Recurse
    }

    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path -Parent $Exe
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($key in @($start.Environment.Keys)) {
        if ($key -match '^(NETTEXT_|RUSTTEXT_)') { [void]$start.Environment.Remove($key) }
    }
    if ($Case.Path) { $start.ArgumentList.Add($Case.Path) }
    if ($isRust) {
        $start.Environment['RUSTTEXT_PROFILE_DIR'] = $profile
        $start.Environment['RUSTTEXT_MEASURE'] = '1'
    } else {
        $start.Environment['LOCALAPPDATA'] = $profile
        $start.Environment['NETTEXT_MEASURE'] = '1'
        $start.Environment['NETTEXT_DISABLE_UPDATES'] = '1'
    }

    $requiresReady = [bool]$Case.Path
    $expectedMode = if (-not $requiresReady) { '' } elseif ($Case.Name -eq 'log') { 'log' } else { 'editor' }
    $readyPattern = ''
    $readyTitleFile = ''
    if ($requiresReady) {
        if ($Case.Name -eq 'log') {
            $readyPattern = 'first_log_frame_ms=\d+'
        } elseif (-not $isRust) {
            $readyPattern = 'document_ready_ms=\d+'
        }
        if ($isRust) {
            $readyTitleFile = [IO.Path]::GetFileName($Case.Path)
        }
    }

    $sample = [ordered]@{
        scenario = $Case.Name; cache = $Case.Cache; app = $App; iteration = $Iteration; order = $Order
        startedUtc = [datetime]::UtcNow.ToString('o'); status = 'failed'; windowMs = $null; responsiveMs = $null
        readyMs = $null; openedMode = $null; logMarkerMs = $null; memorySampleMs = $null; workingSetBytes = $null
        peakWorkingSetBytes = $null; privateBytes = $null; cpuMs = $null
        title = ''; stdout = ''; stderr = ''; exitCode = $null; killed = $false; error = $null
    }
    $process = $null
    $stdoutCapture = $null
    $stderrCapture = $null
    $window = [IntPtr]::Zero
    try {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $process = [Diagnostics.Process]::Start($start)
        $stdoutCapture = [ComparisonStreamCapture]::new($process.StandardOutput, $clock, $readyPattern)
        $stderrCapture = [ComparisonStreamCapture]::new($process.StandardError, $clock, '')
        while (-not $process.HasExited -and $clock.ElapsedMilliseconds -lt [Math]::Max(15000, $SampleMs + 5000)) {
            if ($null -eq $sample.memorySampleMs -and $clock.ElapsedMilliseconds -ge $SampleMs) {
                $process.Refresh()
                $sample.memorySampleMs = $clock.Elapsed.TotalMilliseconds
                $sample.workingSetBytes = $process.WorkingSet64
                $sample.peakWorkingSetBytes = $process.PeakWorkingSet64
                $sample.privateBytes = $process.PrivateMemorySize64
                $sample.cpuMs = $process.TotalProcessorTime.TotalMilliseconds
            }
            if ($window -eq [IntPtr]::Zero) {
                $window = [ComparisonWindowsProbe]::VisibleWindow($process.Id)
                if ($window -ne [IntPtr]::Zero) { $sample.windowMs = $clock.Elapsed.TotalMilliseconds }
            }
            if ($null -eq $sample.responsiveMs -and $window -ne [IntPtr]::Zero -and [ComparisonWindowsProbe]::Responds($window)) {
                $sample.responsiveMs = $clock.Elapsed.TotalMilliseconds
            }
            if ($requiresReady -and $null -eq $sample.readyMs) {
                if ($isRust) {
                    if ($window -ne [IntPtr]::Zero) {
                        $currentTitle = [ComparisonWindowsProbe]::Title($window)
                        $sample.title = $currentTitle
                        if ($currentTitle.Contains($readyTitleFile, [StringComparison]::OrdinalIgnoreCase)) {
                            if (-not $sample.openedMode) {
                                $sample.openedMode = Get-RustTextOpenedMode $recentPath $Case.Path
                            }
                            if ($Case.Name -eq 'log' -and $null -eq $sample.logMarkerMs) {
                                $sample.logMarkerMs = $stdoutCapture.ReadyMs
                            }
                            if ($sample.openedMode -ceq $expectedMode -and ($Case.Name -ne 'log' -or $null -ne $sample.logMarkerMs)) {
                                $sample.readyMs = $clock.Elapsed.TotalMilliseconds
                            }
                        }
                    }
                } else {
                    $observedReady = $stdoutCapture.ReadyMs
                    if ($null -ne $observedReady) {
                        $sample.readyMs = $observedReady
                        $sample.openedMode = $expectedMode
                        if ($Case.Name -eq 'log') { $sample.logMarkerMs = $observedReady }
                    }
                }
            }
            $wrongMode = $requiresReady -and $sample.openedMode -and $sample.openedMode -cne $expectedMode
            if ($null -ne $sample.memorySampleMs -and $null -ne $sample.responsiveMs -and (-not $requiresReady -or $null -ne $sample.readyMs -or $wrongMode)) { break }
            Start-Sleep -Milliseconds 5
        }
        if ($null -eq $sample.memorySampleMs -or $null -eq $sample.responsiveMs) {
            throw 'Process quit or timed out before the window and memory observations.'
        }
        if ($sample.memorySampleMs - $SampleMs -gt 100) { throw 'Memory sample missed its common sampling window.' }
        if ($window -ne [IntPtr]::Zero -and -not $sample.title) {
            $sample.title = [ComparisonWindowsProbe]::Title($window)
        }
        if ($requiresReady) {
            if ($null -eq $sample.readyMs) {
                throw "Expected mode '$expectedMode' during the observation window; observed '$($sample.openedMode)'."
            }
            if ($sample.readyMs -gt $sample.memorySampleMs) {
                throw "Expected mode '$expectedMode' before the memory sample; readyMs=$($sample.readyMs), memorySampleMs=$($sample.memorySampleMs)."
            }
        }
        $sample.status = 'ok'
    } catch {
        $sample.error = $_.Exception.Message
    } finally {
        if ($process) {
            if (-not $process.HasExited) {
                if ($window -ne [IntPtr]::Zero) { [ComparisonWindowsProbe]::Close($window) }
                if (-not $process.WaitForExit(1500)) {
                    $sample.killed = $true
                    try { $process.Kill($true) } catch {}
                    [void]$process.WaitForExit(3000)
                }
            }
            if ($process.HasExited) {
                $sample.exitCode = $process.ExitCode
            }
            $stdoutDone = if ($stdoutCapture) { $stdoutCapture.WaitForCompletion(3000) } else { $true }
            $stderrDone = if ($stderrCapture) { $stderrCapture.WaitForCompletion(3000) } else { $true }
            if ($stdoutCapture) { $sample.stdout = $stdoutCapture.GetText() }
            if ($stderrCapture) { $sample.stderr = $stderrCapture.GetText() }
            $process.Dispose()
            if (-not $stdoutDone -or -not $stderrDone) {
                if ($sample.status -eq 'ok') {
                    $sample.status = 'failed'
                    $sample.error = 'Timed out waiting for redirected stdout or stderr to close after process exit.'
                }
            }
        }
        if ($sample.status -eq 'ok' -and ($sample.killed -or $sample.exitCode -ne 0)) {
            $sample.status = 'failed'
            $sample.error = 'Process failed to shut down normally after sampling.'
        }
        if ($sample.status -eq 'ok' -and $Case.Name -ne 'log' -and $sample.stdout -match 'first_log_frame_ms=\d+') {
            $sample.status = 'failed'
            $sample.error = 'Unexpected log view.'
        }
        if ($sample.status -eq 'ok' -and $Case.Name -eq 'log' -and (-not $isRust) -and $sample.stdout -match 'document_ready_ms=\d+') {
            $sample.status = 'failed'
            $sample.error = 'Unexpected editor view.'
        }
        $sample | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $RunRoot "gui/$id.json") -Encoding utf8
    }
    if ($sample.status -ne 'ok') { throw "$id failed: $($sample.error)" }
    return $sample
}
