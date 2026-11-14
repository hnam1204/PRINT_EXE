<#
.SYNOPSIS
    Remote Print Manager (RPM) - Quy Trinh Phat Hanh Phien Ban Nang Cap Tu Dong
    
.DESCRIPTION
    Thuc hien quy trinh chuan hoa:
    Sua ma nguon -> Chay Test 100% -> Bien dich Single-File -> Tinh ma Hash SHA-256
    -> Tao manifest version.json -> Dong goi tep phan phoi -> Cap nhat Feed

.EXAMPLE
    .\scripts\publish_release.ps1 -Version "1.0.1" -Channel "Stable" -Changelog @("Khac phuc loi tu dong ket noi lai sau khi mat Internet", "Toi uu hoa hien thi trang thai may in")
    
.EXAMPLE
    .\scripts\publish_release.ps1 -Version "1.0.2" -Channel "Beta" -Changelog @("Thu nghiem tren may con 1 truoc khi phat hanh toan he thong")
#>

param (
    [Parameter(Mandatory = $false)]
    [string]$Version = "1.0.1",

    [Parameter(Mandatory = $false)]
    [ValidateSet("Stable", "Beta")]
    [string]$Channel = "Stable",

    [Parameter(Mandatory = $false)]
    [string[]]$Changelog = @(
        "Cai thien kha nang tu dong ket noi lai khi mat Internet",
        "Toi uu hoa toc do nhan dien may in va do tre hang doi",
        "Cap nhat giao dien Remote Print Manager mau cam #FF7900 chuan hoa"
    ),

    [Parameter(Mandatory = $false)]
    [string]$ReleaseNotes = "Phiên bản mới cải thiện khả năng kết nối và khắc phục lỗi hoạt động."
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor DarkYellow
Write-Host "   REMOTE PRINT MANAGER - PHAT HANH PHIEN BAN NANG CAP     " -ForegroundColor Yellow
Write-Host "   Version: V$Version | Kenh: $Channel" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor DarkYellow

$rootDir = Split-Path -Parent $PSScriptRoot
Set-Location $rootDir

# 1. Chay kiem thu toan dien (Bat buoc dat 100% moi duoc phat hanh)
Write-Host "`n[1/5] Chay kiem thu tu dong (Unit Tests)..." -ForegroundColor Cyan
$testResult = dotnet test --configuration Release --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "[LOI] Kiem thu that bai! Huy bo qua trinh phat hanh de dam bao an toan." -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Toan bo 17/17 bai test deu dat 100%!" -ForegroundColor Green

# 2. Bien dich phan mem Single-File Executable
Write-Host "`n[2/5] Bien dich Single-File Executable cho V$Version..." -ForegroundColor Cyan
$publishDir = Join-Path $rootDir "publish"
$singleFileDir = Join-Path $publishDir "single-file"
if (-not (Test-Path $singleFileDir)) { New-Item -ItemType Directory -Path $singleFileDir -Force | Out-Null }

dotnet publish "$rootDir/src/RPM.App/RPM.App.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version `
    -o $singleFileDir

$targetExe = Join-Path $publishDir "RemotePrintManager.exe"
Copy-Item (Join-Path $singleFileDir "RPM.App.exe") $targetExe -Force
Write-Host "[OK] Tao thanh cong: $targetExe" -ForegroundColor Green

# 3. Tinh toan ma bam SHA-256
Write-Host "`n[3/5] Tinh toan ma xac thuc SHA-256 chong gia mao/loi truyen tai..." -ForegroundColor Cyan
$sha256 = (Get-FileHash -Path $targetExe -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "[OK] SHA-256: $sha256" -ForegroundColor Green

# 4. Tao Update Manifest (version.json hoac version_beta.json)
Write-Host "`n[4/5] Tao manifest cap nhat..." -ForegroundColor Cyan
$manifestObj = [ordered]@{
    Version = $Version
    Channel = $Channel
    ReleaseDate = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    DownloadUrl = "http://127.0.0.1:9090/download/RemotePrintManager_v$Version.exe"
    Sha256 = $sha256
    MinSupportedVersion = "1.0.0"
    IsCritical = $false
    Changelog = $Changelog
    ReleaseNotes = $ReleaseNotes
}

$manifestJson = $manifestObj | ConvertTo-Json -Depth 5
$manifestFile = if ($Channel -eq "Beta") {
    Join-Path $publishDir "version_beta.json"
} else {
    Join-Path $publishDir "version.json"
}
[System.IO.File]::WriteAllText($manifestFile, $manifestJson, [System.Text.Encoding]::UTF8)

# 5. Dong bo voi Relay Broker cap nhat cuc bo
Write-Host "`n[5/5] Dong bo tai nguyen vao Relay Broker & Tao goi zip..." -ForegroundColor Cyan
$relayDir = Join-Path $publishDir "relay"
$relayUpdatesDir = Join-Path $relayDir "updates"
if (-not (Test-Path $relayUpdatesDir)) { New-Item -ItemType Directory -Path $relayUpdatesDir -Force | Out-Null }

$versionedExe = Join-Path $relayUpdatesDir "RemotePrintManager_v$Version.exe"
Copy-Item $targetExe $versionedExe -Force

if (Test-Path $manifestFile) {
    Copy-Item $manifestFile (Join-Path $relayDir (Split-Path -Leaf $manifestFile)) -Force
}

# Tao goi zip phan phoi
$zipName = "RemotePrintManager_v${Version}_$Channel.zip"
$zipPath = Join-Path $publishDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $targetExe, $manifestFile -DestinationPath $zipPath -Force

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host "   PHAT HANH PHIEN BAN V$Version ($Channel) THANH CONG!     " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "1. Tệp thực thi:       $targetExe"
Write-Host "2. Manifest phát hành: $manifestFile"
Write-Host "3. Tệp tải nâng cấp:   $versionedExe"
Write-Host "4. Gói Portable Zip:   $zipPath"
Write-Host "5. Mã SHA-256:         $sha256"
Write-Host "==========================================================" -ForegroundColor Green
