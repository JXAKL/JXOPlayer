#ifndef Payload
  #error Payload must be specified
#endif
#ifndef ReleaseVersion
  #define ReleaseVersion "2.0.2"
#endif
[Setup]
AppId={{E3E13C5B-B2E7-4D29-B85E-61B64CD8F990}
AppName=JXOPlayer
AppVersion={#ReleaseVersion}
AppVerName=JXOPlayer {#ReleaseVersion}
DefaultDirName={localappdata}\Programs\JXOPlayer
DefaultGroupName=JXOPlayer 2.0
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
DisableProgramGroupPage=yes
DisableDirPage=no
UsePreviousAppDir=yes
UninstallDisplayIcon={app}\JXOPlayer.exe
SetupIconFile={#Payload}\Runtime\JXOPlayer.ico
OutputDir=..\Releases
OutputBaseFilename=JXOPlayer-{#ReleaseVersion}-Setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Excludes: "JXOPlayer.config.json,Data\RandomPlayback.settings.json,Plugins\FloatingAxisControl.config.json,PotPlayer\PotPlayerMini64.ini"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Payload}\JXOPlayer.config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#Payload}\Data\RandomPlayback.settings.json"; DestDir: "{app}\Data"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#Payload}\Plugins\FloatingAxisControl.config.json"; DestDir: "{app}\Plugins"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#Payload}\PotPlayer\PotPlayerMini64.ini"; DestDir: "{app}\PotPlayer"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\JXOPlayer 2.0"; Filename: "{app}\JXOPlayer.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\JXOPlayer 2.0"; Filename: "{app}\JXOPlayer.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Code]
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
    if FileExists(ExpandConstant('{app}\JXOPlayer.exe')) and
       not FileExists(ExpandConstant('{app}\.clean-distribution')) then
    begin
      MsgBox('此目录已有其他版本。请选择新目录安装干净版，以保留原软件和个人设置。', mbError, MB_OK);
      Result := False;
    end;
end;
