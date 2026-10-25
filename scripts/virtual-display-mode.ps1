[CmdletBinding()]
param(
    [ValidateSet('List', 'Set')]
    [string]$Action = 'List',
    [string]$DisplayName,
    [ValidateRange(1, 16384)] [int]$Width = 824,
    [ValidateRange(1, 16384)] [int]$Height = 1200,
    [ValidateRange(1, 1000)] [int]$Frequency = 60,
    [int]$PositionX = 0,
    [int]$PositionY = 0,
    [string]$BaselinePath = (Join-Path $PSScriptRoot 'display-baseline.json'),
    [switch]$DryRun
)

# Default invocation only enumerates displays. Set requires an exact, newly added
# Virtual Display Driver device. The existing Oray adapter and baseline adapters
# are always rejected. DryRun performs CDS_TEST only and changes no settings.
# Example: pass an exact DisplayName returned by List together with -DryRun.
# The same invocation without -DryRun applies to this named display only.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('KindleView.DisplayNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KindleView {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayDevice {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DevMode {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags;
        public uint dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType;
        public uint dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    public class ModeInfo {
        public uint Width, Height, Frequency, BitsPerPixel;
        public int X, Y;
    }

    public class DisplayInfo {
        public string DisplayName, Adapter, DeviceID;
        public bool Primary, Attached;
        public uint StateFlags;
        public ModeInfo Mode;
    }

    public static class DisplayNative {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayDevicesW(string device, uint index,
            ref DisplayDevice display, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplaySettingsExW(string device, int modeNumber,
            ref DevMode mode, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int ChangeDisplaySettingsExW(string device, ref DevMode mode,
            IntPtr window, uint flags, IntPtr parameter);

        public static DevMode NewMode() {
            var mode = new DevMode();
            mode.dmSize = (ushort)Marshal.SizeOf(typeof(DevMode));
            mode.dmDriverExtra = 0;
            if (mode.dmSize != 220) throw new InvalidOperationException("Unexpected DEVMODEW layout.");
            return mode;
        }

        public static DisplayInfo[] Enumerate() {
            var result = new List<DisplayInfo>();
            for (uint index = 0; ; index++) {
                var device = new DisplayDevice();
                device.cb = (uint)Marshal.SizeOf(typeof(DisplayDevice));
                if (device.cb != 840) throw new InvalidOperationException("Unexpected DISPLAY_DEVICEW layout.");
                if (!EnumDisplayDevicesW(null, index, ref device, 0)) break;
                var mode = NewMode();
                ModeInfo info = null;
                if (EnumDisplaySettingsExW(device.DeviceName, -1, ref mode, 0)) {
                    info = new ModeInfo { Width = mode.dmPelsWidth, Height = mode.dmPelsHeight,
                        X = mode.dmPositionX, Y = mode.dmPositionY,
                        Frequency = mode.dmDisplayFrequency, BitsPerPixel = mode.dmBitsPerPel };
                }
                result.Add(new DisplayInfo { DisplayName = device.DeviceName,
                    Adapter = device.DeviceString, DeviceID = device.DeviceID,
                    Primary = (device.StateFlags & 4) != 0,
                    Attached = (device.StateFlags & 1) != 0,
                    StateFlags = device.StateFlags, Mode = info });
            }
            return result.ToArray();
        }

        public static DevMode SupportedMode(string device, uint width, uint height, uint frequency) {
            for (int index = 0; ; index++) {
                var mode = NewMode();
                if (!EnumDisplaySettingsExW(device, index, ref mode, 0)) break;
                if (mode.dmPelsWidth == width && mode.dmPelsHeight == height &&
                    mode.dmDisplayFrequency == frequency && mode.dmBitsPerPel == 32) return mode;
            }
            throw new InvalidOperationException("Requested 32-bit mode is not advertised by " + device +
                ": " + width + "x" + height + "@" + frequency + ". Configure the virtual driver modes first.");
        }

        public static string ResultName(int result) {
            switch (result) {
                case 0: return "DISP_CHANGE_SUCCESSFUL";
                case 1: return "DISP_CHANGE_RESTART";
                case -1: return "DISP_CHANGE_FAILED";
                case -2: return "DISP_CHANGE_BADMODE";
                case -3: return "DISP_CHANGE_NOTUPDATED";
                case -4: return "DISP_CHANGE_BADFLAGS";
                case -5: return "DISP_CHANGE_BADPARAM";
                case -6: return "DISP_CHANGE_BADDUALVIEW";
                default: return "UNKNOWN_" + result;
            }
        }
    }
}
'@
}

$displays = @([KindleView.DisplayNative]::Enumerate())
if ($Action -eq 'List') {
    if ($DisplayName) {
        $displays | Where-Object DisplayName -EQ $DisplayName
    } else {
        $displays
    }
    return
}

if (-not $DisplayName) { throw 'Set requires an exact -DisplayName from a fresh List invocation.' }
$matchingDisplays = @($displays | Where-Object DisplayName -EQ $DisplayName)
if ($matchingDisplays.Count -ne 1) { throw "DisplayName must identify exactly one enumerated device: $DisplayName" }
$target = $matchingDisplays[0]
if ($target.Primary) { throw 'Refusing to alter the primary display.' }
if ($target.Adapter -match '(?i)oray' -or $target.DeviceID -match '(?i)oray') {
    throw 'Refusing to alter the existing Oray display adapter.'
}
if ($target.Adapter -ne 'Virtual Display Driver' -or $target.DeviceID -notmatch '(?i)MttVDD') {
    throw 'Target is not positively identified as a supported virtual display adapter.'
}
if (-not (Test-Path -LiteralPath $BaselinePath -PathType Leaf)) {
    throw "A pre-install display baseline is required: $BaselinePath"
}
$baseline = @(Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json)
if ($baseline.Count -eq 0) { throw 'The pre-install display baseline is empty.' }
if (@($baseline | Where-Object DeviceID -EQ $target.DeviceID).Count -gt 0) {
    throw 'Refusing to alter an adapter that existed in the pre-install baseline.'
}

. (Join-Path $PSScriptRoot 'kindle-config.ps1')
$configuration = Get-KindleConfiguration
Assert-KindleDisplayInstance $configuration.DisplayInstanceId
if (@($displays | Where-Object { $_.Adapter -eq 'Virtual Display Driver' -and $_.DeviceID -match '(?i)MttVDD' }).Count -ne 1) {
    throw 'Cannot identify exactly one dedicated MttVDD display.'
}
if (-not $PSBoundParameters.ContainsKey('PositionX')) { $PositionX = $configuration.PositionX }
if (-not $PSBoundParameters.ContainsKey('PositionY')) { $PositionY = $configuration.PositionY }

$mode = [KindleView.DisplayNative]::SupportedMode($DisplayName, $Width, $Height, $Frequency)
$mode.dmPositionX = $PositionX
$mode.dmPositionY = $PositionY
# DM_POSITION | DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY.
# CDS_SET_PRIMARY is never supplied. No call targets the default/null device.
$mode.dmFields = [uint32]0x005C0020
$mode.dmDriverExtra = 0
$testResult = [KindleView.DisplayNative]::ChangeDisplaySettingsExW(
    $DisplayName, [ref]$mode, [IntPtr]::Zero, [uint32]2, [IntPtr]::Zero)
if ($testResult -ne 0) {
    throw ('Requested mode failed CDS_TEST: ' + [KindleView.DisplayNative]::ResultName($testResult))
}

$result = [ordered]@{
    DisplayName = $target.DisplayName
    Adapter = $target.Adapter
    DeviceID = $target.DeviceID
    DryRun = [bool]$DryRun
    Before = $target.Mode
    Requested = [ordered]@{ Width=$Width; Height=$Height; X=$PositionX; Y=$PositionY; Frequency=$Frequency }
    TestResult = [KindleView.DisplayNative]::ResultName($testResult)
}
if ($DryRun) {
    [pscustomobject]$result
    return
}

# The targeted call changes only this display, without updating registry defaults.
$applyResult = [KindleView.DisplayNative]::ChangeDisplaySettingsExW(
    $DisplayName, [ref]$mode, [IntPtr]::Zero, [uint32]0, [IntPtr]::Zero)
$result.ApplyResult = [KindleView.DisplayNative]::ResultName($applyResult)
if ($applyResult -ne 0) { throw ('Targeted mode change failed: ' + $result.ApplyResult) }
$after = @([KindleView.DisplayNative]::Enumerate())
$result.After = ($after | Where-Object DisplayName -EQ $DisplayName).Mode

# Detect an unexpected OS/driver side effect rather than silently altering others.
$changedOthers = foreach ($previous in $displays | Where-Object DisplayName -NE $DisplayName) {
    $current = @($after | Where-Object DisplayName -EQ $previous.DisplayName)
    if ($current.Count -ne 1 -or
        ($previous | ConvertTo-Json -Depth 4 -Compress) -ne ($current[0] | ConvertTo-Json -Depth 4 -Compress)) {
        $previous.DisplayName
    }
}
$result.OtherDisplaysChanged = @($changedOthers)
if ($result.OtherDisplaysChanged.Count -gt 0) {
    Write-Warning ('Windows or the driver changed another display: ' + ($result.OtherDisplaysChanged -join ', '))
}
[pscustomobject]$result
