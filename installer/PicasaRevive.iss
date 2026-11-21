; svacitra - optional Inno Setup installer script (https://jrsoftware.org/isinfo.php).
; 1. Run build.bat (or publish.ps1) so that ..\publish\svacitra.exe exists.
; 2. Compile this script with Inno Setup 6:  iscc installer\PicasaRevive.iss
; The installed app keeps its library in %LOCALAPPDATA%\PicasaRevive, so uninstalling never touches photos.

#define AppName "svacitra"
#define AppVersion GetStringFileInfo("..\publish\svacitra.exe", "ProductVersion")
#define AppExe "svacitra.exe"

[Setup]
AppId={{6F1B7C0E-5B7B-4E0C-9E5B-2A7C1D0E9A41}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Fajar Isnanto
AppContact=fajari@gmail.com
AppCopyright=Copyright (c) 2026 Fajar Isnanto - Jakarta, Indonesia.
VersionInfoCompany=Fajar Isnanto
DefaultDirName={autopf}\svacitra
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousGroup=no
LicenseFile=..\LICENSE
InfoBeforeFile=..\DISCLAIMER.txt
OutputDir=..\publish
OutputBaseFilename=svacitra-Setup-{#AppVersion}
SetupIconFile=..\PicasaRevive.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\DISCLAIMER.txt"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
Type: files; Name: "{app}\FramefolioStudio.exe"
Type: files; Name: "{autodesktop}\Framefolio Studio.lnk"
Type: files; Name: "{autoprograms}\Framefolio Studio\Framefolio Studio.lnk"
Type: files; Name: "{app}\PicasaRevive.exe"
Type: files; Name: "{autodesktop}\Picasa Revive.lnk"
Type: files; Name: "{autoprograms}\Picasa Revive\Picasa Revive.lnk"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
