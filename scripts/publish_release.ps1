<#
.SYNOPSIS
    Remote Print Manager (RPM) - Quy Trinh Phat Hanh Phien Ban
.DESCRIPTION
    Dieu huong va thuc thi quy trinh dong goi chuan hoa tu build.ps1
#>

param (
    [Parameter(Mandatory = $false)]
    [string]$Version = "1.0.0",

    [Parameter(Mandatory = $false)]
    [ValidateSet("Stable", "Beta")]
    [string]$Channel = "Stable",

    [Parameter(Mandatory = $false)]
    [string[]]$Changelog = @(
        "Toi uu hoa giao dien thuong mai hoa chuan Windows 11",
        "Tu dong khoi chay Cloud Relay Broker noi bo",
        "Bao toan 100% cau hinh va thiet lap in an"
    )
)

$rootDir = Split-Path -Parent $PSScriptRoot
$buildScript = Join-Path $rootDir "build.ps1"

& $buildScript -Version $Version -Changelog $Changelog
