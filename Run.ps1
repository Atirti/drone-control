<#
.SYNOPSIS
Запускает WPF-приложение из исходников. Требуется .NET 10 SDK.
#>
$ErrorActionPreference = 'Stop'
dotnet run --project "$PSScriptRoot\src\DroneControl" -c Release
exit $LASTEXITCODE
