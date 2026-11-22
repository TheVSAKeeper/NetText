function Get-ComparisonMedian([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}

function Format-ComparisonRange([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $culture = [Globalization.CultureInfo]::InvariantCulture
    '{0} [{1}–{2}]' -f (Get-ComparisonMedian $sorted).ToString('0.000', $culture),
        $sorted[0].ToString('0.000', $culture), $sorted[-1].ToString('0.000', $culture)
}

function Format-ComparisonPairs($Samples, [string]$Metric, [int]$Iterations, [double]$Unit) {
    $rust = [Collections.Generic.List[double]]::new()
    $net = [Collections.Generic.List[double]]::new()
    $ratios = [Collections.Generic.List[double]]::new()
    for ($i = 1; $i -le $Iterations; $i++) {
        $pair = @($Samples | Where-Object iteration -eq $i)
        $r = @($pair | Where-Object app -eq RustText)
        $n = @($pair | Where-Object app -eq NetText)
        $expectedRustOrder = if ($i % 2 -eq 1) { 1 } else { 2 }
        if ($pair.Count -ne 2 -or $r.Count -ne 1 -or $n.Count -ne 1 -or $r[0].order -ne $expectedRustOrder -or $r[0].order + $n[0].order -ne 3) {
            throw "Invalid benchmark pair for $($Samples[0].scenario) iteration $i."
        }
        if ($null -eq $r[0].$Metric -or $null -eq $n[0].$Metric) { throw "Missing measured $Metric." }
        $rust.Add($r[0].$Metric / $Unit)
        $net.Add($n[0].$Metric / $Unit)
        if ($n[0].$Metric -gt 0) { $ratios.Add($r[0].$Metric / $n[0].$Metric) }
    }
    $ratio = if ($ratios.Count -eq $Iterations) { Format-ComparisonRange $ratios.ToArray() } else { '–' }
    return "$(Format-ComparisonRange $rust.ToArray()) | $(Format-ComparisonRange $net.ToArray()) | $ratio"
}

function Write-ComparisonMeasurementReport($Data, [string]$Path, [string]$RawJsonLink = 'raw.json') {
    $lines = [Collections.Generic.List[string]]::new()
    $mode = if ($Data.smoke) { 'Проверочный прогон с малыми входами.' } else { 'Полный сценарий.' }
    $lines.AddRange([string[]]@(
        "# NetText и RustText $($Data.rustVersion)",
        '',
        "$mode Windows x64, .NET SDK $($Data.environment.dotnetSdk), по $($Data.iterations) пар на сценарий.",
        '',
        "- NetText: ``$($Data.netRevision)``. RustText: ``$($Data.rustRevision)``.",
        "- Размер EXE NetText $($Data.binaries.netText.bytes) байт, RustText $($Data.binaries.rustText.bytes) байт. RustText дополнительно извлёк $($Data.binaries.rustText.extractedDllBytes) байт DLL.",
        "- Сырые наблюдения, окружение, хеши бинарников и входов – в [$RawJsonLink]($RawJsonLink).",
        '',
        '## GUI',
        '',
        "Окно обнаружено внешней пробой Windows, ответ означает успешный ``WM_NULL``. Память снята примерно через $($Data.memorySampleTargetMs) мс от запуска процесса. Cold относится к кэшу приложения, состояние кэша ОС естественное.",
        '',
        'В ячейках медиана [минимум–максимум]. Последний столбец – медиана и размах отношений внутри каждой пары.',
        '',
        '| Сценарий | Метрика | RustText | NetText | RustText / NetText |',
        '| --- | --- | ---: | ---: | ---: |'
    ))
    foreach ($scenario in 'empty-cold', 'empty', 'text', 'json', 'log') {
        $rows = @($Data.guiRuns | Where-Object scenario -eq $scenario)
        foreach ($metric in 'windowMs', 'responsiveMs', 'workingSetBytes', 'privateBytes', 'peakWorkingSetBytes') {
            $unit = if ($metric.EndsWith('Bytes')) { 1MB } else { 1 }
            $label = if ($unit -eq 1) { "$metric, мс" } else { "$metric, MiB" }
            $lines.Add("| $scenario | $label | $(Format-ComparisonPairs $rows $metric $Data.iterations $unit) |")
        }
    }
    $lines.AddRange([string[]]@(
        '',
        '## Ядро',
        '',
        'Время hot path, мс. Стенд проверяет результаты каждого запуска. Аллокации текущего потока сохранены в байтах в сырых данных и не включают нативную память.',
        '',
        '| Операция | RustText | NetText | RustText / NetText |',
        '| --- | ---: | ---: | ---: |'
    ))
    foreach ($operation in $Data.coreRuns.scenario | Select-Object -Unique) {
        $rows = @($Data.coreRuns | Where-Object scenario -eq $operation)
        $lines.Add("| $operation | $(Format-ComparisonPairs $rows 'ms' $Data.iterations 1) |")
    }
    [IO.File]::WriteAllLines($Path, $lines, [Text.UTF8Encoding]::new($false))
}
