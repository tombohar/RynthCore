# Build-SehTrampoline.ps1
# Compiles RynthCore.SehTrampoline.dll (x86 MSVC) and copies to the runtime.
# Run from anywhere; finds VS automatically via vswhere.

$ErrorActionPreference = "Stop"

$vsPath = & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath 2>$null

if (-not $vsPath) { Write-Error "Visual Studio with C++ tools not found."; exit 1 }

$vcVer    = (Get-ChildItem "$vsPath\VC\Tools\MSVC" | Sort-Object Name -Descending | Select-Object -First 1).Name
$vcBase   = "$vsPath\VC\Tools\MSVC\$vcVer"
$cl       = "$vcBase\bin\Hostx86\x86\cl.exe"
$sdkVer   = (Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\Include" | Sort-Object Name -Descending | Select-Object -First 1).Name
$sdkInc   = "C:\Program Files (x86)\Windows Kits\10\Include\$sdkVer"
$sdkLib   = "C:\Program Files (x86)\Windows Kits\10\Lib\$sdkVer"

$env:INCLUDE = "$vcBase\include;$sdkInc\ucrt;$sdkInc\um;$sdkInc\shared"
$env:LIB     = "$vcBase\lib\x86;$sdkLib\ucrt\x86;$sdkLib\um\x86"

$src  = $PSScriptRoot
$out  = Join-Path $src "bin"
New-Item -ItemType Directory -Path $out -Force | Out-Null

Write-Host "Compiling SehTrampoline.c (MSVC $vcVer, x86)..."
Set-Location $out
& $cl /nologo /O2 /MT /GS- /LD /W3 "$src\SehTrampoline.c" `
    /link /MACHINE:X86 /OUT:"$out\RynthCore.SehTrampoline.dll"
if ($LASTEXITCODE -ne 0) { Write-Error "Compilation failed."; exit $LASTEXITCODE }

$size = (Get-Item "$out\RynthCore.SehTrampoline.dll").Length
Write-Host "Built: RynthCore.SehTrampoline.dll ($size bytes)"

$runtimeDst = "C:\Games\RynthCore\Runtime\RynthCore.SehTrampoline.dll"
Copy-Item "$out\RynthCore.SehTrampoline.dll" $runtimeDst -Force
Write-Host "Deployed to $runtimeDst"
