; Parc Spend installer
#ifndef PublishDirectory
  #define PublishDirectory AddBackslash(SourcePath) + "..\artifacts\publish\win-x64"
#endif
#define Launcher AddBackslash(PublishDirectory) + "MonzoManager.exe"
#if !FileExists(Launcher)
  #error "Build the Windows x64 payload before compiling the installer."
#endif
#define AppVersion GetVersionNumbersString(Launcher)

[Setup]
AppId={{18A511F3-D786-4638-B1B4-7A2BB27E62A9}
AppName=Parc Spend
AppVersion={#AppVersion}
AppPublisher=Parc
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Parc Spend
VersionInfoCompany=Parc
VersionInfoDescription=Parc Spend Setup
ArchitecturesAllowed=x64compatible
MinVersion=10.0.19041
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
UsePreviousPrivileges=yes
DefaultDirName={autopf}\Parc Spend
UsePreviousAppDir=yes
DefaultGroupName=Parc Spend
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=ParcSpend-Setup
SetupIconFile=..\Assets\Parc.ico
UninstallDisplayName=Parc Spend
UninstallDisplayIcon={app}\MonzoManager.exe
CloseApplications=yes
RestartApplications=no
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dynamic
WizardImageFile=
WizardSmallImageFile=..\Assets\Parc.png
WizardSmallImageFileDynamicDark=..\Assets\Parc.png

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Excludes: "*.pdb,MonzoManager.xml,*.log,*.csv,*.xlsx,*.monzobak,.env*,*.p12,*.pfx,credentials.dat,credentials.json,tokens.dat,tokens.json,manual-finance.json,monthly-net-worth.json,finance-preferences.json,transaction-ledger.json,budgets.json,networth-history.json,restore-pending.dat"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Parc Spend"; Filename: "{app}\MonzoManager.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Parc Spend"; Filename: "{app}\MonzoManager.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\MonzoManager.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,Parc Spend}"; Flags: nowait postinstall skipifsilent runasoriginaluser
