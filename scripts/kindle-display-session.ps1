[CmdletBinding()]
param([ValidateSet('Start', 'Stop')] [string]$Action = 'Start')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'kindle-config.ps1')
$configuration = Get-KindleConfiguration
$knownHosts = ([IO.Path]::GetFullPath((Join-Path $projectRoot 'runtime\kindle_known_hosts'))).Replace('\', '/')
$sshExe = Join-Path $env:WINDIR 'System32\OpenSSH\ssh.exe'
$pwshExe = $configuration.PowerShellPath
$vncScript = Join-Path $PSScriptRoot 'kindle-vnc-server.ps1'
$displayScript = Join-Path $PSScriptRoot 'virtual-display-mode.ps1'
$displayInstance = $configuration.DisplayInstanceId
$remoteScript = '/mnt/us/kindlevncviewer/kindle-display.sh'
$pythonExe = $configuration.PythonPath
$controllerPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'kindle-display-controller.py'))
$runtimeDirectory = Join-Path $projectRoot 'runtime'
$stopPath = Join-Path $runtimeDirectory 'kindle-display.stop'

function Test-OurControllerCommand([string]$CommandLine) {
    if (-not $CommandLine) { return $false }
    $normalized = $CommandLine.Replace('\', '/').Replace('"', '')
    $expected = [regex]::Escape($pythonExe.Replace('\', '/')) + '\s+' +
        [regex]::Escape($controllerPath.Replace('\', '/'))
    # This launcher uses exactly the Python executable and this script, with no
    # arguments. In particular --once-status is never treated as a live session.
    return $normalized -match ('(?i)^\s*' + $expected + '\s*$')
}

function Get-OurControllers {
    foreach ($details in @(Get-CimInstance Win32_Process -Filter "Name = 'python.exe'")) {
        if ($details.ExecutablePath -ne $pythonExe -or -not (Test-OurControllerCommand $details.CommandLine)) { continue }
        $process = Get-Process -Id ([int]$details.ProcessId) -ErrorAction SilentlyContinue
        if ($null -eq $process -or $process.Path -ne $pythonExe) { continue }
        [pscustomobject]@{
            ProcessId = $process.Id
            StartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks.ToString()
            CommandLine = $details.CommandLine
        }
    }
}

function Get-ExactController($Identity) {
    $process = Get-Process -Id ([int]$Identity.ProcessId) -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    $details = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + [int]$Identity.ProcessId)
    if ($process.Path -ne $pythonExe -or
        $process.StartTime.ToUniversalTime().Ticks.ToString() -ne $Identity.StartTimeUtcTicks -or
        $null -eq $details -or $details.CommandLine -cne $Identity.CommandLine -or
        -not (Test-OurControllerCommand $details.CommandLine)) {
        throw 'Controller identity changed; refusing to control or clean up its resources.'
    }
    return $process
}

function Stop-OurControllers($Controllers) {
    if ($Controllers.Count -eq 0) { return }
    foreach ($identity in $Controllers) { $null = Get-ExactController $identity }
    [IO.File]::WriteAllText($stopPath, [DateTime]::UtcNow.ToString('o'))
    foreach ($identity in $Controllers) {
        $process = Get-ExactController $identity
        if ($null -ne $process -and -not $process.WaitForExit(45000)) {
            throw 'The Kindle controller is still stopping. VNC and the virtual display were left running; try Stop again.'
        }
    }
}

function Test-OurSshCommand([string]$CommandLine) {
    if (-not $CommandLine) { return $false }
    $normalized = $CommandLine.Replace('\', '/').Replace('"', '')
    # Exact tokens identify this project's USB session, not arbitrary ssh.exe.
    $required = @(
        ('(?i)(?:^|\s)UserKnownHostsFile=' + [regex]::Escape($knownHosts) + '(?:\s|$)'),
        '(?:^|\s)root@192\.168\.2\.2(?:\s|$)',
        ('(?:^|\s)' + [regex]::Escape($remoteScript) + '(?:\s|$)'),
        '(?:^|\s)127\.0\.0\.1:5905:127\.0\.0\.1:5905(?:\s|$)',
        '(?:^|\s)127\.0\.0\.1:5(?:\s|$)'
    )
    foreach ($pattern in $required) { if ($normalized -notmatch $pattern) { return $false } }
    return $true
}

function Get-OurSshSessions {
    foreach ($details in @(Get-CimInstance Win32_Process -Filter "Name = 'ssh.exe'")) {
        if ($details.ExecutablePath -ne $sshExe -or -not (Test-OurSshCommand $details.CommandLine)) { continue }
        $process = Get-Process -Id ([int]$details.ProcessId) -ErrorAction SilentlyContinue
        if ($null -eq $process -or $process.Path -ne $sshExe) { continue }
        [pscustomobject]@{
            ProcessId = $process.Id
            StartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks.ToString()
            CommandLine = $details.CommandLine
        }
    }
}

function Stop-ExactSshSession($Identity) {
    $process = Get-Process -Id ([int]$Identity.ProcessId) -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }
    $details = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + [int]$Identity.ProcessId)
    if ($process.Path -ne $sshExe -or
        $process.StartTime.ToUniversalTime().Ticks.ToString() -ne $Identity.StartTimeUtcTicks -or
        $null -eq $details -or $details.CommandLine -cne $Identity.CommandLine -or
        -not (Test-OurSshCommand $details.CommandLine)) {
        throw 'SSH identity changed; refusing to stop the process.'
    }
    $process.Kill()
    if (-not $process.WaitForExit(10000)) { throw 'The matching SSH session did not stop.' }
}

function Assert-DedicatedDisplay {
    Assert-KindleDisplayInstance $displayInstance
}

function Clear-SessionResources([bool]$StopVnc, [bool]$DisableDisplay) {
    $cleanupMutex = [Threading.Mutex]::new($false, 'Local\KindleView-DisplaySession-Cleanup')
    $cleanupLocked = $false
    try {
        try { $cleanupLocked = $cleanupMutex.WaitOne(15000) }
        catch [Threading.AbandonedMutexException] { $cleanupLocked = $true }
        if (-not $cleanupLocked) { throw 'Another session cleanup is still running.' }
        if ($StopVnc) { & $vncScript -Action Stop | Out-Null }
        if ($DisableDisplay) {
            Assert-DedicatedDisplay
            $problem = (Get-PnpDeviceProperty -InstanceId $displayInstance `
                -KeyName 'DEVPKEY_Device_ProblemCode').Data
            if ($problem -ne 22) {
                Disable-PnpDevice -InstanceId $displayInstance -Confirm:$false -ErrorAction Stop
            }
        }
    } finally {
        if ($cleanupLocked) { $cleanupMutex.ReleaseMutex() }
        $cleanupMutex.Dispose()
    }
}

$sessions = @(Get-OurSshSessions)
$controllers = @(Get-OurControllers)
if ($Action -eq 'Start' -and ($sessions.Count -gt 0 -or $controllers.Count -gt 0)) {
    $existingIds = @($sessions | ForEach-Object ProcessId) + @($controllers | ForEach-Object ProcessId)
    throw ('This Kindle display session is already running (PID ' + ($existingIds -join ', ') + '). Nothing was changed.')
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Administrator access is needed only to enable/disable this project''s dedicated virtual display driver.'
    # The launcher displays the UAC result; the persistent controller is hidden.
    $elevated = Start-Process -FilePath $pwshExe -Verb RunAs -WindowStyle Hidden `
        -ArgumentList @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            ('"' + $PSCommandPath + '"'), '-Action', $Action) -Wait -PassThru
    exit $elevated.ExitCode
}

$startMutex = [Threading.Mutex]::new($false, 'Local\KindleView-DisplaySession-StartStop')
$startLocked = $false
try {
    try { $startLocked = $startMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $startLocked = $true }
    if (-not $startLocked) { throw 'Another Kindle display Start/Stop command is active. Nothing was changed.' }

    # Recheck under the shared mutex, including after UAC.
    $sessions = @(Get-OurSshSessions)
    $controllers = @(Get-OurControllers)
    if ($Action -eq 'Stop') {
        Stop-OurControllers $controllers
        # Retain exact matching of the previous interactive OpenSSH launcher
        # while transitioning an existing session to the background controller.
        foreach ($session in $sessions) { Stop-ExactSshSession $session }
        if (@(Get-OurControllers).Count -gt 0 -or @(Get-OurSshSessions).Count -gt 0) {
            throw 'A Kindle session is still running; display resources were left intact.'
        }
        Clear-SessionResources -StopVnc $true -DisableDisplay $true
        Write-Host 'Kindle display stopped; PC VNC settings restored and the dedicated display disabled.'
        return
    }
    if ($sessions.Count -gt 0 -or $controllers.Count -gt 0) {
        throw 'This Kindle display session is already running. Nothing was changed.'
    }

# These checks happen before the try/finally claims resources, so an existing
# session or server cannot be stopped by an unsuccessful duplicate Start.
$vncBefore = & $vncScript -Action Status
if ($vncBefore.Running -or $vncBefore.OtherServerProcessIds.Count -gt 0 -or $vncBefore.RegistryRestorePending) {
    throw 'A VNC instance or pending configuration already exists. Use the matching Stop command first; nothing was changed.'
}
if (-not (Test-Path -LiteralPath $knownHosts -PathType Leaf)) { throw 'The trusted Kindle known_hosts file is missing.' }
if (-not (Test-Path -LiteralPath $pythonExe -PathType Leaf)) { throw 'The configured Python executable is missing.' }
if (-not (Test-Path -LiteralPath $controllerPath -PathType Leaf)) { throw 'The Kindle connection controller is missing.' }
$usbAddress = @(Get-NetIPAddress -AddressFamily IPv4 -IPAddress '192.168.2.1' -ErrorAction SilentlyContinue)
$usbIndices = @($usbAddress | ForEach-Object InterfaceIndex)
$usbAdapters = @(Get-NetAdapter | Where-Object {
    $_.InterfaceIndex -in $usbIndices -and $_.InterfaceDescription -match '(?i)RNDIS|Remote NDIS' -and $_.Status -eq 'Up'
})
if ($usbAdapters.Count -eq 0) {
    throw 'Connect the Kindle in USBNetwork mode first. An active USB RNDIS adapter with address 192.168.2.1 is required; no network settings were changed.'
}
Assert-DedicatedDisplay

$displayClaimed = $false
$vncClaimed = $false
$controllerIdentity = $null
$launched = $false
try {
    $displayClaimed = $true
    $problem = (Get-PnpDeviceProperty -InstanceId $displayInstance -KeyName 'DEVPKEY_Device_ProblemCode').Data
    if ($problem -eq 22) {
        Enable-PnpDevice -InstanceId $displayInstance -Confirm:$false -ErrorAction Stop
    } elseif ($problem -ne 0) { throw "Dedicated virtual display has unexpected problem code $problem." }

    $display = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $displayMatches = @(& $displayScript -Action List | Where-Object {
            $_.DeviceID -match '(?i)MttVDD' -and $_.Adapter -eq 'Virtual Display Driver'
        })
        $attached = @($displayMatches | Where-Object Attached)
        if ($attached.Count -eq 1) { $display = $attached[0]; break }
        if ($displayMatches.Count -eq 1) { $display = $displayMatches[0]; break }
        Start-Sleep -Milliseconds 500
    }
    if ($null -eq $display) { throw 'The dedicated virtual display did not enumerate uniquely.' }
    if ($null -eq $display.Mode -or -not $display.Attached -or
        $display.Mode.Width -ne 824 -or $display.Mode.Height -ne 1200 -or
        $display.Mode.X -ne $configuration.PositionX -or $display.Mode.Y -ne $configuration.PositionY -or $display.Mode.Frequency -ne 60) {
        & $displayScript -Action Set -DisplayName $display.DisplayName -PositionX $configuration.PositionX -PositionY $configuration.PositionY | Out-Null
    }
    $vncClaimed = $true
    $vncStarted = & $vncScript -Action Start
    if (-not $vncStarted.Running) { throw 'PC VNC server did not start.' }

    if (Test-Path -LiteralPath $stopPath) { Remove-Item -LiteralPath $stopPath -Force }
    # Existing empty-password USB authentication and the pinned known_hosts
    # entry are used by the controller. It never installs keys or credentials.
    $controllerProcess = Start-Process -FilePath $pythonExe -WindowStyle Hidden `
        -WorkingDirectory $projectRoot -ArgumentList ('"' + $controllerPath + '"') `
        -RedirectStandardOutput (Join-Path $runtimeDirectory 'controller.stdout.log') `
        -RedirectStandardError (Join-Path $runtimeDirectory 'controller.stderr.log') -PassThru
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        $controllerIdentity = @(Get-OurControllers | Where-Object ProcessId -EQ $controllerProcess.Id) | Select-Object -First 1
        if ($null -ne $controllerIdentity -or $controllerProcess.HasExited) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($null -eq $controllerIdentity) { throw 'Could not verify the newly started Kindle controller. Check runtime/controller.stderr.log.' }
    Start-Sleep -Milliseconds 1200
    if ($null -eq (Get-ExactController $controllerIdentity)) { throw 'The Kindle controller exited during startup. Check runtime/controller.stderr.log.' }
    $launched = $true
    Write-Host ('Kindle display controller started in the background (PID ' + $controllerProcess.Id + ').')
    Write-Host 'The background connection is active. Select a direction in KindleDisplay.exe; USB reconnection is automatic.'
    Write-Host 'Use Disconnect in KindleDisplay.exe to stop the display.'
} finally {
    # After a successful launch the persistent controller keeps the session;
    # only failed startup rolls back here. Normal cleanup belongs to Stop.
    if (-not $launched) {
        if ($null -ne $controllerIdentity) { Stop-OurControllers @($controllerIdentity) }
        Clear-SessionResources -StopVnc $vncClaimed -DisableDisplay $displayClaimed
    }
}
} finally {
    if ($startLocked) { $startMutex.ReleaseMutex() }
    $startMutex.Dispose()
}
