<#
.SYNOPSIS
Проверяет известные видеопорты дрона без полётных команд.
.DESCRIPTION
Требуется PowerShell 7 и Wi-Fi-адрес компьютера 192.168.169.2.
Сохраняет первые 8 датаграмм в analysis/live. Работает около 9 секунд.
#>
$ErrorActionPreference = 'Stop'
$outputDirectory = Join-Path $PSScriptRoot '..\analysis\live'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

foreach ($port in @(554, 7070)) {
    $tcp = [Net.Sockets.TcpClient]::new()
    try {
        $connection = $tcp.ConnectAsync('192.168.169.1', $port)
        if ($connection.Wait(1800)) {
            "TCP ${port}: OPEN"
        } else {
            "TCP ${port}: TIMEOUT"
        }
    } catch {
        "TCP ${port}: $($_.Exception.GetBaseException().Message)"
    } finally {
        $tcp.Dispose()
    }
}

$localEndpoint = [Net.IPEndPoint]::new([Net.IPAddress]::Parse('192.168.169.2'), 0)
$remoteEndpoint = [Net.IPEndPoint]::new([Net.IPAddress]::Parse('192.168.169.1'), 8800)
$udp = [Net.Sockets.UdpClient]::new($localEndpoint)
$udp.Client.ReceiveTimeout = 110
$deadline = [DateTime]::UtcNow.AddSeconds(5)
$packetCount = 0
$hello = [byte[]](239, 0, 4, 0)

try {
    while ([DateTime]::UtcNow -lt $deadline) {
        [void]$udp.Send($hello, $hello.Length, $remoteEndpoint)
        try {
            $sender = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
            $packet = $udp.Receive([ref]$sender)
            $packetCount++
            "Peer: $sender"

            if ($packetCount -le 8) {
                [Convert]::ToHexString($packet[0..([Math]::Min(55, $packet.Length - 1))])
                $outputPath = Join-Path $outputDirectory "packet-$packetCount.bin"
                [IO.File]::WriteAllBytes($outputPath, $packet)
            }
        } catch [Net.Sockets.SocketException] {
            if ($_.Exception.SocketErrorCode -ne 'TimedOut') {
                throw
            }
        }
    }
    "UDP 8800 packets=$packetCount"
} finally {
    $udp.Dispose()
}
