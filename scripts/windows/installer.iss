#ifndef PayloadDir
  #error Build with scripts/windows/build.ps1 -Installer.
#endif
#ifndef StagingDir
  #error Installer staging is required.
#endif
#ifndef AppIcon
  #error apps/windows/app/Assets/AppIcon.ico is required.
#endif
#ifndef ProductNumericVersion
  #error Read the numeric product version from the published EXE.
#endif
#define ProductExe PayloadDir + "\meowfilm.exe"
#define NumericVersion GetVersionNumbersString(ProductExe)
#if NumericVersion == ""
  #error The published meowfilm.exe has no numeric file version.
#endif

[Setup]
AppId={{907D14BD-668B-4CA5-BC4D-4EE9746438C9}
AppName=meowfilm
AppVersion={#ProductNumericVersion}
AppPublisher=meowfilm
DefaultDirName={localappdata}\Programs\meowfilm
DefaultGroupName=meowfilm
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\meowfilm.exe
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#ProductNumericVersion}
VersionInfoTextVersion={#NumericVersion}
VersionInfoProductTextVersion={#ProductNumericVersion}
OutputBaseFilename=meowfilm-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
AppMutex={code:RunningMutex}
SetupMutex=meowfilm-907D14BD-668B-4CA5-BC4D-4EE9746438C9-Setup
UninstallLogMode=append
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.DesktopShortcut=Create a desktop shortcut
english.AppRunning=Close all meowfilm windows for this user, wait for exit, then retry.
english.MaintenanceBusy=Another meowfilm installation or uninstall is in progress. Finish it and retry.
english.KeepData=Keep meowfilm user data?%n%nYes: keep data (default)%nNo: delete this user's settings, accounts and script data%nCancel: cancel uninstall
english.DataDeleteFailed=User data could not be fully deleted. Uninstall has stopped. Check permissions or open files and retry:
english.UnsafePath=A path contains a reparse point. Operation stopped:

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: unchecked

[Files]
#include StagingDir + "\payload-files.iss"
Source: "{#StagingDir}\meowfilm-install-files.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\meowfilm"; Filename: "{app}\meowfilm.exe"; WorkingDir: "{app}"; IconFilename: "{app}\meowfilm.exe"
Name: "{userdesktop}\meowfilm"; Filename: "{app}\meowfilm.exe"; WorkingDir: "{app}"; IconFilename: "{app}\meowfilm.exe"; Tasks: desktopicon

[Code]
const
  InventoryName = 'meowfilm-install-files.txt';
  InventoryHeader = 'meowfilm-installer-files-v1';
var
  Maintenance: THandle;
  MaintenanceOwned: Boolean;
  PreviousFiles: TStringList;
  NextFiles: TStringList;

function CreateMutexW(Attributes: THandle; InitialOwner: BOOL; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function ReleaseMutex(Handle: THandle): BOOL;
  external 'ReleaseMutex@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
function GetFileAttributesW(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function MutexPrefix: String;
begin
  { AppInstallationLifetime uses the same SHA256 of UTF-16 LocalAppData. Global
    coordinates this user across sessions; it does not match process names. }
  Result := 'Global\meowfilm-' + Lowercase(GetSHA256OfUnicodeString(ExpandConstant('{localappdata}')));
end;

function RunningMutex(Param: String): String;
begin
  Result := MutexPrefix + '-Running';
end;

procedure LeaveMaintenance;
begin
  if MaintenanceOwned then ReleaseMutex(Maintenance);
  MaintenanceOwned := False;
  if Maintenance <> 0 then CloseHandle(Maintenance);
  Maintenance := 0;
end;

function EnterMaintenance: String;
var
  WaitResult: Cardinal;
begin
  Result := '';
  if MaintenanceOwned then Exit;
  Maintenance := CreateMutexW(0, False, MutexPrefix + '-Maintenance');
  if Maintenance = 0 then RaiseException('Cannot create the meowfilm installation mutex.');
  WaitResult := WaitForSingleObject(Maintenance, 0);
  MaintenanceOwned := (WaitResult = 0) or (WaitResult = $80);
  if not MaintenanceOwned then begin
    LeaveMaintenance;
    Result := CustomMessage('MaintenanceBusy');
  end else if CheckForMutexes(RunningMutex('')) then begin
    LeaveMaintenance;
    Result := CustomMessage('AppRunning');
  end;
end;

procedure OrdinaryAncestors(Path: String);
var
  Attributes, Error: Cardinal;
  Parent: String;
begin
  repeat
    Attributes := GetFileAttributesW(Path);
    if Attributes = $FFFFFFFF then begin
      Error := DLLGetLastError;
      if (Error <> 2) and (Error <> 3) then
        RaiseException('Cannot inspect path: ' + Path + ' (' + IntToStr(Error) + ')');
    end else if (Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
      RaiseException(CustomMessage('UnsafePath') + ' ' + Path);
    Parent := ExtractFileDir(Path);
    if Parent = Path then Break;
    Path := Parent;
  until Path = '';
end;

function InstalledPath(Name: String): String;
begin
  StringChangeEx(Name, '/', '\', True);
  Result := AddBackslash(ExpandConstant('{app}')) + Name;
end;

function ReadInventory(Path: String): TStringList;
var
  Lines: TArrayOfString;
  I, J: Integer;
  Name, Part, Remaining: String;
begin
  Result := TStringList.Create;
  Result.Sorted := True;
  Result.CaseSensitive := False;
  if not LoadStringsFromFile(Path, Lines) then RaiseException('Cannot read installation file inventory: ' + Path);
  if GetArrayLength(Lines) < 3 then RaiseException('Incomplete installation file inventory.');
  if Lines[0] <> InventoryHeader then RaiseException('Unknown installation file inventory.');
  for I := 1 to GetArrayLength(Lines) - 1 do begin
    Name := Lines[I];
    if Name = '' then Continue;
    Remaining := Name + '/';
    repeat
      J := Pos('/', Remaining);
      Part := Copy(Remaining, 1, J - 1);
      Delete(Remaining, 1, J);
      if (Part = '') or (Part = '.') or (Part = '..') then RaiseException('Unsafe installation file inventory.');
      if (Part[Length(Part)] = '.') or (Part[Length(Part)] = ' ') then RaiseException('Unsafe installation file inventory.');
      for J := 1 to Length(Part) do
        if (Ord(Part[J]) < 32) or (Ord(Part[J]) = 127) or (Pos(Part[J], '\:*?"<>|{}[];') > 0) then
          RaiseException('Unsafe installation file inventory.');
    until Remaining = '';
    if (Result.IndexOf(Name) >= 0) or (CompareText(Copy(Name, 1, 5), 'unins') = 0) then
      RaiseException('Invalid installation file inventory.');
    Result.Add(Name);
    OrdinaryAncestors(InstalledPath(Name));
  end;
  if (Result.IndexOf('meowfilm.exe') < 0) or (Result.IndexOf(InventoryName) < 0) then
    RaiseException('Installation ownership inventory is incomplete.');
end;

procedure CheckExistingPayload;
var
  I: Integer;
  Attributes, Error: Cardinal;
  Path: String;
begin
  for I := 0 to NextFiles.Count - 1 do begin
    Path := InstalledPath(NextFiles[I]);
    Attributes := GetFileAttributesW(Path);
    if Attributes = $FFFFFFFF then begin
      Error := DLLGetLastError;
      if (Error <> 2) and (Error <> 3) then
        RaiseException('Cannot inspect installation destination: ' + Path + ' (' + IntToStr(Error) + ')');
    end else if ((Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) or (PreviousFiles.IndexOf(NextFiles[I]) < 0) then
      RaiseException('The destination already contains a file or directory not owned by this installer. Choose another installation directory: ' + Path);
  end;
end;

procedure CheckExistingRuntime(Directory, Prefix: String);
var
  Found: TFindRec;
  Name, Path: String;
begin
  if FindFirst(AddBackslash(Directory) + '*', Found) then begin
    try
      repeat
        if (Found.Name <> '.') and (Found.Name <> '..') then begin
          Name := Prefix + '/' + Found.Name;
          Path := AddBackslash(Directory) + Found.Name;
          if (Found.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
            RaiseException(CustomMessage('UnsafePath') + ' ' + Path);
          if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            CheckExistingRuntime(Path, Name)
          else if (PreviousFiles.IndexOf(Name) < 0) and (NextFiles.IndexOf(Name) < 0) then
            RaiseException('The runtime directory contains a file not owned by this installer. Choose another installation directory: ' + Path);
        end;
      until not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  App, Data: String;
begin
  Result := EnterMaintenance;
  if Result <> '' then Exit;
  try
    App := AddBackslash(ExpandConstant('{app}'));
    Data := AddBackslash(ExpandConstant('{localappdata}\meowfilm\aggregate'));
    if (Pos(Lowercase(App), Lowercase(Data)) = 1) or (Pos(Lowercase(Data), Lowercase(App)) = 1) then
      RaiseException('Application files and user data must be installed in separate directories.');
    OrdinaryAncestors(ExpandConstant('{app}'));
    if PreviousFiles <> nil then begin
      PreviousFiles.Free;
      PreviousFiles := nil;
    end;
    if NextFiles <> nil then begin
      NextFiles.Free;
      NextFiles := nil;
    end;
    PreviousFiles := TStringList.Create;
    PreviousFiles.Sorted := True;
    PreviousFiles.CaseSensitive := False;
    if FileExists(InstalledPath(InventoryName)) then begin
      PreviousFiles.Free;
      PreviousFiles := nil;
      PreviousFiles := ReadInventory(InstalledPath(InventoryName));
    end;
    ExtractTemporaryFile(InventoryName);
    NextFiles := ReadInventory(ExpandConstant('{tmp}\' + InventoryName));
    CheckExistingPayload;
    OrdinaryAncestors(InstalledPath('runtime'));
    CheckExistingRuntime(InstalledPath('runtime'), 'runtime');
  except
    Result := GetExceptionMessage;
    LeaveMaintenance;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  I: Integer;
  Path, Parent: String;
begin
  if CurStep = ssPostInstall then begin
    LeaveMaintenance;
    Exit;
  end;
  if CurStep <> ssInstall then Exit;
  { Only obsolete paths from the previous install's explicit ownership list.
    No recursive application or runtime deletion; unrelated files survive. }
  for I := 0 to PreviousFiles.Count - 1 do begin
    if NextFiles.IndexOf(PreviousFiles[I]) < 0 then begin
      Path := InstalledPath(PreviousFiles[I]);
      OrdinaryAncestors(Path);
      if FileExists(Path) and not DeleteFile(Path) then
        RaiseException('Cannot remove an obsolete installed file: ' + Path);
      Parent := ExtractFileDir(Path);
      while CompareText(Parent, ExpandConstant('{app}')) <> 0 do begin
        if not RemoveDir(Parent) then Break;
        Parent := ExtractFileDir(Parent);
      end;
    end;
  end;
end;

function InitializeUninstall: Boolean;
var
  Error: String;
begin
  Result := False;
  repeat
    Error := EnterMaintenance;
    if Error = '' then Break;
    Log(Error);
    if UninstallSilent then Exit;
    if MsgBox(Error, mbError, MB_RETRYCANCEL) <> IDRETRY then Exit;
  until False;
  OrdinaryAncestors(ExpandConstant('{app}'));
  PreviousFiles := ReadInventory(InstalledPath(InventoryName));
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Choice: Integer;
  Attributes, Error: Cardinal;
  Data: String;
begin
  if (CurUninstallStep <> usUninstall) or UninstallSilent then Exit;
  Choice := MsgBox(CustomMessage('KeepData'), mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON1);
  if Choice = IDCANCEL then Abort;
  if Choice = IDYES then Exit;
  Data := ExpandConstant('{localappdata}\meowfilm\aggregate');
  OrdinaryAncestors(ExpandConstant('{localappdata}\meowfilm'));
  Attributes := GetFileAttributesW(Data);
  if Attributes = $FFFFFFFF then begin
    Error := DLLGetLastError;
    if (Error = 2) or (Error = 3) then Exit;
    RaiseException(CustomMessage('DataDeleteFailed') + ' ' + Data + ' (' + IntToStr(Error) + ')');
  end;
  if (Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
    RaiseException(CustomMessage('DataDeleteFailed') + ' ' + Data);
  { Inno DelTree removes reparse entries themselves, never their targets.
    Do this before uninstalling files; a failure leaves a retryable uninstall. }
  if not DelTree(Data, True, True, True) then
    RaiseException(CustomMessage('DataDeleteFailed') + ' ' + Data);
end;

procedure DeinitializeSetup;
begin
  LeaveMaintenance;
  if PreviousFiles <> nil then PreviousFiles.Free;
  if NextFiles <> nil then NextFiles.Free;
end;

procedure DeinitializeUninstall;
begin
  LeaveMaintenance;
  if PreviousFiles <> nil then PreviousFiles.Free;
end;
