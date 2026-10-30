# Publish-EngineCoreClr.ps1
# Builds the CoreCLR-hosted engine (docs/UNLOADABLE_ENGINE_PLAN.md) and lays it out in a
# Runtime folder:
#   RynthCore.Loader.dll        native loader (native\Loader), hosts CoreCLR
#   RynthCore.Shim.dll          default-ALC shim (src\RynthCore.Shim)
#   RynthCore.Engine.dll (+pdb) the engine as a managed assembly (-p:EngineHost=coreclr)
#   Avalonia*, ImGui.NET, ...   the engine's managed dependencies (load into its collectible ALC)
#   cimgui, minhook, Skia, ...  native libraries (process-wide, loaded once)
#   dotnet\                     app-local CoreCLR x86 (from the NuGet runtime pack)
#
# Default target is the live install; a running client hot-reloads onto a new engine by itself:
#   .\scripts\Publish-EngineCoreClr.ps1                       -> C:\Games\RynthCore\Runtime
#   .\scripts\Publish-EngineCoreClr.ps1 -Target <dir> -EngineOnly   (engine + deps only: a hot reload)

param(
    [string]$Target = "C:\Games\RynthCore\Runtime",
    [string]$Configuration = "Release",
    [switch]$EngineOnly,
    # The engine is precompiled (ReadyToRun) unless -NoReadyToRun: each generation then
    # loads in ~130 ms instead of ~750 (less JIT); the DLL is ~5 MB instead of ~2.
    [switch]$NoReadyToRun,
    # Extra MSBuild arguments for the engine and shim builds (the installer passes -p:Version=...).
    [string[]]$MsBuildArgs = @(),
    # Deploy to the live Runtime even though status-export has commits this checkout lacks.
    [switch]$AllowBehind
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

# Deploying to the live install from a checkout that lacks status-export's commits rolls
# back other sessions' engine fixes on every running client (2026-09-29, API v70). Refuse.
$liveRuntime = "C:\Games\RynthCore\Runtime"
if ([IO.Path]::GetFullPath($Target).TrimEnd('\') -ieq $liveRuntime -and -not $AllowBehind) {
    git -C $repo merge-base --is-ancestor status-export HEAD 2>$null
    if ($LASTEXITCODE -ne 0) {
        $missing = git -C $repo log --oneline HEAD..status-export
        throw "This checkout is missing status-export commits; deploying it to the live Runtime would roll them back:`n$($missing -join "`n")`nMerge status-export first (or pass -AllowBehind if you really mean it)."
    }
}
$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"
New-Item -ItemType Directory -Force -Path $Target | Out-Null

$stage = Join-Path $repo "out\engine-clr"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Write-Host "Building the engine (EngineHost=coreclr)..."
$r2r = if (-not $NoReadyToRun) { "-p:PublishReadyToRun=true" } else { "-p:PublishReadyToRun=false" }
dotnet publish "$repo\src\RynthCore.Engine\RynthCore.Engine.csproj" -c $Configuration -p:EngineHost=coreclr $r2r -o $stage -nologo -v q @MsBuildArgs
if ($LASTEXITCODE -ne 0) { throw "engine build failed" }

if (-not $EngineOnly) {
    Write-Host "Building the native loader..."
    & "$repo\native\Loader\Build-Loader.ps1"
    Write-Host "Building the shim..."
    dotnet build "$repo\src\RynthCore.Shim\RynthCore.Shim.csproj" -c $Configuration -nologo -v q @MsBuildArgs
    if ($LASTEXITCODE -ne 0) { throw "shim build failed" }

    # The loader and the shim load once per client: a running client keeps its copy (and
    # locks the file) until it restarts. Skip them with a warning rather than fail.
    $hostFiles = @(
        "$repo\native\Loader\bin\RynthCore.Loader.dll", "$repo\native\Loader\bin\RynthCore.Loader.pdb",
        "$repo\src\RynthCore.Shim\bin\$Configuration\net10.0-windows\RynthCore.Shim.dll",
        "$repo\src\RynthCore.Shim\bin\$Configuration\net10.0-windows\RynthCore.Shim.pdb")
    # A running client holds these files; Windows lets a loaded DLL be renamed, so the old
    # copy is moved aside (the client keeps using it) and the new one goes in for the next
    # launch. Earlier moved-aside copies are deleted once no client holds them.
    Get-ChildItem $Target -Filter "RynthCore.*.old-*" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    foreach ($f in $hostFiles) {
        $dest = Join-Path $Target (Split-Path $f -Leaf)
        try { Copy-Item $f $dest -Force -ErrorAction Stop }
        catch {
            try {
                Rename-Item $dest ("{0}.old-{1:yyyyMMddHHmmss}" -f (Split-Path $dest -Leaf), (Get-Date)) -ErrorAction Stop
                Copy-Item $f $dest -Force -ErrorAction Stop
                Write-Host "$(Split-Path $f -Leaf) was in use: moved the old one aside; new launches get the new one."
            }
            catch { Write-Warning "$(Split-Path $f -Leaf) is in use by a running client; it keeps the old one until it restarts." }
        }
    }

    $seh = "$repo\native\SehTrampoline\bin\RynthCore.SehTrampoline.dll"
    $sehC = "$repo\native\SehTrampoline\SehTrampoline.c"
    # bin\ is gitignored, so a checkout keeps whatever DLL was last built there. Until
    # 2026-10-12 this shipped a June v1 build over every newer source (v4 was merged):
    # its crash logger turned managed NullReferenceExceptions into fatal CLR errors and
    # the Client::UseTime guard stayed off. Same staleness rule as Deploy-RynthCore.ps1.
    if ((Test-Path $seh) -and (Test-Path $sehC) -and
        (Get-Item $sehC).LastWriteTime -gt (Get-Item $seh).LastWriteTime) {
        throw "RynthCore.SehTrampoline.dll is STALE (SehTrampoline.c is newer). Rebuild with native\SehTrampoline\Build-SehTrampoline.ps1 -NoDeploy, then re-run."
    }
    if (-not (Test-Path $seh)) { $seh = "C:\Games\RynthCore\Runtime\RynthCore.SehTrampoline.dll" }
    $sehDest = Join-Path (Resolve-Path $Target).Path "RynthCore.SehTrampoline.dll"
    $sameSeh = (Test-Path $sehDest) -and ((Resolve-Path $seh).Path -eq $sehDest -or
        (Get-FileHash $seh).Hash -eq (Get-FileHash $sehDest).Hash)
    if (-not $sameSeh) {
        # Same move-aside as the loader and shim: a locked copy would otherwise keep every
        # new launch on the old trampoline until all clients close.
        try { Copy-Item $seh $sehDest -Force -ErrorAction Stop }
        catch {
            try {
                Rename-Item $sehDest ("RynthCore.SehTrampoline.dll.old-{0:yyyyMMddHHmmss}" -f (Get-Date)) -ErrorAction Stop
                Copy-Item $seh $sehDest -Force -ErrorAction Stop
                Write-Host "RynthCore.SehTrampoline.dll was in use: moved the old one aside; new launches get the new one."
            }
            catch { Write-Warning "RynthCore.SehTrampoline.dll changed but is in use by a running client; it gets the new one when it restarts." }
        }
    }

    # App-local runtime: the win-x86 runtime pack the SDK restores (same version the engine was built against).
    $dotnet = Join-Path $Target "dotnet"
    if (-not (Test-Path "$dotnet\coreclr.dll")) {
        $pack = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.netcore.app.runtime.win-x86"
        $ver = (Get-ChildItem $pack | Where-Object Name -like "10.*" | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).Name
        if (-not $ver) { throw "microsoft.netcore.app.runtime.win-x86 10.x not in the NuGet cache (restore any win-x86 self-contained project once)" }
        $rt = "$pack\$ver\runtimes\win-x86"
        New-Item -ItemType Directory -Force -Path $dotnet | Out-Null
        Copy-Item "$rt\native\*" $dotnet -Force
        Copy-Item "$rt\lib\net10.0\*.dll" $dotnet -Force
        # msquic is never loaded. createdump.exe and the DAC (mscordaccore_*) stay: the loader
        # turns on CoreCLR's crash dumps (Logs\dumps\clr_<pid>_<time>.dmp), which run
        # createdump.exe from this folder, and a debugger reads the dump with the DAC.
        Remove-Item "$dotnet\msquic.dll" -Force -ErrorAction SilentlyContinue
        Write-Host "App-local runtime $ver -> $dotnet"
    }
    elseif (-not (Test-Path "$dotnet\createdump.exe")) {
        # Runtimes laid out before 2026-10-11 had createdump.exe and the DAC deleted, so a
        # CoreCLR fatal error (0x80131506) left no dump. Put them back from the runtime pack
        # whose coreclr.dll is this one (same file version), never from another version.
        $have = (Get-Item "$dotnet\coreclr.dll").VersionInfo.FileVersion
        $pack = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.netcore.app.runtime.win-x86"
        $match = Get-ChildItem $pack -Directory -ErrorAction SilentlyContinue | Where-Object Name -like "10.*" |
            ForEach-Object { "$($_.FullName)\runtimes\win-x86\native" } |
            Where-Object { (Test-Path "$_\createdump.exe") -and (Get-Item "$_\coreclr.dll").VersionInfo.FileVersion -eq $have } |
            Select-Object -First 1
        if ($match) {
            Copy-Item "$match\createdump.exe" $dotnet -Force
            Get-ChildItem $match -Filter "mscordaccore_*.dll" | Copy-Item -Destination $dotnet -Force
            Write-Host "Put createdump.exe and the DAC back into $dotnet (CoreCLR crash dumps)."
        }
        else { Write-Warning "createdump.exe is missing from $dotnet and no runtime pack matches coreclr $have; a CoreCLR crash will leave no dump." }
    }

    # Patched CoreCLR (2026-10-23, runtime-patches\coreclr-10.0.12-x86\README.md): the hot-reload
    # statics crash, dotnet/runtime #135441. Its coreclr.dll, DAC and DBI go over the runtime pack's;
    # the official CoreLib and JIT stay. Refused for a runtime of another version: a coreclr has to
    # match its CoreLib. In use by a running client: moved aside, as the loader and shim are.
    $patch = Join-Path $repo "runtime-patches\coreclr-10.0.12-x86"
    if (Test-Path "$patch\coreclr.dll") {
        $lib = (Get-Item "$dotnet\System.Private.CoreLib.dll").VersionInfo
        $pv = (Get-Item "$patch\coreclr.dll").VersionInfo
        if ($lib.FileMajorPart -ne $pv.FileMajorPart -or $lib.FileMinorPart -ne $pv.FileMinorPart -or $lib.FileBuildPart -ne $pv.FileBuildPart) {
            throw "runtime-patches\coreclr-10.0.12-x86 was built for runtime $($pv.FileMajorPart).$($pv.FileMinorPart).$($pv.FileBuildPart), but $dotnet has CoreLib $($lib.FileMajorPart).$($lib.FileMinorPart).$($lib.FileBuildPart): rebuild the patch for it or remove the folder (see its README)."
        }
        $dacName = "mscordaccore_x86_x86_{0}.{1}.{2}.{3}.dll" -f $pv.FileMajorPart, $pv.FileMinorPart, $pv.FileBuildPart, $pv.FilePrivatePart
        $copied = 0
        foreach ($pair in @(@('coreclr.dll', 'coreclr.dll'), @('mscordaccore.dll', 'mscordaccore.dll'), @('mscordbi.dll', 'mscordbi.dll'), @('mscordaccore.dll', $dacName))) {
            $src = Join-Path $patch $pair[0]
            $dest = Join-Path $dotnet $pair[1]
            if ((Test-Path $dest) -and (Get-FileHash $dest).Hash -eq (Get-FileHash $src).Hash) { continue }
            try { Copy-Item $src $dest -Force -ErrorAction Stop }
            catch {
                Rename-Item $dest ("{0}.old-{1:yyyyMMddHHmmss}" -f $pair[1], (Get-Date)) -ErrorAction Stop
                Copy-Item $src $dest -Force -ErrorAction Stop
                Write-Host "$($pair[1]) was in use: moved the old one aside; new launches get the patched one."
            }
            $copied++
        }
        if ($copied -gt 0) { Write-Host "Patched CoreCLR $($pv.FileVersion) -> $dotnet ($copied file(s); runtime-patches\coreclr-10.0.12-x86)" }
    }
}

# Engine + dependencies. The canonical RynthCore.Engine.dll goes last: the loader's file
# watcher reloads on its timestamp, so everything it needs must already be in place.
# Only what changed. Native libraries (cimgui, Skia...) are loaded once per client and
# locked while it runs; they rarely change, so a locked unchanged one is simply skipped.
foreach ($f in Get-ChildItem $stage -File | Where-Object { $_.Name -notlike "RynthCore.Engine.*" -and $_.Name -ne "minhook.x86.dll" }) {
    $dest = Join-Path $Target $f.Name
    if ((Test-Path $dest) -and (Get-Item $dest).Length -eq $f.Length -and
        (Get-FileHash $dest).Hash -eq (Get-FileHash $f.FullName).Hash) { continue }
    try { Copy-Item $f.FullName $dest -Force -ErrorAction Stop }
    catch { Write-Warning "$($f.Name) changed but is in use by a running client; it gets the new one when it restarts." }
}
if (-not (Test-Path "$Target\minhook.x86.dll")) { Copy-Item "$stage\minhook.x86.dll" $Target -Force }
Copy-Item "$stage\RynthCore.Engine.pdb" $Target -Force
Copy-Item "$stage\RynthCore.Engine.dll" $Target -Force
Write-Host "Published the CoreCLR engine to $Target"
