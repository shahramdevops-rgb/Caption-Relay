# Minimizes or restores a top-level window by title prefix WITHOUT activating anything (test helper).
# Windows PowerShell 5.1, ASCII only.
param(
    [Parameter(Mandatory = $true)][string]$TitleContains,
    [ValidateSet('minimize', 'restore', 'state')][string]$Action = 'state'
)
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class WW {
    public delegate bool P(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@
$found = [IntPtr]::Zero
[WW]::EnumWindows({ param($h, $l)
    if ([WW]::IsWindowVisible($h)) {
        $t = New-Object System.Text.StringBuilder 512; [void][WW]::GetWindowText($h, $t, 512)
        if ($t.ToString().Contains($TitleContains)) { $script:found = $h; return $false }
    }
    return $true }, [IntPtr]::Zero) | Out-Null
if ($found -eq [IntPtr]::Zero) { "window not found"; exit 1 }
$fg = [WW]::GetForegroundWindow()
if ($Action -eq 'minimize') { [void][WW]::ShowWindow($found, 7) }   # SW_SHOWMINNOACTIVE
if ($Action -eq 'restore') { [void][WW]::ShowWindow($found, 4) }    # SW_SHOWNOACTIVATE
Start-Sleep -Milliseconds 300
"minimized=" + [WW]::IsIconic($found) + " foregroundUnchanged=" + ([WW]::GetForegroundWindow() -eq $fg)
