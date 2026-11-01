param([Parameter(Mandatory)] [string] $Exe, [int] $Seconds = 6)
# Functional test for the keyboard-hook gate. Floods synthetic F24 key presses and measures the
# app's CPU time, first with its window active, then minimized. A process with a low-level keyboard
# hook burns CPU for every key event; without the hook its CPU stays flat.
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class KeyFeed {
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }  // x64 layout (PowerShell is 64-bit)
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    public static long Press(int seconds) {
        var inp = new INPUT[100];
        for (int i = 0; i < inp.Length; i++) { inp[i].type = 1; inp[i].ki.wVk = 0x87; inp[i].ki.dwFlags = (uint)(i % 2 == 0 ? 0 : 2); }
        long sent = 0; var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) sent += SendInput((uint)inp.Length, inp, Marshal.SizeOf(typeof(INPUT)));
        return sent;
    }
}
"@
$name = [IO.Path]::GetFileNameWithoutExtension($Exe)
Get-Process -Name $name, Autoupdate -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
$p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
do { Start-Sleep -Milliseconds 200; $p.Refresh() } while ($p.MainWindowHandle -eq 0)
Start-Sleep -Seconds 6
Get-Process -Name Autoupdate -ErrorAction SilentlyContinue | Stop-Process -Force
$h = $p.MainWindowHandle
function Phase([string] $label, [bool] $activate) {
    if ($activate) {
        [KeyFeed]::ShowWindow($h, 9) | Out-Null
        [KeyFeed]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [KeyFeed]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
        [KeyFeed]::SetForegroundWindow($h) | Out-Null
    } else { [KeyFeed]::ShowWindow($h, 6) | Out-Null }
    Start-Sleep -Milliseconds 700
    $p.Refresh(); $c0 = $p.TotalProcessorTime
    $sent = [KeyFeed]::Press($Seconds)
    Start-Sleep -Milliseconds 300
    $p.Refresh()
    [pscustomobject]@{ exe = $name; phase = $label; app_in_foreground = ([KeyFeed]::GetForegroundWindow() -eq $h)
        key_events = $sent; app_cpu_ms = [math]::Round(($p.TotalProcessorTime - $c0).TotalMilliseconds) }
}
Phase "idle-no-keys" $true | Out-Null   # warm-up
Phase "active" $true
Phase "minimized" $false
Phase "active-again" $true
$p | Stop-Process -Force
Get-Process -Name Autoupdate -ErrorAction SilentlyContinue | Stop-Process -Force
