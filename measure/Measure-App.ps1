param(
    [Parameter(Mandatory)] [string] $Exe,
    [int] $Seconds = 60,
    [int] $Runs = 3,
    [string] $Label = "run",
    [string] $OutDir = "$PSScriptRoot\..\results"
)
# Starts the app, waits for its main window, then samples CPU, memory and UI-thread
# responsiveness (WM_NULL round trip via SendMessageTimeout) for $Seconds seconds.

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

public static class AppProbe {
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

    public static Dictionary<string, object> Run(string exe, int seconds) {
        var r = new Dictionary<string, object>();
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = System.IO.Path.GetDirectoryName(exe), UseShellExecute = false };
        var sw = Stopwatch.StartNew();
        var p = Process.Start(psi);
        IntPtr hwnd = IntPtr.Zero;
        while (sw.ElapsedMilliseconds < 60000) {
            p.Refresh();
            if (p.HasExited) { r["error"] = "exited early, code " + p.ExitCode; return r; }
            hwnd = p.MainWindowHandle;
            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd)) break;
            Thread.Sleep(10);
        }
        r["window_ms"] = sw.ElapsedMilliseconds;
        IntPtr res;
        while (SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero, 0, 5000, out res) == IntPtr.Zero && sw.ElapsedMilliseconds < 60000) Thread.Sleep(10);
        r["responsive_ms"] = sw.ElapsedMilliseconds;
        // startup phase: UI-thread stalls while MainPage and its 18 sub-pages are built
        double startMax = 0; long lastStall = 0;
        var startEnd = sw.ElapsedMilliseconds + 8000;
        while (sw.ElapsedMilliseconds < startEnd) {
            var s0 = Stopwatch.StartNew();
            SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero, 0, 5000, out res);
            var ms = s0.Elapsed.TotalMilliseconds;
            startMax = Math.Max(startMax, ms);
            if (ms > 50) lastStall = sw.ElapsedMilliseconds;
            Thread.Sleep(20);
        }
        r["startup_ui_max_stall_ms"] = Math.Round(startMax, 1);
        r["startup_settled_ms"] = lastStall;
        p.Refresh();
        r["private_mb_after_startup"] = Math.Round(p.PrivateMemorySize64 / 1048576.0, 1);
        p.Refresh();
        var cpu0 = p.TotalProcessorTime; var t0 = sw.Elapsed;
        var lat = new List<double>(); long peakPriv = 0, peakWs = 0; int maxThreads = 0;
        var end = sw.Elapsed + TimeSpan.FromSeconds(seconds);
        while (sw.Elapsed < end) {
            var s = Stopwatch.StartNew();
            var ok = SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero, 0, 5000, out res) != IntPtr.Zero;
            lat.Add(ok ? s.Elapsed.TotalMilliseconds : 5000);
            p.Refresh();
            peakPriv = Math.Max(peakPriv, p.PrivateMemorySize64);
            peakWs = Math.Max(peakWs, p.WorkingSet64);
            maxThreads = Math.Max(maxThreads, p.Threads.Count);
            Thread.Sleep(50);
        }
        p.Refresh();
        var cpu = (p.TotalProcessorTime - cpu0).TotalMilliseconds;
        var wall = (sw.Elapsed - t0).TotalMilliseconds;
        r["cpu_pct_one_core"] = Math.Round(100.0 * cpu / wall, 2);
        r["cpu_ms_per_min"] = Math.Round(cpu * 60000.0 / wall);
        lat.Sort();
        Func<double, double> pct = q => lat[Math.Min(lat.Count - 1, (int)(q * lat.Count))];
        r["ui_ping_p50_ms"] = Math.Round(pct(0.50), 2);
        r["ui_ping_p99_ms"] = Math.Round(pct(0.99), 2);
        r["ui_ping_max_ms"] = Math.Round(lat.Last(), 1);
        r["ui_stalls_over_50ms"] = lat.Count(x => x > 50);
        r["ui_pings"] = lat.Count;
        r["private_mb_end"] = Math.Round(p.PrivateMemorySize64 / 1048576.0, 1);
        r["private_mb_peak"] = Math.Round(peakPriv / 1048576.0, 1);
        r["workingset_mb_peak"] = Math.Round(peakWs / 1048576.0, 1);
        r["threads_max"] = maxThreads;
        r["handles_end"] = p.HandleCount;
        try { p.Kill(); p.WaitForExit(5000); } catch { }
        return r;
    }
}
"@

$name = [IO.Path]::GetFileNameWithoutExtension($Exe)
$all = @()
for ($i = 1; $i -le $Runs; $i++) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    $r = [AppProbe]::Run((Resolve-Path $Exe).Path, $Seconds)
    $r["run"] = $i
    $all += [pscustomobject]$r
    # the app launches data\Autoupdate.exe (separate window) on every start while a newer version exists
    Get-Process -Name Autoupdate -ErrorAction SilentlyContinue | Stop-Process -Force
}
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$all | ConvertTo-Json | Out-File -Encoding utf8 (Join-Path $OutDir "$Label-$stamp.json")
$all | Format-Table -AutoSize | Out-String -Width 300
