# Builds tools\TestClient\bin\TestClient.exe (x86).
$ErrorActionPreference = "Stop"
$vsPath = & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcVer  = (Get-ChildItem "$vsPath\VC\Tools\MSVC" | Sort-Object Name -Descending | Select-Object -First 1).Name
$vcBase = "$vsPath\VC\Tools\MSVC\$vcVer"
$sdkVer = (Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\Include" | Sort-Object Name -Descending | Select-Object -First 1).Name
$sdkInc = "C:\Program Files (x86)\Windows Kits\10\Include\$sdkVer"
$sdkLib = "C:\Program Files (x86)\Windows Kits\10\Lib\$sdkVer"
$env:INCLUDE = "$vcBase\include;$sdkInc\ucrt;$sdkInc\um;$sdkInc\shared"
$env:LIB     = "$vcBase\lib\x86;$sdkLib\ucrt\x86;$sdkLib\um\x86"
$out = Join-Path $PSScriptRoot "bin"
New-Item -ItemType Directory -Force -Path "$out\obj" | Out-Null
Push-Location "$out\obj"
try {
    & "$vcBase\bin\Hostx86\x86\cl.exe" /nologo /O2 /MT /W3 "$PSScriptRoot\TestClient.c" /link /MACHINE:X86 /OUT:"$out\TestClient.exe"
    if ($LASTEXITCODE -ne 0) { throw "TestClient build failed" }
} finally { Pop-Location }
Write-Host "Built $out\TestClient.exe"
