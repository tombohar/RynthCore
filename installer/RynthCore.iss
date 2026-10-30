; RynthCore + RynthAi Installer
; Build with: Build-Installer.ps1 (requires Inno Setup 6)
; Source: https://jrsoftware.org/isdl.php
;
; NOTE: No ISPP (#define / {#...}) macros are used in this file.
; Build-Installer.ps1 replaces the AppVersion placeholder via text substitution
; before invoking ISCC, so the preprocessor has nothing to expand.
; AppVersion placeholder — replaced by Build-Installer.ps1 before ISCC runs:
; APPVERSION_PLACEHOLDER=0.0.0

[Setup]
AppId={{A8B4C2D1-E3F5-4678-9ABC-DEF012345678}
AppName=RynthCore
AppVersion=0.0.0
AppPublisher=RynthCore
AppPublisherURL=https://aelrynth.com/git/rynth/RynthCore
DefaultDirName=C:\Games\RynthCore
DisableDirPage=no
DefaultGroupName=RynthCore
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=RynthCore-Setup
SetupIconFile=..\src\RynthCore.App.Avalonia\LogoCore.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Run as lowest privilege; Windows will prompt for elevation only when
; the chosen install folder requires it (e.g. C:\Games\).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; 32-bit app; works on 32/64-bit Windows
ArchitecturesAllowed=x86 x64 arm64
ArchitecturesInstallIn64BitMode=

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

; Program files
[Files]
; Launcher + engine runtime + Loot Editor go to {app} (default C:\Games\RynthCore\)
; overwritereadonly: a file left read-only on the player's PC is replaced without a question
; (a /SILENT update can't answer one); the build clears ReadOnly on everything staged, and Setup
; doesn't give installed files any attributes, so nothing lands read-only.
Source: "staging\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs overwritereadonly

; Plugin DLL goes to its canonical home next to plugin data dirs.
; Engine does NOT auto-scan this folder — user adds the full DLL path in the
; launcher's Plugins tab to enable the plugin. See BUILD.md "Deploy RynthAi Plugin".
Source: "staging\plugins\RynthAi\RynthCore.Plugin.RynthAi.dll"; DestDir: "C:\Games\RynthSuite\RynthAi"; Flags: ignoreversion overwritereadonly

; Data directories (created once; never removed on uninstall)
;
; Permissions: users-modify (2026-10-13). The game client never runs elevated, and RynthAi saves
; metas, navs, loot and settings profiles in these folders. An "install for all users" (elevated)
; Setup creates them as an administrator; on a PC whose C:\ permissions aren't Windows' defaults,
; such folders are read-only for everyone else and RynthAi's saves failed. users-modify gives the
; Users group Modify, inherited by everything inside, every time Setup runs (also on an update, so
; it repairs older installs when that update runs elevated). A non-elevated Setup can only change
; folders its user owns; where it can't, Setup logs "Failed to set permissions" and carries on
; (not an error), and the launcher's Recovery Actions > Fix folder permissions does it instead.
[Dirs]
Name: "{app}";                                                   Permissions: users-modify
Name: "C:\Games\RynthSuite";                                     Permissions: users-modify; Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi";                             Permissions: users-modify; Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\NavProfiles";                 Permissions: users-modify; Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\LootProfiles";                Permissions: users-modify; Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\MetaFiles";                   Permissions: users-modify; Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\MetaProfiles";                Permissions: users-modify; Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\SettingsProfiles";            Permissions: users-modify; Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\SettingsProfiles\ACEmulator"; Permissions: users-modify; Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\LuaScripts";                  Permissions: users-modify; Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\Logs";                        Permissions: users-modify; Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\pvars";                       Permissions: users-modify; Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\ItemGiver";                   Permissions: users-modify; Flags: uninsneveruninstall

; Shortcuts
[Icons]
; Start Menu
Name: "{group}\RynthCore"; Filename: "{app}\RynthCore.exe"; WorkingDir: "{app}"; Comment: "Launch RynthCore and inject into Asheron's Call"
Name: "{group}\Loot Editor"; Filename: "{app}\Tools\LootEditor\RynthCore.LootEditor.exe"; WorkingDir: "{app}\Tools\LootEditor"; Comment: "Edit VTank-style loot profiles"
Name: "{group}\Uninstall RynthCore"; Filename: "{uninstallexe}"

; Optional Desktop shortcut (created only when the desktopicon task is checked)
Name: "{autodesktop}\RynthCore"; Filename: "{app}\RynthCore.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Interactive install: offer to start the launcher on the last page.
Filename: "{app}\RynthCore.exe"; WorkingDir: "{app}"; Description: "Launch RynthCore"; Flags: nowait postinstall skipifsilent
; The launcher's updater runs this installer with /SILENT /RELAUNCH after closing itself;
; bring it back when the upgrade is done.
Filename: "{app}\RynthCore.exe"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser; Check: CmdLineParamExists('/RELAUNCH')

[UninstallRun]
; Decal + RynthCore (experimental): remove the Decal bridge's registration, only
; what RynthCore added (see docs/DECAL_BRIDGE_PLAN.md). Every release ships the bridge
; ({app}\DecalBridge, staged by Build-Installer.ps1), so this always runs; with nothing
; registered it changes nothing. Runs before the files are removed. It removes:
;   - our entry from any per-user (VirtualStore) copy of Decal's filter list;
;   - a per-user copy an earlier RynthCore created (decal-bridge-registration.json says so),
;     whatever is in it now: our entries and the HKLM filters it mirrored (HKLM keeps the
;     originals). It is exported first to %APPDATA%\RynthCore\decal-backup-<date>.reg. If the
;     copy holds entries that are neither ours nor in HKLM, they belong to someone else: the
;     copy stays and the uninstall log (launcher.log) says why;
;   - this install's machine-wide entry - directly when the uninstaller is elevated, otherwise
;     after Windows' administrator prompt, which appears only when that entry exists.
Filename: "{app}\RynthCore.exe"; Parameters: "--decal-bridge-unregister"; Flags: runhidden waituntilterminated; RunOnceId: "RynthCoreDecalBridgeUnregister"

[Code]
function CmdLineParamExists(const Value: string): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Value) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

(* -------------------------------------------------------------------------------------
  Decal + RynthCore (experimental), strictly opt-in (since 2026-10-10): on install and on
  every update (the launcher's updater runs this installer with /SILENT), the RynthCore
  Decal bridge is registered ONLY when the installing user has a Decal + RynthCore account
  (%APPDATA%\RynthCore\decal-accounts.json, which the launcher deletes when no account uses
  the mode). Otherwise nothing Decal-related is touched; the launcher cleans up after
  itself when no account uses the mode any more.

  Where it goes:
    1. Machine-wide, always: HKLM\SOFTWARE\WOW6432Node\Decal\NetworkFilters\{5B3E0D57-...}
       (32-bit view), Decal's own list. Every client reads it unless a per-user copy hides it.
    2. Per-user: NEVER a new per-user (VirtualStore) copy of that key. acclient.exe is a
       legacy 32-bit program; when HKCU\Software\Classes\VirtualStore\MACHINE\SOFTWARE\
       WOW6432Node\Decal\NetworkFilters exists, a non-elevated AC reads nothing but that copy,
       so every filter installed for all users afterwards is hidden from Decal (a player's
       new plugins and UtilityBelt's newer service vanished, 2026-10). Launchers before
       2026-10-10 created such a copy; --decal-bridge-install-user now removes a copy an
       earlier RynthCore created (backed up first to %APPDATA%\RynthCore\decal-backup-<date>.reg;
       kept when it holds someone else's entries), and only adds our entry to a copy that
       exists anyway (Decal can make one itself). The launcher's Recovery Actions > Repair
       Decal removes any such copy after a backup.

  The machine-wide entry is harmless: Decal clients load the bridge, which stays idle (no
  hooks, no threads) unless the RynthCore loader is in the same client. Uninstall removes
  what RynthCore added ([UninstallRun] --decal-bridge-unregister, see above).

  Rights. This installer runs with the lowest privileges (PrivilegesRequired=lowest; the
  updater starts it without elevation). Writing HKLM needs administrator rights, so, for a
  user with a Decal + RynthCore account and only when the machine-wide entry is missing or
  points at another folder:
    - Setup already elevated (installed "for all users", or started by an elevated
      launcher): written directly, no prompt.
    - Not elevated: Windows' administrator prompt is shown once. Saying no is harmless for
      Decal: the launcher asks again before the next Decal + RynthCore launch (interactive
      launches only), and Decal + RynthCore can't be used until it is registered.
  The commands are RynthCore.exe's own (Program.cs): --decal-bridge-register-machine and
  --decal-bridge-install-user, so the installer and the launcher write identical entries.
  ------------------------------------------------------------------------------------- *)
const
  BridgeEntryKey = 'SOFTWARE\Decal\NetworkFilters\{5B3E0D57-2C41-4F8A-9D6E-8C1B70DECA11}';
  BridgeObject = 'RynthCore.DecalBridge.BridgeFilter';

function DecalInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM32, 'SOFTWARE\Decal\Agent');
end;

function BridgeDir: String;
begin
  Result := ExpandConstant('{app}\DecalBridge');
end;

function MachineBridgeEntryCurrent: Boolean;
var
  P, O: String;
  E: Cardinal;
begin
  Result := RegQueryStringValue(HKLM32, BridgeEntryKey, 'Path', P) and
            (CompareText(RemoveBackslashUnlessRoot(P), BridgeDir) = 0) and
            RegQueryStringValue(HKLM32, BridgeEntryKey, 'Object', O) and (O = BridgeObject) and
            RegQueryDWordValue(HKLM32, BridgeEntryKey, 'Enabled', E) and (E = 1);
end;

function UsesDecalBridge: Boolean;
begin
  Result := FileExists(ExpandConstant('{userappdata}\RynthCore\decal-accounts.json'));
end;

procedure RegisterDecalBridge;
var
  Exe, Params: String;
  Code: Integer;
begin
  if not DecalInstalled then Exit;
  if not UsesDecalBridge then
  begin
    Log('Decal bridge: no Decal + RynthCore account for this user - nothing registered (opt-in only).');
    Exit;
  end;
  if not FileExists(BridgeDir + '\RynthCore.DecalBridge.dll') then Exit;
  Exe := ExpandConstant('{app}\RynthCore.exe');
  Params := '--decal-bridge-register-machine "' + BridgeDir + '"';
  if not MachineBridgeEntryCurrent then
  begin
    if IsAdmin then
    begin
      if not Exec(Exe, Params, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) then
        Log('Decal bridge: machine-wide registration did not start: ' + SysErrorMessage(Code))
      else
        Log('Decal bridge: machine-wide registration exit code ' + IntToStr(Code));
    end
    else
    begin
      if not ShellExec('runas', Exe, Params, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) then
        Log('Decal bridge: elevated machine-wide registration not done (declined or failed): ' + SysErrorMessage(Code))
      else
        Log('Decal bridge: elevated machine-wide registration exit code ' + IntToStr(Code));
    end;
  end;
  if not MachineBridgeEntryCurrent then
  begin
    { No per-user fallback: Decal + RynthCore waits for the machine-wide entry (the launcher
      asks again before the next interactive Decal + RynthCore launch). }
    Log('Decal bridge: not registered machine-wide - per-user step skipped.');
    Exit;
  end;
  { Per-user half, as the user who started Setup (the same user unless Setup was elevated
    with other credentials): removes an earlier RynthCore's per-user copy (backed up first),
    adds our entry to a per-user copy that exists anyway. Never creates one. }
  if not ExecAsOriginalUser(Exe, '--decal-bridge-install-user "' + BridgeDir + '"', ExpandConstant('{app}'),
                            SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('Decal bridge: per-user step did not start: ' + SysErrorMessage(Code));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { After the files, before [Run] (which relaunches the launcher after an update). }
  if CurStep = ssPostInstall then
    RegisterDecalBridge;
end;
