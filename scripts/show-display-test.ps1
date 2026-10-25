param([Parameter(Mandatory = $true)][string]$TargetDisplay)

$ErrorActionPreference = 'Stop'
$runtimeFolder = Join-Path (Split-Path -Parent $PSScriptRoot) 'runtime'
New-Item -ItemType Directory -Path $runtimeFolder -Force | Out-Null
$testLog = Join-Path $runtimeFolder 'display-test.log'
Start-Transcript -LiteralPath $testLog -Append | Out-Null
try {
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class KindleTestWindowNative {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
}
'@
[void][KindleTestWindowNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$targetScreen = [System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.DeviceName -eq $TargetDisplay } | Select-Object -First 1
if (-not $targetScreen) { throw "Kindle target display is not active: $TargetDisplay" }
$targetBounds = $targetScreen.Bounds
if ($targetBounds.Width -ne 824 -or $targetBounds.Height -ne 1200) { throw "Unexpected Kindle display dimensions: $targetBounds" }
Write-Output "Target display: $TargetDisplay; bounds: $targetBounds"
[System.Windows.Forms.Application]::EnableVisualStyles()
$form = [System.Windows.Forms.Form]::new()
$form.Text = 'Kindle 扩展屏测试'
$form.StartPosition = 'Manual'
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::None
$form.Location = [System.Drawing.Point]::new($targetBounds.Left + 20, $targetBounds.Top + 20)
$form.Size = [System.Drawing.Size]::new(780, 1100)
$form.BackColor = [System.Drawing.Color]::White
$form.Font = [System.Drawing.Font]::new('Microsoft YaHei UI', 20)
$title = [System.Windows.Forms.Label]::new()
$title.Text = "Kindle 独立扩展屏"
$title.Font = [System.Drawing.Font]::new('Microsoft YaHei UI', 30, [System.Drawing.FontStyle]::Bold)
$title.SetBounds(35, 60, 700, 100)
$form.Controls.Add($title)
$body = [System.Windows.Forms.Label]::new()
$body.Text = "分辨率：824 × 1200`r`n`r`n这是 Windows 新增的一块桌面。`r`n`r`n从主屏右侧拖动窗口，就能把它放到 Kindle 上。`r`n`r`n适合阅读文字、代码和文档。`r`n`r`n点击电脑控制窗口的「断开连接」可退出显示。"
$body.SetBounds(40, 200, 680, 500)
$form.Controls.Add($body)
$clock = [System.Windows.Forms.Label]::new()
$clock.Font = [System.Drawing.Font]::new('Consolas', 28)
$clock.SetBounds(40, 820, 680, 70)
$form.Controls.Add($clock)
$timer = [System.Windows.Forms.Timer]::new()
$timer.Interval = 2000
$timer.Add_Tick({ $clock.Text = [DateTime]::Now.ToString('HH:mm:ss') })
$form.Add_Shown({
    $clock.Text = [DateTime]::Now.ToString('HH:mm:ss')
    $timer.Start()
    # A hidden PowerShell process can pass SW_HIDE to the first native ShowWindow.
    # Explicitly show this form after creation, without activating another window.
    [void]$form.BeginInvoke([Action]{
        try {
            [void][KindleTestWindowNative]::ShowWindow($form.Handle, 4)
            if (-not [KindleTestWindowNative]::SetWindowPos($form.Handle, [IntPtr]::Zero, $targetBounds.Left + 20, $targetBounds.Top + 20, 780, 1100, 0x50)) {
                throw "SetWindowPos failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
            }
            "$(Get-Date -Format o) HWND=$($form.Handle) Visible=$([KindleTestWindowNative]::IsWindowVisible($form.Handle))" | Add-Content -LiteralPath $testLog
        } catch {
            $_.Exception.ToString() | Add-Content -LiteralPath $testLog
            $form.Close()
        }
    })
})
$form.Add_FormClosed({ $timer.Stop(); $timer.Dispose() })
[void]$form.ShowDialog()
$form.Dispose()
} catch {
    $_.Exception.ToString() | Add-Content -LiteralPath $testLog
    throw
} finally {
    Stop-Transcript | Out-Null
}
