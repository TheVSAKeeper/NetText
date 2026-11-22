[CmdletBinding()]
param(
    [string]$RustVersion = '2.4.0',
    [string]$RustRevision,
    [string]$RustTextRepository = (Join-Path $PSScriptRoot '../../RustText'),
    [ValidateRange(2, 100)][int]$Iterations = 6,
    [ValidateRange(500, 60000)][int]$MemorySampleMs = 2000,
    [int]$OrderSeed = 20261121,
    [string]$OutputDirectory,
    [switch]$Smoke,
    [switch]$RenderFromRawJson,
    [string]$RawJsonPath,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($RenderFromRawJson) {
    if (-not $RawJsonPath) { $RawJsonPath = Join-Path $root 'docs/comparison-raw.json' }
    if (-not $ReportPath) { $ReportPath = Join-Path $root 'docs/comparison.md' }
    . (Join-Path $PSScriptRoot 'AvaloniaCompare/Report.ps1')
    $loaded = Get-Content -LiteralPath $RawJsonPath -Raw -Encoding utf8 | ConvertFrom-Json
    if ($loaded.schema -ne 'avalonia-comparison-v1' -or $loaded.status -ne 'complete') {
        throw "Unsupported or incomplete comparison JSON: $RawJsonPath"
    }
    $reportDir = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReportPath))
    $rawLink = [IO.Path]::GetRelativePath($reportDir, [IO.Path]::GetFullPath($RawJsonPath)).Replace('\', '/')
    Write-ComparisonMeasurementReport $loaded $ReportPath $rawLink
    Write-Host "Report written to: $ReportPath"
    return
}
if (-not $IsWindows) { throw 'The Avalonia comparison requires Windows x64 with an interactive desktop.' }
if ($Iterations % 2 -ne 0) { throw '-Iterations must be even to balance which application runs first.' }
$ci = [Globalization.CultureInfo]::InvariantCulture
if (-not (Test-Path -LiteralPath $RustTextRepository -PathType Container)) {
    throw "RustText repository was not found at '$RustTextRepository'. Clone https://github.com/Illson/RustText.git (with tag v$RustVersion) or pass -RustTextRepository <path>."
}
$RustTextRepository = (Resolve-Path -LiteralPath $RustTextRepository).Path
$pinnedRustRevisions = @{
    '2.3.0' = 'd5e53ed245becbbfe79e8f6f15ce16cda52ebd59'
    '2.4.0' = 'b2e93831443d4cbe72ff33d20cd8a61bace2a0d8'
}
$rustTargetRef = if ($RustRevision) {
    $RustRevision
} elseif ($pinnedRustRevisions.ContainsKey($RustVersion)) {
    $pinnedRustRevisions[$RustVersion]
} else {
    "refs/tags/v$RustVersion"
}
$rustRevisionLines = @(& git -C $RustTextRepository rev-parse --verify "${rustTargetRef}^{commit}" 2>$null)
$rustRevision = if ($LASTEXITCODE -eq 0 -and $rustRevisionLines.Count -gt 0) { $rustRevisionLines[0].Trim() } else { '' }
if (-not $rustRevision -and -not $RustRevision -and -not $pinnedRustRevisions.ContainsKey($RustVersion)) {
    $rustRevisionLines = @(& git -C $RustTextRepository rev-parse --verify "${RustVersion}^{commit}" 2>$null)
    if ($LASTEXITCODE -eq 0 -and $rustRevisionLines.Count -gt 0) { $rustRevision = $rustRevisionLines[0].Trim() }
}
if (-not $rustRevision) {
    throw "Unable to resolve RustText commit for '$rustTargetRef' in '$RustTextRepository'."
}
if (-not $OutputDirectory) {
    $runId = [datetime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputDirectory = Join-Path $root "artifacts/comparison/$runId"
}
$runRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $runRoot) { throw "Choose a new output directory: $runRoot" }
foreach ($folder in 'logs', 'gui', 'profiles', 'fixtures', 'harness') {
    New-Item -ItemType Directory -Path (Join-Path $runRoot $folder) | Out-Null
}
Write-Host "Comparison output: $runRoot"

function Get-HarnessSnapshotHashes([string]$BaseDir, [string[]]$Files) {
    @(foreach ($file in $Files) {
        [ordered]@{ file = $file; sha256 = (Get-FileHash -LiteralPath (Join-Path $BaseDir $file)).Hash.ToLowerInvariant() }
    })
}

function Assert-HarnessSnapshotUnchanged([string]$BaseDir, [string[]]$Files, $Expected) {
    $current = Get-HarnessSnapshotHashes $BaseDir $Files
    for ($i = 0; $i -lt $Expected.Count; $i++) {
        if ($current[$i].file -ne $Expected[$i].file -or $current[$i].sha256 -ne $Expected[$i].sha256) {
            throw "Harness snapshot file changed during the run: $($Expected[$i].file)"
        }
    }
}

function Invoke-ComparisonCommand([string]$Name, [string]$Command, [string[]]$Arguments) {
    $log = Join-Path $runRoot "logs/$Name.log"
    $Arguments | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot "logs/$Name.args.json") -Encoding utf8
    Write-Host "$Name ($log)"
    & $Command @Arguments *> $log
    $exitCode = $LASTEXITCODE
    Set-Content -LiteralPath (Join-Path $runRoot "logs/$Name.exitcode.txt") -Value $exitCode
    if ($exitCode -ne 0) { throw "$Name failed with exit $exitCode. See $log" }
    return $log
}

function Get-GitValue([string[]]$Arguments) {
    $value = & git -C $root @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed." }
    return $value
}

function Get-BinaryIdentity([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    [ordered]@{ file = $item.Name; bytes = $item.Length; version = $item.VersionInfo.ProductVersion; sha256 = (Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant() }
}

function Save-ComparisonData {
    $data | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $runRoot 'raw.json') -Encoding utf8
}

$data = [ordered]@{
    schema = 'avalonia-comparison-v1'; status = 'running'; generatedAt = [datetime]::UtcNow.ToString('o')
    smoke = [bool]$Smoke; iterations = $Iterations; memorySampleTargetMs = $MemorySampleMs; orderSeed = $OrderSeed
    rustVersion = $RustVersion; rustRevision = $rustRevision; netRevision = $null; netSourceTree = $null
    environment = $null; harness = $null; binaries = $null; guiFixtures = @(); coreFixtures = @()
    guiRuns = [Collections.Generic.List[object]]::new(); coreRuns = @(); error = $null
}
Save-ComparisonData

try {
    $harnessFiles = @(
        'Measure-AvaloniaComparison.ps1',
        'AvaloniaCompare/Gui.ps1',
        'AvaloniaCompare/WindowsProbe.cs',
        'AvaloniaCompare/Report.ps1',
        'AvaloniaCoreBench/AvaloniaCoreBench.csproj',
        'AvaloniaCoreBench/Program.cs',
        'AvaloniaCoreBench/RustTextStubs.cs'
    )
    $harnessSnapshot = Join-Path $runRoot 'harness'
    foreach ($file in $harnessFiles) {
        $target = Join-Path $harnessSnapshot $file
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $target
    }
    $data.harness = Get-HarnessSnapshotHashes $harnessSnapshot $harnessFiles
    . (Join-Path $harnessSnapshot 'AvaloniaCompare/Gui.ps1')
    . (Join-Path $harnessSnapshot 'AvaloniaCompare/Report.ps1')
    $netChanges = @(Get-GitValue @('status', '--porcelain', '--untracked-files=all', '--', 'src/NetText', 'native/win-x64/License.txt', 'tools/Build-NativeEditor.ps1'))
    if ($netChanges.Count -gt 0) {
        throw 'NetText application source or its native build inputs differ from HEAD. Commit or use a clean checkout for a comparable source identity.'
    }
    $data.netRevision = Get-GitValue @('rev-parse', 'HEAD')
    $data.netSourceTree = Get-GitValue @('rev-parse', 'HEAD:src/NetText')
    $cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
    $mem = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    $os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture
    $gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate)
    $volumeRoot = [IO.Path]::GetPathRoot($runRoot).TrimEnd('\')
    $volume = Get-CimInstance Win32_LogicalDisk | Where-Object DeviceID -eq $volumeRoot | Select-Object DeviceID, FileSystem, Size, FreeSpace
    $drives = @(Get-CimInstance Win32_DiskDrive | Select-Object Model, InterfaceType, MediaType, Size)
    $data.environment = [ordered]@{ cpu = $cpu; physicalMemoryBytes = $mem; os = $os; gpu = $gpu; volume = $volume; diskDrives = $drives; dotnetSdk = $null; powershell = $PSVersionTable.PSVersion.ToString() }

    $archive = Join-Path $runRoot 'rust-source.zip'
    $rustSource = Join-Path $runRoot 'rust-source'
    Invoke-ComparisonCommand 'rust-source' git @('-C', $RustTextRepository, 'archive', '--format=zip', "--output=$archive", $rustRevision) | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $rustSource
    Push-Location $root
    try {
        $netSdk = (Get-Content -LiteralPath (Invoke-ComparisonCommand 'net-sdk' dotnet @('--version')) -Raw).Trim()
    } finally { Pop-Location }
    Push-Location $rustSource
    try {
        $rustSdk = (Get-Content -LiteralPath (Invoke-ComparisonCommand 'rust-sdk' dotnet @('--version')) -Raw).Trim()
        if ($rustSdk -ne $netSdk -or $netSdk -notmatch '^11\.') { throw "Expected the same .NET 11 SDK for both builds, got $netSdk and $rustSdk." }
        $data.environment.dotnetSdk = $netSdk
        $rustPublish = Join-Path $runRoot 'rust-publish'
        Invoke-ComparisonCommand 'rust-publish' dotnet @('publish', 'src/RustText/RustText.csproj', '-c', 'Release', '-r', 'win-x64',
            "-p:SourceRevisionId=$rustRevision", '-o', $rustPublish) | Out-Null
    } finally { Pop-Location }

    $netPublish = Join-Path $runRoot 'net-publish'
    $corePublish = Join-Path $runRoot 'core-publish'
    $coreProject = Join-Path $harnessSnapshot 'AvaloniaCoreBench/AvaloniaCoreBench.csproj'
    $netProject = Join-Path $root 'src/NetText/NetText.csproj'
    Push-Location $root
    try {
        Invoke-ComparisonCommand 'net-publish' dotnet @('publish', 'src/NetText/NetText.csproj', '-c', 'Release', '-r', 'win-x64', '-o', $netPublish) | Out-Null
        Invoke-ComparisonCommand 'core-publish' dotnet @('publish', $coreProject, '-c', 'Release', '-r', 'win-x64',
            "-p:RustSource=$rustSource", "-p:RustVersion=$RustVersion", "-p:NetTextProject=$netProject", '-p:NetTextSingleExecutable=false', '-o', $corePublish) | Out-Null
    } finally { Pop-Location }
    Assert-HarnessSnapshotUnchanged $harnessSnapshot $harnessFiles $data.harness
    if ((Get-GitValue @('rev-parse', 'HEAD')) -ne $data.netRevision -or
        @(Get-GitValue @('status', '--porcelain', '--untracked-files=all', '--', 'src/NetText', 'native/win-x64/License.txt', 'tools/Build-NativeEditor.ps1')).Count -ne 0) {
        throw 'NetText source identity changed during the build.'
    }
    $executables = @{ NetText = (Join-Path $netPublish 'NetText.exe'); RustText = (Join-Path $rustPublish 'rusttext.exe') }
    $coreExe = Join-Path $corePublish 'AvaloniaCoreBench.exe'
    $data.binaries = [ordered]@{ netText = Get-BinaryIdentity $executables.NetText; rustText = Get-BinaryIdentity $executables.RustText; core = Get-BinaryIdentity $coreExe }
    if (-not $data.binaries.rustText.version.EndsWith("+$rustRevision", [StringComparison]::OrdinalIgnoreCase) -or
        -not $data.binaries.netText.version.EndsWith("+$($data.netRevision)", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Published application metadata does not match the source revisions.'
    }

    $fixtures = Join-Path $runRoot 'fixtures'
    $textFile = Join-Path $fixtures 'editor.txt'
    $jsonFile = Join-Path $fixtures 'records.json'
    $logFile = Join-Path $fixtures 'events.log'
    $textSize = if ($Smoke) { 64KB } else { 40MB }
    $jsonSize = if ($Smoke) { 64KB } else { 10MB }
    $logSize = if ($Smoke) { 2MB } else { 10GB }
    $coreSize = if ($Smoke) { 2MB } else { 64MB }
    $data.guiFixtures = @(
        New-GuiFixture $textFile $textSize '' "Editor plain text – русский текст and additional content for scrolling.`r`nLine without timestamps or level labels.`n" ''
        New-GuiFixture $jsonFile $jsonSize "[`n" "{`"id`":12345,`"description`":`"Обработка записи`",`"active`":true},`n" '{"id":0}]'
        New-GuiFixture $logFile $logSize '' "2026-11-21T00:30:00Z INFO Worker Сервис started`n2026-11-21T00:30:01Z WARN slow response, retry requested`r`n2026-11-21T00:30:02Z ERROR upstream reset`n" ''
    )
    Save-ComparisonData
    $cases = @(
        @{ Name = 'empty-cold'; Cache = 'cold'; Path = $null },
        @{ Name = 'empty'; Cache = 'warm'; Path = $null },
        @{ Name = 'text'; Cache = 'warm'; Path = $textFile },
        @{ Name = 'json'; Cache = 'warm'; Path = $jsonFile },
        @{ Name = 'log'; Cache = 'warm'; Path = $logFile }
    )
    foreach ($app in 'RustText', 'NetText') {
        $null = Invoke-ComparisonGuiSample @{ Name = 'warmup'; Cache = 'cold'; Path = $null } $app $executables[$app] 0 0 $MemorySampleMs $runRoot ''
    }
    $warmNative = Join-Path $runRoot 'profiles/warmup-0-RustText/native'
    $payload = @(Get-ChildItem -LiteralPath $warmNative -Filter '*.dll' -Recurse -File)
    if ($payload.Count -ne 3) { throw 'The warm RustText profile must contain the three extracted native DLLs.' }
    $data.binaries.rustText.extractedDllBytes = ($payload | Measure-Object -Property Length -Sum).Sum
    $random = [Random]::new($OrderSeed)
    for ($i = 1; $i -le $Iterations; $i++) {
        Write-Host "GUI pair block $i/$Iterations"
        foreach ($case in ($cases | Sort-Object { $random.Next() })) {
            $apps = if ($i % 2 -eq 1) { @('RustText', 'NetText') } else { @('NetText', 'RustText') }
            for ($order = 0; $order -lt 2; $order++) {
                $app = $apps[$order]
                $data.guiRuns.Add((Invoke-ComparisonGuiSample $case $app $executables[$app] $i ($order + 1) $MemorySampleMs $runRoot $warmNative))
            }
        }
        Save-ComparisonData
    }

    $coreLog = Invoke-ComparisonCommand 'core-measure' $coreExe @([string]$Iterations, [string]$coreSize, (Join-Path $fixtures 'core.log'))
    $data.coreRuns = @(foreach ($line in Get-Content -LiteralPath $coreLog) {
        $fields = $line.Split("`t")
        if ($fields[0] -eq 'sample' -and $fields.Count -eq 7) {
            [ordered]@{ scenario = $fields[1]; app = $fields[2]; iteration = [int]$fields[3]; order = [int]$fields[4]; ms = [double]::Parse($fields[5], $ci); managedBytes = [long]$fields[6] }
        } elseif ($fields[0] -eq 'fixture' -and $fields.Count -eq 4) {
            $data.coreFixtures += [ordered]@{ name = $fields[1]; bytes = [long]$fields[2]; sha256 = $fields[3].ToLowerInvariant() }
        } else { throw "Unexpected core benchmark output: $line" }
    })
    if ($data.coreRuns.Count -ne 10 * $Iterations * 2 -or $data.coreFixtures.Count -ne 3) { throw 'Incomplete core sample or fixture set.' }
    Assert-HarnessSnapshotUnchanged $harnessSnapshot $harnessFiles $data.harness
    Write-ComparisonMeasurementReport $data (Join-Path $runRoot 'report.md')
    $data.status = 'complete'
} catch {
    $data.status = 'failed'
    $data.error = $_.Exception.Message
    throw
} finally {
    Save-ComparisonData
}
Write-Host "Comparison completed: $runRoot"
