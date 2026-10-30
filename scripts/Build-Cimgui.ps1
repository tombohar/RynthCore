<#
.SYNOPSIS
    Builds the engine's 32-bit cimgui.dll from pinned upstream sources.

.DESCRIPTION
    Fetches cimgui at tag 1.91.6dock (and its Dear ImGui submodule) into
    native\cimgui\_src, verifies both commits, fetches ImGuiColorTextEdit
    (santaclose fork, the Meta/Lua source editor) into native\cimgui\_textedit
    at a pinned commit and applies native\cimgui\textedit-rynth.patch, builds
    with MSVC via CMake (Win32, Release, static CRT) using
    native\cimgui\CMakeLists.txt, and copies
    the result to src\RynthCore.Engine\Native\cimgui.dll.

    Why a custom build: ImGui.NET 1.91.6.1 treats ImTextureID as a pointer,
    but Dear ImGui 1.91.6 defaults it to a 64-bit integer, so on x86 the stock
    NuGet cimgui.dll does not match the wrapper (see native\cimgui\rynth_imconfig.h).

    Needs git and Visual Studio 2022 (Build Tools is enough) with the C++
    workload; CMake is taken from PATH or from the VS install.

.PARAMETER Clean
    Delete the CMake build directory first.
#>
[CmdletBinding()]
param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$Tag = '1.91.6dock'
$CimguiCommit = '970c614802935f51f451aa21ae06e838bdcf9349'
$ImguiCommit = '947aa9c9722bd6ff740dd757da609ff41f4d3ba3'
$TextEditCommit = '264bee49ddc3c789b05d928d09c628649458da47'

$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $repoRoot 'native\cimgui'
$srcDir = Join-Path $nativeDir '_src'
$textEditDir = Join-Path $nativeDir '_textedit'
$textEditPatch = Join-Path $nativeDir 'textedit-rynth.patch'
$buildDir = Join-Path $nativeDir '_build'
$outDll = Join-Path $repoRoot 'src\RynthCore.Engine\Native\cimgui.dll'

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" }
}

# ---- Sources ----
if (-not (Test-Path -LiteralPath $srcDir)) {
    Write-Host "Fetching cimgui $Tag ..."
    Invoke-Checked 'git clone' { git clone -q --depth 1 --branch $Tag --recurse-submodules --shallow-submodules https://github.com/cimgui/cimgui $srcDir }
}
$head = (git -C $srcDir rev-parse HEAD).Trim()
$imguiHead = (git -C (Join-Path $srcDir 'imgui') rev-parse HEAD).Trim()
if ($head -ne $CimguiCommit) { throw "native\cimgui\_src is at $head, expected $CimguiCommit ($Tag). Delete _src and rerun." }
if ($imguiHead -ne $ImguiCommit) { throw "native\cimgui\_src\imgui is at $imguiHead, expected $ImguiCommit. Delete _src and rerun." }

# ImGuiColorTextEdit: no submodules (its Boost.Regex is replaced by
# textedit_shim\boost\regex.hpp). LF checkout so the committed patch applies.
if (-not (Test-Path -LiteralPath $textEditDir)) {
    Write-Host "Fetching ImGuiColorTextEdit $TextEditCommit ..."
    Invoke-Checked 'git clone (textedit)' { git -c core.autocrlf=false clone -q --no-checkout https://github.com/santaclose/ImGuiColorTextEdit $textEditDir }
    Invoke-Checked 'git checkout (textedit)' { git -C $textEditDir -c core.autocrlf=false checkout -q $TextEditCommit }
}
$teHead = (git -C $textEditDir rev-parse HEAD).Trim()
if ($teHead -ne $TextEditCommit) { throw "native\cimgui\_textedit is at $teHead, expected $TextEditCommit. Delete _textedit and rerun." }
git -C $textEditDir apply --reverse --check $textEditPatch 2>$null
if ($LASTEXITCODE -ne 0) {
    Invoke-Checked 'git apply (textedit patch)' { git -C $textEditDir apply $textEditPatch }
}

# ---- CMake ----
$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'Visual Studio with the C++ x86/x64 tools was not found.' }
    $cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    if (-not (Test-Path -LiteralPath $cmake)) { throw "CMake not found on PATH or at $cmake" }
}

if ($Clean -and (Test-Path -LiteralPath $buildDir)) { Remove-Item -LiteralPath $buildDir -Recurse -Force }

Invoke-Checked 'cmake configure' { & $cmake -S $nativeDir -B $buildDir -G 'Visual Studio 17 2022' -A Win32 }
Invoke-Checked 'cmake build' { & $cmake --build $buildDir --config Release }

$built = Join-Path $buildDir 'Release\cimgui.dll'
if (-not (Test-Path -LiteralPath $built)) { throw "Build produced no $built" }

Copy-Item -LiteralPath $built -Destination $outDll -Force
$size = (Get-Item -LiteralPath $outDll).Length
Write-Host "cimgui.dll ($size bytes) -> $outDll"
