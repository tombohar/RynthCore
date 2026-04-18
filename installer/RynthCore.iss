; RynthCore + RynthAi Installer
; Build with: Build-Installer.ps1 (requires Inno Setup 6)
; Source: https://jrsoftware.org/isdl.php

#define AppName      "RynthCore"
#define AppVersion   "0.2"
#define AppPublisher "RynthCore"
#define AppExeName   "RynthCore.exe"
#define DataRoot     "C:\Games\RynthSuite\RynthAi"

; NOTE: Staging directory is populated by Build-Installer.ps1 before ISCC is invoked.
; All files under staging\app\ are installed to {app}\ recursively.

[Setup]
AppId={{A8B4C2D1-E3F5-4678-9ABC-DEF012345678}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/tombohar/RynthCore
DefaultDirName=C:\Games\RynthCore
DisableDirPage=no
DefaultGroupName={#AppName}
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

; ── Program files ─────────────────────────────────────────────────────────────
[Files]
; Installs the entire staging layout (launcher, Runtime\, Runtime\Plugins\, etc.)
Source: "staging\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; ── Data directories (created once; never removed on uninstall) ──────────────
[Dirs]
Name: "{#DataRoot}";                             Flags: uninsneveruninstall
Name: "{#DataRoot}\NavProfiles";                 Flags: uninsneveruninstall
Name: "{#DataRoot}\LootProfiles";                Flags: uninsneveruninstall
Name: "{#DataRoot}\MetaFiles";                   Flags: uninsneveruninstall
Name: "{#DataRoot}\MetaProfiles";                Flags: uninsneveruninstall
Name: "{#DataRoot}\SettingsProfiles";            Flags: uninsneveruninstall
Name: "{#DataRoot}\SettingsProfiles\ACEmulator"; Flags: uninsneveruninstall
Name: "{#DataRoot}\LuaScripts";                  Flags: uninsneveruninstall
Name: "{#DataRoot}\Logs";                        Flags: uninsneveruninstall
Name: "{#DataRoot}\pvars";                       Flags: uninsneveruninstall
Name: "{#DataRoot}\ItemGiver";                   Flags: uninsneveruninstall

; ── Shortcuts ─────────────────────────────────────────────────────────────────
[Icons]
; Start Menu
Name: "{group}\RynthCore"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "Launch RynthCore and inject into Asheron's Call"
Name: "{group}\Uninstall RynthCore"; Filename: "{uninstallexe}"

; Optional Desktop shortcut (created only when the desktopicon task is checked)
Name: "{autodesktop}\RynthCore"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

; ── Pascal scripting ──────────────────────────────────────────────────────────
[Code]

// Returns True if acclient.exe has any running instances.
function IsAcClientRunning(): Boolean;
var
  WbemLocator, WbemService, QueryResult: Variant;
begin
  Result := False;
  try
    WbemLocator  := CreateOleObject('WbemScripting.SWbemLocator');
    WbemService  := WbemLocator.ConnectServer('.', 'root\cimv2', '', '');
    QueryResult  := WbemService.ExecQuery(
                     'SELECT Handle FROM Win32_Process WHERE Name = "acclient.exe"');
    Result := QueryResult.Count > 0;
  except
    // If WMI fails for any reason just let the install continue.
    Result := False;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;

  if IsAcClientRunning() then
  begin
    if MsgBox(
      'Asheron''s Call (acclient.exe) is currently running.' + #13#10 + #13#10 +
      'Please close Asheron''s Call before continuing — the installer ' +
      'needs to write files that are loaded by acclient.exe.' + #13#10 + #13#10 +
      'Click OK once you have closed it, or Cancel to abort.',
      mbConfirmation, MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False;
    end;
  end;
end;

// Customise the Finish page with getting-started instructions.
procedure CurPageChanged(CurPageID: Integer);
var
  Msg: String;
begin
  if CurPageID <> wpFinish then Exit;

  Msg :=
    'RynthCore is installed.' + #13#10 + #13#10 +
    'Getting started:' + #13#10 +
    '  1. Start Asheron''s Call and log in to your character.' + #13#10 +
    '  2. Open RynthCore (Start Menu or Desktop shortcut).' + #13#10 +
    '  3. In the Launcher, set your acclient.exe path under Runtime Paths.' + #13#10 +
    '  4. Click "Inject Running AC" to activate the overlay.' + #13#10 +
    '     The RynthAi panel should appear inside the AC window.' + #13#10 + #13#10 +
    'Alternatively, use "Launch + Inject" to have the launcher start' + #13#10 +
    'AC and inject automatically once it finishes loading.' + #13#10 + #13#10 +
    'Loot, nav, and meta profiles go in:' + #13#10 +
    '  C:\Games\RynthSuite\RynthAi\' + #13#10 + #13#10 +
    'Log file: %USERPROFILE%\Desktop\RynthCore.log';

  WizardForm.FinishedLabel.Caption := Msg;
end;
