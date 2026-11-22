[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$downloads = [IO.Directory]::CreateDirectory((Join-Path $output 'downloads')).FullName
$work = [IO.Directory]::CreateDirectory((Join-Path $output ("build-" + [Guid]::NewGuid().ToString('N')))).FullName
$sources = @(
    @{ Archive = 'scintilla569.zip'; Hash = 'FDDC637AFA6E3A9205A5AD67C777D711A921ED2797D792F3034010C1BE1539D6'; Project = 'scintilla/win32'; Makefile = 'scintilla.mak'; Library = 'libscintilla.lib' }
    @{ Archive = 'lexilla555.zip'; Hash = '713ADA097B7DA0594638E2C5723D8DEA486653C23EC2E0C12631DF7E5B7F0991'; Project = 'lexilla/src'; Makefile = 'lexilla.mak'; Library = 'liblexilla.lib' }
)

try {
    foreach ($source in $sources) {
        $archive = Join-Path $downloads $source.Archive
        if (!(Test-Path -LiteralPath $archive -PathType Leaf) -or (Get-FileHash -LiteralPath $archive).Hash -ne $source.Hash) {
            $download = Join-Path $work $source.Archive
            Invoke-WebRequest -Uri ("https://www.scintilla.org/" + $source.Archive) -OutFile $download
            if ((Get-FileHash -LiteralPath $download).Hash -ne $source.Hash) {
                throw "SHA-256 mismatch for $($source.Archive)"
            }
            [IO.File]::Move($download, $archive, $true)
        }
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $work)
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or !$installation) { throw 'Visual Studio C++ x64 build tools are required.' }
    $developerShell = Join-Path $installation 'Common7/Tools/Launch-VsDevShell.ps1'
    & $developerShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Host

    foreach ($source in $sources) {
        $project = Join-Path $work $source.Project
        Push-Location -LiteralPath $project
        try {
            $target = "..\bin\$($source.Library)"
            [IO.Directory]::CreateDirectory((Join-Path $project 'obj')) | Out-Null
            & nmake.exe /NOLOGO /F $source.Makefile 'QUIET=1' 'CXXNDEBUG=-O2 -MT -DNDEBUG' $target
            if ($LASTEXITCODE -ne 0) { throw "$($source.Makefile) failed with exit code $LASTEXITCODE" }
        }
        finally { Pop-Location }
    }

    foreach ($source in $sources) {
        $library = Join-Path (Join-Path $work $source.Project) "../bin/$($source.Library)"
        [IO.File]::Move($library, (Join-Path $output $source.Library), $true)
    }
}
finally {
    $resolvedWork = (Resolve-Path -LiteralPath $work).Path
    $resolvedOutput = (Resolve-Path -LiteralPath $output).Path
    if ([IO.Path]::GetDirectoryName($resolvedWork).Equals($resolvedOutput, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
