; EasyPhotoShow Installer Script
; Inno Setup 6.x
; https://jrsoftware.org/ishelp/

#define MyAppName "EasyPhotoShow"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "EasyPhotoShow"
#define MyAppURL "https://easyphotoshow.com"
; NOTE: the published executable is EasyPhotoShow.exe because the App csproj sets
; <AssemblyName>EasyPhotoShow</AssemblyName> (not EasyPhotoShow.App.exe).
#define MyAppExeName "EasyPhotoShow.exe"
#define MyAppDescription "Simple Windows slideshow app for families and events"
#define SourceDir "..\publish\win-x64"
#define AssetsDir "..\src\EasyPhotoShow.App\Assets"

[Setup]
AppId={{A3F2B8C1-4D7E-4F9A-B2C3-D4E5F6A7B8C9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=
OutputDir=Output
OutputBaseFilename=EasyPhotoShowSetup
SetupIconFile={#AssetsDir}\easyphotoshow.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppDescription}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Main application — all files from publish output
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; App icon for shortcuts. The icon is embedded in the exe (it's a <Resource> in the csproj),
; so publish does not emit a loose Assets\easyphotoshow.ico. Bundle it from source so the
; [Icons] IconFilename references below resolve on the target machine.
Source: "{#AssetsDir}\easyphotoshow.ico"; DestDir: "{app}\Assets"; Flags: ignoreversion

[Icons]
; Start Menu shortcut
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\easyphotoshow.ico"
; Start Menu uninstall shortcut
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
; Desktop shortcut (optional, unchecked by default)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\easyphotoshow.ico"; Tasks: desktopicon

[Run]
; Offer to launch after install
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up app log folder on uninstall
Type: filesandordirs; Name: "{localappdata}\EasyPhotoShow\logs"
