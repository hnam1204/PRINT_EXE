$ErrorActionPreference = 'Stop'
$resultPath = Join-Path $env:TEMP 'rpm-ipp-add-printer-result.txt'
try {
    Add-Printer -Name 'Remote Print Manager' -IppURL 'ipp://MSI:9199/printers/rpm' -ErrorAction Stop
    Get-Printer -Name 'Remote Print Manager' |
        Format-List Name,DriverName,PortName,PrinterStatus |
        Out-File -LiteralPath $resultPath -Encoding utf8
    exit 0
}
catch {
    $_ | Format-List * -Force | Out-File -LiteralPath $resultPath -Encoding utf8
    exit 1
}
