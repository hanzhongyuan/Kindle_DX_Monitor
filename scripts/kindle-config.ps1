# Shared machine-local configuration. Loading this file changes no devices.
function Get-KindleConfiguration {
    $configRoot = Split-Path $PSScriptRoot -Parent
    $configPath = Join-Path $configRoot 'kindle-display.local.json'
    $configValues = @{}
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        $configValues = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable
        if ($configValues -isnot [System.Collections.IDictionary]) { throw 'kindle-display.local.json must contain a JSON object.' }
    }
    function Resolve-ConfiguredPath([string]$Name, [string]$Default) {
        $value = $configValues[$Name]
        if ($null -ne $value -and $value -isnot [string]) { throw "$Name must be a string." }
        if ([string]::IsNullOrWhiteSpace($value)) { $value = $Default }
        $value = [Environment]::ExpandEnvironmentVariables($value)
        if (-not [IO.Path]::IsPathRooted($value)) { $value = Join-Path $configRoot $value }
        return [IO.Path]::GetFullPath($value)
    }
    $defaultPowerShell = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
    if (-not (Test-Path -LiteralPath $defaultPowerShell -PathType Leaf)) {
        $foundPowerShell = Get-Command pwsh.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $foundPowerShell) { $defaultPowerShell = $foundPowerShell.Source }
    }
    $pythonPath = Resolve-ConfiguredPath 'PythonPath' '.venv\Scripts\python.exe'
    $pythonHost = $pythonPath
    $venvConfig = Join-Path (Split-Path (Split-Path $pythonPath -Parent) -Parent) 'pyvenv.cfg'
    if (Test-Path -LiteralPath $venvConfig -PathType Leaf) {
        foreach ($line in Get-Content -LiteralPath $venvConfig) {
            if ($line -match '^\s*home\s*=\s*(.+?)\s*$') { $pythonHost = Join-Path $Matches[1] 'python.exe'; break }
        }
    }
    $instanceId = $configValues['DisplayInstanceId']
    if ($null -ne $instanceId -and $instanceId -isnot [string]) { throw 'DisplayInstanceId must be a string.' }
    $installPath = Join-Path $configRoot 'virtual-display-install.json'
    if ([string]::IsNullOrWhiteSpace($instanceId) -and (Test-Path -LiteralPath $installPath -PathType Leaf)) {
        $installation = Get-Content -LiteralPath $installPath -Raw | ConvertFrom-Json
        if ($installation.PSObject.Properties['InstanceId']) { $instanceId = [string]$installation.InstanceId }
    }
    Add-Type -AssemblyName System.Windows.Forms
    $primaryBounds = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $coordinates = @{ PositionX = $primaryBounds.Right; PositionY = $primaryBounds.Top }
    foreach ($name in @('PositionX', 'PositionY')) {
        if ($null -ne $configValues[$name]) {
            $coordinate = $configValues[$name]
            if (($coordinate -isnot [int] -and $coordinate -isnot [long]) -or $coordinate -lt -32768 -or $coordinate -gt 32767) {
                throw "$name must be an integer from -32768 to 32767, or null for automatic placement."
            }
            $coordinates[$name] = [int]$coordinate
        }
    }
    [pscustomobject]@{
        PowerShellPath = Resolve-ConfiguredPath 'PowerShellPath' $defaultPowerShell
        PythonPath = $pythonPath
        PythonHostPath = Resolve-ConfiguredPath 'PythonHostPath' $pythonHost
        VncServerPath = Resolve-ConfiguredPath 'VncServerPath' (Join-Path $env:ProgramFiles 'TightVNC\tvnserver.exe')
        DisplayInstanceId = $instanceId
        PositionX = $coordinates.PositionX
        PositionY = $coordinates.PositionY
    }
}

function Assert-KindleDisplayInstance([string]$InstanceId) {
    if ([string]::IsNullOrWhiteSpace($InstanceId)) {
        throw 'Dedicated display is not configured. Install it with New-KindleVirtualDisplay.ps1, or set DisplayInstanceId in kindle-display.local.json after checking the device identity.'
    }
    $device = Get-PnpDevice -InstanceId $InstanceId -ErrorAction Stop
    $hardwareIds = @((Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction Stop).Data)
    if ($device.FriendlyName -ne 'Virtual Display Driver' -or 'Root\MttVDD' -notin $hardwareIds) {
        throw 'The configured display is not the dedicated MttVDD device; refusing to change it.'
    }
    $matchingDevices = @(Get-PnpDevice -Class Display -PresentOnly | Where-Object FriendlyName -EQ 'Virtual Display Driver')
    if ($matchingDevices.Count -ne 1 -or $matchingDevices[0].InstanceId -ne $InstanceId) {
        throw 'Exactly one present Virtual Display Driver matching DisplayInstanceId is required.'
    }
}
