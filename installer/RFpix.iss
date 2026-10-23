; RFpix.iss - the Windows installer, built with Inno Setup 6 (free; jrsoftware.org).
;
; What it does:
;   - asks where to put RFpix (normally the user's own programs folder, which needs no
;     administrator rights; it can also install for everyone on the computer);
;   - adds RFpix to the Start menu, and a desktop shortcut if wanted;
;   - adds an uninstaller to Windows' Settings > Apps. Uninstalling removes the program and
;     asks whether to remove the settings too. Received pictures, recordings and test results
;     (Documents\RFpix) are never removed: they're the user's own files.
;   - running a newer installer updates RFpix in place (it closes a running RFpix first).
;
; The build (.github/workflows/build.yml) runs:  iscc /DAppVersion=0.2.1 installer\RFpix.iss
; with the program's files already published into publish-folder.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; Windows' file version must be numbers only: "0.5.2-beta" gives 0.5.2.
#if Pos("-", AppVersion) > 0
  #define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
#else
  #define NumericVersion AppVersion
#endif

[Setup]
; The AppId identifies RFpix to Windows across versions: never change it.
AppId={{E9F900F9-71AC-491A-8283-D53EDF719FA6}
AppName=RFpix
AppVersion={#AppVersion}
AppVerName=RFpix {#AppVersion}
VersionInfoVersion={#NumericVersion}
AppPublisher=Dan Brandon, KD9DD
AppPublisherURL=https://github.com/brandandon/RFpix
AppSupportURL=https://github.com/brandandon/RFpix
AppUpdatesURL=https://github.com/brandandon/RFpix/releases/latest
DefaultDirName={autopf}\RFpix
DisableDirPage=no
DefaultGroupName=RFpix
DisableProgramGroupPage=yes
; Install for just this user (no administrator needed) unless the user picks "everyone".
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE
SetupIconFile=..\src\RFpix\Assets\RFpix.ico
UninstallDisplayIcon={app}\RFpix.exe
UninstallDisplayName=RFpix
OutputDir=..\installer-output
OutputBaseFilename=RFpix-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Put a shortcut on the desktop"; Flags: unchecked

[Files]
Source: "..\publish-folder\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Tidy up after earlier single-file versions of RFpix.exe, which unpacked a copy of
; their parts here every time a new version first ran.
Type: filesandordirs; Name: "{%TEMP}\.net\RFpix"

[Icons]
Name: "{autoprograms}\RFpix"; Filename: "{app}\RFpix.exe"
Name: "{autodesktop}\RFpix"; Filename: "{app}\RFpix.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RFpix.exe"; Description: "Start RFpix now"; Flags: nowait postinstall skipifsilent

[Code]
// After uninstalling: offer to remove the settings too (radio, sound devices, callsign...).
// Documents\RFpix (pictures, recordings, test results) is always kept.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Settings: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Settings := ExpandConstant('{userappdata}\RFpix');
    if DirExists(Settings) and not UninstallSilent then
      if MsgBox('Also remove your RFpix settings (radio, sound devices, callsign and so on)?' + #13#10#13#10 +
                'Your received pictures, recordings and test results in Documents\RFpix are kept either way.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Settings, True, True, True);
  end;
end;
