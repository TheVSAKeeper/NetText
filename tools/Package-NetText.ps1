[CmdletBinding()]
param(
    [string]$PublishDirectory,
    [string]$ReadmePath,
    [string]$ComparisonPath,
    [string]$ComparisonRawJsonPath,
    [string]$OutputDirectory,
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$PackageName = 'NetText',
    [switch]$Publish,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\NetText\NetText.csproj'

if (-not $PublishDirectory) {
    $PublishDirectory = Join-Path $repositoryRoot 'publish'
}
if (-not $ReadmePath) {
    $ReadmePath = Join-Path $repositoryRoot 'README.md'
}
if (-not $ComparisonPath) {
    $ComparisonPath = Join-Path $repositoryRoot 'docs\comparison.md'
}
if (-not $ComparisonRawJsonPath) {
    $ComparisonRawJsonPath = Join-Path $repositoryRoot 'docs\comparison-raw.json'
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot 'dist'
}

$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($Publish) {
    dotnet publish $projectPath -c Release -r win-x64 -o $PublishDirectory | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'NetText publish failed'
    }
}

$licenseInPublish = Join-Path $PublishDirectory 'Scintilla-License.txt'
$licenseFallback = Join-Path $repositoryRoot 'native\win-x64\License.txt'
$licensePath = if (Test-Path -LiteralPath $licenseInPublish -PathType Leaf) { $licenseInPublish } else { $licenseFallback }

$entries = [ordered]@{
    'NetText.exe' = [IO.Path]::GetFullPath((Join-Path $PublishDirectory 'NetText.exe'))
    'Scintilla-License.txt' = [IO.Path]::GetFullPath($licensePath)
    'README.md' = [IO.Path]::GetFullPath($ReadmePath)
    'docs/comparison.md' = [IO.Path]::GetFullPath($ComparisonPath)
    'docs/comparison-raw.json' = [IO.Path]::GetFullPath($ComparisonRawJsonPath)
}

$scintilla = Join-Path $PublishDirectory 'Scintilla.dll'
$lexilla = Join-Path $PublishDirectory 'Lexilla.dll'
if (!$Publish -and ((Test-Path -LiteralPath $scintilla) -or (Test-Path -LiteralPath $lexilla))) {
    $entries['Scintilla.dll'] = $scintilla
    $entries['Lexilla.dll'] = $lexilla
}

foreach ($file in $entries.Values) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required package input was not found: $file"
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$archivePath = Join-Path $OutputDirectory "$PackageName.zip"
$checksumPath = "$archivePath.sha256"
if (-not $Force -and ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath $checksumPath))) {
    throw "Refusing to overwrite an existing package or checksum: $archivePath"
}

$nonce = [Guid]::NewGuid().ToString('N')
$temporaryArchive = Join-Path $OutputDirectory ".$PackageName.$nonce.tmp"
$temporaryChecksum = "$temporaryArchive.sha256"
$archiveCommitted = $false

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($temporaryArchive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $entries.GetEnumerator()) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $entry.Value,
                $entry.Key,
                [IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }

    $hash = (Get-FileHash -LiteralPath $temporaryArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        $temporaryChecksum,
        "$hash  $PackageName.zip`n",
        [Text.UTF8Encoding]::new($false)
    )

    if ($Force) {
        if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
        if (Test-Path -LiteralPath $checksumPath) { Remove-Item -LiteralPath $checksumPath -Force }
    }

    [IO.File]::Move($temporaryArchive, $archivePath)
    $archiveCommitted = $true
    [IO.File]::Move($temporaryChecksum, $checksumPath)
}
catch {
    if ($archiveCommitted -and (Test-Path -LiteralPath $archivePath)) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    throw
}
finally {
    foreach ($temporaryPath in @($temporaryArchive, $temporaryChecksum)) {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

$sizeMiB = [math]::Round((Get-Item -LiteralPath $archivePath).Length / 1MB, 2)
Write-Output "Package: $archivePath ($sizeMiB MiB)"
Write-Output "SHA-256: $checksumPath ($hash)"
