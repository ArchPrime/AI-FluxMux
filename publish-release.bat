@echo off
rem ============================================================================
rem  AI-FluxMux - publish a runnable, self-contained Release build for end users
rem
rem  Produces:
rem    artifacts\publish\win-x64\FluxMux.Avalonia.exe   (self-contained, bundles .NET 10)
rem    artifacts\publish\AI-FluxMux-<version>-beta.zip   (portable, just unzip + run)
rem
rem  HOW IT AVOIDS THE "APP IS RUNNING" LOCK PROBLEM:
rem    The running AI-FluxMux app locks its build output DLLs, so a normal
rem    publish into the project's own bin\ fails. Instead of requiring the app
rem    to be closed, this script COPIES the project source to a temporary
rem    directory first, then builds + publishes from that copy. A file copy
rem    succeeds even while the app is running (Windows allows reading open
rem    files), so no process check / taskkill is needed. The temp copy is
rem    deleted when done.
rem
rem  Usage:
rem    Run:  publish-release.bat
rem    (The app may stay open; it will not be closed or killed.)
rem ============================================================================
setlocal
cd /d "%~dp0"

rem ----------------------------------------------------------------------------
rem  Concurrent-publish guard: prevent two runs of this script from racing on
rem  the build output (e.g. the PDB). If a lock file already exists, another
rem  publish is in progress - refuse to start a second one.
rem ----------------------------------------------------------------------------
set LOCKFILE=artifacts\publish\.publishing.lock
if exist "%LOCKFILE%" (
  echo.
  echo  ERROR: another publish is already in progress (lock file found: %LOCKFILE%).
  echo  Wait for it to finish, or delete the lock file if it is stale, then re-run.
  echo.
  echo  Press any key to exit...
  pause >NUL
  exit /b 1
)
echo %~dp0> "%LOCKFILE%"
echo  Lock acquired: %LOCKFILE%

rem ----------------------------------------------------------------------------
rem  Temp workspace: a clean copy of the project source, used as the build root
rem  so the running app's locked DLLs are never touched.
rem ----------------------------------------------------------------------------
set TMPROOT=%TEMP%\FluxMuxPublish_%RANDOM%
echo.
echo === [1/5] Copying project source to a temp workspace ===
echo  Temp: %TMPROOT%
if not exist "%TMPROOT%" mkdir "%TMPROOT%"

rem  Copy the whole project tree, then strip out the things that are NOT source
rem  (build output, git metadata, secrets, logs, models). Excluding bin/obj/
rem  means the copy is clean and the build starts from scratch in the temp dir.
robocopy "%~dp0" "%TMPROOT%" /E /NFL /NDL /NJH /NJS /NC /NS /NP /R:1 /W:1 /XD bin obj artifacts .vs .git .vscode tmp_decompile _cloud_duty_attach /XF *.log *.gguf *.bin *.tmp *.cache *.suo *.user *.userosscache *.sln.docstates fluxmux_config.json fluxmux_secrets.json FluxMux_Startup_Error.log AI-FluxMux-could-not-start.txt
rem  robocopy exit codes 0-7 are success; >=8 is a real error.
if errorlevel 8 (
  echo  ERROR: could not copy the project source to the temp workspace.
  if exist "%LOCKFILE%" del "%LOCKFILE%"
  if exist "%TMPROOT%" rmdir /s /q "%TMPROOT%"
  echo.
  echo  Press any key to exit...
  pause >NUL
  exit /b 1
)
echo  OK - source copied to temp workspace.

rem  All subsequent build/publish steps run from the temp copy, not the live
rem  project, so the running app's locked files are never in the way.
cd /d "%TMPROOT%"

echo.
echo === [2/5] Building the standalone updater (Release) ===
rem  The updater targets net10.0-windows (WinForms). It must exist in Release so the
rem  main app's CopyStandaloneUpdater target can copy FluxMux.Updates.exe into the
rem  publish output (used by the "Update now" buttons).
dotnet build FluxMux.Updates\FluxMux.Updates.csproj -c Release --nologo -v q
if errorlevel 1 (
  echo  ERROR: updater build failed.
  if exist "%LOCKFILE%" del "%LOCKFILE%"
  if exist "%TMPROOT%" rmdir /s /q "%TMPROOT%"
  echo.
  echo  Press any key to exit...
  pause >NUL
  exit /b 1
)

echo.
echo === [3/5] Publishing the main app (Release, self-contained win-x64) ===
rem  -c Release           : optimized, excludes AvaloniaUI.DiagnosticsSupport
rem  -r win-x64           : Windows x64
rem  --self-contained     : bundles the .NET 10 runtime (end users need no install)
rem  -p:PublishReadyToRun : precompiled native code for faster startup
dotnet publish FluxMux.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o artifacts\publish\win-x64
if errorlevel 1 (
  echo  ERROR: publish failed.
  if exist "%LOCKFILE%" del "%LOCKFILE%"
  if exist "%TMPROOT%" rmdir /s /q "%TMPROOT%"
  echo.
  echo  Press any key to exit...
  pause >NUL
  exit /b 1
)

rem  Bring the freshly built publish output back into the real project so the
rem  standard artifacts\publish\win-x64 location is populated (and the zip below
rem  is created from the real project tree).
echo.
echo === [4/5] Moving the publish output back into the project ===
if not exist "artifacts\publish\win-x64" mkdir "artifacts\publish\win-x64"
robocopy "%TMPROOT%\artifacts\publish\win-x64" "%~dp0artifacts\publish\win-x64" /E /NFL /NDL /NJH /NJS /NC /NS /NP /R:1 /W:1
if errorlevel 8 (
  echo  ERROR: could not move the publish output back into the project.
  if exist "%LOCKFILE%" del "%LOCKFILE%"
  if exist "%TMPROOT%" rmdir /s /q "%TMPROOT%"
  echo.
  echo  Press any key to exit...
  pause >NUL
  exit /b 1
)
echo  OK - publish output is in artifacts\publish\win-x64.

echo.
echo === [5/5] Zipping the portable build ===
rem  Read the version from the csproj so the zip name always matches the build.
for /f "tokens=2 delims=<> %%V in ('findstr /C:"<Version>" "%~dp0FluxMux.Avalonia.csproj"')" do set VER=%%V
set ZIP=%~dp0artifacts\publish\AI-FluxMux-%VER%-beta.zip
if exist "%ZIP%" del "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%~dp0artifacts\publish\win-x64\*' -DestinationPath '%ZIP%'"
if errorlevel 1 (
  echo  ERROR: zip creation failed.
  if exist "%LOCKFILE%" del "%LOCKFILE%"
  if exist "%TMPROOT%" rmdir /s /q "%TMPROOT%"
  echo.
  echo  Press any key to exit...
  pause >NUL
  exit /b 1
)

rem  Clean up the temp workspace now that the build + zip are done.
if exist "%TMPROOT%" rmdir /s /q "%TMPROOT%"
echo  Temp workspace cleaned up.

echo.
echo === Done ===
powershell -NoProfile -Command "Get-Item '%ZIP%' | ForEach-Object { Write-Host ('  ' + $_.Name + '  (' + [math]::Round($_.Length/1MB,1) + ' MB)'); Write-Host ('  ' + $_.FullName) }"
echo  Portable build ready: %ZIP%
echo  (End users: unzip, then run FluxMux.Avalonia.exe. No .NET install required.)
if exist "%LOCKFILE%" del "%LOCKFILE%"
echo  Lock released.
echo.
echo  Press any key to exit...
pause >NUL
endlocal
