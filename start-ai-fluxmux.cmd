@echo off
setlocal

:: Taskbar / Start Menu shortcut entry. Builds Debug, then starts the exe
:: from this project folder so Help.html next to the csproj wins over the
:: stale copy in bin\Debug. Do not start the exe by hand from bin\Debug.
set "AVALONIA_START_DIR=%~dp0"
if "%AVALONIA_START_DIR:~-1%"=="\" set "AVALONIA_START_DIR=%AVALONIA_START_DIR:~0,-1%"
set "AVALONIA_PROJECT=%AVALONIA_START_DIR%\FluxMux.Avalonia.csproj"
set "AVALONIA_EXE=%AVALONIA_START_DIR%\bin\Debug\net10.0\FluxMux.Avalonia.exe"
set "DOTNET_EXE="

for /f "delims=" %%D in ('where dotnet.exe 2^>nul') do (
    if not defined DOTNET_EXE set "DOTNET_EXE=%%D"
)

if not defined DOTNET_EXE (
    echo ERROR: dotnet.exe not found on PATH.
    exit /b 1
)

if not exist "%AVALONIA_PROJECT%" (
    echo ERROR: Avalonia project not found: "%AVALONIA_PROJECT%"
    exit /b 1
)

:: Rebuild needs the Debug exe unlocked. Find every FluxMux.Avalonia process,
:: stop it, then wait up to 15 seconds so the file lock is gone before build.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& { $procs = @(Get-Process -Name 'FluxMux.Avalonia' -ErrorAction SilentlyContinue); foreach ($p in $procs) { Write-Host ('Stopping previous AI-FluxMux PID ' + $p.Id); try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch {} }; $deadline = (Get-Date).AddSeconds(15); while ((Get-Process -Name 'FluxMux.Avalonia' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 } }"

pushd "%AVALONIA_START_DIR%"
"%DOTNET_EXE%" build "%AVALONIA_PROJECT%" --framework net10.0
set "BUILD_EXIT=%ERRORLEVEL%"
popd
if not "%BUILD_EXIT%"=="0" exit /b %BUILD_EXIT%

if not exist "%AVALONIA_EXE%" (
    echo ERROR: built exe not found: "%AVALONIA_EXE%"
    exit /b 1
)

:: Start the exe in its own process with the project folder as the working
:: directory (AVALONIA_START_DIR). That is what lets Help.html next to the
:: csproj win over a stale copy in bin\Debug. UseShellExecute keeps this
:: window from owning the app, so closing the editor does not stop AI-FluxMux.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& { $exe = $env:AVALONIA_EXE; $psi = New-Object System.Diagnostics.ProcessStartInfo; $psi.FileName = $exe; $start = $env:AVALONIA_START_DIR; if ([string]::IsNullOrWhiteSpace($start) -or -not (Test-Path $start)) { $start = [System.IO.Path]::GetDirectoryName($exe) }; $psi.WorkingDirectory = $start; $psi.UseShellExecute = $true; $started = [System.Diagnostics.Process]::Start($psi); if ($null -eq $started) { Write-Error 'Could not start AI-FluxMux.'; exit 1 }; Write-Host ('Started AI-FluxMux detached PID ' + $started.Id) }"
exit /b %ERRORLEVEL%
