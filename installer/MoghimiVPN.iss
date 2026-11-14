#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{A6278D1B-44C4-4F96-AC5D-9EA5AB6F2B2C}
AppName=Moghimi VPN
AppVersion={#AppVersion}
AppPublisher=Moghimi
DefaultDirName={autopf}\Moghimi VPN
DefaultGroupName=Moghimi VPN
UninstallDisplayName=Moghimi VPN
UninstallDisplayIcon={app}\MoghimiVPN.exe
OutputDir=..\artifacts
OutputBaseFilename=MoghimiVPN-Setup-x64
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
SetupLogging=yes
CloseApplications=yes
RestartIfNeededByRun=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Moghimi VPN"; Filename: "{app}\MoghimiVPN.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Moghimi VPN"; Filename: "{app}\MoghimiVPN.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\MoghimiVPN.exe"; Description: "Launch Moghimi VPN"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
; User settings, encrypted profiles and logs under LocalAppData are intentionally preserved.
