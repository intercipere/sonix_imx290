; Installer for the Sonix IMX290 ASCOM camera driver (Inno Setup 6).
; Built by build.cmd from dist\Sonix290Camera; does what package\install.cmd does: copy to
; Program Files\Sonix290Camera and register the in-process COM driver for 64-bit and 32-bit clients.

#define Dist AddBackslash(SourcePath) + "..\dist\Sonix290Camera"
#define FullVersion GetVersionNumbersString(Dist + "\ASCOM.Sonix290.Camera.dll")
; 0.5.0.0 -> 0.5.0
#define AppVersion Copy(FullVersion, 1, RPos(".", FullVersion) - 1)

[Setup]
AppId={{2E730A04-E58E-4389-AA2D-26C04698F7C8}
AppName=Sonix IMX290 ASCOM Driver
AppVersion={#AppVersion}
AppVerName=Sonix IMX290 ASCOM Driver {#AppVersion}
AppPublisher=OpenAstroTech
VersionInfoVersion={#FullVersion}
DefaultDirName={autopf64}\Sonix290Camera
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\..\LICENSE
OutputDir=..\dist
OutputBaseFilename=Sonix290Camera-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Sonix IMX290 ASCOM Driver {#AppVersion}
; The driver DLL is locked while a client (PHD2, N.I.N.A., SharpCap, ConformU) has it loaded.
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#Dist}\ASCOM.Sonix290.Camera.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\ASCOM.Sonix290.Camera.pdb"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\SxTest.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\SxTest.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\comtest.vbs"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{dotnet4064}\RegAsm.exe"; Parameters: "/nologo /codebase ""{app}\ASCOM.Sonix290.Camera.dll"""; \
  StatusMsg: "Registering the driver for 64-bit programs..."; Flags: runhidden waituntilterminated
Filename: "{dotnet4032}\RegAsm.exe"; Parameters: "/nologo /codebase ""{app}\ASCOM.Sonix290.Camera.dll"""; \
  StatusMsg: "Registering the driver for 32-bit programs (PHD2)..."; Flags: runhidden waituntilterminated

[UninstallRun]
; /unregister also removes the ASCOM Chooser entry (the driver's ComUnregisterFunction).
Filename: "{dotnet4064}\RegAsm.exe"; Parameters: "/nologo /unregister ""{app}\ASCOM.Sonix290.Camera.dll"""; \
  RunOnceId: "Unregister64"; Flags: runhidden waituntilterminated
Filename: "{dotnet4032}\RegAsm.exe"; Parameters: "/nologo /unregister ""{app}\ASCOM.Sonix290.Camera.dll"""; \
  RunOnceId: "Unregister32"; Flags: runhidden waituntilterminated

[UninstallDelete]
; The folder may predate this installer (package\install.cmd); remove it once it is empty.
Type: dirifempty; Name: "{app}"

[Code]
const
  ClsidKey = 'SOFTWARE\Classes\CLSID\{e47f8cb6-ed19-4267-aba5-837b7d353c88}';
  ProgIdKey = 'SOFTWARE\Classes\ASCOM.Sonix290.Camera';

procedure RegAsm(const Framework, Args: String);
var
  Code: Integer;
begin
  Exec(ExpandConstant(Framework) + '\RegAsm.exe', Args, '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Dll: String;
begin
  // regasm adds a subkey per assembly version and never removes old ones: unregister the DLL being replaced
  // with its own version first, so an upgrade leaves nothing behind.
  if CurStep = ssInstall then
  begin
    Dll := ExpandConstant('{app}\ASCOM.Sonix290.Camera.dll');
    if FileExists(Dll) then
    begin
      RegAsm('{dotnet4064}', '/nologo /unregister "' + Dll + '"');
      RegAsm('{dotnet4032}', '/nologo /unregister "' + Dll + '"');
    end;
  end;
end;

procedure RemoveStaleClass(Root: Integer);
begin
  // Left over from versions registered by other means; only when no version is registered any more.
  if not RegValueExists(Root, ClsidKey + '\InprocServer32', 'Assembly') then
  begin
    RegDeleteKeyIncludingSubkeys(Root, ClsidKey);
    RegDeleteKeyIncludingSubkeys(Root, ProgIdKey);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveStaleClass(HKLM64);
    RemoveStaleClass(HKLM32);
  end;
end;

function AscomPlatformVersion(): String;
begin
  if not RegQueryStringValue(HKLM32, 'SOFTWARE\ASCOM\Platform', 'Platform Version', Result) then
    Result := '';
end;

function InitializeSetup(): Boolean;
var
  Version: String;
begin
  Result := True;
  if not IsDotNetInstalled(net48, 0) then
  begin
    MsgBox('This driver needs the .NET Framework 4.8, which is not installed.' + #13#10 +
      'Install it from Microsoft (it is included in Windows 10 1903 and later), then run this setup again.',
      mbCriticalError, MB_OK);
    Result := False;
    exit;
  end;
  Version := AscomPlatformVersion();
  if Version = '' then
    Result := MsgBox('The ASCOM Platform does not seem to be installed.' + #13#10 +
      'Programs find this driver through the ASCOM Chooser, so install the ASCOM Platform 6.6 or later ' +
      'from ascom-standards.org first.' + #13#10#13#10 + 'Install the driver anyway?',
      mbConfirmation, MB_YESNO) = IDYES;
end;
