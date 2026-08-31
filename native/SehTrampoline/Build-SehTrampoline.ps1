# Build-SehTrampoline.ps1
# Compiles RynthCore.SehTrampoline.dll (x86 MSVC) and copies to the runtime.
# Run from anywhere; finds VS automatically via vswhere.

$ErrorActionPreference = "Stop"

# Toolchain discovery, in order:
#   1. An already-configured environment: cl.exe on PATH with INCLUDE and LIB
#      set. This covers a VS Developer Prompt and equally a portable toolchain
#      (see tools\msvc-env.ps1), so no Visual Studio install is required.
#   2. vswhere against a real Visual Studio install.
$cl = $null
$preConfigured = (Get-Command cl.exe -ErrorAction SilentlyContinue) -and
                 $env:INCLUDE -and $env:LIB

if ($preConfigured) {
    $cl = (Get-Command cl.exe).Source
    Write-Host "Using pre-configured MSVC environment: $cl"
} else {
    $vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    $vsPath = if (Test-Path -LiteralPath $vswhere) {
        & $vswhere -latest -products * `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -property installationPath 2>$null
    }

    if (-not $vsPath) {
        Write-Error @"
No C++ toolchain found. Either:
  - source a configured environment first (e.g. . tools\msvc-env.ps1), or
  - install Visual Studio with the Desktop development with C++ workload.
"@
        exit 1
    }

    $vcVer  = (Get-ChildItem "$vsPath\VC\Tools\MSVC" | Sort-Object Name -Descending | Select-Object -First 1).Name
    $vcBase = "$vsPath\VC\Tools\MSVC\$vcVer"
    $cl     = "$vcBase\bin\Hostx86\x86\cl.exe"
    $sdkVer = (Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\Include" | Sort-Object Name -Descending | Select-Object -First 1).Name
    $sdkInc = "C:\Program Files (x86)\Windows Kits\10\Include\$sdkVer"
    $sdkLib = "C:\Program Files (x86)\Windows Kits\10\Lib\$sdkVer"

    $env:INCLUDE = "$vcBase\include;$sdkInc\ucrt;$sdkInc\um;$sdkInc\shared"
    $env:LIB     = "$vcBase\lib\x86;$sdkLib\ucrt\x86;$sdkLib\um\x86"
}

$src  = $PSScriptRoot
$out  = Join-Path $src "bin"
New-Item -ItemType Directory -Path $out -Force | Out-Null

Write-Host "Compiling SehTrampoline.c (x86) with $cl ..."
Set-Location $out
& $cl /nologo /O2 /MT /GS- /LD /W3 "$src\SehTrampoline.c" `
    /link /MACHINE:X86 /OUT:"$out\RynthCore.SehTrampoline.dll"
if ($LASTEXITCODE -ne 0) { Write-Error "Compilation failed."; exit $LASTEXITCODE }

$size = (Get-Item "$out\RynthCore.SehTrampoline.dll").Length
Write-Host "Built: RynthCore.SehTrampoline.dll ($size bytes)"

$runtimeDst = "C:\Games\RynthCore\Runtime\RynthCore.SehTrampoline.dll"
Copy-Item "$out\RynthCore.SehTrampoline.dll" $runtimeDst -Force
Write-Host "Deployed to $runtimeDst"
