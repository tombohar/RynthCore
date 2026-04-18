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
AppPublisherURL=https://github.com/tombohar/RynthCore
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
; Installs the entire staging layout (launcher, Runtime\, Runtime\Plugins\, etc.)
Source: "staging\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Data directories (created once; never removed on uninstall)
[Dirs]
Name: "C:\Games\RynthSuite\RynthAi";                             Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\NavProfiles";                 Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\LootProfiles";                Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\MetaFiles";                   Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\MetaProfiles";                Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\SettingsProfiles";            Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\SettingsProfiles\ACEmulator"; Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\LuaScripts";                  Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\Logs";                        Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\pvars";                       Flags: uninsneveruninstall
Name: "C:\Games\RynthSuite\RynthAi\ItemGiver";                   Flags: uninsneveruninstall

; Shortcuts
[Icons]
; Start Menu
Name: "{group}\RynthCore"; Filename: "{app}\RynthCore.exe"; WorkingDir: "{app}"; Comment: "Launch RynthCore and inject into Asheron's Call"
Name: "{group}\Uninstall RynthCore"; Filename: "{uninstallexe}"

; Optional Desktop shortcut (created only when the desktopicon task is checked)
Name: "{autodesktop}\RynthCore"; Filename: "{app}\RynthCore.exe"; WorkingDir: "{app}"; Tasks: desktopicon
