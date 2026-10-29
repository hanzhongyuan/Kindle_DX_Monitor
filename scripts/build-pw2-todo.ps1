[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$pluginRoot = Join-Path $projectRoot 'device\pw2-todo\koreader\plugins\kindletodo.koplugin'
$outputPath = Join-Path $projectRoot 'KindlePaperwhite2Todo.zip'
$entryPrefix = 'koreader/plugins/kindletodo.koplugin/'

# Only ship the plugin's top-level Lua sources. In particular, never package a
# Kindle installation tree, settings, imported text, backups, or runtime files.
foreach ($relativeDirectory in @('device', 'device\pw2-todo', 'device\pw2-todo\koreader',
        'device\pw2-todo\koreader\plugins', 'device\pw2-todo\koreader\plugins\kindletodo.koplugin')) {
    $directory = Get-Item -LiteralPath (Join-Path $projectRoot $relativeDirectory) -Force
    if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Build input must be a real directory: $($directory.FullName)"
    }
}
foreach ($requiredName in @('main.lua', '_meta.lua', 'store.lua', 'board.lua', 'inbox.lua')) {
    if (-not (Test-Path -LiteralPath (Join-Path $pluginRoot $requiredName) -PathType Leaf)) {
        throw "Missing plugin source: $requiredName"
    }
}
[string[]]$sourcePaths = @(Get-ChildItem -LiteralPath $pluginRoot -File -Filter '*.lua' -Force | ForEach-Object {
    if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Plugin source must not be a link: $($_.FullName)"
    }
    $_.FullName
})
[Array]::Sort($sourcePaths, [StringComparer]::Ordinal)
if (Test-Path -LiteralPath $outputPath) {
    $existingOutput = Get-Item -LiteralPath $outputPath -Force
    if ($existingOutput.PSIsContainer -or ($existingOutput.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Output must be an ordinary ZIP file: $outputPath"
    }
}

Add-Type -AssemblyName System.IO.Compression
$temporaryPath = Join-Path $projectRoot ('.KindlePaperwhite2Todo.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$archiveStream = $null
$archive = $null
try {
    $archiveStream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $false, [Text.Encoding]::UTF8)
    foreach ($sourcePath in $sourcePaths) {
        $entry = $archive.CreateEntry($entryPrefix + [IO.Path]::GetFileName($sourcePath), [IO.Compression.CompressionLevel]::NoCompression)
        # Fixed timestamps, attributes, entry order and uncompressed payloads
        # make repeated builds of the same source bytes reproducible.
        $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $entry.ExternalAttributes = 0
        $entryStream = $entry.Open()
        try {
            $sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
            $entryStream.Write($sourceBytes, 0, $sourceBytes.Length)
        }
        finally { $entryStream.Dispose() }
    }
    $archive.Dispose()
    $archive = $null
    $archiveStream.Dispose()
    $archiveStream = $null
    if ([IO.File]::Exists($outputPath)) { [IO.File]::Replace($temporaryPath, $outputPath, [NullString]::Value) }
    else { [IO.File]::Move($temporaryPath, $outputPath) }
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if ($null -ne $archiveStream) { $archiveStream.Dispose() }
    if ([IO.File]::Exists($temporaryPath)) { Remove-Item -LiteralPath $temporaryPath }
}

[PSCustomObject]@{
    Archive = $outputPath
    SourceFiles = $sourcePaths.Length
    Bytes = (Get-Item -LiteralPath $outputPath).Length
    SHA256 = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash
}
