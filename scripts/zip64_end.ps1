# zip의 끝 기록(EOCD)을 zip64 형식으로 바꿉니다. 파일 내용과 목록(central directory)은 그대로 두고,
# 목록 뒤에 zip64 끝 기록과 위치 기록(locator)을 넣은 뒤 원래 끝 기록의 값을 0xFFFF/0xFFFFFFFF로 둡니다.
#
# 왜: RocketRPG 1.1.0~1.1.2의 빠른 업데이트(바뀐 파일만 받기)는 진행 창 오류('다른 스레드가 이 개체를 소유…')로 실패합니다.
#     그 버전들은 zip64 끝 기록을 읽지 못하면 'zip 전체 받기'로 넘어가는데, 그 길은 오류 없이 업데이트됩니다.
#     1.1.3부터는 zip64 끝 기록도 읽으므로 계속 바뀐 파일만 받습니다. .NET, 탐색기, tar 모두 그대로 풉니다.
#   pwsh -File scripts\zip64_end.ps1 dist\RocketRPG-1.1.3-portable.zip
param([Parameter(Mandatory)][string]$Zip)
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path $Zip).Path
$fs = [IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
try {
    $len = $fs.Length
    $tailLen = [int][Math]::Min($len, 65557)
    $tail = New-Object byte[] $tailLen
    $fs.Position = $len - $tailLen
    [void]$fs.Read($tail, 0, $tailLen)
    $at = -1
    for ($i = $tailLen - 22; $i -ge 0; $i--) {
        if ([BitConverter]::ToUInt32($tail, $i) -eq 0x06054b50) { $at = $i; break }
    }
    if ($at -lt 0) { throw "end of central directory not found: $path" }
    $count = [BitConverter]::ToUInt16($tail, $at + 10)
    $cdSize = [BitConverter]::ToUInt32($tail, $at + 12)
    $cdOffset = [BitConverter]::ToUInt32($tail, $at + 16)
    if ($count -eq 0xFFFF -or $cdSize -eq [uint32]::MaxValue -or $cdOffset -eq [uint32]::MaxValue) { Write-Host "already zip64: $path"; return }
    $eocdPos = $len - $tailLen + $at

    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    # zip64 끝 기록 (56바이트)
    $w.Write([uint32]0x06064b50); $w.Write([uint64]44); $w.Write([uint16]45); $w.Write([uint16]45)
    $w.Write([uint32]0); $w.Write([uint32]0)
    $w.Write([uint64]$count); $w.Write([uint64]$count); $w.Write([uint64]$cdSize); $w.Write([uint64]$cdOffset)
    # zip64 끝 기록 위치 (20바이트)
    $w.Write([uint32]0x07064b50); $w.Write([uint32]0); $w.Write([uint64]$eocdPos); $w.Write([uint32]1)
    # 원래 끝 기록: 값은 zip64 기록에 있음 (22바이트, 설명 없음)
    $w.Write([uint32]0x06054b50); $w.Write([uint16]0); $w.Write([uint16]0)
    $w.Write([uint16]0xFFFF); $w.Write([uint16]0xFFFF); $w.Write([uint32]::MaxValue); $w.Write([uint32]::MaxValue); $w.Write([uint16]0)
    $w.Flush()

    $fs.SetLength($eocdPos)
    $fs.Position = $eocdPos
    $bytes = $ms.ToArray()
    $fs.Write($bytes, 0, $bytes.Length)
    Write-Host "zip64 end records written: $path ($count entries)"
}
finally { $fs.Dispose() }
