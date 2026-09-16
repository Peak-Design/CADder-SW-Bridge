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
UninstallDisplayIcon={app}\Peak.Cadder.dll
WizardStyle=modern

[Files]
Source: "{#Source}\Peak.Cadder.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Source}\icons\*.png"; DestDir: "{app}\icons"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Run]
; /codebase records the DLL path in the COM registration, which is what
; lets SolidWorks find an assembly that is not in the GAC.
Filename: "{#RegAsm}"; Parameters: """{app}\Peak.Cadder.dll"" /codebase"; \
  StatusMsg: "Registering the add-in with SolidWorks..."; Flags: runhidden waituntilterminated

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
  end;
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
