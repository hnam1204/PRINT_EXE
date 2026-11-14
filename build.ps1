param(
    [string]$Version = "1.0.0",
    [string[]]$Changelog = @(
        "Tự động kết nối lại máy chủ khi mạng Internet phục hồi",
        "Tối ưu hóa hàng đợi in và bảo vệ chống in trùng lặp",
        "Bổ sung chức năng Cập nhật & Nâng cấp phần mềm trực tiếp trong ứng dụng",
        "Cải tiến tốc độ nhận diện máy in USB và trạng thái trực tuyến"
    )
)

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   REMOTE PRINT MANAGER (RPM) - BUILD & DISTRIBUTION      " -ForegroundColor Yellow
Write-Host "   Version: V$Version" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Cyan

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# 1. Clean previous build outputs
Write-Host "`n[1/5] Dang don dep thu muc build cu..." -ForegroundColor Gray
if (Test-Path "publish") {
    Remove-Item -Recurse -Force "publish"
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
dotnet publish src/RPM.App/RPM.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/single-file
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Publish RPM.App that bai!" -ForegroundColor Red
    Exit 1
}

# Rename single file for distribution
Copy-Item "publish/single-file/RPM.App.exe" -Destination "publish/RemotePrintManager.exe" -Force

# 4. Publish Standalone Cloud Relay Broker
Write-Host "`n[4/5] Dang dong goi RPM.Relay Broker..." -ForegroundColor Gray
dotnet publish src/RPM.Relay/RPM.Relay.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/relay
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Publish RPM.Relay that bai!" -ForegroundColor Red
    Exit 1
}

# 5. Generate Manifest version.json with SHA-256 Checksum
Write-Host "`n[5/5] Dang tinh toan ma SHA-256 va tao version.json cho he thong cap nhat..." -ForegroundColor Gray

$fileHash = (Get-FileHash -Path "publish/RemotePrintManager.exe" -Algorithm SHA256).Hash.ToLowerInvariant()

$manifestObj = [ordered]@{
    version = $Version
    releaseDate = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    downloadUrl = "http://127.0.0.1:9090/download/RemotePrintManager.exe"
    sha256 = $fileHash
    minSupportedVersion = "1.0.0"
    changelog = $Changelog
    releaseNotes = "Phien ban V$Version mang lai kha nang cap nhat tu dong va on dinh ket noi mang."
}

$manifestJson = $manifestObj | ConvertTo-Json -Depth 5
$manifestJson | Set-Content -Path "publish/version.json" -Encoding UTF8

# Prepare relay server staging for immediate local/network update hosting
$relayUpdatesDir = "publish/relay/updates"
New-Item -ItemType Directory -Force -Path $relayUpdatesDir | Out-Null
Copy-Item "publish/version.json" -Destination "publish/relay/version.json" -Force
Copy-Item "publish/RemotePrintManager.exe" -Destination "$relayUpdatesDir/RemotePrintManager.exe" -Force

# Create portable zip distribution
Write-Host "`nDang tao file zip phan phoi..." -ForegroundColor Gray
Compress-Archive -Path "publish/single-file/*" -DestinationPath "publish/RemotePrintManager_v$Version`_Portable.zip" -Force

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host "   DONG GOI THANH CONG! TEP PHAN PHOI SAN SANG:           " -ForegroundColor Green
Write-Host "   1. Ung dung chay truc tiep: publish/RemotePrintManager.exe" -ForegroundColor White
Write-Host "   2. Goi Portable Zip:        publish/RemotePrintManager_v$Version`_Portable.zip" -ForegroundColor White
Write-Host "   3. Relay Broker:            publish/relay/RPM.Relay.exe" -ForegroundColor White
Write-Host "   4. Update Manifest:         publish/version.json" -ForegroundColor White
Write-Host "      (SHA-256: $fileHash)" -ForegroundColor DarkGray
Write-Host "==========================================================" -ForegroundColor Green
