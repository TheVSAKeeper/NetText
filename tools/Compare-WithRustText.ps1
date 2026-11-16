[CmdletBinding()]
param(
    [string]$RustTextRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\RustText')).Path,
    [switch]$IncludeBuildBench,
    [switch]$IncludeCleanBuild,
    [switch]$Include10GiB,
    [switch]$Include20GiB,
    [switch]$IncludeE2E,
    [switch]$IncludeCoreBench,
    [int]$Iterations = 30,
    [int]$E2EIterations = 5,
    [switch]$SkipStartup,
    [switch]$SkipTests,
    [switch]$SkipRustBuild,
    [switch]$RebuildNet,
    [ValidateSet('All', 'NetText', 'RustText')][string]$AppFilter = 'All',
    [string[]]$E2EScenarios = @('10GiB E2E', 'Mixed 40MB+10GiB', '20GiB E2E'),
    [switch]$IndexOnly,
    [string]$FixtureDir = '',
    [string]$PartialJsonOutPath = '',
    [switch]$WriteReport,
    [switch]$RenderFromRawJson,
    [string]$ReportPath = (Join-Path $PSScriptRoot '..\docs\comparison.md'),
    [string]$RawJsonPath = (Join-Path $PSScriptRoot '..\docs\comparison-raw.json')
)

$ErrorActionPreference = 'Stop'
if ($WriteReport -and -not $RenderFromRawJson -and ($SkipStartup -or $SkipTests -or $SkipRustBuild -or $IndexOnly -or $AppFilter -ne 'All' -or @($E2EScenarios).Count -lt 3)) {
    throw "-WriteReport requires a full comparison (-AppFilter All without -SkipStartup/-SkipTests/-SkipRustBuild/-IndexOnly or filtered -E2EScenarios)."
}
$ci = [System.Globalization.CultureInfo]::InvariantCulture
$netRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$netProj = Join-Path $netRoot 'src\NetText\NetText.csproj'
$netTestProj = Join-Path $netRoot 'tests\NetText.Tests\NetText.Tests.csproj'
$coreBenchProj = Join-Path $netRoot 'tools\CoreBench\CoreBench.csproj'
$netPubDir = Join-Path $netRoot 'src\NetText\bin\Release\net11.0-windows\win-x64\publish'
$netExe = Join-Path $netPubDir 'NetText.exe'
$rustExe = Join-Path $RustTextRoot 'target\release\rusttext.exe'
$rustCoreBenchExe = Join-Path $RustTextRoot 'target\release\core_bench.exe'

function Measure-CommandTime([scriptblock]$Action) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $Action
    $sw.Stop()
    return [math]::Round($sw.Elapsed.TotalSeconds, 2)
}

function Get-Percentile([double[]]$Values, [double]$Pct) {
    if (-not $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 1) { return $sorted[0] }
    $rank = ($Pct / 100.0) * ($sorted.Count - 1)
    $lo = [int][Math]::Floor($rank)
    $hi = [int][Math]::Ceiling($rank)
    $frac = $rank - $lo
    return $sorted[$lo] * (1.0 - $frac) + $sorted[$hi] * $frac
}

function FmtNum([double]$v) {
    return $v.ToString('0.00', $ci)
}

function FmtMedP95Int([double[]]$Values) {
    if (-not $Values -or $Values.Count -eq 0) { return '–' }
    $med = [math]::Round((Get-Percentile $Values 50))
    if ($Values.Count -eq 1) { return "$med" }
    $p95 = [math]::Round((Get-Percentile $Values 95))
    return "$med / $p95"
}

function FmtMedP95MiB([double[]]$Values) {
    if (-not $Values -or $Values.Count -eq 0) { return '–' }
    $med = Get-Percentile $Values 50
    if ($Values.Count -eq 1) { return $med.ToString('0.00', $ci) }
    $p95 = Get-Percentile $Values 95
    return "$($med.ToString('0.00', $ci)) / $($p95.ToString('0.00', $ci))"
}

function Get-CodebaseStats {
    $netFiles = Get-ChildItem (Join-Path $netRoot 'src') -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    $netLines = ($netFiles | Get-Content | Measure-Object -Line).Lines
    $netTestFiles = Get-ChildItem (Join-Path $netRoot 'tests') -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    $netTestLines = ($netTestFiles | Get-Content | Measure-Object -Line).Lines
    $rustFiles = Get-ChildItem (Join-Path $RustTextRoot 'src') -Recurse -Filter '*.rs'
    $rustLines = ($rustFiles | Get-Content | Measure-Object -Line).Lines
    $rustTestFiles = Get-ChildItem (Join-Path $RustTextRoot 'tests') -Recurse -Filter '*.rs'
    $rustTestLines = ($rustTestFiles | Get-Content | Measure-Object -Line).Lines
    $rustSizeMiB = if (Test-Path $rustExe) { [math]::Round((Get-Item $rustExe).Length / 1MB, 2) } else { 0.0 }
    $netExeMiB = if (Test-Path $netExe) { [math]::Round((Get-Item $netExe).Length / 1MB, 2) } else { 0.0 }
    $netScintillaMiB = if (Test-Path (Join-Path $netPubDir 'Scintilla.dll')) { [math]::Round((Get-Item (Join-Path $netPubDir 'Scintilla.dll')).Length / 1MB, 2) } else { 0.0 }
    $netLexillaMiB = if (Test-Path (Join-Path $netPubDir 'Lexilla.dll')) { [math]::Round((Get-Item (Join-Path $netPubDir 'Lexilla.dll')).Length / 1MB, 2) } else { 0.0 }
    $netTotalMiB = [math]::Round(($netExeMiB + $netScintillaMiB + $netLexillaMiB), 2)
    return [ordered]@{
        netSrcFiles = $netFiles.Count
        netSrcLines = $netLines
        netTestFiles = $netTestFiles.Count
        netTestLines = $netTestLines
        rustSrcFiles = $rustFiles.Count
        rustSrcLines = $rustLines
        rustTestFiles = $rustTestFiles.Count
        rustTestLines = $rustTestLines
        rustSizeMiB = $rustSizeMiB
        netExeMiB = $netExeMiB
        netScintillaMiB = $netScintillaMiB
        netLexillaMiB = $netLexillaMiB
        netTotalMiB = $netTotalMiB
    }
}

function Format-MarkdownTable([string[]]$Headers, [bool[]]$RightAlign, [object[][]]$TableRows) {
    $cols = $Headers.Count
    $widths = [int[]]::new($cols)
    for ($c = 0; $c -lt $cols; $c++) {
        $widths[$c] = [Math]::Max(3, $Headers[$c].Length)
    }
    foreach ($row in $TableRows) {
        for ($c = 0; $c -lt $cols; $c++) {
            $val = [string]$row[$c]
            if ($val.Length -gt $widths[$c]) { $widths[$c] = $val.Length }
        }
    }
    $out = [System.Collections.Generic.List[string]]::new()
    $hdrCells = for ($c = 0; $c -lt $cols; $c++) {
        if ($RightAlign[$c]) { $Headers[$c].PadLeft($widths[$c]) } else { $Headers[$c].PadRight($widths[$c]) }
    }
    $sepCells = for ($c = 0; $c -lt $cols; $c++) {
        if ($RightAlign[$c]) { ('-' * ($widths[$c] - 1)) + ':' } else { '-' * $widths[$c] }
    }
    $out.Add('| ' + ($hdrCells -join ' | ') + ' |')
    $out.Add('| ' + ($sepCells -join ' | ') + ' |')
    foreach ($row in $TableRows) {
        $cells = for ($c = 0; $c -lt $cols; $c++) {
            $val = [string]$row[$c]
            if ($RightAlign[$c]) { $val.PadLeft($widths[$c]) } else { $val.PadRight($widths[$c]) }
        }
        $out.Add('| ' + ($cells -join ' | ') + ' |')
    }
    return $out
}

function Get-AdvantageLabel([double]$RustMed, [double]$NetMed, [string]$Kind) {
    if ($Kind -eq 'time') {
        $diff = [math]::Abs($RustMed - $NetMed)
        if ($diff -le 2.0) { return 'Паритет' }
        if ($RustMed -le 0 -or $NetMed -le 0) { return 'Паритет' }
        $ratio = if ($NetMed -lt $RustMed) { $RustMed / $NetMed } else { $NetMed / $RustMed }
        if ($ratio -lt 1.05) { return 'Паритет' }
        if ($NetMed -lt $RustMed) { return "NetText в $($ratio.ToString('0.00', $ci))×" }
        return "RustText в $($ratio.ToString('0.00', $ci))×"
    }
    if ($RustMed -le 0 -or $NetMed -le 0) { return '–' }
    $ratioMem = if ($NetMed -lt $RustMed) { $RustMed / $NetMed } else { $NetMed / $RustMed }
    if ($ratioMem -lt 1.02) { return 'Паритет' }
    if ($NetMed -lt $RustMed) {
        $pct = [math]::Round((1.0 - ($NetMed / $RustMed)) * 100.0, 1)
        return "NetText: −$($pct.ToString('0.0', $ci))% ($($ratioMem.ToString('0.00', $ci))×)"
    }
    $pctR = [math]::Round((1.0 - ($RustMed / $NetMed)) * 100.0, 1)
    return "RustText: −$($pctR.ToString('0.0', $ci))% ($($ratioMem.ToString('0.00', $ci))×)"
}

function Get-SampleProp([object]$Sample, [string]$PropName) {
    if ($PropName.StartsWith('Metrics.')) {
        $mKey = $PropName.Substring(8)
        $mObj = $Sample.Metrics
        if ($mObj -is [System.Collections.IDictionary]) {
            if ($mObj.Contains($mKey)) { return [double]$mObj[$mKey] }
            return $null
        }
        $p = $mObj.PSObject.Properties[$mKey]
        if ($null -ne $p) { return [double]$p.Value }
        return $null
    }
    $p2 = $Sample.PSObject.Properties[$PropName]
    if ($null -ne $p2) { return [double]$p2.Value }
    return $null
}

function Write-ComparisonReport([object]$RawObj, [System.Collections.IDictionary]$CodeStats) {
    $envInfo = $RawObj.environment
    $deps = $RawObj.dependencies
    $bt = $RawObj.buildsAndTests
    $iterCount = $RawObj.iterations
    $e2eCount = $RawObj.e2eIterations
    $reportDir = Split-Path -Parent $ReportPath
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('# Сравнение NetText (.NET 11 NativeAOT) и RustText (Rust + GPUI Kit)')
    [void]$sb.AppendLine()
    $genTime = if ($RawObj.generatedAt -is [datetime] -or $RawObj.generatedAt -is [datetimeoffset]) { $RawObj.generatedAt.ToString('yyyy-MM-dd HH:mm:ss') } else { [datetimeoffset]::Parse([string]$RawObj.generatedAt, $ci).ToString('yyyy-MM-dd HH:mm:ss') }
    [void]$sb.AppendLine(("Дата замера: {0} | Повторов старта: {1} (чередующийся порядок, Median / P95) | Прогонов E2E: {2}" -f $genTime, $iterCount, $e2eCount))
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## 1. Среда, исходники и воспроизводимость')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("- **ОС**: $($envInfo.os).")
    [void]$sb.AppendLine("- **Инструментарий**: .NET SDK ``$($envInfo.dotnetSdk)`` (NativeAOT + VC++), ``$($envInfo.rustc)``, ``$($envInfo.cargo)``, ``Scintilla.dll`` $($envInfo.scintillaVersion) / ``Lexilla.dll`` $($envInfo.lexillaVersion).")
    $envShaLine = ''
    if ($envInfo.netTextCommit) {
        $netDirtyTxt = if ($envInfo.netTextDirty) { ' (dirty)' } else { '' }
        $envShaLine = "; ``NetText`` ветка ``$($envInfo.netTextBranch)`` (коммит ``$($envInfo.netTextCommit)``$netDirtyTxt)"
    }
    [void]$sb.AppendLine("- **Состояние репозиториев и бинарников**: ``RustText`` ветка ``$($envInfo.rustTextBranch)`` (коммит ``$($envInfo.rustTextCommit)``$(if ($envInfo.rustTextDirty) { ', с несохранёнными правками' }))$envShaLine; SHA-256 измеренных бинарников и сырые данные каждого запуска сохранены в [comparison-raw.json](comparison-raw.json).")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## 2. Код, зависимости, тесты и сборка')
    [void]$sb.AppendLine()

    $devRows = [System.Collections.Generic.List[object[]]]::new()
    $devRows.Add(@('Файлы кода (`src`)', "$($CodeStats.rustSrcFiles) ($($CodeStats.rustSrcLines) строк)", "$($CodeStats.netSrcFiles) ($($CodeStats.netSrcLines) строк)"))
    $devRows.Add(@('Файлы тестов (`tests`)', "$($CodeStats.rustTestFiles) ($($CodeStats.rustTestLines) строк)", "$($CodeStats.netTestFiles) ($($CodeStats.netTestLines) строк)"))
    $devRows.Add(@('Прямые зависимости манифеста', "$($deps.rustDirectRuntime) runtime (+$($deps.rustDirectBuild) build) в ``Cargo.toml``", "$($deps.netNugetPackages) NuGet + ``Scintilla.dll`` / ``Lexilla.dll`` + Win32 P/Invoke"))
    $devRows.Add(@('Транзитивный граф (Windows x64)', "$($deps.rustWinTransitiveRuntime) runtime / $($deps.rustWinGraphTotal) всего ($($deps.rustCargoLockAllTargets) в ``Cargo.lock``)", 'Стандартная библиотека .NET 11 BCL (ILC NativeAOT) + prebuilt Win32 DLL'))
    $devRows.Add(@('Покрытие модульными тестами', "$($bt.rustBinTestPassed) тестов (``rusttext``, всего $($bt.rustTotalTestPassed) по таргетам; $($bt.rustTestSec) с)", "$($bt.netTestPassed) тестов (``NetText.Tests``; $($bt.netTestSec) с)"))
    if ($null -ne $bt.rustIncrementalBuildSec -and $null -ne $bt.netIncrementalBuildSec) {
        $devRows.Add(@('Инкрементальная сборка (после правки кода)', "$(FmtNum ([double]$bt.rustIncrementalBuildSec)) с", "$(FmtNum ([double]$bt.netIncrementalBuildSec)) с"))
    }
    if ($null -ne $bt.rustCleanBuildSec -and $null -ne $bt.netCleanBuildSec) {
        $devRows.Add(@('Чистая сборка (`cargo clean` / `dotnet clean`)', "$(FmtNum ([double]$bt.rustCleanBuildSec)) с", "$(FmtNum ([double]$bt.netCleanBuildSec)) с"))
    }
    $devRows.Add(@('Релизная поставка на диске', "$(FmtNum ([double]$CodeStats.rustSizeMiB)) MiB (1 файл ``rusttext.exe``)", "$(FmtNum ([double]$CodeStats.netTotalMiB)) MiB (``NetText.exe`` $(FmtNum ([double]$CodeStats.netExeMiB)) + ``Scintilla.dll`` $(FmtNum ([double]$CodeStats.netScintillaMiB)) + ``Lexilla.dll`` $(FmtNum ([double]$CodeStats.netLexillaMiB)))"))

    foreach ($line in (Format-MarkdownTable @('Параметр', 'RustText', 'NetText') @($false, $true, $true) $devRows.ToArray())) {
        [void]$sb.AppendLine($line)
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('- Длительность прогона тестов отражает только время выполнения разных наборов проверок и не измеряет качество или полноту тестирования.')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## 3. Матрица функционального соответствия и границы вывода')
    [void]$sb.AppendLine()
    $parityRows = @(
        @('Текстовый редактор (вкладки, UTF-8/UTF-16/Win-1251, подсветка)', 'Есть (GPUI EditorState + syntect)', 'Есть (Win32 Scintilla + Lexilla)'),
        @('Поиск и замена (обычный/regex, по вкладкам, `$1`)', 'Есть (`search.rs`)', 'Есть (`EditorSearch.cs`, UTF-8 и string пути)'),
        @('Форматирование JSON (`Ctrl+Shift+J`, строгий парсер)', 'Есть (`serde_json`)', 'Есть (`Utf8JsonReader` + строгий суррогат-валидатор)'),
        @('Большие логи 10–20+ GiB (индекс, jump %, scroll, search/cancel, trim)', 'Есть (`log_io` + GPUI)', 'Есть (`LogIo` + Win32 GDI `LogViewport` + LRU cache)'),
        @('Сессия, недавние, SingleInstance IPC', 'Есть (GPUI / Named Pipe)', 'Есть (Named Pipe v2 + журнал сессии)'),
        @('Автообновление из GitHub Releases + SHA-256', 'Есть (`updater.rs`)', 'Нет (не портировалось)')
    )
    foreach ($line in (Format-MarkdownTable @('Подсистема', 'RustText', 'NetText') @($false, $false, $false) $parityRows)) {
        [void]$sb.AppendLine($line)
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('- **Границы вывода**: замеры относятся к этим двум конкретным реализациям (Win32/GDI + Scintilla/DirectWrite в `NetText` против GPUI Kit / DirectX в `RustText`) и ничего не доказывают о преимуществе .NET над Rust или наоборот.')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("## 4. Запуск и первичная готовность (Median / P95 по $iterCount прогонам)")
    [void]$sb.AppendLine()

    $startupScenarios = @($RawObj.rawStartupRuns | ForEach-Object { $_.Scenario } | Select-Object -Unique)
    $startupTable = [System.Collections.Generic.List[object[]]]::new()
    foreach ($sc in $startupScenarios) {
        $rRuns = @($RawObj.rawStartupRuns | Where-Object { $_.Scenario -eq $sc -and $_.App -eq 'RustText' })
        $nRuns = @($RawObj.rawStartupRuns | Where-Object { $_.Scenario -eq $sc -and $_.App -eq 'NetText' })
        $readyProp = if ($sc -eq 'empty') { 'Metrics.first_frame_presented_ms' } elseif ($sc -match '10GiB|20GiB') { 'Metrics.first_log_frame_ms' } else { 'Metrics.document_ready_ms' }
        $metricSpecs = @(
            @{ Label = 'Окно, мс'; Prop = 'WindowMs'; Kind = 'time'; Unit = 'мс' },
            @{ Label = '1-й кадр, мс'; Prop = 'Metrics.first_frame_presented_ms'; Kind = 'time'; Unit = 'мс' },
            @{ Label = 'Готовность, мс'; Prop = $readyProp; Kind = 'time'; Unit = 'мс' },
            @{ Label = 'Working Set, MiB'; Prop = 'WorkingSetMiB'; Kind = 'mem'; Unit = 'MiB' },
            @{ Label = 'Peak WS, MiB'; Prop = 'PeakWorkingSetMiB'; Kind = 'mem'; Unit = 'MiB' },
            @{ Label = 'Private Memory, MiB'; Prop = 'PrivateMiB'; Kind = 'mem'; Unit = 'MiB' }
        )
        foreach ($ms in $metricSpecs) {
            if ($sc -eq 'empty' -and $ms.Label -eq 'Готовность, мс') { continue }
            $rVals = [double[]]@($rRuns | ForEach-Object { Get-SampleProp $_ $ms.Prop } | Where-Object { $null -ne $_ })
            $nVals = [double[]]@($nRuns | ForEach-Object { Get-SampleProp $_ $ms.Prop } | Where-Object { $null -ne $_ })
            if ($rVals.Count -eq 0 -or $nVals.Count -eq 0) { continue }
            $rMed = Get-Percentile $rVals 50
            $nMed = Get-Percentile $nVals 50
            $rText = if ($ms.Kind -eq 'time') { "$(FmtMedP95Int $rVals) мс" } else { "$(FmtMedP95MiB $rVals) MiB" }
            $nText = if ($ms.Kind -eq 'time') { "$(FmtMedP95Int $nVals) мс" } else { "$(FmtMedP95MiB $nVals) MiB" }
            $adv = Get-AdvantageLabel $rMed $nMed $ms.Kind
            $startupTable.Add(@($sc, $ms.Label, $rText, $nText, $adv))
        }
    }
    foreach ($line in (Format-MarkdownTable @('Сценарий', 'Метрика', 'RustText', 'NetText', 'Преимущество') @($false, $false, $true, $true, $false) $startupTable.ToArray())) {
        [void]$sb.AppendLine($line)
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('- Значения указаны в формате `Median / P95`; «Паритет» определяется эвристическими порогами скрипта (`|Δ| ≤ 2 мс` или `< 1.05×` по медиане времени; `< 1.02×` по памяти) без формального теста статистической эквивалентности.')
    [void]$sb.AppendLine('- `Готовность, мс` измеряет завершение первичной отрисовки нативными для каждого стека событиями: в `NetText` – синхронный возврат `WM_PAINT`/`BitBlt` и `Scintilla SCN_PAINTED`; в `RustText` – вложенный GPUI `window.on_next_frame` (выполняется строго после `window.draw()` и `window.present()` на следующем тике кадра и потому включает дополнительный цикл кадра/презентации порядка одного или нескольких интервалов VSync под нагрузкой).')

    $e2eScenarios = @($RawObj.rawE2ERuns | ForEach-Object { $_.Scenario } | Select-Object -Unique)
    if ($e2eScenarios.Count -gt 0) {
        $starSuffix = if ($RawObj.partialRerunNote) { '*' } else { '' }
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("## 5. Полная нагрузка E2E и профиль памяти во времени (Median / P95 по $e2eCount прогонам)$starSuffix")
        [void]$sb.AppendLine()
        $e2eTable = [System.Collections.Generic.List[object[]]]::new()
        $e2eSpecs = @(
            @{ Label = '1-й кадр лога, мс'; Prop = 'Metrics.first_log_frame_ms'; Kind = 'time' },
            @{ Label = 'Полный индекс, мс'; Prop = 'Metrics.index_ready_ms'; Kind = 'time' },
            @{ Label = 'Jump 50%, мс'; Prop = 'Metrics.jump_50pct_ms'; Kind = 'time' },
            @{ Label = 'Jump EOF, мс'; Prop = 'Metrics.jump_eof_ms'; Kind = 'time' },
            @{ Label = 'Scroll Prev, мс'; Prop = 'Metrics.scroll_prev_ms'; Kind = 'time' },
            @{ Label = 'Search EOF, мс'; Prop = 'Metrics.search_eof_ms'; Kind = 'time' },
            @{ Label = 'Cancel поиска, мс'; Prop = 'Metrics.cancel_search_ms'; Kind = 'time' },
            @{ Label = 'Private (1-й кадр), MiB'; Prop = 'Metrics.mem_first_frame_priv_mib'; Kind = 'mem' },
            @{ Label = 'Private (индекс), MiB'; Prop = 'Metrics.mem_index_ready_priv_mib'; Kind = 'mem' },
            @{ Label = 'Private (после E2E), MiB'; Prop = 'Metrics.mem_after_ops_priv_mib'; Kind = 'mem' },
            @{ Label = 'Working Set (после E2E), MiB'; Prop = 'Metrics.mem_after_ops_ws_mib'; Kind = 'mem' },
            @{ Label = 'Peak WS (за E2E), MiB'; Prop = 'Metrics.mem_after_ops_peak_ws_mib'; Kind = 'mem' }
        )
        foreach ($sc in $e2eScenarios) {
            $rRuns = @($RawObj.rawE2ERuns | Where-Object { $_.Scenario -eq $sc -and $_.App -eq 'RustText' })
            $nRuns = @($RawObj.rawE2ERuns | Where-Object { $_.Scenario -eq $sc -and $_.App -eq 'NetText' })
            foreach ($es in $e2eSpecs) {
                $rVals = [double[]]@($rRuns | ForEach-Object { Get-SampleProp $_ $es.Prop } | Where-Object { $null -ne $_ })
                $nVals = [double[]]@($nRuns | ForEach-Object { Get-SampleProp $_ $es.Prop } | Where-Object { $null -ne $_ })
                if ($rVals.Count -eq 0 -or $nVals.Count -eq 0) { continue }
                $rMed = Get-Percentile $rVals 50
                $nMed = Get-Percentile $nVals 50
                $rText = if ($es.Kind -eq 'time') { "$(FmtMedP95Int $rVals) мс" } else { "$(FmtMedP95MiB $rVals) MiB" }
                $nText = if ($es.Kind -eq 'time') { "$(FmtMedP95Int $nVals) мс" } else { "$(FmtMedP95MiB $nVals) MiB" }
                $adv = Get-AdvantageLabel $rMed $nMed $es.Kind
                $e2eTable.Add(@($sc, $es.Label, $rText, $nText, $adv))
            }
        }
        foreach ($line in (Format-MarkdownTable @('Сценарий', 'Метрика', 'RustText', 'NetText', 'Преимущество') @($false, $false, $true, $true, $false) $e2eTable.ToArray())) {
            [void]$sb.AppendLine($line)
        }
        $eofRuns = @($RawObj.rawE2ERuns)
        $eofMissing = 0
        $eofMismatch = 0
        foreach ($er in $eofRuns) {
            $eofC = Get-SampleProp $er 'Metrics.search_eof_complete'
            $eofH = Get-SampleProp $er 'Metrics.search_eof_hits'
            if ($null -eq $eofC -or $null -eq $eofH) {
                $eofMissing++
            } elseif ([double]$eofC -ne 1 -or [double]$eofH -ne 1) {
                $eofMismatch++
            }
        }
        $eofSummaryText = if ($eofRuns.Count -gt 0 -and $eofMissing -eq 0 -and $eofMismatch -eq 0) {
            "Во всех прогонах поиска ``E2E_EOF_SENTINEL`` (10 GiB и 20 GiB) для обеих программ по сырым метрикам проверены ``search_eof_complete=1`` и ``search_eof_hits=1``."
        } elseif ($eofMissing -gt 0) {
            "Примечание: в сырых данных для $eofMissing из $($eofRuns.Count) E2E-запусков отсутствуют ``search_eof_complete``/``search_eof_hits`` (данные собраны до добавления проверки полноты EOF-поиска)."
        } else {
            "Внимание: в сырых данных для $eofMismatch из $($eofRuns.Count) E2E-запусков ``search_eof_complete``/``search_eof_hits`` не равны 1."
        }
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("- Сценарий ``Mixed 40MB+10GiB`` открывает одновременно вкладку редактора на 40 MB (``Scintilla`` / GPUI ``EditorState``) и лог на 10 GiB; P95 по $e2eCount прогонам приведён как описательная оценка. $eofSummaryText")
        if ($RawObj.partialRerunNote) {
            [void]$sb.AppendLine("- ``*`` $($RawObj.partialRerunNote)")
        }
    }

    $netCoreLines = @($RawObj.coreBench.netText)
    $rustCoreLines = @($RawObj.coreBench.rustText)
    if ($netCoreLines.Count -gt 0 -or $rustCoreLines.Count -gt 0) {
        $cbStarSuffix = if ($RawObj.partialRerunNote) { '*' } else { '' }
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("## 6. Эквивалентные алгоритмические бенчмарки ядра (``CoreBench`` vs ``core_bench``)$cbStarSuffix")
        [void]$sb.AppendLine()
        $rustMap = @{}
        foreach ($line in $rustCoreLines) {
            $parts = $line -split ':', 2
            if ($parts.Count -eq 2) { $rustMap[$parts[0].Trim()] = $parts[1].Trim() }
        }
        $netMap = @{}
        $orderedOps = [System.Collections.Generic.List[string]]::new()
        foreach ($line in $netCoreLines) {
            $parts = $line -split ':', 2
            if ($parts.Count -eq 2) {
                $k = $parts[0].Trim()
                $netMap[$k] = $parts[1].Trim()
                $orderedOps.Add($k)
            }
        }
        foreach ($k in $rustMap.Keys) {
            if (-not $netMap.ContainsKey($k)) { $orderedOps.Add($k) }
        }
        $netOnlyLabels = @{
            'JsonFormatUtf8(40MiB)' = 'Только NetText (UTF-8, managed LOH)'
            'JsonFormatUtf8Native(40MiB)' = 'Только NetText (UTF-8 NativeMemory, 0.00 MiB managed)'
            'EditorSearchUtf8(40MiB)' = 'Только NetText (UTF-8, 0.00 MiB alloc)'
            'EditorReplaceAllUtf8(40MiB)' = 'Только NetText (UTF-8, ArrayPool byte[])'
        }
        $cbRows = [System.Collections.Generic.List[object[]]]::new()
        foreach ($op in $orderedOps) {
            $rVal = if ($rustMap.ContainsKey($op)) { $rustMap[$op] } else { '–' }
            $nVal = if ($netMap.ContainsKey($op)) { $netMap[$op] } else { '–' }
            $adv = if ($netOnlyLabels.ContainsKey($op)) { $netOnlyLabels[$op] } else { 'Только одна реализация' }
            if ($rVal -match '^([0-9]+(?:\.[0-9]+)?)\s*ms') {
                $rMs = [double]::Parse($Matches[1], $ci)
                if ($nVal -match '^([0-9]+(?:\.[0-9]+)?)\s*ms') {
                    $nMs = [double]::Parse($Matches[1], $ci)
                    if ([math]::Abs($rMs - $nMs) -le 0.5) {
                        $adv = 'Паритет (<1 мс)'
                    } elseif ($nMs -lt $rMs) {
                        $adv = "NetText в $(($rMs / $nMs).ToString('0.00', $ci))×"
                    } else {
                        $adv = "RustText в $(($nMs / $rMs).ToString('0.00', $ci))×"
                    }
                }
            }
            $cbRows.Add(@($op, 'Время выполнения', $rVal, $nVal, $adv))
        }
        foreach ($line in (Format-MarkdownTable @('Операция', 'Метрика', 'RustText (`core_bench`)', 'NetText (`CoreBench`)', 'Преимущество') @($false, $false, $false, $false, $false) $cbRows.ToArray())) {
            [void]$sb.AppendLine($line)
        }
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('- `CoreBench` / `core_bench` выполняются однократно на прогретом файловом кэше; порог паритета – `|Δ| ≤ 0.5 мс`.')
    }

    [void]$sb.AppendLine()
    [void]$sb.AppendLine('Повторный запуск замеров: `pwsh -NoProfile -File tools\Compare-WithRustText.ps1 -Iterations 30 -E2EIterations 5 -IncludeBuildBench -IncludeCleanBuild -Include10GiB -Include20GiB -IncludeE2E -IncludeCoreBench -WriteReport`.')
    [System.IO.File]::WriteAllText($ReportPath, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
    Write-Host "Report written to: $ReportPath"
}

if ($RenderFromRawJson) {
    if (-not (Test-Path $RawJsonPath)) { throw "Raw JSON not found: $RawJsonPath" }
    $loadedRaw = Get-Content $RawJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($loadedRaw.PSObject.Properties['codeStats'] -and $null -ne $loadedRaw.codeStats) {
        $codeStats = [ordered]@{}
        foreach ($prop in $loadedRaw.codeStats.PSObject.Properties) {
            $codeStats[$prop.Name] = $prop.Value
        }
    } else {
        $codeStats = Get-CodebaseStats
    }
    Write-ComparisonReport $loadedRaw $codeStats
    return
}

Write-Host '=== 0. Environment & Toolchain ==='
$osInfo = Get-CimInstance Win32_OperatingSystem | Select-Object -First 1
$dotnetVer = (& dotnet --version).Trim()
$rustcVer = (& rustc --version).Trim()
$cargoVer = (& cargo --version).Trim()
Push-Location $RustTextRoot
try {
    $rustGitSha = (& git rev-parse --short HEAD).Trim()
    $rustGitBranch = (& git branch --show-current).Trim()
    $rustGitDirty = [bool](& git status --porcelain)
} finally { Pop-Location }

$scintillaInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $netRoot 'native\win-x64\Scintilla.dll'))
$lexillaInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $netRoot 'native\win-x64\Lexilla.dll'))

Write-Host '=== 1. Tests & Build ==='
$script:netTestCount = 0
$netTestSec = 0.0
if (-not $SkipTests -and $AppFilter -ne 'RustText') {
    $netTestSec = Measure-CommandTime {
        $netTestOut = & dotnet test $netTestProj -c Release --no-restore 2>&1
        $netTestOut | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "NetText tests failed" }
        foreach ($line in $netTestOut) {
            if ("$line" -match '(?:^|,\s*)пройдено\s+(\d+)|Passed:\s*(\d+)') {
                $n = if ($Matches[1]) { [int]$Matches[1] } else { [int]$Matches[2] }
                $script:netTestCount += $n
            }
        }
    }
    $netTestCount = $script:netTestCount
    if ($netTestCount -le 0) { throw "Failed to parse NetText test count" }
}
$netTestCount = $script:netTestCount

$script:rustBinTestCount = 0
$script:rustTotalTestCount = 0
$rustTestSec = 0.0
if (-not $SkipTests -and -not $SkipRustBuild -and $AppFilter -ne 'NetText') {
    $rustTestSec = Measure-CommandTime {
        Push-Location $RustTextRoot
        try {
            $rustTestOut = & cargo test --locked 2>&1
            $rustTestOut | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "RustText tests failed" }
            foreach ($line in $rustTestOut) {
                if ("$line" -match 'test result: ok\.\s+(\d+)\s+passed') {
                    $cnt = [int]$Matches[1]
                    $script:rustTotalTestCount += $cnt
                    if ($cnt -gt $script:rustBinTestCount) { $script:rustBinTestCount = $cnt }
                }
            }
        } finally { Pop-Location }
    }
    $rustBinTestCount = $script:rustBinTestCount
    $rustTotalTestCount = $script:rustTotalTestCount
    if ($rustBinTestCount -le 0) { throw "Failed to parse RustText test count" }
}

if (($AppFilter -ne 'RustText') -and ($RebuildNet -or -not (Test-Path $netExe))) {
    dotnet publish $netProj -c Release -r win-x64 --no-restore -v minimal | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "NetText publish failed" }
}
if ((-not $SkipRustBuild) -and ($AppFilter -ne 'NetText') -and (-not (Test-Path $rustExe) -or -not (Test-Path $rustCoreBenchExe))) {
    Push-Location $RustTextRoot
    try {
        cargo build --release --locked --bin rusttext --bin core_bench | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Initial RustText release build failed" }
    } finally { Pop-Location }
}

$netIncBuildSec = $null
$rustIncBuildSec = $null
if ($IncludeBuildBench) {
    Write-Host '--- Incremental Build after Real Source Edit ---'
    $netProgramPath = Join-Path $netRoot 'src\NetText\Program.cs'
    $netOrigBytes = [System.IO.File]::ReadAllBytes($netProgramPath)
    try {
        $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
        $touched = [System.IO.File]::ReadAllText($netProgramPath, $utf8NoBom) + "`r`n// inc_bench_" + [guid]::NewGuid().ToString('N')
        [System.IO.File]::WriteAllText($netProgramPath, $touched, $utf8NoBom)
        $netIncBuildSec = Measure-CommandTime {
            dotnet publish $netProj -c Release -r win-x64 --no-restore -v minimal | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "NetText incremental publish failed" }
        }
    } finally {
        [System.IO.File]::WriteAllBytes($netProgramPath, $netOrigBytes)
    }

    $rustMainPath = Join-Path $RustTextRoot 'src\main.rs'
    $rustOrigBytes = [System.IO.File]::ReadAllBytes($rustMainPath)
    Push-Location $RustTextRoot
    try {
        try {
            $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
            $touched = [System.IO.File]::ReadAllText($rustMainPath, $utf8NoBom) + "`n// inc_bench_" + [guid]::NewGuid().ToString('N')
            [System.IO.File]::WriteAllText($rustMainPath, $touched, $utf8NoBom)
            $rustIncBuildSec = Measure-CommandTime {
                cargo build --release --locked --bin rusttext --bin core_bench | Out-Host
                if ($LASTEXITCODE -ne 0) { throw "RustText incremental build failed" }
            }
        } finally {
            [System.IO.File]::WriteAllBytes($rustMainPath, $rustOrigBytes)
        }
    } finally { Pop-Location }

    # Rebuild once after restore so final artifacts match exact clean source
    dotnet publish $netProj -c Release -r win-x64 --no-restore -v quiet | Out-Null
    Push-Location $RustTextRoot
    try { cargo build --release --locked --bin rusttext --bin core_bench --quiet | Out-Null } finally { Pop-Location }
}

$netCleanBuildSec = $null
$rustCleanBuildSec = $null
if ($IncludeCleanBuild) {
    Write-Host '--- Clean Build Benchmark ---'
    dotnet clean $netProj -c Release -v quiet | Out-Null
    $netCleanBuildSec = Measure-CommandTime {
        dotnet publish $netProj -c Release -r win-x64 --no-restore -v minimal | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "NetText clean publish failed" }
    }

    Push-Location $RustTextRoot
    try {
        cargo clean | Out-Null
        $rustCleanBuildSec = Measure-CommandTime {
            cargo build --release --locked --bin rusttext --bin core_bench | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "RustText clean build failed" }
        }
    } finally { Pop-Location }
}

$rustCratesLock = 0
$rustWinRuntimeTotal = 0
$rustWinBuildDirect = 0
$rustWinGraphTotal = 0
$rustDirectRuntime = 0
$rustDirectBuild = 0
$netNugetCount = 0
$rustSizeMiB = 0.0
$netExeMiB = 0.0
$netScintillaMiB = 0.0
$netLexillaMiB = 0.0
$netTotalMiB = 0.0
if (-not ($SkipStartup -and -not $WriteReport)) {
    Write-Host '=== 2. Codebase, Dependencies & Binary Metrics ==='
    $netFiles = Get-ChildItem (Join-Path $netRoot 'src') -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    $netLines = ($netFiles | Get-Content | Measure-Object -Line).Lines
    $netTestFiles = Get-ChildItem (Join-Path $netRoot 'tests') -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    $netTestLines = ($netTestFiles | Get-Content | Measure-Object -Line).Lines

    $rustFiles = Get-ChildItem (Join-Path $RustTextRoot 'src') -Recurse -Filter '*.rs'
    $rustLines = ($rustFiles | Get-Content | Measure-Object -Line).Lines
    $rustTestFiles = Get-ChildItem (Join-Path $RustTextRoot 'tests') -Recurse -Filter '*.rs'
    $rustTestLines = ($rustTestFiles | Get-Content | Measure-Object -Line).Lines

    $rustCratesLock = (Select-String -Path (Join-Path $RustTextRoot 'Cargo.lock') -Pattern '^\[\[package\]\]').Count
    Push-Location $RustTextRoot
    try {
        $metaJson = cargo metadata --locked --filter-platform x86_64-pc-windows-msvc --format-version 1 | ConvertFrom-Json
        $rootId = $metaJson.resolve.root
        $rootPkg = $metaJson.packages | Where-Object { $_.id -eq $rootId }
        $rustDirectRuntime = @($rootPkg.dependencies | Where-Object { $null -eq $_.kind -or $_.kind -eq 'normal' }).Count
        $rustDirectBuild = @($rootPkg.dependencies | Where-Object { $_.kind -eq 'build' }).Count
        $nodeMap = @{}
        foreach ($n in $metaJson.resolve.nodes) { $nodeMap[$n.id] = $n }
        $normalSet = [System.Collections.Generic.HashSet[string]]::new()
        $buildSet = [System.Collections.Generic.HashSet[string]]::new()
        $queue = [System.Collections.Generic.Queue[string]]::new()
        $visited = [System.Collections.Generic.HashSet[string]]::new()
        $queue.Enqueue($rootId)
        $visited.Add($rootId) | Out-Null
        while ($queue.Count -gt 0) {
            $cur = $queue.Dequeue()
            $node = $nodeMap[$cur]
            foreach ($dep in $node.deps) {
                foreach ($dk in $dep.dep_kinds) {
                    if ($null -eq $dk.kind -or $dk.kind -eq 'normal') {
                        $normalSet.Add($dep.pkg) | Out-Null
                        if ($visited.Add($dep.pkg)) { $queue.Enqueue($dep.pkg) }
                    } elseif ($dk.kind -eq 'build') {
                        $buildSet.Add($dep.pkg) | Out-Null
                    }
                }
            }
        }
        $rustWinRuntimeTotal = $normalSet.Count
        $rustWinBuildDirect = $buildSet.Count
        $rustWinGraphTotal = @($metaJson.resolve.nodes).Count
    } finally { Pop-Location }

    $netCsprojXml = [xml](Get-Content $netProj)
    $netNugetCount = @($netCsprojXml.SelectNodes('//PackageReference')).Count
    $rustSizeMiB = [math]::Round((Get-Item $rustExe).Length / 1MB, 2)
    $netExeMiB = [math]::Round((Get-Item $netExe).Length / 1MB, 2)
    $netScintillaMiB = [math]::Round((Get-Item (Join-Path $netPubDir 'Scintilla.dll')).Length / 1MB, 2)
    $netLexillaMiB = [math]::Round((Get-Item (Join-Path $netPubDir 'Lexilla.dll')).Length / 1MB, 2)
    $netTotalBytes = (Get-Item $netExe).Length + (Get-Item (Join-Path $netPubDir 'Scintilla.dll')).Length + (Get-Item (Join-Path $netPubDir 'Lexilla.dll')).Length
    $netTotalMiB = [math]::Round($netTotalBytes / 1MB, 2)
}

Write-Host '=== 3. Runtime & Memory Benchmarks ==='
$benchRoot = Join-Path $env:TEMP ('nettext_compare_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $benchRoot | Out-Null
$fixtureRoot = if ($FixtureDir) { $FixtureDir } else { $benchRoot }
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
$runRust = $AppFilter -in @('All', 'RustText')
$runNet = $AppFilter -in @('All', 'NetText')

function Get-MetricsFromLines([string[]]$Lines) {
    $map = @{}
    foreach ($line in $Lines) {
        if (-not $line) { continue }
        foreach ($m in [regex]::Matches($line, '([a-z0-9_]+)=([0-9]+(?:\.[0-9]+)?)')) {
            $map[$m.Groups[1].Value] = [double]::Parse($m.Groups[2].Value, $ci)
        }
    }
    return $map
}

function Invoke-AppBench {
    param(
        [string]$App,
        [string]$Scenario,
        [int]$RunIndex,
        [string]$Order,
        [string]$Exe,
        [string[]]$FileArgs,
        [string]$WaitMarkerRegex = '',
        [int]$SettleMs = 250,
        [int]$TimeoutMs = 15000,
        [switch]$E2E,
        [string]$E2ENeedle = '',
        [switch]$IsWarmup
    )

    $prof = Join-Path $benchRoot ('prof_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $prof | Out-Null

    $psi = [System.Diagnostics.ProcessStartInfo]::new($Exe)
    if ($FileArgs -and $FileArgs.Count -gt 0) {
        $psi.Arguments = ($FileArgs | ForEach-Object { "`"$_`"" }) -join ' '
    }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.Environment['LOCALAPPDATA'] = $prof
    $psi.Environment['RUSTTEXT_MEASURE'] = '1'
    $psi.Environment['NETTEXT_MEASURE'] = '1'
    if ($E2E) {
        $psi.Environment['RUSTTEXT_E2E_PROBE'] = '1'
        $psi.Environment['NETTEXT_E2E_PROBE'] = '1'
        if ($E2ENeedle) {
            $psi.Environment['RUSTTEXT_E2E_NEEDLE'] = $E2ENeedle
            $psi.Environment['NETTEXT_E2E_NEEDLE'] = $E2ENeedle
        }
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $stderrTask = $p.StandardError.ReadToEndAsync()
    $stdoutLines = [System.Collections.Generic.List[string]]::new()
    $lineTask = $p.StandardOutput.ReadLineAsync()

    $winMs = $null
    $markerAtMs = $null
    while (-not $p.HasExited -and $sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if ($null -eq $winMs -and $p.MainWindowHandle -ne 0) {
            $winMs = $sw.ElapsedMilliseconds
        }
        while ($null -ne $lineTask -and $lineTask.IsCompleted) {
            $ln = $lineTask.GetAwaiter().GetResult()
            if ($null -eq $ln) {
                $lineTask = $null
                break
            }
            $stdoutLines.Add($ln)
            if ($WaitMarkerRegex -and $null -eq $markerAtMs -and ($ln -match $WaitMarkerRegex)) {
                $markerAtMs = $sw.ElapsedMilliseconds
            }
            $lineTask = $p.StandardOutput.ReadLineAsync()
        }
        if ($E2E) {
            if ($null -ne $markerAtMs) { break }
        } else {
            if ($null -ne $winMs -and (-not $WaitMarkerRegex -or $null -ne $markerAtMs)) {
                $baseMs = if ($null -ne $markerAtMs) { $markerAtMs } else { $winMs }
                if (($sw.ElapsedMilliseconds - $baseMs) -ge $SettleMs) {
                    break
                }
            }
        }
        Start-Sleep -Milliseconds 5
    }

    if ($null -eq $winMs) {
        $winMs = $sw.ElapsedMilliseconds
    }

    $ws = -1.0
    $peakWs = -1.0
    $priv = -1.0
    if (-not $p.HasExited) {
        $p.Refresh()
        $ws = [math]::Round($p.WorkingSet64 / 1MB, 2)
        $peakWs = [math]::Round($p.PeakWorkingSet64 / 1MB, 2)
        $priv = [math]::Round($p.PrivateMemorySize64 / 1MB, 2)
        $p.CloseMainWindow() | Out-Null
        if (-not $p.WaitForExit(3000)) { $p.Kill() }
    } else {
        $p.WaitForExit() | Out-Null
    }

    while ($null -ne $lineTask) {
        $ln = $lineTask.GetAwaiter().GetResult()
        if ($null -eq $ln) { break }
        $stdoutLines.Add($ln)
        if ($WaitMarkerRegex -and $null -eq $markerAtMs -and ($ln -match $WaitMarkerRegex)) {
            $markerAtMs = $sw.ElapsedMilliseconds
        }
        $lineTask = $p.StandardOutput.ReadLineAsync()
    }
    $stderrText = ''
    try { $stderrText = $stderrTask.GetAwaiter().GetResult().Trim() } catch {}

    $metrics = Get-MetricsFromLines $stdoutLines.ToArray()
    if ($peakWs -lt 0 -and $metrics.ContainsKey('mem_after_ops_peak_ws_mib')) {
        $peakWs = $metrics['mem_after_ops_peak_ws_mib']
        $ws = $metrics['mem_after_ops_ws_mib']
        $priv = $metrics['mem_after_ops_priv_mib']
    }

    if (-not $IsWarmup) {
        if ($WaitMarkerRegex -and $null -eq $markerAtMs) {
            throw "[$App $Scenario run=$RunIndex] Missing required readiness marker matching '$WaitMarkerRegex' within ${TimeoutMs}ms. Stdout: '$($stdoutLines -join '; ')'. Stderr: '$stderrText'"
        }
        if ($ws -lt 0 -or $priv -lt 0) {
            throw "[$App $Scenario run=$RunIndex] Process terminated before memory snapshot (exitCode=$($p.ExitCode)). Stdout: '$($stdoutLines -join '; ')'"
        }
    }

    [pscustomobject]@{
        App = $App
        Scenario = $Scenario
        Case = "$App $Scenario"
        RunIndex = $RunIndex
        Order = $Order
        WindowMs = $winMs
        WorkingSetMiB = $ws
        PeakWorkingSetMiB = $peakWs
        PrivateMiB = $priv
        Metrics = $metrics
    }
}

try {
    $txtFile = Join-Path $fixtureRoot 'bench_40mb.txt'
    $jsonFile = Join-Path $fixtureRoot 'bench_40mb.json'
    $needTxt = (-not $SkipStartup) -or ($IncludeE2E -and ($E2EScenarios -contains 'Mixed 40MB+10GiB'))
    if ($needTxt -and -not (Test-Path $txtFile)) {
        $lineTxt = "2026-11-16T21:30:00Z INFO rusttext::bench line=12345 message=hello_world_test_data`r`n"
        $sb1 = [System.Text.StringBuilder]::new(42 * 1024 * 1024)
        for ($i = 0; $i -lt 500000; $i++) { [void]$sb1.Append($lineTxt) }
        [System.IO.File]::WriteAllText($txtFile, $sb1.ToString(), [System.Text.UTF8Encoding]::new($false))
    }
    if (-not $SkipStartup -and -not (Test-Path $jsonFile)) {
        $lineJson = "  {`"id`": 12345, `"level`": `"INFO`", `"msg`": `"hello_world_test_data`", `"ok`": true},`r`n"
        $sb2 = [System.Text.StringBuilder]::new(42 * 1024 * 1024)
        [void]$sb2.Append("[`r`n")
        for ($i = 0; $i -lt 500000; $i++) { [void]$sb2.Append($lineJson) }
        [void]$sb2.Append("  {`"id`": 0}`r`n]")
        [System.IO.File]::WriteAllText($jsonFile, $sb2.ToString(), [System.Text.UTF8Encoding]::new($false))
    }

    $rawStartupSamples = [System.Collections.Generic.List[object]]::new()
    $rawE2ESamples = [System.Collections.Generic.List[object]]::new()

    function Summarize-Samples([string]$Name, [object[]]$Samples, [string]$ReadyKey) {
        $winExt = [double[]]@($Samples | ForEach-Object { [double]$_.WindowMs })
        $firstFrame = [double[]]@($Samples | Where-Object { $_.Metrics.ContainsKey('first_frame_presented_ms') } | ForEach-Object { $_.Metrics['first_frame_presented_ms'] })
        $ready = [double[]]@($Samples | Where-Object { $_.Metrics.ContainsKey($ReadyKey) } | ForEach-Object { $_.Metrics[$ReadyKey] })
        $ws = [double[]]@($Samples | ForEach-Object { [double]$_.WorkingSetMiB })
        $peak = [double[]]@($Samples | ForEach-Object { [double]$_.PeakWorkingSetMiB })
        $priv = [double[]]@($Samples | ForEach-Object { [double]$_.PrivateMiB })

        if ($firstFrame.Count -ne $Samples.Count) {
            throw "[$Name] Missing first_frame_presented_ms in $($Samples.Count - $firstFrame.Count) of $($Samples.Count) runs"
        }
        if ($ready.Count -ne $Samples.Count) {
            throw "[$Name] Missing $ReadyKey in $($Samples.Count - $ready.Count) of $($Samples.Count) runs"
        }

        [pscustomobject]@{
            Case = $Name
            Runs = $Samples.Count
            WindowMsText = FmtMedP95Int $winExt
            FirstFrameText = FmtMedP95Int $firstFrame
            ReadyMsText = FmtMedP95Int $ready
            WorkingSetText = FmtMedP95MiB $ws
            PeakWsText = FmtMedP95MiB $peak
            PrivateText = FmtMedP95MiB $priv
        }
    }

    function Invoke-AlternatingSeries {
        param(
            [string]$Scenario,
            [string[]]$FileArgs,
            [string]$WaitMarkerRegex,
            [string]$ReadyKey,
            [int]$SettleMs,
            [int]$Count
        )
        $rustSamples = [System.Collections.Generic.List[object]]::new()
        $netSamples = [System.Collections.Generic.List[object]]::new()
        for ($i = 0; $i -lt $Count; $i++) {
            $order = if (($i % 2) -eq 0) { 'RustFirst' } else { 'NetFirst' }
            $rS = $null
            $nS = $null
            if ($order -eq 'RustFirst') {
                if ($runRust) { $rS = Invoke-AppBench -App 'RustText' -Scenario $Scenario -RunIndex ($i + 1) -Order $order -Exe $rustExe -FileArgs $FileArgs -WaitMarkerRegex $WaitMarkerRegex -SettleMs $SettleMs }
                if ($runNet) { $nS = Invoke-AppBench -App 'NetText' -Scenario $Scenario -RunIndex ($i + 1) -Order $order -Exe $netExe -FileArgs $FileArgs -WaitMarkerRegex $WaitMarkerRegex -SettleMs $SettleMs }
            } else {
                if ($runNet) { $nS = Invoke-AppBench -App 'NetText' -Scenario $Scenario -RunIndex ($i + 1) -Order $order -Exe $netExe -FileArgs $FileArgs -WaitMarkerRegex $WaitMarkerRegex -SettleMs $SettleMs }
                if ($runRust) { $rS = Invoke-AppBench -App 'RustText' -Scenario $Scenario -RunIndex ($i + 1) -Order $order -Exe $rustExe -FileArgs $FileArgs -WaitMarkerRegex $WaitMarkerRegex -SettleMs $SettleMs }
            }
            if ($null -ne $rS) { $rustSamples.Add($rS); $rawStartupSamples.Add($rS) }
            if ($null -ne $nS) { $netSamples.Add($nS); $rawStartupSamples.Add($nS) }
        }
        $outSummaries = @()
        if ($rustSamples.Count -gt 0) { $outSummaries += (Summarize-Samples "RustText $Scenario" $rustSamples.ToArray() $ReadyKey) }
        if ($netSamples.Count -gt 0) { $outSummaries += (Summarize-Samples "NetText $Scenario" $netSamples.ToArray() $ReadyKey) }
        return $outSummaries
    }

    $rows = [System.Collections.Generic.List[object]]::new()
    if (-not $SkipStartup) {
        if ($runRust) { Invoke-AppBench -App 'RustText' -Scenario 'warmup' -RunIndex 0 -Order 'Warmup' -Exe $rustExe -FileArgs @() -WaitMarkerRegex 'first_frame_presented_ms=' -SettleMs 150 -IsWarmup | Out-Null }
        if ($runNet) { Invoke-AppBench -App 'NetText' -Scenario 'warmup' -RunIndex 0 -Order 'Warmup' -Exe $netExe -FileArgs @() -WaitMarkerRegex 'first_frame_presented_ms=' -SettleMs 150 -IsWarmup | Out-Null }

        Write-Host "--- Running startup series: empty ($Iterations runs) ---"
        foreach ($r in (Invoke-AlternatingSeries 'empty' @() 'first_frame_presented_ms=' 'first_frame_presented_ms' 200 $Iterations)) { $rows.Add($r) }
        Write-Host "--- Running startup series: 40MB .txt ($Iterations runs) ---"
        foreach ($r in (Invoke-AlternatingSeries '40MB .txt' @($txtFile) '(?:document_ready_ms|editor_ready_ms)=' 'document_ready_ms' 200 $Iterations)) { $rows.Add($r) }
        Write-Host "--- Running startup series: 40MB .json ($Iterations runs) ---"
        foreach ($r in (Invoke-AlternatingSeries '40MB .json' @($jsonFile) '(?:document_ready_ms|editor_ready_ms)=' 'document_ready_ms' 200 $Iterations)) { $rows.Add($r) }
    }

    function Write-LogFixture([string]$Path, [long]$TargetBytes, [bool]$Append = $false) {
        $blockBytes = 128 * 512
        $writeBytes = 4MB
        $block = [byte[]]::new($blockBytes)
        $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
        for ($r = 0; $r -lt 512; $r++) {
            $l = ("{0:D10}|INFO|Событие обработки|file=Журнал.txt|строка={0:D10}|ok=true|" -f $r)
            $lb = $utf8.GetBytes($l)
            $off = $r * 128
            for ($k = 0; $k -lt 127; $k++) { $block[$off + $k] = 32 }
            [System.Buffer]::BlockCopy($lb, 0, $block, $off, $lb.Length)
            $block[$off + 127] = 10
        }
        $wbuf = [byte[]]::new($writeBytes)
        for ($r = 0; $r -lt ($writeBytes / $blockBytes); $r++) {
            [System.Buffer]::BlockCopy($block, 0, $wbuf, $r * $blockBytes, $blockBytes)
        }
        $mode = if ($Append) { [System.IO.FileMode]::Append } else { [System.IO.FileMode]::CreateNew }
        $fs = [System.IO.FileStream]::new($Path, $mode, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read, 1MB, [System.IO.FileOptions]::SequentialScan)
        try {
            $chunks = [long]($TargetBytes / $writeBytes)
            for ($w = [long]0; $w -lt $chunks; $w++) { $fs.Write($wbuf, 0, $wbuf.Length) }
            $sentinel = $utf8.GetBytes("9999999999|INFO|E2E_EOF_SENTINEL|ok=true|`n")
            $fs.Write($sentinel, 0, $sentinel.Length)
            $fs.Flush($true)
        } finally { $fs.Dispose() }
    }

    $log10 = Join-Path $fixtureRoot 'bench_10gib.txt'
    $log20 = Join-Path $fixtureRoot 'bench_20gib.txt'
    $e2eRows = [System.Collections.Generic.List[object]]::new()

    function Invoke-E2ESeries([string]$Scenario, [string[]]$FileArgs, [int]$Count) {
        $rustSamples = [System.Collections.Generic.List[object]]::new()
        $netSamples = [System.Collections.Generic.List[object]]::new()
        $requiredE2EKeys = if ($IndexOnly) {
            @('first_log_frame_ms', 'index_ready_ms')
        } else {
            @('first_log_frame_ms', 'index_ready_ms', 'jump_50pct_ms', 'jump_eof_ms', 'scroll_prev_ms', 'search_eof_ms', 'search_eof_hits', 'search_eof_complete', 'cancel_search_ms', 'mem_first_frame_priv_mib', 'mem_index_ready_priv_mib', 'mem_after_ops_priv_mib')
        }
        $waitRx = if ($IndexOnly) { 'index_ready_ms=' } else { 'e2e_done=1' }
        $settle = if ($IndexOnly) { 0 } else { 100 }
        for ($i = 0; $i -lt $Count; $i++) {
            $order = if (($i % 2) -eq 0) { 'RustFirst' } else { 'NetFirst' }
            $rS = $null
            $nS = $null
            if ($order -eq 'RustFirst') {
                if ($runRust) { $rS = Invoke-AppBench -App 'RustText' -Scenario $Scenario -RunIndex ($i + 1) -Order $order -Exe $rustExe -FileArgs $FileArgs -WaitMarkerRegex $waitRx -SettleMs $settle -TimeoutMs 180000 -E2E:(-not $IndexOnly) -E2ENeedle 'E2E_EOF_SENTINEL' }
                if ($runNet) { $nS = Invoke-AppBench -App 'NetText' -Scenario $Scenario -RunIndex ($i + 1) -Order $order -Exe $netExe -FileArgs $FileArgs -WaitMarkerRegex $waitRx -SettleMs $settle -TimeoutMs 180000 -E2E:(-not $IndexOnly) -E2ENeedle 'E2E_EOF_SENTINEL' }
            } else {
                if ($runNet) { $nS = Invoke-AppBench -App 'NetText' -Scenario $Scenario -RunIndex ($i + 1) -Order $order -Exe $netExe -FileArgs $FileArgs -WaitMarkerRegex $waitRx -SettleMs $settle -TimeoutMs 180000 -E2E:(-not $IndexOnly) -E2ENeedle 'E2E_EOF_SENTINEL' }
                if ($runRust) { $rS = Invoke-AppBench -App 'RustText' -Scenario $Scenario -RunIndex ($i + 1) -Order $order -Exe $rustExe -FileArgs $FileArgs -WaitMarkerRegex $waitRx -SettleMs $settle -TimeoutMs 180000 -E2E:(-not $IndexOnly) -E2ENeedle 'E2E_EOF_SENTINEL' }
            }
            foreach ($sample in (@($rS, $nS) | Where-Object { $null -ne $_ })) {
                foreach ($reqKey in $requiredE2EKeys) {
                    if (-not $sample.Metrics.ContainsKey($reqKey)) {
                        throw "[$($sample.Case) run=$($sample.RunIndex)] Missing E2E metric '$reqKey'"
                    }
                }
                if (-not $IndexOnly -and ($sample.Metrics['search_eof_complete'] -ne 1 -or $sample.Metrics['search_eof_hits'] -ne 1)) {
                    throw "[$($sample.Case) run=$($sample.RunIndex)] Invalid E2E EOF search result: complete=$($sample.Metrics['search_eof_complete']), hits=$($sample.Metrics['search_eof_hits'])"
                }
            }
            if ($null -ne $rS) { $rustSamples.Add($rS); $rawE2ESamples.Add($rS) }
            if ($null -ne $nS) { $netSamples.Add($nS); $rawE2ESamples.Add($nS) }
        }

        $activePairs = [System.Collections.Generic.List[object]]::new()
        if ($rustSamples.Count -gt 0) { $activePairs.Add(@("RustText $Scenario", $rustSamples.ToArray())) }
        if ($netSamples.Count -gt 0) { $activePairs.Add(@("NetText $Scenario", $netSamples.ToArray())) }
        foreach ($pair in $activePairs) {
            $label = $pair[0]
            $arr = $pair[1]
            $m = { param([string]$k) [double[]]@($arr | Where-Object { $_.Metrics.ContainsKey($k) } | ForEach-Object { $_.Metrics[$k] }) }
            [pscustomobject]@{
                Case = $label
                Runs = $arr.Count
                FirstFrameMs = FmtMedP95Int (& $m 'first_log_frame_ms')
                IndexReadyMs = FmtMedP95Int (& $m 'index_ready_ms')
                Jump50Ms = FmtMedP95Int (& $m 'jump_50pct_ms')
                JumpEofMs = FmtMedP95Int (& $m 'jump_eof_ms')
                ScrollPrevMs = FmtMedP95Int (& $m 'scroll_prev_ms')
                SearchEofMs = FmtMedP95Int (& $m 'search_eof_ms')
                CancelSearchMs = FmtMedP95Int (& $m 'cancel_search_ms')
                MemStartPriv = FmtMedP95MiB (& $m 'mem_first_frame_priv_mib')
                MemIndexPriv = if ($IndexOnly) { FmtMedP95MiB ([double[]]@($arr | ForEach-Object { [double]$_.PrivateMiB })) } else { FmtMedP95MiB (& $m 'mem_index_ready_priv_mib') }
                MemOpsPriv = FmtMedP95MiB (& $m 'mem_after_ops_priv_mib')
                MemOpsWs = if ($IndexOnly) { FmtMedP95MiB ([double[]]@($arr | ForEach-Object { [double]$_.WorkingSetMiB })) } else { FmtMedP95MiB (& $m 'mem_after_ops_ws_mib') }
                MemOpsPeakWs = if ($IndexOnly) { FmtMedP95MiB ([double[]]@($arr | ForEach-Object { [double]$_.PeakWorkingSetMiB })) } else { FmtMedP95MiB (& $m 'mem_after_ops_peak_ws_mib') }
            }
        }
    }

    $need10 = $Include10GiB -or ($IncludeE2E -and (($E2EScenarios -contains '10GiB E2E') -or ($E2EScenarios -contains 'Mixed 40MB+10GiB')))
    $need20 = $Include20GiB -or ($IncludeE2E -and ($E2EScenarios -contains '20GiB E2E'))
    if ($need10 -or $need20) {
        if ($need10) {
            if (-not (Test-Path $log10) -or (Get-Item $log10).Length -lt (10L * 1GB)) {
                Write-Host '--- Generating 10 GiB fixture ---'
                if (Test-Path $log10) { Remove-Item -LiteralPath $log10 -Force }
                Write-LogFixture $log10 (10L * 1GB) $false
            } else {
                Write-Host "--- Reusing 10 GiB fixture: $log10 ---"
            }
            if ($Include10GiB -and -not $SkipStartup) {
                Write-Host "--- Running startup series: 10GiB .txt ($Iterations runs) ---"
                foreach ($r in (Invoke-AlternatingSeries '10GiB .txt' @($log10) 'first_log_frame_ms=' 'first_log_frame_ms' 200 $Iterations)) { $rows.Add($r) }
            }
            if ($IncludeE2E) {
                if ($E2EScenarios -contains '10GiB E2E') {
                    Write-Host "--- Running E2E 10 GiB ($E2EIterations runs) ---"
                    foreach ($er in (Invoke-E2ESeries '10GiB E2E' @($log10) $E2EIterations)) { $e2eRows.Add($er) }
                }
                if ($E2EScenarios -contains 'Mixed 40MB+10GiB') {
                    Write-Host "--- Running E2E Mixed 40MB+10GiB ($E2EIterations runs) ---"
                    foreach ($er in (Invoke-E2ESeries 'Mixed 40MB+10GiB' @($txtFile, $log10) $E2EIterations)) { $e2eRows.Add($er) }
                }
            }
        }
        if ($need20) {
            if (-not (Test-Path $log20) -or (Get-Item $log20).Length -lt (20L * 1GB)) {
                Write-Host '--- Generating 20 GiB fixture ---'
                if (Test-Path $log20) { Remove-Item -LiteralPath $log20 -Force }
                if (-not $FixtureDir -and (Test-Path $log10)) {
                    Move-Item $log10 $log20
                    $fsTrunc = [System.IO.File]::Open($log20, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
                    try { $fsTrunc.SetLength(10L * 1GB) } finally { $fsTrunc.Dispose() }
                    Write-LogFixture $log20 (10L * 1GB) $true
                } else {
                    Write-LogFixture $log20 (20L * 1GB) $false
                }
            } else {
                Write-Host "--- Reusing 20 GiB fixture: $log20 ---"
            }
            if ($Include20GiB -and -not $SkipStartup) {
                Write-Host "--- Running startup series: 20GiB .txt ($Iterations runs) ---"
                foreach ($r in (Invoke-AlternatingSeries '20GiB .txt' @($log20) 'first_log_frame_ms=' 'first_log_frame_ms' 200 $Iterations)) { $rows.Add($r) }
            }
            if ($IncludeE2E -and ($E2EScenarios -contains '20GiB E2E')) {
                Write-Host "--- Running E2E 20 GiB ($E2EIterations runs) ---"
                foreach ($er in (Invoke-E2ESeries '20GiB E2E' @($log20) $E2EIterations)) { $e2eRows.Add($er) }
            }
        }
    }

    if ($rows.Count -gt 0) {
        $rows | Select-Object Case, Runs, WindowMsText, FirstFrameText, ReadyMsText, WorkingSetText, PeakWsText, PrivateText | Format-Table -AutoSize
    }
    if ($e2eRows.Count -gt 0) {
        Write-Host '=== E2E Workflow & Temporal Memory ==='
        $e2eRows | Select-Object Case, Runs, FirstFrameMs, IndexReadyMs, Jump50Ms, JumpEofMs, SearchEofMs, CancelSearchMs, MemStartPriv, MemIndexPriv, MemOpsPriv, MemOpsWs | Format-Table -AutoSize
    }

    $netCoreLines = @()
    $rustCoreLines = @()
    if ($IncludeCoreBench -or (-not $SkipStartup -and (Test-Path $coreBenchProj))) {
        Write-Host '=== 4. Paired Core Engine Benchmarks ==='
        if ($runNet) {
            $rawNet = & dotnet run --project $coreBenchProj -c Release --no-restore 2>&1
            if ($LASTEXITCODE -ne 0) { throw "NetText CoreBench failed" }
            $netCoreLines = @($rawNet | Where-Object { $_ -is [string] -and $_ -match ':' })
            Write-Host '[NetText CoreBench]'
            $netCoreLines | ForEach-Object { Write-Host "  $_" }
        }
        if ($runRust) {
            if (-not (Test-Path $rustCoreBenchExe)) { throw "Missing $rustCoreBenchExe" }
            $rawRust = & $rustCoreBenchExe 2>&1
            if ($LASTEXITCODE -ne 0) { throw "RustText core_bench failed" }
            $rustCoreLines = @($rawRust | Where-Object { $_ -is [string] -and $_ -match ':' })
            Write-Host '[RustText core_bench]'
            $rustCoreLines | ForEach-Object { Write-Host "  $_" }
        }
    }

    if ($WriteReport) {
        Push-Location $netRoot
        try {
            $netGitSha = (& git rev-parse --short HEAD).Trim()
            $netGitBranch = (& git branch --show-current).Trim()
            $netGitDirty = [bool](& git status --porcelain)
        } finally { Pop-Location }
        $binHashes = [ordered]@{
            'NetText.exe' = (Get-FileHash -LiteralPath $netExe -Algorithm SHA256).Hash.ToLowerInvariant()
            'Scintilla.dll' = (Get-FileHash -LiteralPath (Join-Path $netPubDir 'Scintilla.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
            'Lexilla.dll' = (Get-FileHash -LiteralPath (Join-Path $netPubDir 'Lexilla.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
            'rusttext.exe' = (Get-FileHash -LiteralPath $rustExe -Algorithm SHA256).Hash.ToLowerInvariant()
            'core_bench.exe' = (Get-FileHash -LiteralPath $rustCoreBenchExe -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $codeStats = Get-CodebaseStats
        $rawObj = [ordered]@{
            generatedAt = (Get-Date).ToString('o')
            iterations = $Iterations
            e2eIterations = $E2EIterations
            environment = [ordered]@{
                os = "$($osInfo.Caption) ($($osInfo.Version), $($osInfo.OSArchitecture))"
                dotnetSdk = $dotnetVer
                rustc = $rustcVer
                cargo = $cargoVer
                rustTextBranch = $rustGitBranch
                rustTextCommit = $rustGitSha
                rustTextDirty = $rustGitDirty
                netTextBranch = $netGitBranch
                netTextCommit = $netGitSha
                netTextDirty = $netGitDirty
                scintillaVersion = $scintillaInfo.ProductVersion
                lexillaVersion = $lexillaInfo.ProductVersion
                binarySha256 = $binHashes
            }
            codeStats = $codeStats
            dependencies = [ordered]@{
                rustDirectRuntime = $rustDirectRuntime
                rustDirectBuild = $rustDirectBuild
                rustWinTransitiveRuntime = $rustWinRuntimeTotal
                rustWinBuildDirect = $rustWinBuildDirect
                rustWinGraphTotal = $rustWinGraphTotal
                rustCargoLockAllTargets = $rustCratesLock
                netNugetPackages = $netNugetCount
                netPublishedFiles = @('NetText.exe', 'Scintilla.dll', 'Lexilla.dll')
            }
            buildsAndTests = [ordered]@{
                netTestSec = $netTestSec
                netTestPassed = $netTestCount
                rustTestSec = $rustTestSec
                rustBinTestPassed = $rustBinTestCount
                rustTotalTestPassed = $rustTotalTestCount
                netIncrementalBuildSec = $netIncBuildSec
                rustIncrementalBuildSec = $rustIncBuildSec
                netCleanBuildSec = $netCleanBuildSec
                rustCleanBuildSec = $rustCleanBuildSec
            }
            startupSummary = $rows
            e2eSummary = $e2eRows
            coreBench = [ordered]@{
                netText = $netCoreLines
                rustText = $rustCoreLines
            }
            rawStartupRuns = $rawStartupSamples
            rawE2ERuns = $rawE2ESamples
        }
        $rawDir = Split-Path -Parent $RawJsonPath
        New-Item -ItemType Directory -Force -Path $rawDir | Out-Null
        [System.IO.File]::WriteAllText($RawJsonPath, ($rawObj | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
        Write-Host "Raw JSON written to: $RawJsonPath"

        Write-ComparisonReport $rawObj $codeStats
    } elseif ($PartialJsonOutPath) {
        Push-Location $netRoot
        try {
            $netGitSha = (& git rev-parse --short HEAD).Trim()
            $netGitDirty = [bool](& git status --porcelain)
        } finally { Pop-Location }
        $partialObj = [ordered]@{
            generatedAt = (Get-Date).ToString('o')
            netTextCommit = $netGitSha
            netTextDirty = $netGitDirty
            netExeSha256 = (Get-FileHash -LiteralPath $netExe -Algorithm SHA256).Hash.ToLowerInvariant()
            e2eSummary = $e2eRows
            coreBenchNetText = $netCoreLines
            rawE2ERuns = $rawE2ESamples
        }
        $pDir = Split-Path -Parent $PartialJsonOutPath
        if ($pDir) { New-Item -ItemType Directory -Force -Path $pDir | Out-Null }
        [System.IO.File]::WriteAllText($PartialJsonOutPath, ($partialObj | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
        Write-Host "Partial JSON written to: $PartialJsonOutPath"
    }
} finally {
    Remove-Item $benchRoot -Recurse -Force -ErrorAction SilentlyContinue
}
