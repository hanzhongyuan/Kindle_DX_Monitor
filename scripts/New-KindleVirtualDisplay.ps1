param(
    [Parameter(Mandatory = $true)]
    [string]$DriverDirectory
)

$ErrorActionPreference = 'Stop'
$hardwareId = 'Root\MttVDD'
$displayClass = '{4D36E968-E325-11CE-BFC1-08002BE10318}'
$existing = @(Get-PnpDevice | Where-Object {
    $_.FriendlyName -eq 'Virtual Display Driver' -or $_.InstanceId -match 'MTTVDD'
})
if ($existing.Count) { throw 'A Virtual Display Driver already exists. Inspect it before installing.' }
$driverDirectory = (Resolve-Path -LiteralPath $DriverDirectory).Path
$inf = Join-Path $driverDirectory 'MttVDD.inf'
foreach ($file in @('mttvdd.cat', 'MttVDD.dll')) {
    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $driverDirectory $file)
    if ($signature.Status -ne 'Valid') { throw "Invalid signature on $file" }
}
if ((Get-Content -LiteralPath $inf -Raw) -notmatch 'Root\\MttVDD') {
    throw 'Unexpected driver hardware ID.'
}

# Record this computer's displays before installing anything. Never replace an
# existing baseline: it protects adapters that predate this project.
$baselinePath = Join-Path $PSScriptRoot 'display-baseline.json'
if (Test-Path -LiteralPath $baselinePath) {
    $baseline = @(Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json)
    if ($baseline.Count -eq 0 -or @($baseline | Where-Object { -not $_.DeviceID }).Count -gt 0) {
        throw 'Existing display-baseline.json is invalid; inspect it before installing.'
    }
} else {
    $baseline = @(& (Join-Path $PSScriptRoot 'virtual-display-mode.ps1') -Action List)
    if ($baseline.Count -eq 0) { throw 'Could not enumerate pre-install displays.' }
    [IO.File]::WriteAllText($baselinePath, ($baseline | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}

# Stage the catalog-verified package first. Windows must accept its signature.
& pnputil.exe /add-driver $inf
if ($LASTEXITCODE -ne 0) { throw "Driver staging failed: $LASTEXITCODE" }

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class KindleRootDevice {
    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }
    [DllImport("setupapi.dll", SetLastError=true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid guid, IntPtr parent);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string name,
        ref Guid guid, string description, IntPtr parent, uint flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set,
        ref SP_DEVINFO_DATA data, uint property, byte[] buffer, uint size);
    [DllImport("setupapi.dll", SetLastError=true)]
    private static extern bool SetupDiCallClassInstaller(uint function,
        IntPtr set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set,
        ref SP_DEVINFO_DATA data, StringBuilder id, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError=true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    private static void Check(bool success, string operation) {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }
    public static string Create(string classId, string hardwareId) {
        Guid guid = new Guid(classId);
        IntPtr set = SetupDiCreateDeviceInfoList(ref guid, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            SP_DEVINFO_DATA data = new SP_DEVINFO_DATA();
            data.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
            Check(SetupDiCreateDeviceInfoW(set, "Display", ref guid,
                "Kindle DX Extended Display", IntPtr.Zero, 1, ref data), "Create devnode");
            byte[] ids = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            Check(SetupDiSetDeviceRegistryPropertyW(set, ref data, 1,
                ids, (uint)ids.Length), "Set hardware ID");
            Check(SetupDiCallClassInstaller(0x19, set, ref data), "Register devnode");
            StringBuilder id = new StringBuilder(512);
            uint required;
            Check(SetupDiGetDeviceInstanceIdW(set, ref data, id, 512, out required), "Read instance ID");
            return id.ToString();
        } finally {
            SetupDiDestroyDeviceInfoList(set);
        }
    }
}
'@

$instanceId = [KindleRootDevice]::Create($displayClass, $hardwareId)
$statePath = Join-Path (Split-Path $PSScriptRoot -Parent) 'virtual-display-install.json'
@{ InstanceId=$instanceId; HardwareId=$hardwareId; InfPath=$inf; Status='Created' } |
    ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8

& pnputil.exe /add-driver $inf /install
$installExit = $LASTEXITCODE
if ($installExit -notin @(0, 3010)) {
    & pnputil.exe /remove-device $instanceId
    throw "Driver binding failed ($installExit); removed only the new devnode $instanceId."
}
@{ InstanceId=$instanceId; HardwareId=$hardwareId; InfPath=$inf;
    Status='Installed'; RebootRequested=($installExit -eq 3010) } |
    ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
Get-PnpDevice -InstanceId $instanceId | Select-Object FriendlyName, InstanceId, Status
