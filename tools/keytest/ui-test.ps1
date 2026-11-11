# Real UI test of the CaptionRelay indicator (app must be running). Uses the real mouse on the indicator only,
# checks the foreground window never changes, restores the mouse position. Optional menu actions via UI Automation.
# Windows PowerShell 5.1, ASCII only.
param(
    [ValidateSet('basic', 'menu-hide-lc', 'menu-exit', 'where')][string]$Mode = 'basic'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class UT {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public static IntPtr FindByPidAndClassPart(uint pid, string titlePrefix, string classPart) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            if ((titlePrefix == null || t.ToString().StartsWith(titlePrefix)) && (classPart == null || c.ToString().Contains(classPart))) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static string Title(IntPtr h) { var t = new StringBuilder(512); GetWindowText(h, t, 512); return t.ToString(); }
}
"@
[void][UT]::SetProcessDPIAware()

$app = Get-Process CaptionRelay -ErrorAction Stop | Select-Object -First 1
$h = [UT]::FindByPidAndClassPart([uint32]$app.Id, 'Captions ', $null)
if ($h -eq [IntPtr]::Zero) { throw 'Indicator window not found' }

function Rect { $r = New-Object UT+RECT; [void][UT]::GetWindowRect($h, [ref]$r); return $r }
function Show([string]$label) { $r = Rect; "{0,-34} rect=({1},{2})-({3},{4}) h={5}  title='{6}'" -f $label, $r.L, $r.T, $r.R, $r.B, ($r.B - $r.T), [UT]::Title($h) }
function Fg {
    $w = [UT]::GetForegroundWindow(); $fpid = [uint32]0; [void][UT]::GetWindowThreadProcessId($w, [ref]$fpid)
    $c = New-Object System.Text.StringBuilder 256; [void][UT]::GetClassName($w, $c, 256)
    $name = try { (Get-Process -Id $fpid).ProcessName } catch { '?' }
    return "$name/$($c.ToString())"
}
function PillCenter { $r = Rect; return @([int](($r.L + $r.R) / 2), [int]($r.B - 13)) }
function Click([int]$x, [int]$y, [string]$button = 'left') {
    [void][UT]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 40
    if ($button -eq 'left') { [UT]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [UT]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero) }
    else { [UT]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); [UT]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero) }
}

$mouse = New-Object UT+POINT; [void][UT]::GetCursorPos([ref]$mouse)
$fg = [UT]::GetForegroundWindow(); "Foreground at start: " + (Fg)

try {
    if ($Mode -eq 'where') { Show 'indicator'; return }

    if ($Mode -eq 'basic') {
        $ex = [UT]::GetWindowLong($h, -20)
        "ExStyle: NOACTIVATE=" + (($ex -band 0x08000000) -ne 0) + " TOOLWINDOW=" + (($ex -band 0x80) -ne 0) + " TOPMOST=" + (($ex -band 0x8) -ne 0)
        Show 'start'
        $c = PillCenter
        Click $c[0] $c[1]; Start-Sleep -Milliseconds 400
        Show 'after 1 click (expanded?)'
        "  foreground unchanged: " + ([UT]::GetForegroundWindow() -eq $fg) + "  now: " + (Fg)
        $c = PillCenter
        Click $c[0] $c[1]; Start-Sleep -Milliseconds 400
        Show 'after 2nd click (collapsed?)'
        $c = PillCenter
        Click $c[0] $c[1]; Start-Sleep -Milliseconds 120; Click $c[0] $c[1]; Start-Sleep -Milliseconds 120; Click $c[0] $c[1]; Start-Sleep -Milliseconds 500
        Show 'after 3 quick clicks (expanded?)'
        $c = PillCenter
        Click $c[0] $c[1]; Start-Sleep -Milliseconds 400
        Show 'after 1 more click (collapsed?)'
        "  foreground unchanged: " + ([UT]::GetForegroundWindow() -eq $fg) + "  now: " + (Fg)

        # Drag 120 px left and 60 px up.
        $c = PillCenter
        [void][UT]::SetCursorPos($c[0], $c[1]); Start-Sleep -Milliseconds 50
        [UT]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 50
        for ($i = 1; $i -le 12; $i++) { [void][UT]::SetCursorPos($c[0] - 10 * $i, $c[1] - 5 * $i); Start-Sleep -Milliseconds 25 }
        [UT]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 400
        Show 'after drag (-120, -60)'
        "  foreground unchanged: " + ([UT]::GetForegroundWindow() -eq $fg) + "  now: " + (Fg)
    }

    if ($Mode -like 'menu-*') {
        $c = PillCenter
        Click $c[0] $c[1] 'right'; Start-Sleep -Milliseconds 600
        $cond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Menu)))
        # The menu is usually a child of the desktop, but after a drag it can hang under another window: search deeper.
        $menu = $null
        for ($try = 0; $try -lt 10 -and $menu -eq $null; $try++) {
            $menu = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
            if ($menu -eq $null) { $menu = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond) }
            if ($menu -eq $null) { Start-Sleep -Milliseconds 200 }
        }
        if ($menu -eq $null) { throw 'Menu not found' }
        $items = $menu.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))
        "Menu items: " + (($items | ForEach-Object { $_.Current.Name }) -join ' | ')
        $target = if ($Mode -eq 'menu-exit') { 'Exit' } else { 'Hide Live Captions window' }
        $item = $items | Where-Object { $_.Current.Name -eq $target } | Select-Object -First 1
        $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        "Invoked: $target"
        Start-Sleep -Milliseconds 800
    }
}
finally {
    [void][UT]::SetCursorPos($mouse.X, $mouse.Y)
    "Mouse restored; foreground same as at start: " + ([UT]::GetForegroundWindow() -eq $fg)
}
