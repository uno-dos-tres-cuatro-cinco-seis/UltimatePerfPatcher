param(
    [Parameter(Mandatory)] [string] $Exe,
    [int] $Seconds = 60,
    [string] $Label = "minimized",
    [string] $OutDir = "$PSScriptRoot\..\results"
)
# Starts the app, minimizes its main window and measures CPU time and context switches
# of the whole process while it sits in the background.

Add-Type -Namespace Native -Name Win -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
"@

$name = [IO.Path]::GetFileNameWithoutExtension($Exe)
Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$p = Start-Process -FilePath (Resolve-Path $Exe).Path -WorkingDirectory (Split-Path (Resolve-Path $Exe).Path) -PassThru
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($p.MainWindowHandle -eq 0 -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 50; $p.Refresh() }
Start-Sleep -Seconds 8
[Native.Win]::ShowWindow($p.MainWindowHandle, 6) | Out-Null   # SW_MINIMIZE
Start-Sleep -Seconds 5
$p.Refresh()
$cpu0 = $p.TotalProcessorTime
$cs0 = ($p.Threads | Measure-Object -Property Id).Count
$t0 = Get-Date
Start-Sleep -Seconds $Seconds
$p.Refresh()
$wall = ((Get-Date) - $t0).TotalMilliseconds
$cpu = ($p.TotalProcessorTime - $cpu0).TotalMilliseconds
$r = [pscustomobject]@{
    label           = $Label
    cpu_ms_per_min  = [math]::Round($cpu * 60000 / $wall)
    cpu_pct_one_core = [math]::Round(100 * $cpu / $wall, 3)
    private_mb      = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    threads         = $p.Threads.Count
}
$p | Stop-Process -Force
Get-Process -Name Autoupdate -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $OutDir | Out-Null
$r | ConvertTo-Json | Out-File -Encoding utf8 (Join-Path $OutDir "$Label-$(Get-Date -Format yyyyMMdd-HHmmss).json")
$r
