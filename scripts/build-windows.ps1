# Builds a self-contained GigaTTS folder, a zip, and an installer when Inno Setup is present.
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Нужен .NET SDK 8 или новее: https://dotnet.microsoft.com/download"
}

$Name = if ($Runtime -eq "win-x64") { "GigaTTS-windows" } else { "GigaTTS-$Runtime" }
$Out = Join-Path $Root "dist\$Name"
$Zip = if ($Runtime -eq "win-x64") {
    Join-Path $Root "dist\GigaTTS-windows-x64.zip"
} else {
    Join-Path $Root "dist\$Name.zip"
}

if (Test-Path $Out) {
    Remove-Item -Recurse -Force $Out
}
New-Item -ItemType Directory -Force -Path (Join-Path $Root "dist") | Out-Null

dotnet publish (Join-Path $Root "Windows\src\GigaTTS.csproj") `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -o $Out `
    -p:DebugType=none `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish завершился с кодом $LASTEXITCODE"
}

Copy-Item (Join-Path $Root "Worker\worker.py") (Join-Path $Out "worker.py") -Force
Copy-Item (Join-Path $Root "Worker\requirements.txt") (Join-Path $Out "requirements.txt") -Force

if (Test-Path $Zip) {
    Remove-Item -Force $Zip
}
Compress-Archive -Path $Out -DestinationPath $Zip
Write-Host $Zip

if ($Runtime -eq "win-x64") {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc) {
        & $iscc (Join-Path $Root "Windows\GigaTTS.iss")
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup завершился с кодом $LASTEXITCODE"
        }
    } else {
        Write-Host "Inno Setup 6 не найден, установщик exe пропущен. Готовы папка и zip."
    }
}
