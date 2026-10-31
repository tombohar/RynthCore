#Requires -Version 7
<#
.SYNOPSIS
    Builds, signs and publishes a RynthCore + RynthSuite release to the launchers' update feed.

.DESCRIPTION
    One command per release:
      1. Refuses if the signing key isn't set up or isn't trusted by the launcher being shipped,
         if either repo has uncommitted changes, or if -Version isn't newer than the live feed;
         with -SkipNavData, also if the last published RynthNav map can't be found or isn't on
         the server as signed (a feed without it gives new RynthNav players no map).
      2. Runs every offline test program in RynthCore, RynthSuite and RynthServers
         (scripts\Run-AllTests.ps1) and stops if any fails: nothing is built or uploaded.
         Then builds the installer (Build-Installer.ps1 -Version; it includes the Decal bridge, so the
         build machine needs Decal installed) and the RynthSuite plugins into
         installer\Releases\<release>\ — never into the live C:\Games folders.
      3. Writes the manifest (sizes + SHA-256 of every file), signs it with the DPAPI key
         (Sign-Update.ps1, no prompt) and checks the signature the way the launcher will.
      4. Uploads the release files, checks their hashes on the server, replaces the website's
         RynthCore-Setup.exe and RynthSuite-Plugins.zip (old ones kept in ~/backups/rynth),
         and swaps in update.json last, atomically.
      5. Fetches the live feed back and compares it byte for byte.

    Launchers pick it up at their next check (start-up, every 6 hours, or the button). Plugins
    swap in place without touching running bots; the core installer waits until every
    RynthCore client is closed.

.EXAMPLE
    .\Publish-Update.ps1 -Version 2026.9.28.1 -Changes "Arrow crafting covers every head type.", "The Tell button works on players too."
#>
param(
    [Parameter(Mandatory)][string]$Version,
    # Release notes for players, one line per change: they become this release's entry in
    # downloads/rynth/releases.json, which the "What's new" list on aelrynth.com/rynth.html shows.
    [string[]]$Changes = @(),
    # Short text the launcher shows next to an available update; defaults to the changes.
    [string]$Notes = "",
    [switch]$DryRun,        # build + sign + verify, upload nothing
    [switch]$AllowDirty,    # publish with uncommitted changes (don't)
    # Publish without running the test programs first (step 2). For an emergency hotfix only; the
    # release log says the tests were skipped.
    [switch]$SkipTests,
    [string]$SshHost = "aelrynth-vps",
    [string]$RemoteDir = "/var/www/site/downloads/rynth",
    [string]$BaseUrl = "https://aelrynth.com/downloads/rynth",
    [string]$KeyDir = (Join-Path $env:USERPROFILE ".rynthcore-update-signing"),
    [string]$RynthSuiteRoot = "",
    # The server plugins' repository (2026-10-21), beside RynthSuite (it compiles RynthSuite's shared
    # contracts and the same RynthCore SDK, so their shared DLLs match). Defaults to ..\RynthServers.
    [string]$RynthServersRoot = "",
    [string]$TrustFile = "",  # defaults to the launcher's UpdateTrust.cs; override for tests
    # RynthNav's data shipped to launchers as the signed "navdata" archive: tiles (nav_XXXX.tile),
    # portals.tsv and locations.json from this folder. Only re-uploaded when the set changes
    # (its id is a hash of every file); otherwise the live feed's entry is carried over.
    [string]$NavDataDir = "C:\Games\RynthCore\NavData",
    # Build and upload no new RynthNav archive: the feed carries the last published navdata entry
    # instead (see "RynthNav's map with -SkipNavData" below). It never ships a feed without the map.
    [switch]$SkipNavData,
    # With -SkipNavData: an earlier update.json (signed) or update.payload.json whose navdata entry to
    # carry, for when neither the live feed nor any kept release manifest has one.
    [string]$NavDataFrom = "",
    [switch]$NativeAotPlugins, # the old NativeAOT plugin builds (each reload keeps a copy of every one)
    # The World Map's optional sizes (RynthNav.Baker --map-world): worldmap-hd.rmap, worldmap-ultra.rmap and worldmap-p32.rmap from this
    # folder are offered by the launcher as separate downloads ("mappacks" in the feed). Each is uploaded once under
    # mappacks/<first 16 hex of its SHA-256>/ and kept by later releases. Without this folder the live feed's packs are
    # carried over unchanged, so a release that doesn't touch the maps never drops them.
    [string]$MapPacksDir = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$RepoRoot = Split-Path $ScriptDir -Parent
if (-not $RynthSuiteRoot) { $RynthSuiteRoot = Join-Path (Split-Path $RepoRoot -Parent) "RynthSuite" }
if (-not $RynthServersRoot) { $RynthServersRoot = Join-Path (Split-Path $RynthSuiteRoot -Parent) "RynthServers" }
if (-not (Test-Path (Join-Path $RynthServersRoot "Plugins"))) { throw "No RynthServers checkout at $RynthServersRoot (pass -RynthServersRoot)." }
if ((Resolve-Path (Split-Path $RynthServersRoot -Parent)).Path -ne (Resolve-Path (Split-Path $RynthSuiteRoot -Parent)).Path) {
    throw "RynthServers ($RynthServersRoot) must sit beside RynthSuite ($RynthSuiteRoot): it builds against that RynthSuite and RynthCore."
}
if (-not $TrustFile) { $TrustFile = Join-Path $RepoRoot "src\RynthCore.App.Avalonia\UpdateTrust.cs" }

function Step($m) { Write-Host ""; Write-Host "== $m" -ForegroundColor Cyan }
function Sha256([string]$path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Sha256Text([string]$text) { [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant() }

# Same check as RynthUpdater.VerifyEnvelope: any trusted key's signature over the payload bytes.
function Test-Envelope([string]$path, [string[]]$keys) {
    $envelope = Get-Content $path -Raw | ConvertFrom-Json
    $payload = [Convert]::FromBase64String($envelope.payload)
    foreach ($k in $keys) {
        $spki = [Convert]::FromBase64String($k)
        $id = ([Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($spki), 0, 8)).ToLowerInvariant()
        $ec = [System.Security.Cryptography.ECDsa]::Create()
        $read = 0; $ec.ImportSubjectPublicKeyInfo($spki, [ref]$read)
        foreach ($s in $envelope.signatures) {
            if ($s.key -ne $id) { continue }
            if ($ec.VerifyData($payload, [Convert]::FromBase64String($s.sig),
                    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                    [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { return $true }
        }
    }
    return $false
}

# ── 1. Preconditions ─────────────────────────────────────────────────────────
Step "Checking preconditions"
$p = $Version.Split('.')
if ($p.Count -ne 4 -or ($p | Where-Object { $_ -notmatch '^\d+$' })) { throw "-Version must be yyyy.m.d.n, got '$Version'" }
$release = [int]$p[0] * 1000000 + [int]$p[1] * 10000 + [int]$p[2] * 100 + [int]$p[3]
if ($Changes.Count -eq 0 -and $Notes) { $Changes = @($Notes -split ';\s*' | Where-Object { $_ }) }
if ($Changes.Count -eq 0) { throw "Give -Changes: the release notes players see on aelrynth.com/rynth.html (one string per change)." }
if (-not $Notes) { $Notes = $Changes -join " " }

$trusted = [regex]::Matches((Get-Content $TrustFile -Raw), '"([A-Za-z0-9+/=]{80,})"') | ForEach-Object { $_.Groups[1].Value }
if ($trusted.Count -lt 2) {
    throw "UpdateTrust.cs has $($trusted.Count) key(s); it needs the primary and the backup. Run New-UpdateSigningKey.ps1 and commit UpdateTrust.cs."
}
$probe = New-TemporaryFile
try {
    Set-Content $probe "probe $release" -NoNewline
    & (Join-Path $ScriptDir "Sign-Update.ps1") -PayloadPath $probe -OutPath "$probe.sig" -KeyDir $KeyDir | Out-Null
    if (-not (Test-Envelope "$probe.sig" $trusted)) {
        throw "The signing key in $KeyDir is not one UpdateTrust.cs trusts: launchers would refuse this release."
    }
} finally { Remove-Item $probe, "$probe.sig" -ErrorAction SilentlyContinue }
Write-Host "  signing key is trusted by the launcher ($($trusted.Count) keys)"

if (-not $AllowDirty) {
    foreach ($repo in $RepoRoot, $RynthSuiteRoot, $RynthServersRoot) {
        $dirty = git -C $repo status --porcelain --untracked-files=no
        if ($dirty) { throw "$repo has uncommitted changes — release from committed code (or pass -AllowDirty):`n$dirty" }
    }
    Write-Host "  RynthCore $(git -C $RepoRoot rev-parse --short HEAD), RynthSuite $(git -C $RynthSuiteRoot rev-parse --short HEAD), RynthServers $(git -C $RynthServersRoot rev-parse --short HEAD)"
}

# The plugins build against the RynthCore beside RynthSuite (its SDK and build targets), not
# necessarily this checkout. Those targets must derive minEngineApi for release builds
# (RynthPluginSharedSdk, see the plugin build below): with older ones every managed plugin's
# manifest would keep its csproj's stale minimum (66), and launchers would offer it to cores that
# can't even load managed plugins.
$managedPluginApi = 0
if (-not $NativeAotPlugins) {
    $sdkTargets = Join-Path (Split-Path $RynthSuiteRoot -Parent) "RynthCore\src\RynthCore.PluginSdk\build\RynthCore.PluginSdk.targets"
    $targetsText = if (Test-Path $sdkTargets) { Get-Content $sdkTargets -Raw } else { "" }
    $floor = [regex]::Match($targetsText, '<RynthCoreManagedPluginApi>(\d+)</RynthCoreManagedPluginApi>')
    if (-not $floor.Success -or $targetsText -notmatch "'\`$\(RynthPluginSharedSdk\)' == 'true'") {
        throw "The plugins' SDK build targets ($sdkTargets) don't derive minEngineApi for release builds (RynthPluginSharedSdk). Update the RynthCore checkout beside RynthSuite to one with this script's SDK."
    }
    $managedPluginApi = [int]$floor.Groups[1].Value
    Write-Host "  plugin SDK targets: minEngineApi derived per plugin (managed plugins need API $managedPluginApi+)"
}

$liveManifest = $null
try {
    $live = Invoke-RestMethod "$BaseUrl/update.json" -TimeoutSec 20
    $liveManifest = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($live.payload)) | ConvertFrom-Json
    $liveRelease = $liveManifest.release
    if ($release -le $liveRelease) { throw "Release $release is not newer than the live feed's $liveRelease — bump -Version." }
    Write-Host "  live feed is at $liveRelease; publishing $release"
} catch [Microsoft.PowerShell.Commands.HttpResponseException] {
    Write-Host "  no live feed yet — this is the first release"
}

# ── RynthNav's map with -SkipNavData ─────────────────────────────────────────
# -SkipNavData means "build and upload no new archive", never "ship without the map": the feed
# carries the last published "navdata" entry, the way map packs are carried. It used to drop the
# entry, so every feed from 2026.10.18.1 on had none and a player who added RynthNav got no tiles,
# route graph or World Map (structural audit 2026-10-21, #6). The entry comes from, in order:
# -NavDataFrom, the live feed, then each earlier release's own signed manifest
# (releases/<n>/update.json, kept from 2026.10.19.1 on; releases.json names them, newest first).
# The first one found is used once its archive is checked on the server (size and SHA-256, the
# same ssh + sha256sum check as the uploads); a missing or changed archive, or no entry anywhere,
# stops the release here, before anything is built.

# A manifest's navdata object as the feed writes it (ConvertFrom-Json: whole numbers forced back to
# integers, see the [long] note at the navdata step), or $null when it isn't a usable entry. The
# URL must be inside the feed folder, as the launcher requires (RynthUpdater.CheckUrl).
function ConvertTo-NavDataEntry($nd) {
    if (-not $nd) { return $null }
    try {
        $e = [ordered]@{
            id      = ([string]$nd.id).ToLowerInvariant()
            release = [long]$nd.release
            url     = [string]$nd.url
            size    = [long]$nd.size
            sha256  = ([string]$nd.sha256).ToLowerInvariant()
            files   = [long]$nd.files
            bytes   = [long]$nd.bytes
        }
    } catch { return $null }
    if ($e.id -notmatch '^[0-9a-f]{16,64}$' -or $e.sha256 -notmatch '^[0-9a-f]{64}$' -or $e.size -le 0 -or $e.files -le 0 -or
        -not $e.url.StartsWith("$BaseUrl/", [StringComparison]::Ordinal) -or $e.url.Contains("..")) { return $null }
    return $e
}

# The manifest in a signed update.json (only when a key the launcher trusts signed it) or in a plain
# update.payload.json (a local release folder's); $null when it can't be read.
function Read-ManifestFile([string]$path) {
    try {
        $doc = Get-Content $path -Raw | ConvertFrom-Json
        if ($doc.payload -and $doc.signatures) {
            if (-not (Test-Envelope $path $trusted)) { Write-Warning "$path is not signed by a key the launcher trusts - ignored."; return $null }
            return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($doc.payload)) | ConvertFrom-Json
        }
        return $doc
    } catch { Write-Warning "Can't read ${path}: $($_.Exception.Message)"; return $null }
}

# @{ Entry; From } for the last published navdata entry (see above), or $null.
function Find-PublishedNavData {
    if ($NavDataFrom) {
        if (-not (Test-Path $NavDataFrom)) { throw "-NavDataFrom: no file at $NavDataFrom" }
        $m = Read-ManifestFile $NavDataFrom
        $e = if ($m) { ConvertTo-NavDataEntry $m.navdata } else { $null }
        if ($null -eq $e) { throw "-NavDataFrom: $NavDataFrom has no usable navdata entry (or isn't signed by a trusted key)." }
        return @{ Entry = $e; From = $NavDataFrom }
    }
    if ($liveManifest) {
        $e = ConvertTo-NavDataEntry $liveManifest.navdata
        if ($null -ne $e) { return @{ Entry = $e; From = "the live feed (release $($liveManifest.release))" } }
    }
    $history = @()
    try { $history = @((Invoke-RestMethod "$BaseUrl/releases.json" -TimeoutSec 20).releases) }
    catch { Write-Warning "releases.json: $($_.Exception.Message)" }
    $looked = 0
    foreach ($h in $history) {
        $v = ([string]$h.version).Split('.')
        if ($v.Count -ne 4 -or ($v | Where-Object { $_ -notmatch '^\d+$' })) { continue }
        $n = [int]$v[0] * 1000000 + [int]$v[1] * 10000 + [int]$v[2] * 100 + [int]$v[3]
        if ($n -ge $release -or $n -lt 2026101901) { continue }   # 2026101901: the first release whose manifest is kept
        if (++$looked -gt 20) { break }
        $tmp = New-TemporaryFile
        try {
            try { Invoke-WebRequest "$BaseUrl/releases/$n/update.json" -OutFile $tmp -UseBasicParsing -TimeoutSec 20 | Out-Null }
            catch { continue }
            $m = Read-ManifestFile $tmp
            if ($m -and [long]$m.release -eq $n) {
                $e = ConvertTo-NavDataEntry $m.navdata
                if ($null -ne $e) { return @{ Entry = $e; From = "release $n's signed manifest" } }
            }
        } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
    return $null
}

# The archive an entry names must be on the server exactly as signed: the launcher refuses a download
# whose size or SHA-256 differs, so a feed pointing at a gone or changed file gives players no map.
function Assert-RemoteNavData($e, [string]$from) {
    $relPath = $e.url.Substring($BaseUrl.Length + 1)
    if ($relPath -notmatch '^[A-Za-z0-9._/-]+$') { throw "RynthNav's map entry from $from has an unexpected URL: $($e.url)" }
    $remoteFile = "$RemoteDir/$relPath"
    $out = @(ssh $SshHost "stat -c %s '$remoteFile' && sha256sum '$remoteFile'")
    if ($LASTEXITCODE -ne 0 -or $out.Count -lt 2) {
        throw "RynthNav's map from $from ($remoteFile) is not on the server (or ssh failed) - not publishing a feed without the map. " +
              "Leave out -SkipNavData and pass -NavDataDir a complete, finished NavData folder to publish a new archive."
    }
    $size = [long]("$($out[0])".Trim())
    $sum = ("$($out[1])".Trim() -split '\s+')[0].ToLowerInvariant()
    if ($size -ne $e.size -or $sum -ne $e.sha256) {
        throw "RynthNav's map on the server ($remoteFile) is not the one $from signed (size $size vs $($e.size), sha256 $sum vs $($e.sha256)) - not publishing."
    }
    Write-Host ("  RynthNav map: release {0}'s archive ({1:N0} MB, id {2}) is on the server as signed; from {3}" -f $e.release, ($e.size / 1MB), $e.id, $from)
}

if ($NavDataFrom -and -not $SkipNavData) { throw "-NavDataFrom goes with -SkipNavData (without it the archive is built from -NavDataDir)." }
$carriedNavData = $null
if ($SkipNavData) {
    $found = Find-PublishedNavData
    if (-not $found) {
        throw "-SkipNavData, but neither the live feed nor any kept release manifest has RynthNav's map (navdata), and a feed without it " +
              "gives players who add RynthNav no tiles, route graph or World Map - not publishing. Either leave out -SkipNavData and pass " +
              "-NavDataDir a complete, finished NavData folder (not one being re-baked) to publish a new archive, or pass -NavDataFrom an " +
              "earlier update.json / update.payload.json that has the entry."
    }
    Assert-RemoteNavData $found.Entry $found.From
    $carriedNavData = $found.Entry
}

# ── 2. Build ─────────────────────────────────────────────────────────────────
$rel = Join-Path $ScriptDir "Releases\$release"
if (Test-Path $rel) { Remove-Item $rel -Recurse -Force }
New-Item -ItemType Directory -Force "$rel\plugins", "$rel\build" | Out-Null

# Every offline test program in the three repos, before anything is built (2026-10-22: tests only ran
# when someone remembered to). One failure stops the release; the logs stay in Releases\<release>\tests.
if ($SkipTests) {
    Step "Tests SKIPPED (-SkipTests)"
    Write-Warning "Publishing $Version without running the tests."
} else {
    Step "Running the tests (scripts\Run-AllTests.ps1)"
    & (Join-Path $RepoRoot "scripts\Run-AllTests.ps1") -RynthCoreRoot $RepoRoot -RynthSuiteRoot $RynthSuiteRoot `
        -RynthServersRoot $RynthServersRoot -LogDir "$rel\tests"
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed (summary above, logs in $rel\tests) - not publishing. Fix them, or pass -SkipTests for an emergency hotfix."
    }
}

# Plugins: built MANAGED by default (collectible: a reload frees them; NativeAOT plugins can never be
# unloaded, so every reload kept a copy of each one - 220-450 MB of address space per reload,
# 2026-10-19). Each managed plugin still ships as one DLL in the feed: their common dependencies
# (RynthCore.PluginSdk, PluginCore, ImGui.NET, MoonSharp, ...) go into the installer's
# Runtime\PluginShared, where the engine (API 88) finds them. -NativeAotPlugins: the old builds.
# minEngineApi: -p:RynthPluginSharedSdk=true tells the SDK's build targets that these plugins bind
# the PluginShared SDK, which reaches players only with this release's core. Each plugin's manifest
# then says its own declared minimum when that is 88 or more (a checked claim), else this SDK's API
# (_RynthPluginEffectiveMinEngineApi in RynthCore.PluginSdk.targets explains why). The feed copies
# each plugin's minEngineApi from the manifest embedded in its DLL (below), which is also what the
# engine checks at load and what the launcher's EngineBlocker holds updates by. This used to force
# -p:RynthPluginMinEngineApi=88 on every plugin, a constant that offered plugins calling v89 SDK
# members to API-88 cores (structural audit 2026-10-21, F5 / #7).
$suitePlugins = "RynthChat", "RynthTracker", "RynthNav", "RynthVision", "RynthLua", "RynthOracle", "RynthInventory", "RynthSort", "RynthNet", "RynthDefective"
# Server plugins (the RynthServers repository since 2026-10-21; ServerPack is SilentKelpie's): one per
# game server, each loaded by the engine only for its own server. They go in the feed only, so the
# launcher offers each one on its Server plugins tab for the player to install; not in
# RynthSuite-Plugins.zip or the installer. RynthILT and RynthThreads were in the zip until 2026-10-21.
$serverPlugins = "RynthAChard", "RynthAelrynth", "RynthColdeve", "RynthConquest", "RynthDerptide", "RynthDreamWeave", "RynthEversong",
                 "RynthFrostACE", "RynthFrostcull", "RynthILT", "RynthLeafDawn", "RynthLeafdawning", "RynthLevistras", "RynthPortalStorm",
                 "RynthReefcull", "RynthSundering", "RynthThistlecrown", "RynthThreads"
$managedArgs = if ($NativeAotPlugins) { @() } else {
    @("-p:PublishAot=false", "-p:NativeLib=", "-p:SelfContained=false", "-p:RuntimeIdentifier=win-x86",
      "-p:GenerateRuntimeConfigurationFiles=false", "-p:RynthPluginSharedSdk=true")
}
function Build-Plugin([string]$name, [string]$label, [string]$root = $RynthSuiteRoot, [string[]]$extra = @()) {
    Step "Building $name$label"
    $proj = Join-Path $root "Plugins\RynthCore.Plugin.$name\RynthCore.Plugin.$name.csproj"
    if (-not (Test-Path $proj)) { throw "No project for $name at $proj" }
    # PublishDir overrides the projects' own (RynthNav/RynthVision publish into the live C:\Games folders).
    dotnet publish $proj -c Release "-p:Version=$Version" "-p:PublishDir=$rel\build\$name\" @managedArgs @extra -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name" }
}
foreach ($name in @("RynthAi") + $suitePlugins) { Build-Plugin $name "" }
# Against this release's RynthSuite, whatever RynthServers' own Directory.Build.props would pick.
$suiteRootArg = "-p:RynthSuiteRoot=$((Resolve-Path $RynthSuiteRoot).Path.TrimEnd('\'))\"
foreach ($name in $serverPlugins) { Build-Plugin $name " (server plugin, feed only)" $RynthServersRoot @($suiteRootArg) }

$sharedDir = ""
if (-not $NativeAotPlugins) {
    # Every plugin's dependencies, once each; the same name must be the same file in every plugin
    # (they all build from this commit), else the release stops.
    $sharedDir = "$rel\build\PluginShared"
    New-Item -ItemType Directory -Force $sharedDir | Out-Null
    $seen = @{}
    foreach ($name in @("RynthAi") + $suitePlugins + $serverPlugins) {
        foreach ($f in Get-ChildItem "$rel\build\$name\*.dll") {
            if ($f.Name -like "RynthCore.Plugin.*.dll") { continue }
            $hash = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
            if ($seen.ContainsKey($f.Name)) {
                if ($seen[$f.Name] -ne $hash) { throw "PluginShared: $($f.Name) differs between plugins ($name) - not publishing." }
                continue
            }
            $seen[$f.Name] = $hash
            Copy-Item $f.FullName "$sharedDir\"
        }
    }
    Write-Host "  PluginShared: $($seen.Count) file(s): $(($seen.Keys | Sort-Object) -join ', ')"
}

Step "Building the installer ($Version)"
$installerArgs = @{ Version = $Version; RynthSuiteRoot = $RynthSuiteRoot }
if (-not $NativeAotPlugins) {
    $installerArgs.PluginSharedDir = $sharedDir
    $installerArgs.RynthAiDll = "$rel\build\RynthAi\RynthCore.Plugin.RynthAi.dll"
}
& (Join-Path $ScriptDir "Build-Installer.ps1") @installerArgs
if (-not $?) { throw "Build-Installer.ps1 failed" }
# The Decal bridge ships in every release (experimental, opt-in per account): the installer
# carries it, so a launcher that updates through this feed gets it as a fresh install does.
# Build-Installer.ps1 fails without Decal; this guards against a -SkipDecalBridge staging.
$bridgeStaged = Join-Path $ScriptDir "staging\app\DecalBridge\RynthCore.DecalBridge.dll"
if (-not (Test-Path $bridgeStaged)) { throw "The installer was built without the Decal bridge ($bridgeStaged missing) - not publishing." }
Write-Host "  Decal bridge in the installer: $((Get-Item $bridgeStaged).VersionInfo.FileVersion)"
# The headless client ships in every release too (experimental, opt-in per account): rynth-headless with its
# own x64 runtime, and the x64 RynthAi it loads when the launcher's HeadlessPluginPaths is empty. Its
# plugins come with the core installer only; the feed's plugin entries stay the x86 game-client builds.
foreach ($f in "Headless\rynth-headless.exe", "Headless\plugins\RynthAi\RynthCore.Plugin.RynthAi.dll") {
    if (-not (Test-Path (Join-Path $ScriptDir "staging\app\$f"))) { throw "The installer was built without $f - not publishing." }
}
foreach ($f in "Headless\rynth-headless.dll", "Headless\plugins\RynthAi\RynthCore.Plugin.RynthAi.dll") {
    $fv = (Get-Item (Join-Path $ScriptDir "staging\app\$f")).VersionInfo.FileVersion
    if ($fv -ne $Version) { throw "The installer's $f is version $fv, not $Version (stale build?) - not publishing." }
}
Write-Host "  headless client in the installer: rynth-headless and the x64 RynthAi, both $Version"
Copy-Item (Join-Path $ScriptDir "Output\RynthCore-Setup.exe") "$rel\RynthCore-Setup.exe"
Copy-Item (Join-Path $ScriptDir "staging\plugins\RynthAi\RynthCore.Plugin.RynthAi.dll") "$rel\plugins\"
foreach ($name in $suitePlugins + $serverPlugins) {
    Copy-Item "$rel\build\$name\RynthCore.Plugin.$name.dll" "$rel\plugins\"
}

Step "Packing RynthSuite-Plugins.zip"
$zipRoot = "$rel\build\zip"
foreach ($name in $suitePlugins) {
    New-Item -ItemType Directory -Force "$zipRoot\RynthSuite\$name" | Out-Null
    Copy-Item "$rel\plugins\RynthCore.Plugin.$name.dll" "$zipRoot\RynthSuite\$name\"
    # Third-party licence notices travel with the plugin (RynthOracle: Oracle of Dereth, MIT).
    $notice = "$rel\build\$name\THIRD_PARTY_NOTICES.md"
    if (Test-Path $notice) { Copy-Item $notice "$zipRoot\RynthSuite\$name\" }
}
$navSrc = $NavDataDir
New-Item -ItemType Directory -Force "$zipRoot\RynthCore\NavData" | Out-Null
# The route planner's portals.tsv and the Atlas's locations.json (both made by
# tools\navdata\gen_locations.py from the world database), and the Town Network's townnet.json
# (tools\navdata\Build-TownNet.ps1: the world database plus the walks inside from the dats).
# Never the player's own files
# there (atlas.txt, recalls.txt, _player.txt). The tiles are no longer in this zip (the whole
# world is ~40,000 of them): the launcher installs them from the feed's navdata archive.
Get-ChildItem $navSrc -File | Where-Object { $_.Name -eq "portals.tsv" -or $_.Name -eq "locations.json" -or $_.Name -eq "townnet.json" } |
    Copy-Item -Destination "$zipRoot\RynthCore\NavData\"
if (-not (Test-Path "$navSrc\locations.json")) {
    Write-Warning "NavData has no locations.json: RynthNav's Atlas ships empty. Run tools\navdata\gen_locations.py and copy its output there."
}
if (-not (Test-Path "$navSrc\townnet.json")) {
    Write-Warning "NavData has no townnet.json: routes won't use the Town Network. Run tools\navdata\Build-TownNet.ps1 and copy its townnet.json there."
}
@"
RynthSuite plugins for RynthCore (Asheron's Call, retail client) - $Version

Extract this zip into C:\Games - it creates:
  C:\Games\RynthSuite\RynthChat\     RynthChat plugin
  C:\Games\RynthSuite\RynthNav\      RynthNav plugin
  C:\Games\RynthSuite\RynthTracker\  RynthTracker plugin
  C:\Games\RynthSuite\RynthVision\   RynthVision plugin
  C:\Games\RynthSuite\RynthLua\      RynthLua plugin (Lua scripting; was part of RynthAi)
  C:\Games\RynthSuite\RynthOracle\   RynthOracle plugin (quests, character, titles; based on Oracle of Dereth by Advis Eveldan)
  C:\Games\RynthSuite\RynthInventory\ RynthInventory plugin (search every character's items)
  C:\Games\RynthSuite\RynthSort\     RynthSort plugin (keeps your packs sorted; replaces the RynthSort Lua script)
  C:\Games\RynthSuite\RynthNet\      RynthNet plugin (links your clients on this PC: /ub bc, shared vitals, the Characters panel)
  C:\Games\RynthSuite\RynthDefective\ RynthDefective plugin (a shortcut bar, AC's panel buttons and the Selection, in place of the retail toolbar)
  C:\Games\RynthCore\NavData\        RynthNav's portals and Atlas

Plugins for one game server (RynthILT for Infinite Leaftide, RynthThreads for
DreamWeave, and one per server) are not in this zip: install yours from the
launcher's Server plugins tab.

Then, in the RynthCore launcher's Plugins tab, add the full path of each
plugin DLL you want. Install RynthCore first (RynthCore-Setup.exe), which
also installs RynthAi and the Loot Editor. After that the launcher keeps
the plugins up to date by itself, and once RynthNav is in the list it also
downloads RynthNav's navmesh tiles into C:\Games\RynthCore\NavData.

MIT licensed. https://aelrynth.com/projects.html
"@ -replace "`n", "`r`n" | Set-Content "$zipRoot\README.txt" -NoNewline
Compress-Archive -Path "$zipRoot\*" -DestinationPath "$rel\RynthSuite-Plugins.zip" -CompressionLevel Optimal

# RynthNav's data for the launchers (RynthUpdater.NavData.cs): one archive, flat, only the file
# names RynthNav reads. Its id is a hash over every file's name and SHA-256, so an unchanged
# set reuses the live entry (and the archive already on the server) instead of re-uploading.
# -SkipNavData: the entry found and checked in step 1 (never none).
$navData = $carriedNavData
$navZip = $null
if (-not $SkipNavData) {
    Step "RynthNav data (tiles, route graph, portals, Atlas, Town Network, World Map) from $NavDataDir"
    $navFiles = @(Get-ChildItem $NavDataDir -File | Where-Object { $_.Name -match '^(nav_[0-9A-Fa-f]{4}\.tile|portals\.tsv|locations\.json|navgraph\.bin|townnet\.json|worldmap\.rmap|worldmap\.vec)$' } | Sort-Object Name)
    $tileCount = @($navFiles | Where-Object { $_.Extension -eq ".tile" }).Count
    if ($tileCount -eq 0) { throw "$NavDataDir has no tiles. Pass -NavDataDir, or -SkipNavData to carry the last published map." }
    # The long-range route graph (RynthNav.Baker --graph) must come from these very tiles: the
    # plugin ignores a graph whose tiles don't match (long gotos then walk the straight line).
    # The World Map's picture (RynthNav.Baker --map --map-tiles from the dats; the terrain doesn't change between releases).
    if (-not ($navFiles | Where-Object { $_.Name -eq "worldmap.rmap" })) {
        Write-Warning "No worldmap.rmap in ${NavDataDir}: the World Map has no picture. Run: RynthNav.Baker --map --map-tiles --out <dir> and copy worldmap.rmap there."
    }
    if (-not ($navFiles | Where-Object { $_.Name -eq "worldmap.vec" })) {
        Write-Warning "No worldmap.vec in ${NavDataDir}: the World Map has no buildings or roads. Run: RynthNav.Baker --map-vectors --out <dir> and copy worldmap.vec there."
    }
    $graph = $navFiles | Where-Object { $_.Name -eq "navgraph.bin" }
    $newestTile = ($navFiles | Where-Object { $_.Extension -eq ".tile" } | Measure-Object LastWriteTimeUtc -Maximum).Maximum
    if (-not $graph) { Write-Warning "No navgraph.bin in ${NavDataDir}: long gotos will walk the straight line. Run: RynthNav.Baker --graph $NavDataDir" }
    elseif ($graph.LastWriteTimeUtc -lt $newestTile) { Write-Warning "navgraph.bin is older than the newest tile: rebuild it (RynthNav.Baker --graph $NavDataDir), or RynthNav will ignore it." }
    $idText = ($navFiles | ForEach-Object { "$($_.Name) $(Sha256 $_.FullName)" }) -join "`n"
    $navId = (Sha256Text $idText).Substring(0, 32)
    # [long]: Measure-Object's Sum is a double, which ConvertTo-Json writes as 1383002945.0, and the
    # launcher reads "bytes" with GetInt64 - a ".0" failed every launcher's update check (2026.10.3.1/2).
    $navBytes = [long](($navFiles | Measure-Object Length -Sum).Sum)
    if ($liveManifest -and $liveManifest.navdata -and $liveManifest.navdata.id -eq $navId) {
        $navData = $liveManifest.navdata
        # Carried over from the live feed (ConvertFrom-Json): force the whole numbers back to integers.
        $navData = [ordered]@{
            id      = [string]$navData.id
            release = [long]$navData.release
            url     = [string]$navData.url
            size    = [long]$navData.size
            sha256  = [string]$navData.sha256
            files   = [long]$navData.files
            bytes   = [long]$navData.bytes
        }
        Write-Host "  unchanged ($($navFiles.Count) files, id $navId): the live archive from release $($navData.release) stays"
        Assert-RemoteNavData $navData "the live feed"
    } else {
        $navZip = "$rel\RynthNav-NavData.zip"
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
        $fs = [IO.File]::Open($navZip, 'CreateNew')
        try {
            $za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
            foreach ($f in $navFiles) {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $f.Name, [IO.Compression.CompressionLevel]::Optimal)
            }
            $za.Dispose()
        } finally { $fs.Dispose() }
        $zi = Get-Item $navZip
        $navData = [ordered]@{
            id      = $navId
            release = $release
            url     = "$BaseUrl/releases/$release/RynthNav-NavData.zip"
            size    = $zi.Length
            sha256  = Sha256 $zi.FullName
            files   = $navFiles.Count
            bytes   = $navBytes
        }
        Write-Host ("  new set: {0} files ({1} tiles), {2:N0} MB packed into {3:N0} MB, id {4}" -f $navFiles.Count, $tileCount, ($navBytes / 1MB), ($zi.Length / 1MB), $navId)
    }
}

# ── World map packs (optional sizes) ─────────────────────────────────────────
# key -> what the launcher shows; the file names are what RynthNav.Baker --map-world writes (extreme = --ppc 32, whose default name is worldmap-p32.rmap).
$mapPackDefs = [ordered]@{
    hd    = @{ name = "HD";    file = "worldmap-hd.rmap";    description = "Sharper ground, roads, rooftops and trees." }
    ultra = @{ name = "Ultra"; file = "worldmap-ultra.rmap"; description = "Very sharp: individual roofs and trees. Large download." }
    extreme = @{ name = "Extreme"; file = "worldmap-p32.rmap"; description = "Every roof, road and tree in detail. Very large download (about 1 GB)." }
    # (The 64 px "Maximum" map, worldmap-p64.rmap, is deliberately NOT here: it is for private use only and is never offered to players.)
}
$mapPacks = @()
$mapPackUploads = @()       # files to put on the server: @{ Path; Remote; Sha }
if ($MapPacksDir) {
    Step "World map packs from $MapPacksDir"
    foreach ($key in $mapPackDefs.Keys) {
        $def = $mapPackDefs[$key]
        $f = Join-Path $MapPacksDir $def.file
        if (-not (Test-Path $f)) { Write-Host "  $($def.name): no $($def.file) here, not offered by this feed"; continue }
        $sha = Sha256 $f
        $id = $sha.Substring(0, 16)
        $mapPacks += [ordered]@{
            key = $key; name = $def.name; file = $def.file
            url = "$BaseUrl/mappacks/$id/$($def.file)"
            size = [long](Get-Item $f).Length; sha256 = $sha; release = $release; description = $def.description
        }
        $mapPackUploads += @{ Path = $f; Remote = "mappacks/$id/$($def.file)"; Sha = $sha }
        Write-Host ("  {0}: {1:N1} MB, {2}" -f $def.name, ((Get-Item $f).Length / 1MB), $id)
    }
} elseif ($liveManifest -and $liveManifest.mappacks) {
    # Carried over from the live feed (ConvertFrom-Json): force the whole numbers back to integers.
    $mapPacks = @($liveManifest.mappacks | ForEach-Object {
        [ordered]@{ key = [string]$_.key; name = [string]$_.name; file = [string]$_.file; url = [string]$_.url; size = [long]$_.size
                    sha256 = [string]$_.sha256; release = [long]$_.release; description = [string]$_.description }
    })
    Write-Host "  map packs: $($mapPacks.Count) carried over from the live feed"
}

# ── 3. Manifest + signature ──────────────────────────────────────────────────
Step "Writing and signing the manifest"
$setup = Get-Item "$rel\RynthCore-Setup.exe"
# One line per plugin for the launcher's Available plugins list (launchers from 2026.10.5 on;
# older ones ignore the field, and a plugin without one falls back to the launcher's own table
# in RynthUpdater.Available.cs). Plain English for players, no names.
$pluginDescriptions = @{
    RynthAi        = "The bot: combat, buffs, looting, navigation routes and metas."
    RynthChat      = "A chat window that sorts messages into tabs."
    RynthTracker   = "Kills, experience and luminance per hour for each session."
    RynthNav       = "Walks you to coordinates and towns, with route planning and the Atlas. Downloads its map (about 540 MB)."
    RynthVision    = "Shows unclimbable slopes, impassable water and your radar range in the world, and lights dark dungeons."
    RynthLua       = "Lua scripting for metas and your own scripts."
    RynthOracle    = "Quests, character and titles."
    RynthInventory = "Search every character's items."
    RynthSort      = "Keeps your packs sorted: new loot goes to the pack its rule names."
    RynthILT       = "Infinite Leaftide only: the ILT Hub (the Mini Remote's banking, gear, charms and games tabs), the Guardian, bounty tracking and progression planning."
    RynthThreads   = "DreamWeave only: records Fragment Press results and works out what each component brings."
    RynthNet       = "Links your characters on this PC: broadcast commands (/ub bc), shared vitals and the Characters panel."
    RynthDefective = "A shortcut bar with AC's own keys, buttons for AC's panels and the Selection, in place of the retail toolbar."
}
# Plugin manifests (embedded in each DLL by the SDK's build targets): each plugin's minEngineApi
# goes into its feed entry, and the core entry says its engine's API ("engineApi"), so launchers
# (from plugin manifests on) don't install a plugin build the installed engine would refuse; they
# say "needs RynthCore <version>" instead. Older launchers ignore both fields.
. (Join-Path $ScriptDir "PluginManifest.ps1")
$engineApi = Get-RynthEngineApi $RepoRoot
Write-Host "  engine plugin API: $engineApi"
$plugins = foreach ($dll in Get-ChildItem "$rel\plugins\*.dll" | Sort-Object Name) {
    $pluginName = $dll.BaseName -replace '^RynthCore\.Plugin\.', ''
    $entry = [ordered]@{
        name   = $pluginName
        file   = $dll.Name
        url    = "$BaseUrl/releases/$release/plugins/$($dll.Name)"
        size   = $dll.Length
        sha256 = Sha256 $dll.FullName
    }
    $pm = Get-RynthPluginManifest $dll.FullName
    # The table first; a server plugin's own manifest description otherwise (its csproj's RynthPluginDescription).
    if ($pluginDescriptions[$pluginName]) { $entry.description = $pluginDescriptions[$pluginName] }
    elseif ($pm -and $pm.description) { $entry.description = [string]$pm.description }
    else { Write-Warning "No description for $pluginName - the launcher shows its own (or none). Add one to `$pluginDescriptions." }
    if ($pm) {
        if ($pm.name -ne $pluginName) { throw "$($dll.Name): its manifest says '$($pm.name)', the feed would say '$pluginName'." }
        if ($pm.version -and $pm.version -ne $Version) { throw "$($dll.Name): its manifest is version $($pm.version), not $Version (stale build?)." }
        # The plugin's own minimum, as its build wrote it into the DLL (see the plugin build above).
        $min = [int]$pm.minEngineApi
        if ($min -gt $engineApi) { throw "$($dll.Name) needs engine API $min but this release's engine is $engineApi - it would never load." }
        if ($managedPluginApi -gt 0 -and $min -lt $managedPluginApi) {
            throw "$($dll.Name): its manifest says engine API $min, but a managed plugin needs $managedPluginApi just to load - launchers would offer it to cores that can't run it (stale SDK targets beside RynthSuite?)."
        }
        if ($min -gt 0) { $entry.minEngineApi = $min }
        Write-Host "  $pluginName $($pm.version): engine API $min+"
    } else {
        Write-Warning "$($dll.Name) has no plugin manifest - launchers install it on any engine."
    }
    $entry
}
$manifest = [ordered]@{
    schema    = 1
    release   = $release
    version   = $Version
    published = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    notes     = $Notes
    # The changes, one line each: the launcher's update card shows a few as "What's new"
    # (signed with the rest; launchers before 2026.10.5 ignore it and show the notes).
    changes   = @($Changes)
    core      = [ordered]@{
        release = $release
        version = $Version
        url     = "$BaseUrl/releases/$release/RynthCore-Setup.exe"
        size    = $setup.Length
        sha256  = Sha256 $setup.FullName
        # The plugin API of the engine in this installer (see the plugins' minEngineApi above).
        engineApi = $engineApi
    }
    plugins   = @($plugins)
}
# Every feed carries RynthNav's map (built above, unchanged from the live feed, or carried by -SkipNavData).
if (-not $navData) { throw "No RynthNav map (navdata) for this feed - not publishing a feed without it." }
$manifest.navdata = $navData
if ($mapPacks.Count -gt 0) { $manifest.mappacks = @($mapPacks) }
Set-Content "$rel\update.payload.json" ($manifest | ConvertTo-Json -Depth 5) -Encoding utf8NoBOM
& (Join-Path $ScriptDir "Sign-Update.ps1") -PayloadPath "$rel\update.payload.json" -OutPath "$rel\update.json" -KeyDir $KeyDir
if (-not (Test-Envelope "$rel\update.json" $trusted)) { throw "Signed feed does not verify against UpdateTrust.cs" }
Write-Host "  verified against the launcher's trusted keys"

# Release notes: this release on top of the live list (a re-run replaces its own entry).
$history = @()
try { $history = @((Invoke-RestMethod "$BaseUrl/releases.json" -TimeoutSec 20).releases) }
catch [Microsoft.PowerShell.Commands.HttpResponseException] { Write-Host "  no releases.json yet — starting one" }
$entry = [ordered]@{ version = $Version; date = (Get-Date -Format "yyyy-MM-dd"); changes = @($Changes) }
$releaseNotes = [ordered]@{ releases = @($entry) + @($history | Where-Object { $_ -and $_.version -ne $Version }) }
Set-Content "$rel\releases.json" ($releaseNotes | ConvertTo-Json -Depth 5) -Encoding utf8NoBOM
Write-Host "  releases.json: $($releaseNotes.releases.Count) release(s), $($Changes.Count) change(s) in this one"

if ($DryRun) {
    Write-Host ""; Write-Host "Dry run: release built and signed in $rel; nothing uploaded." -ForegroundColor Yellow
    return
}

# ── 4. Upload ────────────────────────────────────────────────────────────────
Step "Uploading release $release"
$remoteRel = "$RemoteDir/releases/$release"
ssh $SshHost "mkdir -p '$remoteRel/plugins'"
if ($LASTEXITCODE -ne 0) { throw "ssh mkdir failed" }
scp -q "$rel\RynthCore-Setup.exe" "${SshHost}:$remoteRel/RynthCore-Setup.exe"
if ($LASTEXITCODE -ne 0) { throw "scp of the installer failed" }
scp -q (Get-ChildItem "$rel\plugins\*.dll").FullName "${SshHost}:$remoteRel/plugins/"
if ($LASTEXITCODE -ne 0) { throw "scp of the plugins failed" }
# The release's own signed manifest stays beside its files: the launcher's Roll back
# (RynthUpdater.Rollback.cs) verifies it to go back to this release later.
scp -q "$rel\update.json" "${SshHost}:$remoteRel/update.json"
if ($LASTEXITCODE -ne 0) { throw "scp of the release's update.json failed" }
$navSum = ""
if ($navZip) {
    scp -q $navZip "${SshHost}:$remoteRel/RynthNav-NavData.zip"
    if ($LASTEXITCODE -ne 0) { throw "scp of the RynthNav data archive failed" }
    $navSum = " RynthNav-NavData.zip"
}

# World map packs: content-addressed (the folder is the start of the file's SHA-256), so a pack already on the server
# is not sent again and an old release's URL keeps working.
foreach ($up in $mapPackUploads) {
    $remoteFile = "$RemoteDir/$($up.Remote)"
    ssh $SshHost "test -f '$remoteFile'"
    if ($LASTEXITCODE -eq 0) { Write-Host "  $($up.Remote): already on the server"; continue }
    ssh $SshHost "mkdir -p '$(Split-Path $remoteFile -Parent | ForEach-Object { $_ -replace '\\','/' })'"
    if ($LASTEXITCODE -ne 0) { throw "ssh mkdir for $($up.Remote) failed" }
    scp -q $up.Path "${SshHost}:$remoteFile.part"
    if ($LASTEXITCODE -ne 0) { throw "scp of $($up.Remote) failed" }
    $got = (ssh $SshHost "sha256sum '$remoteFile.part'") -split '\s+' | Select-Object -First 1
    if ($got -ne $up.Sha) { ssh $SshHost "rm -f '$remoteFile.part'"; throw "Hash mismatch on the server for $($up.Remote) — feed NOT published" }
    ssh $SshHost "mv -f '$remoteFile.part' '$remoteFile'"
    if ($LASTEXITCODE -ne 0) { throw "Publishing $($up.Remote) failed" }
    Write-Host "  $($up.Remote): uploaded and hash-checked"
}

$remoteSums = ssh $SshHost "cd '$remoteRel' && sha256sum RynthCore-Setup.exe update.json plugins/*.dll$navSum"
$expect = @{ "RynthCore-Setup.exe" = $manifest.core.sha256
             "update.json" = (Get-FileHash "$rel\update.json" -Algorithm SHA256).Hash.ToLowerInvariant() }
if ($navZip) { $expect["RynthNav-NavData.zip"] = $navData.sha256 }
foreach ($pl in $plugins) { $expect["plugins/$($pl.file)"] = $pl.sha256 }
foreach ($line in $remoteSums) {
    $sum, $name = $line -split '\s+', 2
    if ($expect[$name.TrimStart('*')] -ne $sum) { throw "Hash mismatch on the server for $name — feed NOT published" }
}
Write-Host "  $($expect.Count) files uploaded and hash-checked"
# A carried RynthNav archive (not uploaded by this release) is checked once more just before the
# feed that points at it goes live.
if (-not $navZip) { Assert-RemoteNavData $navData "this feed" }

Step "Replacing the website downloads"
scp -q "$rel\RynthCore-Setup.exe" "${SshHost}:$RemoteDir/.RynthCore-Setup.exe.new"
if ($LASTEXITCODE -ne 0) { throw "scp of the website installer failed" }
scp -q "$rel\RynthSuite-Plugins.zip" "${SshHost}:$RemoteDir/.RynthSuite-Plugins.zip.new"
if ($LASTEXITCODE -ne 0) { throw "scp of the plugins zip failed" }
ssh $SshHost "set -e; mkdir -p ~/backups/rynth; cd '$RemoteDir'; for f in RynthCore-Setup.exe RynthSuite-Plugins.zip; do [ -f `$f ] && cp -p `$f ~/backups/rynth/`$f.before-$release || true; mv -f .`$f.new `$f; done"
if ($LASTEXITCODE -ne 0) { throw "Replacing the website downloads failed" }

Step "Publishing the feed"
scp -q "$rel\update.json" "${SshHost}:$RemoteDir/.update.json.new"
if ($LASTEXITCODE -ne 0) { throw "scp of update.json failed" }
ssh $SshHost "mv -f '$RemoteDir/.update.json.new' '$RemoteDir/update.json'"
if ($LASTEXITCODE -ne 0) { throw "Publishing update.json failed" }
# The release notes follow the feed, so the page never lists a release launchers can't get yet.
scp -q "$rel\releases.json" "${SshHost}:$RemoteDir/.releases.json.new"
if ($LASTEXITCODE -ne 0) { throw "scp of releases.json failed" }
ssh $SshHost "mv -f '$RemoteDir/.releases.json.new' '$RemoteDir/releases.json'"
if ($LASTEXITCODE -ne 0) { throw "Publishing releases.json failed" }

# ── 5. Verify ────────────────────────────────────────────────────────────────
Step "Verifying the live feed"
$tmp = New-TemporaryFile
try {
    Invoke-WebRequest "$BaseUrl/update.json" -OutFile $tmp -UseBasicParsing -Headers @{ "Cache-Control" = "no-cache" }
    if ((Sha256 $tmp) -ne (Sha256 "$rel\update.json")) { throw "The live update.json differs from the one just signed!" }
    if (-not (Test-Envelope $tmp $trusted)) { throw "The live update.json does not verify!" }
} finally { Remove-Item $tmp -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "Published RynthCore $Version (release $release)." -ForegroundColor Green
Write-Host "Launchers pick it up at their next check, and aelrynth.com/rynth.html lists it. Still by hand: the public source snapshots."
