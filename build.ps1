param(
    [string]$Version = "1.0.1",
    [string]$Channel = "Beta",
    [string[]]$Changelog = @(
        "Toi gian hoan toan Trang chu: Bo thanh toolbar day man hinh, gom 100% chuc nang vao trung tam Cai dat",
        "Tich hop 6 tab in-page trong Cai dat: Thiet lap chung, May in, Thiet bi, Hang doi in, Chan doan, Cap nhat",
        "Sua triet de loi khoi dong NullReferenceException (ApplyClientFilters) khi nap ung dung",
        "Sua triet de loi mat icon tren Taskbar, Explorer va System Tray voi icon multi-res sac net",
        "Khoa nut mo rong Maximize, co dinh kich thuoc chuan 920x630px, ClearType Display Pixel Snapping"
    )
)

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   REMOTE PRINT MANAGER (RPM) - BUILD & DISTRIBUTION      " -ForegroundColor Yellow
Write-Host "   Version: V$Version ($Channel)" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Cyan

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# 1. Clean previous build outputs
Write-Host "`n[1/5] Dang don dep thu muc build cu..." -ForegroundColor Gray
Get-Process -Name "*RemotePrintManager*", "*RPM*" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

if (Test-Path "publish") {
    try {
        Remove-Item -Recurse -Force "publish" -ErrorAction Stop
    } catch {
        Get-ChildItem -Path "publish" -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

# 2. Build solution & Run Automated Tests
Write-Host "`n[2/5] Dang bien dich va chay Test kiem tra toan dien..." -ForegroundColor Gray
dotnet test --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Kiem thu that bai! Vui long kiem tra lai ma nguon." -ForegroundColor Red
    Exit 1
}

# 3. Publish Single File Desktop App
Write-Host "`n[3/5] Dang dong goi Single-File Executable cho RPM.App..." -ForegroundColor Gray
$tempPublish = "publish/temp-app"
dotnet publish src/RPM.App/RPM.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o $tempPublish
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Publish RPM.App that bai!" -ForegroundColor Red
    Exit 1
}

New-Item -ItemType Directory -Force -Path "publish" | Out-Null
Move-Item "$tempPublish/RPM.App.exe" -Destination "publish/RemotePrintManager.exe" -Force
Remove-Item -Recurse -Force $tempPublish -ErrorAction SilentlyContinue

# 4. Publish Standalone Cloud Relay Broker
Write-Host "`n[4/5] Dang dong goi RPM.Relay Broker..." -ForegroundColor Gray
dotnet publish src/RPM.Relay/RPM.Relay.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o publish/relay
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Publish RPM.Relay that bai!" -ForegroundColor Red
    Exit 1
}

# Clean all debug symbols and temporary artifacts in publish directory
Get-ChildItem -Path "publish" -Recurse -Filter "*.pdb" | Remove-Item -Force -ErrorAction SilentlyContinue

# 5. Generate Manifest version.json with SHA-256 Checksum
Write-Host "`n[5/5] Dang tinh toan ma SHA-256 va tao version.json cho he thong cap nhat..." -ForegroundColor Gray

$fileHash = (Get-FileHash -Path "publish/RemotePrintManager.exe" -Algorithm SHA256).Hash.ToLowerInvariant()

$manifestObj = [ordered]@{
    version = $Version
    channel = $Channel
    releaseDate = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    downloadUrl = "http://127.0.0.1:9090/download/RemotePrintManager.exe"
    sha256 = $fileHash
    minSupportedVersion = "1.0.0"
    changelog = $Changelog
    releaseNotes = "Phien ban V$Version ($Channel) voi giao dien One-Screen Minimal Desktop va nhan dien NHN Blue."
}

$manifestJson = $manifestObj | ConvertTo-Json -Depth 5
$manifestJson | Set-Content -Path "publish/version.json" -Encoding UTF8
$manifestJson | Set-Content -Path "publish/version_beta.json" -Encoding UTF8

# Prepare relay server staging for immediate local/network update hosting
$relayUpdatesDir = "publish/relay/updates"
New-Item -ItemType Directory -Force -Path $relayUpdatesDir | Out-Null
Copy-Item "publish/version.json" -Destination "publish/relay/version.json" -Force
Copy-Item "publish/version_beta.json" -Destination "publish/relay/version_beta.json" -Force
Copy-Item "publish/RemotePrintManager.exe" -Destination "$relayUpdatesDir/RemotePrintManager.exe" -Force

# Create clean portable zip distribution (only executable + manifest, no debug symbols)
Write-Host "`nDang tao file zip phan phoi toi gian, sach se..." -ForegroundColor Gray
$zipContent = @("publish/RemotePrintManager.exe", "publish/version.json")
Compress-Archive -Path $zipContent -DestinationPath "publish/RemotePrintManager_v$Version`_Portable.zip" -Force

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host "   DONG GOI THANH CONG! TEP PHAN PHOI SAN SANG:           " -ForegroundColor Green
Write-Host "   1. Ung dung chay truc tiep: publish/RemotePrintManager.exe" -ForegroundColor White
Write-Host "   2. Goi Portable Zip:        publish/RemotePrintManager_v$Version`_Portable.zip" -ForegroundColor White
Write-Host "   3. Relay Broker:            publish/relay/RPM.Relay.exe" -ForegroundColor White
Write-Host "   4. Update Manifest:         publish/version.json & version_beta.json" -ForegroundColor White
Write-Host "      (SHA-256: $fileHash)" -ForegroundColor DarkGray
Write-Host "==========================================================" -ForegroundColor Green
