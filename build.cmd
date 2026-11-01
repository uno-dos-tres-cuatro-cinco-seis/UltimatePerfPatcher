@echo off
rem Builds dist\UltimatePerfPatcher.exe (needs the .NET SDK 6 or newer; the result runs on .NET Framework 4.8).
setlocal
cd /d "%~dp0"
dotnet build -c Release src\AdvancePerf || exit /b 1
dotnet build -c Release src\UltimatePerfPatcher || exit /b 1
if not exist dist mkdir dist
copy /y src\UltimatePerfPatcher\bin\Release\net48\UltimatePerfPatcher.exe dist\ >nul
echo Built dist\UltimatePerfPatcher.exe
