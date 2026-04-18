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

; No [Code] section — the Pascal code block triggers ISPP preprocessor
; errors on CI (Inno Setup 6.7.1) due to how it handles #char literals.
; Getting-started instructions are included in the GitHub Release notes.
