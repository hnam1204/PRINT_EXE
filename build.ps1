# ====================================================================
# Remote Print Manager (RPM) - Build & Package Script
# ====================================================================

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   REMOTE PRINT MANAGER (RPM) - BUILD & DISTRIBUTION      " -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Cyan

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# 1. Clean previous build outputs
Write-Host "`n[1/4] Dang don dep thu muc build cu..." -ForegroundColor Gray
if (Test-Path "publish") {
    Remove-Item -Recurse -Force "publish"
}

# 2. Build solution & Run Automated Tests
Write-Host "`n[2/4] Dang bien dich va chay Test kiem tra toan dien..." -ForegroundColor Gray
dotnet test --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Kiem thu that bai! Vui long kiem tra lai ma nguon." -ForegroundColor Red
    Exit 1
}

# 3. Publish Single File Desktop App
Write-Host "`n[3/4] Dang dong goi Single-File Executable cho RPM.App..." -ForegroundColor Gray
dotnet publish src/RPM.App/RPM.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/single-file
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Publish RPM.App that bai!" -ForegroundColor Red
    Exit 1
}

# 4. Publish Standalone Cloud Relay Broker
Write-Host "`n[4/4] Dang dong goi RPM.Relay Broker..." -ForegroundColor Gray
dotnet publish src/RPM.Relay/RPM.Relay.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/relay
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Publish RPM.Relay that bai!" -ForegroundColor Red
    Exit 1
}

# Rename single file for clarity
Copy-Item "publish/single-file/RPM.App.exe" -Destination "publish/RemotePrintManager.exe" -Force

# Create portable zip distribution
Write-Host "`nDang tao file zip phan phoi..." -ForegroundColor Gray
Compress-Archive -Path "publish/single-file/*" -DestinationPath "publish/RemotePrintManager_v1.0.0_Portable.zip" -Force

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host "   DONG GOI THANH CONG! TEP PHAN PHOI SAN SANG:           " -ForegroundColor Green
Write-Host "   1. Ung dung chay truc tiep: publish/RemotePrintManager.exe" -ForegroundColor White
Write-Host "   2. Goi Portable Zip:        publish/RemotePrintManager_v1.0.0_Portable.zip" -ForegroundColor White
Write-Host "   3. Relay Broker:            publish/relay/RPM.Relay.exe" -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor Green
