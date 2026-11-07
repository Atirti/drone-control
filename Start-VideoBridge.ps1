<#
.SYNOPSIS
Запускает опубликованный видеомост скрытым процессом для VLC.
#>
$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'dist\DroneVideoBridge\DroneVideoBridge.exe'
$existing = Get-Process DroneVideoBridge -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $executable }

if ($existing) {
    Write-Host 'Видеомост уже запущен: http://127.0.0.1:8088/video.mjpg'
    exit
}

Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) -WindowStyle Hidden
Write-Host 'VLC: http://127.0.0.1:8088/video.mjpg'
