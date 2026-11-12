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
Copy-Item (Join-Path $Root "NOTICE") (Join-Path $Out "NOTICE") -Force

$DirName = "pyannote-speaker-diarization-community-1"
function Test-DiarizationReady([string]$Path) {
    return (Test-Path (Join-Path $Path "config.yaml")) `
        -and (Test-Path (Join-Path $Path "embedding\pytorch_model.bin")) `
        -and (Test-Path (Join-Path $Path "segmentation\pytorch_model.bin")) `
        -and (Test-Path (Join-Path $Path "plda\plda.npz")) `
        -and (Test-Path (Join-Path $Path "plda\xvec_transform.npz"))
}
$Source = $env:GIGATTS_DIARIZATION_SOURCE
if (-not $Source) {
    foreach ($candidate in @(
        (Join-Path $Root "BundledModels\$DirName"),
        (Join-Path $Root "dist\models\$DirName"),
        (Join-Path $env:USERPROFILE ".cache\gigatts\$DirName")
    )) {
        if (Test-DiarizationReady $candidate) {
            $Source = $candidate
            break
        }
    }
}
if (-not $Source -or -not (Test-DiarizationReady $Source)) {
    throw "Нет локальной модели diarization. Ожидается BundledModels\$DirName после clone, либо скачайте: Worker\.venv\Scripts\python.exe -u Worker\worker.py --download-diarization, либо положите папку в dist\models\$DirName"
}
$Dest = Join-Path $Out "models\$DirName"
if (Test-Path $Dest) {
    Remove-Item -Recurse -Force $Dest
}
New-Item -ItemType Directory -Force -Path (Join-Path $Dest "embedding") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $Dest "segmentation") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $Dest "plda") | Out-Null
Copy-Item (Join-Path $Source "config.yaml") (Join-Path $Dest "config.yaml") -Force
Copy-Item (Join-Path $Source "embedding\pytorch_model.bin") (Join-Path $Dest "embedding\pytorch_model.bin") -Force
Copy-Item (Join-Path $Source "segmentation\pytorch_model.bin") (Join-Path $Dest "segmentation\pytorch_model.bin") -Force
Copy-Item (Join-Path $Source "plda\plda.npz") (Join-Path $Dest "plda\plda.npz") -Force
Copy-Item (Join-Path $Source "plda\xvec_transform.npz") (Join-Path $Dest "plda\xvec_transform.npz") -Force
Write-Host "diarization bundled: $Dest ← $Source"

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
