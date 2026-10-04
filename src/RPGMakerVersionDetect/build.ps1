# RPGMakerVersionDetect.dll build (clang++ / LLVM-MinGW UCRT x64)
$ErrorActionPreference = 'Stop'
$mingw = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\MartinStorsjo.LLVM-MinGW.UCRT_Microsoft.Winget.Source_8wekyb3d8bbwe\llvm-mingw-20260616-ucrt-x86_64\bin"
if (-not (Test-Path "$mingw\clang++.exe")) { $mingw = 'C:\Program Files\LLVM\bin' }
$cxx = Join-Path $mingw 'clang++.exe'

$outDir = 'build\rpgmakerversiondetect'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$sources = Get-ChildItem src\RPGMakerVersionDetect\src -Filter *.cpp | ForEach-Object FullName
$inc = '-Isrc\RPGMakerVersionDetect\include'
$flags = @('-std=c++20','-O2','-g0','-shared','-fvisibility=hidden',
           '-Wall','-Wno-unused-function','-Wno-macro-redefined',
           '-DRMD_BUILD','-DUNICODE','-D_UNICODE')
$libs = @('-lole32','-luuid','-lshell32','-luser32','-lversion','-ladvapi32')

$target = Join-Path $outDir 'RPGMakerVersionDetect.dll'
& $cxx @flags $inc $sources '-o' $target @libs '-static-libstdc++' '-static'
if ($LASTEXITCODE -ne 0) { throw "build failed: $LASTEXITCODE" }
Write-Host "OK -> $target"
