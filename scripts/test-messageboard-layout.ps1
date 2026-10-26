[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$compilerPath = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sourcePath = Join-Path $projectRoot 'app\KindleDisplay.cs'
$checksPath = Join-Path $PSScriptRoot 'MessageBoardLayoutChecks.cs'
$manifestPath = Join-Path $projectRoot 'app\KindleDisplay.manifest'
foreach ($requiredPath in @($compilerPath, $sourcePath, $checksPath, $manifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Missing test input: $requiredPath" }
}
# Retain uniquely named fixtures and screenshots for inspection; never touch real drafts.
$runName = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$outputDirectory = Join-Path $projectRoot (Join-Path 'runtime\messageboard-layout-checks' $runName)
[void][IO.Directory]::CreateDirectory($outputDirectory)
$executablePath = Join-Path $outputDirectory 'MessageBoardLayoutChecks.exe'
$arguments = @(
    '/nologo', '/target:exe', '/platform:anycpu', '/optimize+', '/codepage:65001',
    '/main:MessageBoardLayoutChecks', ('/out:' + $executablePath), ('/win32manifest:' + $manifestPath),
    '/reference:System.dll', '/reference:System.Core.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    '/reference:System.Management.dll', '/reference:System.Web.Extensions.dll',
    $sourcePath, $checksPath
)
& $compilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw "Layout check compilation failed with exit code $LASTEXITCODE" }
& $executablePath $outputDirectory
if ($LASTEXITCODE -ne 0) { throw "Layout checks failed with exit code $LASTEXITCODE. Artifacts: $outputDirectory" }
