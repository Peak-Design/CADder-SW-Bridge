; Inno Setup script for the CADder Bridge add-in.
;
; Build: tools\Make-Release.ps1 runs this after the Release build and passes
; the version. By hand:
;   ISCC.exe /DVersion=0.1.0 installer\CADder-Bridge.iss
;
; What it does: copies the DLL, the icons and the licence texts to
; Program Files, then runs RegAsm /codebase. The add-in's own
; ComRegisterFunction writes the HKLM SolidWorks add-in key for every
; installed SolidWorks year, so this script does not touch those keys.
; Uninstall runs RegAsm /unregister, which removes them again.

#ifndef Version
  #define Version "0.0.0"
#endif
#define AppName "CADder Bridge"
#define Publisher "Peak Design"
#define Source "..\src\Peak.Cadder\bin\Release"
#define RegAsm "{win}\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"

[Setup]
; Fixed for the life of the product, so an upgrade replaces the old install.
AppId={{9C0B2C64-3E7A-4D0E-9B7E-2F5A8C1D6E43}
AppName={#AppName}
AppVersion={#Version}
AppPublisher={#Publisher}
AppPublisherURL=https://github.com/Peak-Design/CADder-SW-Bridge
AppSupportURL=https://github.com/Peak-Design/CADder-SW-Bridge/issues
AppUpdatesURL=https://github.com/Peak-Design/CADder-SW-Bridge/releases
DefaultDirName={commonpf}\{#Publisher}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=CADder-Bridge-{#Version}-setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
LicenseFile=..\LICENSE
; The icon of the setup program, and the icon Windows shows beside the
; entry in Installed apps.
SetupIconFile=CADder-Bridge.ico
UninstallDisplayIcon={app}\CADder-Bridge.ico
UninstallDisplayName={#AppName}
VersionInfoVersion={#Version}
VersionInfoCompany={#Publisher}
VersionInfoProductName={#AppName}
WizardStyle=modern

[Files]
Source: "{#Source}\Peak.Cadder.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Source}\icons\*.png"; DestDir: "{app}\icons"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "CADder-Bridge.ico"; DestDir: "{app}"; Flags: ignoreversion

[UninstallRun]
Filename: "{#RegAsm}"; Parameters: """{app}\Peak.Cadder.dll"" /unregister"; \
  RunOnceId: "UnregisterAddIn"; Flags: runhidden waituntilterminated

[Code]
// SolidWorks holds the add-in DLL open. Installing over a running
// SolidWorks leaves the old file in use and the new one unwritten, so
// both Setup and Uninstall refuse to start while SLDWORKS.exe runs.
function SolidWorksRunning(): Boolean;
var
  ResultCode: Integer;
begin
  // find sets exit code 0 when the process name appears in the tasklist
  // output and 1 when it does not.
  Result := Exec('cmd.exe',
    '/C tasklist /FI "IMAGENAME eq SLDWORKS.exe" /NH | find /I "SLDWORKS.exe" > nul',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if SolidWorksRunning() then
  begin
    MsgBox('Close SolidWorks before you install CADder Bridge.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  // RegAsm comes with the .NET Framework. Windows 10 and 11 have it, but
  // say so plainly when a machine does not, because the add-in cannot
  // register without it.
  if not FileExists(ExpandConstant('{#RegAsm}')) then
  begin
    MsgBox('Microsoft .NET Framework 4.8 is necessary for CADder Bridge.' + #13#10
      + 'Install it, then start this setup again.', mbError, MB_OK);
    Result := False;
  end;
end;

// The registration is what puts the add-in on the SolidWorks ribbon.
// /codebase records the DLL path in the COM registration, which is what
// lets SolidWorks find an assembly that is not in the GAC. This runs here
// rather than in [Run] so that a failure is reported: an installed add-in
// that SolidWorks cannot see is the worst of the two outcomes.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  if (not Exec(ExpandConstant('{#RegAsm}'),
      '"' + ExpandConstant('{app}\Peak.Cadder.dll') + '" /codebase',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
    MsgBox('CADder Bridge is installed, but it could not be registered with'
      + #13#10 + 'SolidWorks. Start the setup again as an administrator.',
      mbError, MB_OK);
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if SolidWorksRunning() then
  begin
    MsgBox('Close SolidWorks before you remove CADder Bridge.', mbError, MB_OK);
    Result := False;
  end;
end;
