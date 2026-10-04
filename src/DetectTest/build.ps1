# DetectTest.exe build (clang++ / LLVM-MinGW UCRT x64)
$ErrorActionPreference = 'Stop'
$mingw = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\MartinStorsjo.LLVM-MinGW.UCRT_Microsoft.Winget.Source_8wekyb3d8bbwe\llvm-mingw-20260616-ucrt-x86_64\bin"
if (-not (Test-Path "$mingw\clang++.exe")) { $mingw = 'C:\Program Files\LLVM\bin' }
$cxx = Join-Path $mingw 'clang++.exe'

$outDir = 'build\detecttest'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$src = 'src\DetectTest\main.cpp'
$inc = @('-Isrc\RPGMakerVersionDetect\include')
$flags = @('-std=c++20','-O2','-g0','-municode',
           '-Wall','-Wno-unused-function','-Wno-macro-redefined',
           '-DUNICODE','-D_UNICODE')
$libs = @('-lole32','-luuid','-lshell32','-luser32')

$target = Join-Path $outDir 'DetectTest.exe'

# Load the detection DLL at runtime (LoadLibraryW); no import lib needed.
& $cxx @flags $inc $src '-o' $target @libs '-static-libstdc++' '-static'
if ($LASTEXITCODE -ne 0) { throw "build failed: $LASTEXITCODE" }
Write-Host "OK -> $target"

# Keep the DLL next to the test exe so LoadLibrary finds it without PATH edits.
$dll = 'build\rpgmakerversiondetect\RPGMakerVersionDetect.dll'
if (Test-Path $dll) { Copy-Item -Force $dll (Join-Path $outDir 'RPGMakerVersionDetect.dll') }
