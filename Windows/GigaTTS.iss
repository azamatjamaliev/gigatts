; Inno Setup 6. The publish script fills ..\dist\GigaTTS-windows first.

#define AppVersion "1.0.0"

[Setup]
AppId={{7C4E2A18-6B5D-4F0A-9E33-1A2B3C4D5E6F}
AppName=GigaПисец
AppVersion={#AppVersion}
AppPublisher=GigaПисец
DefaultDirName={localappdata}\Programs\GigaTTS
DefaultGroupName=GigaПисец
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=GigaTTS-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
WizardStyle=modern
UninstallDisplayIcon={app}\GigaTTS.exe
CloseApplications=force

[Files]
Source: "..\dist\GigaTTS-windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\GigaПисец"; Filename: "{app}\GigaTTS.exe"
Name: "{userdesktop}\GigaПисец"; Filename: "{app}\GigaTTS.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе"

[Run]
Filename: "{app}\GigaTTS.exe"; Description: "Запустить GigaПисец"; Flags: nowait postinstall skipifsilent
