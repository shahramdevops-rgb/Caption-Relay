# Runs steps inside a temporary foreground test window (keys reach only that window):
#   say:<text>   speak with System.Speech (for Live Captions)
#   send         Ctrl+Shift press/release
#   skip         F9
#   wait:<ms>    pause
#   check        print whether the test window still has the focus
# Windows PowerShell 5.1, ASCII only.
param([Parameter(Mandatory = $true)][string]$Steps, [int]$GapMs = 300)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class KT {
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    public static bool Focus(IntPtr h) {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint me = GetCurrentThreadId();
        if (fgThread != me) AttachThreadInput(me, fgThread, true);
        BringWindowToTop(h);
        bool ok = SetForegroundWindow(h);
        if (fgThread != me) AttachThreadInput(me, fgThread, false);
        return ok;
    }
}
"@

$VK = @{ LCtrl = 0xA2; RCtrl = 0xA3; LShift = 0xA0; T = 0x54; F9 = 0x78; F24 = 0x87 }
$KEYUP = 2; $EXT = 1

$form = New-Object System.Windows.Forms.Form
$form.Text = 'CaptionRelay key test (do not type)'
$form.TopMost = $true
$form.Width = 420; $form.Height = 120
$form.StartPosition = 'CenterScreen'
$box = New-Object System.Windows.Forms.TextBox
$box.Dock = 'Fill'; $box.Multiline = $true
$form.Controls.Add($box)

$before = [KT]::GetForegroundWindow()
$mouse = New-Object KT+POINT
[void][KT]::GetCursorPos([ref]$mouse)

$form.Show()
[System.Windows.Forms.Application]::DoEvents()
[void][KT]::Focus($form.Handle)
Start-Sleep -Milliseconds 300
[System.Windows.Forms.Application]::DoEvents()

function Assert-Front {
    [System.Windows.Forms.Application]::DoEvents()
    if ([KT]::GetForegroundWindow() -ne $form.Handle) {
        [void][KT]::Focus($form.Handle)
        Start-Sleep -Milliseconds 200
        if ([KT]::GetForegroundWindow() -ne $form.Handle) { throw 'Test window is not in front; aborting before sending keys.' }
    }
}
function Down([string]$k) { $f = 0; if ($k -eq 'RCtrl') { $f = $EXT }; [KT]::keybd_event([byte]$VK[$k], 0, $f, [UIntPtr]::Zero); Start-Sleep -Milliseconds 30 }
function Up([string]$k) { $f = $KEYUP; if ($k -eq 'RCtrl') { $f = $KEYUP -bor $EXT }; [KT]::keybd_event([byte]$VK[$k], 0, $f, [UIntPtr]::Zero); Start-Sleep -Milliseconds 30 }
function Tap([string]$k) { Down $k; Up $k }
function Step([string]$name, [scriptblock]$keys) {
    Assert-Front
    (Get-Date).ToString('HH:mm:ss.fff') + '  ' + $name
    & $keys
    Start-Sleep -Milliseconds $GapMs
}

Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SetOutputToDefaultAudioDevice()

try {
    "Foreground is test window: " + ([KT]::GetForegroundWindow() -eq $form.Handle)
    foreach ($step in $Steps.Split('|')) {
        $kind, $arg = $step.Split(':', 2)
        switch ($kind) {
            'say'  { Step "say: $arg" { $synth.Speak($arg) } }
            'send' { Step 'send (Ctrl+Shift)' { Down LCtrl; Down LShift; Up LShift; Up LCtrl } }
            'skip' { Step 'skip (F9)' { Tap F9 } }
            'ctrlshiftt' { Step 'Ctrl+Shift+T (must not send)' { Down LCtrl; Down LShift; Tap T; Up LShift; Up LCtrl } }
            'wait' { Start-Sleep -Milliseconds ([int]$arg) }
            'check' { [System.Windows.Forms.Application]::DoEvents(); (Get-Date).ToString('HH:mm:ss.fff') + '  focus still on the test window: ' + ([KT]::GetForegroundWindow() -eq $form.Handle) }
        }
    }
}
finally {
    $synth.Dispose()
    foreach ($k in 'LCtrl', 'RCtrl', 'LShift') { Up $k }
    $form.Close()
    [void][KT]::SetCursorPos($mouse.X, $mouse.Y)
    if ($before -ne [IntPtr]::Zero) { [void][KT]::Focus($before) }
    Start-Sleep -Milliseconds 200
    "Stuck keys after test: ctrl=" + (([KT]::GetAsyncKeyState(0x11) -band 0x8000) -ne 0) + " shift=" + (([KT]::GetAsyncKeyState(0x10) -band 0x8000) -ne 0)
    "Foreground restored: " + ([KT]::GetForegroundWindow() -eq $before)
}
