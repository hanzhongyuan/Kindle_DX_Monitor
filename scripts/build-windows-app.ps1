[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$compilerPath = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sourcePath = Join-Path $projectRoot 'app\KindleDisplay.cs'
$manifestPath = Join-Path $projectRoot 'app\KindleDisplay.manifest'
foreach ($requiredPath in @($compilerPath, $sourcePath, $manifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Missing build input: $requiredPath" }
}
$applications = @(
    @{ FileName = 'KindleDisplay.exe'; EntryPoint = 'KindleDisplay.Program' },
    @{ FileName = 'KindleMessageBoard.exe'; EntryPoint = 'KindleDisplay.MessageBoardProgram' },
    @{ FileName = 'KindlePaperwhite2MessageBoard.exe'; EntryPoint = 'KindleDisplay.Paperwhite2MessageBoardProgram' }
)
foreach ($application in $applications) {
    $outputPath = Join-Path $projectRoot $application.FileName
    $arguments = @(
        '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
        ('/main:' + $application.EntryPoint), ('/out:' + $outputPath), ('/win32manifest:' + $manifestPath),
        '/reference:System.dll', '/reference:System.Core.dll',
        '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
        '/reference:System.Management.dll', '/reference:System.Web.Extensions.dll',
        $sourcePath
    )
    & $compilerPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "C# build failed for $($application.FileName) with exit code $LASTEXITCODE" }
    $bytes = [IO.File]::ReadAllBytes($outputPath)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    $subsystem = [BitConverter]::ToUInt16($bytes, $peOffset + 24 + 68)
    if ($subsystem -ne 2) { throw "$($application.FileName) is not a Windows GUI application." }
    [pscustomobject]@{Executable=$outputPath;Subsystem='Windows GUI';Bytes=$bytes.Length;SHA256=(Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash}
}
