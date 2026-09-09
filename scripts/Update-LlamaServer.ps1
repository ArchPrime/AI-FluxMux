# Updates the llama-server folder in place from official ggml-org/llama.cpp nightlies.
# Default target matches this PC: C:\AI_Workbench\llama-server (CUDA 13 zip).
#
# Examples:
#   powershell -ExecutionPolicy Bypass -File .\Update-LlamaServer.ps1
#   powershell -ExecutionPolicy Bypass -File .\Update-LlamaServer.ps1 -WhatIf
#   powershell -ExecutionPolicy Bypass -File .\Update-LlamaServer.ps1 -Tag b10629

[CmdletBinding()]
param(
    [string]$InstallDir = "C:\AI_Workbench\llama-server",
    [string]$Tag = "",
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$repo = "ggml-org/llama.cpp"

function Get-CurrentBuild([string]$exe) {
    if (-not (Test-Path $exe)) { return $null }
    $p = Start-Process -FilePath $exe -ArgumentList "--version" -NoNewWindow -Wait `
        -RedirectStandardOutput "$env:TEMP\llama-ver-out.txt" `
        -RedirectStandardError "$env:TEMP\llama-ver-err.txt" -PassThru
    $text = ((Get-Content "$env:TEMP\llama-ver-out.txt","$env:TEMP\llama-ver-err.txt" -ErrorAction SilentlyContinue) -join "`n")
    if ($text -match "build\s+(\d+)") { return [int]$Matches[1] }
    return $null
}

function Get-CudaFlavor([string]$dir) {
    if (Test-Path (Join-Path $dir "cublas64_13.dll")) { return "cuda-13" }
    if (Test-Path (Join-Path $dir "cublas64_12.dll")) { return "cuda-12" }
    return "cuda-13"
}

function Get-GithubJson([string]$url) {
    $headers = @{ "User-Agent" = "AI-FluxMux-llama-update" }
    if ($env:GITHUB_TOKEN) { $headers.Authorization = "Bearer $env:GITHUB_TOKEN" }
    return Invoke-RestMethod -Uri $url -Headers $headers
}

function Find-NightlyRelease([string]$preferredTag) {
    if ($preferredTag) {
        return Get-GithubJson "https://api.github.com/repos/$repo/releases/tags/$preferredTag"
    }

    $releases = Get-GithubJson "https://api.github.com/repos/$repo/releases?per_page=30"
    foreach ($rel in $releases) {
        if ($rel.tag_name -notmatch '^b\d+$') { continue }
        if ($rel.assets | Where-Object { $_.name -match 'bin-win-cuda-.*x64\.zip$' }) {
            return $rel
        }
    }
    throw "No nightly Windows CUDA release found in the last 30 GitHub releases."
}

function Pick-Asset($release, [string]$flavor) {
    $pattern = if ($flavor -eq "cuda-12") {
        'llama-b\d+-bin-win-cuda-12[\d.]*-x64\.zip$'
    } else {
        'llama-b\d+-bin-win-cuda-13[\d.]*-x64\.zip$'
    }
    $asset = $release.assets | Where-Object { $_.name -match $pattern } | Select-Object -First 1
    if (-not $asset) {
        $names = ($release.assets | ForEach-Object { $_.name }) -join "`n  "
        throw "No $flavor Windows x64 zip on $($release.tag_name). Assets:`n  $names"
    }
    return $asset
}

function Pick-Cudart($release, [string]$flavor) {
    $pattern = if ($flavor -eq "cuda-12") {
        'cudart-llama-bin-win-cuda-12[\d.]*-x64\.zip$'
    } else {
        'cudart-llama-bin-win-cuda-13[\d.]*-x64\.zip$'
    }
    return $release.assets | Where-Object { $_.name -match $pattern } | Select-Object -First 1
}

$exe = Join-Path $InstallDir "llama-server.exe"
if (-not (Test-Path $exe)) {
    throw "llama-server.exe not found at $exe"
}

$running = Get-Process -Name "llama-server" -ErrorAction SilentlyContinue
if ($running) {
    throw "llama-server.exe is running (PID $(($running.Id -join ', '))). Stop it in AI-FluxMux first, then rerun."
}

$current = Get-CurrentBuild $exe
$flavor = Get-CudaFlavor $InstallDir
Write-Host "Install dir : $InstallDir"
Write-Host "Current     : build $(if ($null -eq $current) { 'unknown' } else { $current })"
Write-Host "Flavor      : $flavor (from CUDA DLLs already in the folder)"

$release = Find-NightlyRelease $Tag
$asset = Pick-Asset $release $flavor
$cudart = Pick-Cudart $release $flavor
$remoteBuild = if ($release.tag_name -match '^b(\d+)$') { [int]$Matches[1] } else { $null }

Write-Host "Remote      : $($release.tag_name)  $($asset.name)"
if ($cudart) { Write-Host "CUDA runtime: $($cudart.name)" }

if ($null -ne $current -and $null -ne $remoteBuild -and $remoteBuild -le $current) {
    Write-Host "Already on build $current (remote is $remoteBuild). Nothing to do."
    exit 0
}

if ($WhatIf) {
    Write-Host "WhatIf: would download $($asset.browser_download_url) and replace $InstallDir"
    if ($cudart) { Write-Host "WhatIf: would also merge $($cudart.name)" }
    exit 0
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = "$InstallDir.bak-$stamp"
$work = Join-Path $env:TEMP "llama-update-$stamp"
New-Item -ItemType Directory -Path $work | Out-Null

$zip = Join-Path $work $asset.name
Write-Host "Downloading $($asset.name) ..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing

$extract = Join-Path $work "extract"
New-Item -ItemType Directory -Path $extract | Out-Null
Expand-Archive -Path $zip -DestinationPath $extract -Force

$rtExtract = $null
if ($cudart) {
    $rtZip = Join-Path $work $cudart.name
    Write-Host "Downloading $($cudart.name) ..."
    Invoke-WebRequest -Uri $cudart.browser_download_url -OutFile $rtZip -UseBasicParsing
    $rtExtract = Join-Path $work "cudart"
    New-Item -ItemType Directory -Path $rtExtract | Out-Null
    Expand-Archive -Path $rtZip -DestinationPath $rtExtract -Force
}

function Resolve-PayloadDir([string]$root, [string]$mustContain) {
    if (Test-Path (Join-Path $root $mustContain)) {
        return $root
    }
    $nested = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($nested -and (Test-Path (Join-Path $nested.FullName $mustContain))) {
        return $nested.FullName
    }
    return $root
}

$payload = Resolve-PayloadDir $extract "llama-server.exe"
if (-not (Test-Path (Join-Path $payload "llama-server.exe"))) {
    throw "Downloaded zip did not contain llama-server.exe."
}

Write-Host "Backing up current install to $backup"
Rename-Item -Path $InstallDir -NewName (Split-Path $backup -Leaf)
New-Item -ItemType Directory -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $payload "*") -Destination $InstallDir -Recurse -Force

if ($rtExtract) {
    $rtPayload = Resolve-PayloadDir $rtExtract "cudart64_13.dll"
    if (-not (Test-Path (Join-Path $rtPayload "cudart64_13.dll"))) {
        $rtPayload = Resolve-PayloadDir $rtExtract "cudart64_12.dll"
    }
    Copy-Item -Path (Join-Path $rtPayload "*") -Destination $InstallDir -Recurse -Force
}

foreach ($dll in @(
        "cudart64_13.dll", "cudart64_12.dll",
        "cublas64_13.dll", "cublas64_12.dll",
        "cublasLt64_13.dll", "cublasLt64_12.dll")) {
    $from = Join-Path $backup $dll
    $to = Join-Path $InstallDir $dll
    if ((Test-Path $from) -and -not (Test-Path $to)) {
        Copy-Item $from $to -Force
        Write-Host "Restored $dll from previous install."
    }
}

$new = Get-CurrentBuild (Join-Path $InstallDir "llama-server.exe")
Write-Host "Updated to build $(if ($null -eq $new) { 'unknown' } else { $new })."
Write-Host "Previous copy kept at $backup"
Write-Host "In AI-FluxMux, Launch a known local model profile to confirm it still works."
