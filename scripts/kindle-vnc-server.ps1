[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop', 'Status', 'Resize')]
    [string]$Action = 'Status',
    [ValidateSet(824, 1200)] [int]$Width = 824,
    [ValidateSet(824, 1200)] [int]$Height = 1200
)

# Application mode only. No service, startup, firewall, or password changes.
# Start/Stop own a specific PID + creation time. Status is read-only.
# Loopback RFB is intended to be reached through the separate USB SSH tunnel.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'kindle-config.ps1')
$configuration = Get-KindleConfiguration
$serverExe = $configuration.VncServerPath
$runtimeDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\runtime'))
$statePath = Join-Path $runtimeDirectory 'kindle-vnc-server.json'
$registrySubKey = 'Software\TightVNC\Server'
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$shareRectangle = '{0}x{1}{2}{3}' -f $Width, $Height, $configuration.PositionX.ToString('+0;-0;+0'), $configuration.PositionY.ToString('+0;-0;+0')
if ($Action -in @('Start', 'Resize') -and $Width -eq $Height) {
    throw 'Supported Kindle sizes are 824x1200 and 1200x824.'
}
$desired = [ordered]@{
    LoopbackOnly = 1
    AllowLoopback = 1
    RfbPort = 5905
    AcceptRfbConnections = 1
    AcceptHttpConnections = 0
    ExtraPorts = ''
    UseVncAuthentication = 0
    BlockRemoteInput = 1
    EnableFileTransfers = 0
    BlockLocalInput = 0
    BlankScreen = 0
    RemoveWallpaper = 0
    DisconnectAction = 0
    PollingInterval = 500
}
# TightVNC 2.8.81 has no EnableClipboard registry setting. Its ClipboardExchange
# suppresses both directions for view-only clients. BlockRemoteInput is set before
# any client connects; no nonexistent clipboard setting is written here.

function Read-State {
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { return $null }
    $saved = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($saved.SchemaVersion -ne 1 -or $saved.OwnerSid -ne $currentSid -or $saved.ServerExe -ne $serverExe) {
        throw 'The state file does not belong to this script/user/executable.'
    }
    return $saved
}

function Save-State($Saved) {
    if (-not (Test-Path -LiteralPath $runtimeDirectory)) {
        New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
    }
    $temporaryPath = Join-Path $runtimeDirectory ('kindle-vnc-server.' + [guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporaryPath, ($Saved | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $statePath -Force
}

function Get-ServerProcesses {
    @(Get-Process -Name tvnserver -ErrorAction SilentlyContinue)
}

function Get-OwnedProcess($Saved) {
    if ($null -eq $Saved -or $null -eq $Saved.ProcessId) { return $null }
    $candidate = Get-Process -Id ([int]$Saved.ProcessId) -ErrorAction SilentlyContinue
    if ($null -eq $candidate) { return $null }
    if ($candidate.ProcessName -ne 'tvnserver' -or $candidate.Path -ne $serverExe -or
        $candidate.StartTime.ToUniversalTime().Ticks.ToString() -ne $Saved.StartTimeUtcTicks -or
        $candidate.SessionId -ne $Saved.SessionId) {
        throw 'Recorded PID has been reused or does not match our process. Refusing to control it.'
    }
    return $candidate
}

function Get-VerifiedSlaveProcess($Saved, $Identity) {
    if ($Identity.ParentProcessId -ne $Saved.ProcessId -or
        $Identity.ParentStartTimeUtcTicks -ne $Saved.StartTimeUtcTicks) { return $null }
    $candidate = Get-Process -Id ([int]$Identity.ProcessId) -ErrorAction SilentlyContinue
    if ($null -eq $candidate) { return $null }
    if ($candidate.ProcessName -ne 'tvnserver' -or $candidate.Path -ne $serverExe -or
        $candidate.StartTime.ToUniversalTime().Ticks.ToString() -ne $Identity.StartTimeUtcTicks -or
        $candidate.SessionId -ne $Saved.SessionId) { return $null }
    $details = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + [int]$candidate.Id)
    $slaveCommand = '^"?' + [regex]::Escape($serverExe) + '"?\s+-controlapp\s+-slave\s*$'
    if ($null -eq $details -or $details.ParentProcessId -ne $Saved.ProcessId -or
        $details.ExecutablePath -ne $serverExe -or $details.CommandLine -notmatch $slaveCommand) { return $null }
    return $candidate
}

function Get-OwnedSlaveIdentities($Saved) {
    if ($null -eq $Saved) { return }
    $main = Get-OwnedProcess $Saved
    if ($null -ne $main) {
        # Only exact official controlapp/slave children of the verified live main
        # process qualify. Other tvnserver processes, even descendants, do not.
        $children = @(Get-CimInstance Win32_Process -Filter (
            "Name = 'tvnserver.exe' AND ParentProcessId = " + [int]$main.Id))
        foreach ($child in $children) {
            $process = Get-Process -Id ([int]$child.ProcessId) -ErrorAction SilentlyContinue
            if ($null -eq $process) { continue }
            $ticks = $process.StartTime.ToUniversalTime().Ticks
            if ($ticks -lt [long]$Saved.StartTimeUtcTicks) { continue }
            $identity = [pscustomobject]@{
                ProcessId = $process.Id
                StartTimeUtcTicks = $ticks.ToString()
                ParentProcessId = $main.Id
                ParentStartTimeUtcTicks = $Saved.StartTimeUtcTicks
            }
            if ($null -ne (Get-VerifiedSlaveProcess $Saved $identity)) { $identity }
        }
    } elseif ($Saved.PSObject.Properties['SlaveProcesses']) {
        # A Stop interrupted after the main exited can finish only children that
        # were recorded while the original PID/start-time pair was still live.
        foreach ($identity in $Saved.SlaveProcesses) {
            if ($null -ne (Get-VerifiedSlaveProcess $Saved $identity)) { $identity }
        }
    }
}

function Stop-OwnedProcesses($Saved) {
    $main = Get-OwnedProcess $Saved
    $slaves = @(Get-OwnedSlaveIdentities $Saved)
    $Saved | Add-Member -NotePropertyName SlaveProcesses -NotePropertyValue $slaves -Force
    Save-State $Saved
    if ($null -ne $main) {
        $main.Kill()
        if (-not $main.WaitForExit(10000)) { throw 'Owned server did not exit. Configuration backup was retained.' }
    }
    foreach ($identity in $slaves) {
        $slave = Get-VerifiedSlaveProcess $Saved $identity
        if ($null -eq $slave) { continue }
        # Its normal response to the main exiting is to exit itself.
        if ($slave.WaitForExit(3000)) { continue }
        $slave = Get-VerifiedSlaveProcess $Saved $identity
        if ($null -ne $slave) {
            $slave.Kill()
            if (-not $slave.WaitForExit(10000)) { throw 'Owned slave did not exit. Configuration backup was retained.' }
        }
    }
}

function Get-ProcessListeners([int]$ServerProcessId) {
    @(Get-NetTCPConnection -State Listen -ErrorAction Stop |
        Where-Object OwningProcess -EQ $ServerProcessId |
        Select-Object LocalAddress, LocalPort, OwningProcess)
}

function Test-LoopbackListeners($Listeners) {
    foreach ($listener in $Listeners) {
        $address = [Net.IPAddress]::Parse($listener.LocalAddress)
        if ($address.IsIPv4MappedToIPv6) { $address = $address.MapToIPv4() }
        if (-not [Net.IPAddress]::IsLoopback($address)) { return $false }
    }
    return $true
}

function Set-KindleDisplayMode {
    Assert-KindleDisplayInstance $configuration.DisplayInstanceId
    $displayHelper = Join-Path $PSScriptRoot 'virtual-display-mode.ps1'
    $targets = @(& $displayHelper -Action List | Where-Object {
        $_.DeviceID -match '(?i)MttVDD' -and -not $_.Primary -and $_.Adapter -eq 'Virtual Display Driver'
    })
    $attached = @($targets | Where-Object Attached)
    if ($attached.Count -eq 1) { $target = $attached[0] }
    elseif ($targets.Count -eq 1) { $target = $targets[0] }
    else { throw 'Cannot identify exactly one dedicated Kindle display.' }
    if (-not $target.Attached -or $null -eq $target.Mode -or
        $target.Mode.Width -ne $Width -or $target.Mode.Height -ne $Height -or
        $target.Mode.X -ne $configuration.PositionX -or $target.Mode.Y -ne $configuration.PositionY -or $target.Mode.Frequency -ne 60) {
        $change = & $displayHelper -Action Set -DisplayName $target.DisplayName `
            -Width $Width -Height $Height -Frequency 60 -PositionX $configuration.PositionX -PositionY $configuration.PositionY
        if ($change.OtherDisplaysChanged.Count -gt 0) { throw 'The driver unexpectedly changed another display.' }
    }
    $verified = & $displayHelper -Action List -DisplayName $target.DisplayName
    if (-not $verified.Attached -or $null -eq $verified.Mode -or
        $verified.Mode.Width -ne $Width -or $verified.Mode.Height -ne $Height -or
        $verified.Mode.X -ne $configuration.PositionX -or $verified.Mode.Y -ne $configuration.PositionY) {
        throw 'Dedicated display mode verification failed.'
    }
}

function Set-SharedRectangle {
    $control = Start-Process -FilePath $serverExe `
        -ArgumentList @('-controlapp', '-sharerect', $shareRectangle) -WindowStyle Hidden -PassThru
    if (-not $control.WaitForExit(10000)) {
        $control.Kill()
        throw 'TightVNC share-rectangle control command timed out.'
    }
    if ($control.ExitCode -ne 0) { throw ('TightVNC share-rectangle command failed: ' + $control.ExitCode) }
}

function Read-RfbBytes($Stream, [int]$Count) {
    $buffer = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $received = $Stream.Read($buffer, $offset, $Count - $offset)
        if ($received -le 0) { throw 'RFB connection closed before ServerInit.' }
        $offset += $received
    }
    return ,$buffer
}

function Get-RfbSize {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        if (-not $client.ConnectAsync('127.0.0.1', 5905).Wait(3000)) { throw 'RFB connection timed out.' }
        $stream = $client.GetStream()
        $stream.ReadTimeout = 3000
        $stream.WriteTimeout = 3000
        $banner = [Text.Encoding]::ASCII.GetString((Read-RfbBytes $stream 12))
        if ($banner -notmatch '^RFB 003\.00[78]\n$') { throw "Unexpected RFB version: $banner" }
        $reply = [Text.Encoding]::ASCII.GetBytes("RFB 003.008`n")
        $stream.Write($reply, 0, $reply.Length)
        $count = (Read-RfbBytes $stream 1)[0]
        if ($count -eq 0) { throw 'RFB server rejected the connection.' }
        $types = Read-RfbBytes $stream $count
        if (1 -notin $types) { throw 'Expected loopback-only no-auth RFB mode is unavailable.' }
        $stream.WriteByte(1)
        $security = Read-RfbBytes $stream 4
        if (@($security | Where-Object { $_ -ne 0 }).Count -gt 0) { throw 'RFB security negotiation failed.' }
        $stream.WriteByte(1) # Shared, read-only metadata probe; no framebuffer/input request.
        $header = Read-RfbBytes $stream 24
        [pscustomobject]@{ Width=([int]$header[0] * 256 + $header[1]); Height=([int]$header[2] * 256 + $header[3]) }
    } finally { $client.Dispose() }
}

function Assert-RfbSize {
    $lastFailure = $null
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        try {
            $actual = Get-RfbSize
            if ($actual.Width -eq $Width -and $actual.Height -eq $Height) { return }
            $lastFailure = "RFB reports $($actual.Width)x$($actual.Height), expected ${Width}x${Height}."
        } catch { $lastFailure = $_.Exception.Message }
        Start-Sleep -Milliseconds 300
    }
    throw $lastFailure
}

function Get-ConfigBackup {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registrySubKey, $false)
    try {
        foreach ($name in $desired.Keys) {
            # Only this fixed allowlist is read. Never enumerate/read Password values.
            $exists = $false
            $kind = $null
            $value = $null
            if ($null -ne $key) {
                try {
                    $kind = $key.GetValueKind($name).ToString()
                    $value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    $exists = $true
                } catch [System.IO.IOException] { }
            }
            [pscustomobject]@{ Name=$name; Existed=$exists; Kind=$kind; Value=$value }
        }
    } finally { if ($null -ne $key) { $key.Dispose() } }
}

function Set-SessionConfig {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registrySubKey, $true)
    try {
        foreach ($name in $desired.Keys) {
            if ($name -eq 'ExtraPorts') {
                $key.SetValue($name, [string]$desired[$name], [Microsoft.Win32.RegistryValueKind]::String)
            } else {
                $key.SetValue($name, [int]$desired[$name], [Microsoft.Win32.RegistryValueKind]::DWord)
            }
        }
    } finally { $key.Dispose() }
}

function Restore-SessionConfig($Saved) {
    $entries = @($Saved.ConfigBackup)
    if ($entries.Count -ne $desired.Count -or
        @($entries | Where-Object { $_.Name -notin $desired.Keys }).Count -ne 0 -or
        @($entries.Name | Select-Object -Unique).Count -ne $desired.Count) {
        throw 'Backup contains an unexpected registry value name; refusing restoration.'
    }
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registrySubKey, $true)
    try {
        foreach ($entry in $entries) {
            if ($entry.Existed) {
                $kind = [Microsoft.Win32.RegistryValueKind]::$($entry.Kind)
                $value = $entry.Value
                if ($kind -eq [Microsoft.Win32.RegistryValueKind]::DWord) { $value = [int]$value }
                elseif ($kind -eq [Microsoft.Win32.RegistryValueKind]::QWord) { $value = [long]$value }
                elseif ($kind -eq [Microsoft.Win32.RegistryValueKind]::Binary) { $value = [byte[]]$value }
                elseif ($kind -eq [Microsoft.Win32.RegistryValueKind]::MultiString) { $value = [string[]]$value }
                $key.SetValue($entry.Name, $value, $kind)
            } else {
                $key.DeleteValue($entry.Name, $false)
            }
        }
    } finally { $key.Dispose() }
    $Saved.ConfigActive = $false
    $Saved.Status = 'Stopped'
    Save-State $Saved
}

function Get-Status($Saved) {
    $owned = Get-OwnedProcess $Saved
    $slaves = @(Get-OwnedSlaveIdentities $Saved)
    $ownedIds = @($slaves | ForEach-Object ProcessId)
    if ($null -ne $owned) { $ownedIds += $owned.Id }
    $allProcesses = @(Get-ServerProcesses)
    $listeners = @($ownedIds | ForEach-Object { Get-ProcessListeners $_ })
    [pscustomobject]@{
        Action = 'Status'
        Running = ($null -ne $owned)
        OwnedProcessId = $(if ($null -ne $owned) { $owned.Id } else { $null })
        OwnedSlaveProcessIds = @($slaves | ForEach-Object ProcessId)
        RecordedState = $(if ($null -ne $Saved) { $Saved.Status } else { 'NotStarted' })
        RegistryRestorePending = ($null -ne $Saved -and $Saved.ConfigActive)
        OtherServerProcessIds = @($allProcesses | Where-Object { $_.Id -notin $ownedIds } | ForEach-Object Id)
        Listeners = $listeners
        AllListenersLoopback = (Test-LoopbackListeners $listeners)
        RfbPort = 5905
        ShareRectangle = $(if ($null -ne $Saved) { $Saved.ShareRectangle } else { $null })
        StateFile = $statePath
    }
}

$state = Read-State
if ($Action -eq 'Status') {
    Get-Status $state
    return
}

# Serialize our own Start/Stop/Resize invocations. A second script cannot replace backup
# state while the first is starting or restoring the session.
$mutex = [Threading.Mutex]::new($false, ('Local\KindleView-TightVNC-' + $currentSid))
$locked = $false
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another Kindle VNC Start/Stop/Resize operation is running.' }
    $state = Read-State
    $owned = Get-OwnedProcess $state
    $slaves = @(Get-OwnedSlaveIdentities $state)
    $ownedIds = @($slaves | ForEach-Object ProcessId)
    if ($null -ne $owned) { $ownedIds += $owned.Id }
    $foreign = @(Get-ServerProcesses | Where-Object { $_.Id -notin $ownedIds })
    if ($foreign.Count -gt 0) {
        throw ('An unrelated tvnserver process already exists; refusing to control it or change its configuration. PID(s): ' + ($foreign.Id -join ', '))
    }

    if ($Action -eq 'Stop') {
        if ($null -ne $state) {
            # Stop only the main and verified official slave; no global shutdown.
            Stop-OwnedProcesses $state
        }
        if (@(Get-ServerProcesses).Count -gt 0) { throw 'Another server appeared; registry backup was retained.' }
        if ($null -ne $state -and $state.ConfigActive) { Restore-SessionConfig $state }
        Get-Status (Read-State)
        return
    }

    if ($Action -eq 'Resize') {
        if ($null -eq $owned) { throw 'Resize requires the recorded VNC server to be running.' }
        $listeners = @($ownedIds | ForEach-Object { Get-ProcessListeners $_ })
        if (-not (Test-LoopbackListeners $listeners) -or @($listeners | Where-Object LocalPort -EQ 5905).Count -eq 0) {
            throw 'Resize requires the owned server to listen only on loopback, including port 5905.'
        }
        Set-KindleDisplayMode
        Set-SharedRectangle
        Assert-RfbSize
        $state.ShareRectangle = $shareRectangle
        Save-State $state
        Get-Status $state
        return
    }

    if ($null -ne $owned) {
        $currentListeners = @($ownedIds | ForEach-Object { Get-ProcessListeners $_ })
        if (-not (Test-LoopbackListeners $currentListeners)) { throw 'Owned server has a non-loopback listener; run Stop.' }
        Get-Status $state
        return
    }
    if ($null -ne $state -and $state.ConfigActive) {
        throw 'A previous session left a pending registry backup. Run Stop to restore it before Start.'
    }
    if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) { throw 'TightVNC server executable is missing.' }
    if ((Get-AuthenticodeSignature -LiteralPath $serverExe).Status -ne 'Valid') {
        throw 'TightVNC executable signature validation failed.'
    }
    if (@(Get-NetTCPConnection -State Listen -LocalPort 5905 -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'TCP port 5905 is already occupied.'
    }
    Set-KindleDisplayMode

    $state = [pscustomobject]@{
        SchemaVersion = 1
        OwnerSid = $currentSid
        ServerExe = $serverExe
        ProcessId = $null
        StartTimeUtcTicks = $null
        SessionId = $null
        Status = 'Prepared'
        ConfigActive = $true
        ConfigBackup = @(Get-ConfigBackup)
        ShareRectangle = $shareRectangle
        PreparedUtc = [DateTime]::UtcNow.ToString('o')
    }
    # The original values are durable before any registry write.
    Save-State $state
    try {
        Set-SessionConfig
        $process = Start-Process -FilePath $serverExe -ArgumentList '-run' -WindowStyle Hidden -PassThru
        $state.ProcessId = $process.Id
        $state.StartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks.ToString()
        $state.SessionId = $process.SessionId
        $state.Status = 'Starting'
        Save-State $state

        $ready = $false
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            $process.Refresh()
            if ($process.HasExited) { throw 'TightVNC exited before its listener became ready.' }
            $listeners = @(Get-ProcessListeners $process.Id)
            if (-not (Test-LoopbackListeners $listeners)) { throw 'A non-loopback listener was detected; aborting.' }
            if (@($listeners | Where-Object LocalPort -EQ 5905).Count -gt 0) { $ready = $true; break }
            Start-Sleep -Milliseconds 250
        }
        if (-not $ready) { throw 'TightVNC did not open loopback TCP port 5905.' }

        Set-SharedRectangle
        $owned = Get-OwnedProcess $state
        if ($null -eq $owned) { throw 'TightVNC exited while setting the shared rectangle.' }
        $listeners = @(Get-ProcessListeners $owned.Id)
        if (-not (Test-LoopbackListeners $listeners) -or @($listeners | Where-Object LocalPort -EQ 5905).Count -eq 0) {
            throw 'Final loopback listener verification failed.'
        }
        Assert-RfbSize
        $state.Status = 'Running'
        $state | Add-Member -NotePropertyName SlaveProcesses -NotePropertyValue @(Get-OwnedSlaveIdentities $state) -Force
        Save-State $state
        Get-Status $state
    } catch {
        $startError = $_
        Stop-OwnedProcesses $state
        if (@(Get-ServerProcesses).Count -eq 0) { Restore-SessionConfig $state }
        throw $startError
    }
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
