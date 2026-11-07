<#
.SYNOPSIS
Останавливает только видеомост из папки dist этого проекта.
#>
$executable = Join-Path $PSScriptRoot 'dist\DroneVideoBridge\DroneVideoBridge.exe'
Get-Process DroneVideoBridge -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $executable } |
    Stop-Process
