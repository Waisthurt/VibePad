param(
    [string]$Version = "0.1.3"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$iscc = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$project = Join-Path $root "windows\VibePad.Agent\VibePad.Agent.csproj"
$payload = Join-Path $PSScriptRoot "payload"

if (-not (Test-Path -LiteralPath $dotnet)) { throw ".NET SDK was not found at $dotnet" }
if (-not $iscc) { throw "Inno Setup 6 was not found. Install it with: winget install --id JRSoftware.InnoSetup -e" }

& $dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:Version=$Version `
    -o $payload
if ($LASTEXITCODE -ne 0) { throw "Publishing the Windows Agent failed." }

& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot "VibePad-Agent.iss")
if ($LASTEXITCODE -ne 0) { throw "Building the installer failed." }

Write-Output (Join-Path $PSScriptRoot "output\VibePad-Agent-Setup-$Version.exe")
