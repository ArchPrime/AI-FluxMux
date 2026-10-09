@echo off
rem ============================================================================
rem  AI-FluxMux - publish a runnable, self-contained Release build for end users
rem
rem  Produces:
rem    artifacts\publish\win-x64\FluxMux.Avalonia.exe   (self-contained, bundles .NET 10)
rem    artifacts\publish\AI-FluxMux-<version>-beta.zip   (portable, just unzip + run)
rem
rem  PREREQUISITE: the running AI-FluxMux app MUST be closed first, otherwise the
rem  publish step fails because the app locks the build output DLLs.
rem  (Per workspace rules: close the app manually - do NOT taskkill it.)
rem
rem  Usage:
rem    1. Close the AI-FluxMux app.
rem    2. Run:  publish-release.bat
rem ============================================================================
setlocal
cd /d "%~dp0"

echo.
echo === [1/4] Checking the app is not running ===
tasklist /FI "IMAGENAME eq FluxMux.Avalonia.exe" 2>NUL | find /I /N "FluxMux.Avalonia.exe" >NUL
if not errorlevel 1 (
  echo.
  echo  ERROR: AI-FluxMux is still running. Close it first, then re-run this script.
  echo  (Do not taskkill it - it is the process serving the active chat.)
  exit /b 1
)
echo  OK - app is not running.

echo.
echo === [2/4] Building the standalone updater (Release) ===
rem  The updater targets net10.0-windows (WinForms). It must exist in Release so the
rem  main app's CopyStandaloneUpdater target can copy FluxMux.Updates.exe into the
rem  publish output (used by the "Update now" buttons).
dotnet build FluxMux.Updates\FluxMux.Updates.csproj -c Release --nologo -v q
if errorlevel 1 (
  echo  ERROR: updater build failed.
  exit /b 1
)

echo.
echo === [3/4] Publishing the main app (Release, self-contained win-x64) ===
rem  -c Release           : optimized, excludes AvaloniaUI.DiagnosticsSupport
rem  -r win-x64           : Windows x64
rem  --self-contained     : bundles the .NET 10 runtime (end users need no install)
rem  -p:PublishReadyToRun : precompiled native code for faster startup
dotnet publish FluxMux.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o artifacts\publish\win-x64
if errorlevel 1 (
  echo  ERROR: publish failed.
  exit /b 1
)

echo.
echo === [4/4] Zipping the portable build ===
rem  Read the version from the csproj so the zip name always matches the build.
for /f "tokens=2 delims=<>" %%V in ('findstr /C:"<Version>" FluxMux.Avalonia.csproj') do set VER=%%V
set ZIP=artifacts\publish\AI-FluxMux-%VER%-beta.zip
if exist "%ZIP%" del "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path 'artifacts\publish\win-x64\*' -DestinationPath '%ZIP%'"
if errorlevel 1 (
  echo  ERROR: zip failed.
  exit /b 1
)

echo.
echo === Done ===
powershell -NoProfile -Command "Get-Item '%ZIP%' | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}, FullName | Format-List"
echo  Portable build ready: %ZIP%
echo  (End users: unzip, then run FluxMux.Avalonia.exe. No .NET install required.)
endlocal
