param(
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "Publish_VM"
}

Write-Host "=== HomeworkGuard VM Release Paketleme (IS_DEV_SANDBOX_MODE = false) ===" -ForegroundColor Cyan
Write-Host "Kaynak Dizin: $RepoRoot"
Write-Host "Çıktı Dizini: $OutputDir"

if (Test-Path $OutputDir) {
    Remove-Item -Path $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$ServiceProj = Join-Path $RepoRoot "src\HomeworkGuard.Service\HomeworkGuard.Service.csproj"
$UiProj = Join-Path $RepoRoot "src\HomeworkGuard.UI\HomeworkGuard.UI.csproj"
$SetupProj = Join-Path $RepoRoot "src\HomeworkGuard.Setup\HomeworkGuard.Setup.csproj"

Write-Host "`n[1/4] HomeworkGuard.Service derleniyor (win-x64, self-contained, ProductionKiosk=true)..." -ForegroundColor Yellow
dotnet publish $ServiceProj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:ProductionKiosk=true `
    -p:PublishSingleFile=false `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) { throw "Service publish başarısız." }

Write-Host "`n[2/4] HomeworkGuard.UI derleniyor (win-x64, self-contained, ProductionKiosk=true)..." -ForegroundColor Yellow
dotnet publish $UiProj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:ProductionKiosk=true `
    -p:PublishSingleFile=false `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) { throw "UI publish başarısız." }

Write-Host "`n[3/4] HomeworkGuard.Setup (GUI Kurulum Sihirbazı) derleniyor..." -ForegroundColor Yellow
dotnet publish $SetupProj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:ProductionKiosk=true `
    -p:PublishSingleFile=false `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) { throw "Setup publish başarısız." }

Write-Host "`n[4/4] Kurulum ve sıkılaştırma betikleri kopyalanıyor..." -ForegroundColor Yellow
Copy-Item (Join-Path $PSScriptRoot "Install-Hardening.ps1") $OutputDir -Force
Copy-Item (Join-Path $PSScriptRoot "Uninstall-Hardening.ps1") $OutputDir -Force

Write-Host "`n=== Paketleme Tamamlandı: $OutputDir ===" -ForegroundColor Green
Write-Host "Not: Host PC kaynak kodunda IS_DEV_SANDBOX_MODE halen 'true' olarak korunmaktadır." -ForegroundColor Gray
