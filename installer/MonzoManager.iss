; Parc Spend installer script (Inno Setup)

[Setup]
AppId={{18A511F3-D786-4638-B1B4-7A2BB27E62A9}
AppName=Parc Spend
AppVersion=1.0.0
AppPublisher=Parc
DefaultDirName={autopf}\Parc Spend
DefaultGroupName=Parc Spend
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=ParcSpend-Setup
SetupIconFile=..\Assets\Parc.ico
UninstallDisplayIcon={app}\MonzoManager.exe
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Parc Spend"; Filename: "{app}\MonzoManager.exe"
Name: "{autodesktop}\Parc Spend"; Filename: "{app}\MonzoManager.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MonzoManager.exe"; Description: "{cm:LaunchProgram,Parc Spend}"; Flags: nowait postinstall skipifsilent
