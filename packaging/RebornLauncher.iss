#ifndef MyAppVersion
  #define MyAppVersion "0.4.1"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{B5A3D678-6AC4-42B0-9A73-855F9981C9A9}
AppName=Galaxies Reborn Launcher
AppVersion={#MyAppVersion}
AppPublisher=Galaxies Reborn
AppPublisherURL=https://github.com/Galaxies-Reborn
AppSupportURL=https://github.com/Galaxies-Reborn/reborn-launcher/issues
DefaultDirName={localappdata}\Programs\Galaxies Reborn Launcher
DefaultGroupName=Galaxies Reborn
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=RebornLauncher-{#MyAppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayIcon={app}\RebornLauncher.exe
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=Galaxies Reborn
VersionInfoDescription=Galaxies Reborn Launcher Setup
VersionInfoProductName=Galaxies Reborn Launcher

[Files]
Source: "{#PublishDir}\RebornLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion
; Headless front end, for driving a server without the window.
Source: "{#PublishDir}\reborn.exe"; DestDir: "{app}"; Flags: ignoreversion
; MinGit, so a fresh machine needs no Git of its own. The launcher looks for it at
; tools\MinGit\cmd\git.exe; this layout must match GitBootstrapper.BundledRelativePath.
Source: "{#PublishDir}\tools\MinGit\*"; DestDir: "{app}\tools\MinGit"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Galaxies Reborn Launcher"; Filename: "{app}\RebornLauncher.exe"
Name: "{autodesktop}\Galaxies Reborn Launcher"; Filename: "{app}\RebornLauncher.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\RebornLauncher.exe"; Description: "Launch Galaxies Reborn Launcher"; Flags: nowait postinstall skipifsilent
