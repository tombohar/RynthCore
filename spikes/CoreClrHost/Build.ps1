# Build.ps1 - builds the Phase 0 CoreCLR hosting spike into .\bin
#   bin\SpikeHostExe.exe, SpikeHost.dll       native x86 (cl.exe)
#   bin\Shim.dll, Contracts.dll               default ALC (in the TPA list)
#   bin\payload\Payload.dll                   loaded into a collectible ALC per generation
#   bin\runtime\                              app-local CoreCLR x86 from the NuGet runtime pack
# Run: .\bin\SpikeHostExe.exe <cycles> <mode>   (mode bits: see Payload.cs)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$bin = Join-Path $root "bin"
New-Item -ItemType Directory -Force -Path $bin, "$bin\payload", "$bin\runtime", "$bin\obj" | Out-Null

# --- native ---
$vsPath = & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcVer = (Get-ChildItem "$vsPath\VC\Tools\MSVC" | Sort-Object Name -Descending | Select-Object -First 1).Name
$vcBase = "$vsPath\VC\Tools\MSVC\$vcVer"
$cl = "$vcBase\bin\Hostx86\x86\cl.exe"
$sdkVer = (Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\Include" | Sort-Object Name -Descending | Select-Object -First 1).Name
$sdkInc = "C:\Program Files (x86)\Windows Kits\10\Include\$sdkVer"
$sdkLib = "C:\Program Files (x86)\Windows Kits\10\Lib\$sdkVer"
$env:INCLUDE = "$vcBase\include;$sdkInc\ucrt;$sdkInc\um;$sdkInc\shared"
$env:LIB = "$vcBase\lib\x86;$sdkLib\ucrt\x86;$sdkLib\um\x86"

Push-Location "$bin\obj"
try {
    & $cl /nologo /O2 /MT /W3 /LD "$root\native\SpikeHost.c" /link /MACHINE:X86 /OUT:"$bin\SpikeHost.dll"
    if ($LASTEXITCODE -ne 0) { throw "SpikeHost.dll failed" }
    & $cl /nologo /O2 /MT /W3 "$root\native\SpikeHostExe.c" /link /MACHINE:X86 /LARGEADDRESSAWARE /OUT:"$bin\SpikeHostExe.exe"
    if ($LASTEXITCODE -ne 0) { throw "SpikeHostExe.exe failed" }
    & $cl /nologo /O2 /MT /W3 "$root\native\SpikeInject.c" /link /MACHINE:X86 /OUT:"$bin\SpikeInject.exe"
    if ($LASTEXITCODE -ne 0) { throw "SpikeInject.exe failed" }
} finally { Pop-Location }

# --- managed ---
foreach ($p in "Contracts", "Shim", "Payload") {
    dotnet build "$root\$p\$p.csproj" -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "$p build failed" }
}
Copy-Item "$root\Shim\bin\Release\net10.0\Shim.dll" $bin -Force
Copy-Item "$root\Contracts\bin\Release\net10.0\Contracts.dll" $bin -Force
Copy-Item "$root\Payload\bin\Release\net10.0\Payload.dll" "$bin\payload" -Force

# --- app-local runtime ---
$pack = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.netcore.app.runtime.win-x86"
$ver = (Get-ChildItem $pack | Where-Object Name -like "10.*" | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).Name
$rt = "$pack\$ver\runtimes\win-x86"
if (-not (Test-Path "$bin\runtime\coreclr.dll")) {
    Copy-Item "$rt\native\*" "$bin\runtime" -Force
    Copy-Item "$rt\lib\net10.0\*.dll" "$bin\runtime" -Force
}
Write-Host "Built spike with runtime $ver into $bin"
