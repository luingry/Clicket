#ifndef AppVersion
  #error AppVersion must come from VERSION via scripts/build-installer.ps1
#endif
#define AppMutexName "Local\Clicket.SingleInstance"

[Setup]
AppId={{DBD29CBD-D4B5-44F7-9FB2-B5A34D3A645B}
AppName=Clicket
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=luingry
AppPublisherURL=https://github.com/luingry/Clicket
DefaultDirName={localappdata}\Programs\Clicket
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
DisableWelcomePage=yes
PrivilegesRequired=lowest
MinVersion=10.0.14393
OutputDir=..\artifacts
OutputBaseFilename=Clicket-Setup-{#AppVersion}
SetupIconFile=..\assets\clicket.ico
UninstallDisplayIcon={app}\Clicket.exe
UninstallDisplayName=Clicket
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardImageFile=..\assets\installer\wizard-164x314.bmp,..\assets\installer\wizard-328x628.bmp
WizardSmallImageFile=..\assets\installer\wizard-small-55x58.bmp,..\assets\installer\wizard-small-110x116.bmp
CloseApplications=no
RestartApplications=no
AppMutex={#AppMutexName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
Source: "..\src\Clicket\bin\Release\net48\Clicket.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\Clicket\bin\Release\net48\Clicket.exe.config"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Clicket"; Filename: "{app}\Clicket.exe"

[Run]
Filename: "{app}\Clicket.exe"; Description: "{cm:LaunchProgram,Clicket}"; Flags: nowait postinstall skipifsilent

[Registry]
; Created by the "Start with Windows" setting; removed with the app, and nothing else in the key is touched.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Clicket"; Flags: uninsdeletevalue dontcreatekey

[Code]
procedure CloseRunningApp();
var
  Window: HWND;
  Attempt: Integer;
begin
  Window := FindWindowByWindowName('Clicket.Host');
  if Window <> 0 then
    PostMessage(Window, $0010, 0, 0);
  for Attempt := 1 to 40 do begin
    if not CheckForMutexes('{#AppMutexName}') then
      Exit;
    Sleep(100);
  end;
end;

{ Inno checks AppMutex right after these, so the app must leave first. If it did not, that check tells the user. }
function InitializeSetup(): Boolean;
begin
  CloseRunningApp();
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  CloseRunningApp();
  Result := True;
end;
