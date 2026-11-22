#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\tools\AvaloniaCompare\Gui.ps1')

$directory = [IO.Directory]::CreateTempSubdirectory('nettext-rusttext-readiness-')
try {
    $recent = Join-Path $directory.FullName 'recent-files.json'
    $expected = Join-Path $directory.FullName 'events.log'
    $other = Join-Path $directory.FullName 'other\events.log'

    function Assert-OpenedMode($Content, $ExpectedMode, [string]$Label) {
        if ($null -eq $Content) {
            if ([IO.File]::Exists($recent)) { [IO.File]::Delete($recent) }
        } elseif ($Content -is [string]) {
            [IO.File]::WriteAllText($recent, $Content)
        } else {
            $json = ConvertTo-Json -InputObject $Content -Depth 5
            [IO.File]::WriteAllText($recent, $json)
        }
        $actual = Get-RustTextOpenedMode $recent $expected
        if ($actual -cne $ExpectedMode) {
            throw "[$Label] Expected '$ExpectedMode', got '$actual'."
        }
    }

    Assert-OpenedMode $null $null 'missing-file'
    Assert-OpenedMode '' $null 'empty-file'
    Assert-OpenedMode '{broken' $null 'partial-json'
    Assert-OpenedMode @() $null 'empty-array'
    Assert-OpenedMode @(@{ path = $other; view_as_log = $false; line_count = 2; log = $null }) $null 'other-path'
    Assert-OpenedMode @(@{ path = $expected; view_as_log = $false; line_count = $null; log = $null }) $null 'editor-null-lines'
    Assert-OpenedMode @(@{ path = $expected; view_as_log = $false; line_count = 0; log = $null }) $null 'editor-zero-lines'
    Assert-OpenedMode @(@{ path = $expected.ToUpperInvariant(); view_as_log = $false; line_count = 2; log = $null }) 'editor' 'editor-valid'
    Assert-OpenedMode @(@{ path = $expected; view_as_log = $true; log = @{} }) $null 'log-missing-edit-pages'
    Assert-OpenedMode @(@{ path = $expected; view_as_log = $true; log = @{ edit_pages = $true } }) 'paged-editor' 'log-paged-editor'
    Assert-OpenedMode @(@{ path = $expected; view_as_log = $true; log = @{ edit_pages = $false } }) 'log' 'log-valid'

    Write-Host 'Avalonia comparison readiness checks passed (11/11).'
} finally {
    if ([IO.File]::Exists($recent)) { [IO.File]::Delete($recent) }
    $directory.Delete()
}
