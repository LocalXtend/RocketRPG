# RocketRPG Core build (clang++ / LLVM-MinGW UCRT x64)
# Incremental + parallel: each .cpp becomes an object in build\core\obj\<arch>\ and is recompiled only when
# the source or a shared header changed. Native test programs are built only with -Tests.
param([switch]$Tests, [switch]$Clean)
$ErrorActionPreference = 'Stop'
$mingw = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\MartinStorsjo.LLVM-MinGW.UCRT_Microsoft.Winget.Source_8wekyb3d8bbwe\llvm-mingw-20260616-ucrt-x86_64\bin"
if (-not (Test-Path "$mingw\clang++.exe")) { $mingw = 'C:\Program Files\LLVM\bin' }
$cxx = Join-Path $mingw 'clang++.exe'

$outDir = 'build\core'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if ($Clean) { Remove-Item -Recurse -Force (Join-Path $outDir 'obj') -ErrorAction SilentlyContinue }

$inc = '-Isrc\Core\include'
$flags = @('-std=c++20','-O2','-g0','-fvisibility=hidden',
           '-Wall','-Wno-unused-function','-Wno-macro-redefined',
           '-DUNICODE','-D_UNICODE')
$libs = @('-lws2_32','-ld3d11','-ldxgi','-ldwmapi','-lole32','-luuid','-luser32','-lgdi32',
          '-ladvapi32','-lshell32','-lshlwapi','-lversion','-lwinmm','-loleaut32',
          '-lruntimeobject','-lmmdevapi','-ld2d1','-ldwrite','-ldcomp')

# 헤더가 바뀌면 전체 재컴파일 (헤더 수가 적어 개별 의존성 추적보다 단순하고 안전)
$headerTime = (Get-ChildItem src\Core\include\*.h, src\Core\src\*.hpp | Measure-Object LastWriteTimeUtc -Maximum).Maximum

function Compile-Objects([string]$compiler, [string[]]$sources, [string]$arch, [string[]]$extra) {
    $objDir = Join-Path $outDir "obj\$arch"
    New-Item -ItemType Directory -Force -Path $objDir | Out-Null
    $objs = @()
    $jobs = @()
    foreach ($src in $sources) {
        $obj = Join-Path $objDir ([IO.Path]::GetFileNameWithoutExtension($src) + '.o')
        $objs += $obj
        $stale = -not (Test-Path $obj)
        if (-not $stale) {
            $ot = (Get-Item $obj).LastWriteTimeUtc
            $stale = (Get-Item $src).LastWriteTimeUtc -gt $ot -or $headerTime -gt $ot
        }
        if ($stale) { $jobs += [pscustomobject]@{ Src = $src; Obj = $obj } }
    }
    if ($jobs.Count -eq 0) { Write-Host "  [$arch] up to date"; return ,$objs }

    $max = [Math]::Max(1, [Environment]::ProcessorCount)
    $running = @()
    $failed = @()
    $queue = [System.Collections.Queue]::new($jobs)
    while ($queue.Count -gt 0 -or $running.Count -gt 0) {
        while ($queue.Count -gt 0 -and $running.Count -lt $max) {
            $j = $queue.Dequeue()
            # Windows PowerShell 5.1(.NET Framework)에는 ArgumentList가 없어 직접 인용합니다.
            $argv = ($flags + $extra + @($inc, '-c', $j.Src, '-o', $j.Obj)) | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
            $psi = New-Object Diagnostics.ProcessStartInfo $compiler, ($argv -join ' ')
            $psi.UseShellExecute = $false
            $psi.RedirectStandardError = $true
            $p = [Diagnostics.Process]::Start($psi)
            $running += [pscustomobject]@{ P = $p; Job = $j; Err = $p.StandardError.ReadToEndAsync() }
        }
        Start-Sleep -Milliseconds 30
        $still = @()
        foreach ($r in $running) {
            if ($r.P.HasExited) {
                $err = $r.Err.Result
                if ($r.P.ExitCode -ne 0) { $failed += $r.Job.Src; Write-Host $err }
                elseif ($err -match 'warning') { Write-Host $err }
            } else { $still += $r }
        }
        $running = $still
    }
    if ($failed.Count -gt 0) { throw "compile failed: $($failed -join ', ')" }
    Write-Host "  [$arch] compiled $($jobs.Count) file(s)"
    return ,$objs
}

function Link-IfNeeded([string]$compiler, [string[]]$objs, [string]$target, [string[]]$linkArgs) {
    $newest = ($objs | ForEach-Object { (Get-Item $_).LastWriteTimeUtc } | Measure-Object -Maximum).Maximum
    if ((Test-Path $target) -and (Get-Item $target).LastWriteTimeUtc -ge $newest) { Write-Host "OK -> $target (up to date)"; return }
    & $compiler '-shared' @objs '-o' $target @linkArgs '-static-libstdc++' '-static'
    if ($LASTEXITCODE -ne 0) { throw "link failed: $target ($LASTEXITCODE)" }
    Write-Host "OK -> $target"
}

$sw = [Diagnostics.Stopwatch]::StartNew()

# x64 core
$sources = Get-ChildItem src\Core\src -Filter *.cpp | ForEach-Object FullName
$objs64 = Compile-Objects $cxx $sources 'x64' @()
$target = Join-Path $outDir 'RocketRPGCore.dll'
Link-IfNeeded $cxx $objs64 $target $libs

if ($Tests) {
    $testDefs = @(
        @{ Name = 'test_core';     Src = 'src\Core\tests\test_core.cpp';     Args = @('-DUNICODE','-D_UNICODE', $target) }

    )
    foreach ($t in $testDefs) {
        $exe = Join-Path $outDir "$($t.Name).exe"
        & $cxx '-std=c++20' '-O2' '-g0' $inc $t.Src '-o' $exe @($t.Args) '-static-libstdc++' '-static'
        if ($LASTEXITCODE -ne 0) { throw "$($t.Name) build failed: $LASTEXITCODE" }
        Write-Host "OK -> $exe"
    }
}

Write-Host ("core build: {0:0.0}s" -f $sw.Elapsed.TotalSeconds)
