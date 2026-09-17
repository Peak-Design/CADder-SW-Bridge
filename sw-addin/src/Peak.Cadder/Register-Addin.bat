@echo off
REM Register (or unregister) the CADder Bridge add-in with every installed
REM SolidWorks version. Needs administrator rights: the add-in registry keys
REM live under HKLM\SOFTWARE\SolidWorks\<version>\Addins.
REM
REM   Register-Addin.bat            register the Release build
REM   Register-Addin.bat Debug      register the Debug build, which is the
REM                                 only one that carries the test harness
REM   Register-Addin.bat /u         unregister

setlocal
set CONFIG=Release
set ACTION=register
for %%A in (%*) do (
    if /I "%%~A"=="Debug" set CONFIG=Debug
    if /I "%%~A"=="Release" set CONFIG=Release
    if /I "%%~A"=="/u" set ACTION=unregister
)
set DLL=%~dp0bin\%CONFIG%\Peak.Cadder.dll
set REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe

REM Self-elevate if not already running as administrator.
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator rights...
    powershell -Command "Start-Process '%~f0' -ArgumentList '%*' -Verb RunAs"
    exit /b
)

if not exist "%DLL%" (
    echo ERROR: %DLL% not found. Build it first:
    echo     dotnet build -c %CONFIG%
    pause
    exit /b 1
)

REM Take any previous registration out first, by class id. The add-in kept
REM the same class id when it was renamed from SW To Blender, so a stale
REM entry points at a DLL that is no longer there. RegAsm would overwrite
REM most of it, but a clean removal leaves nothing behind to wonder about.
set CLSID={5a19bed7-5bae-4520-a820-99c7466c42ac}
reg delete "HKLM\SOFTWARE\Classes\CLSID\%CLSID%" /f >nul 2>&1
for /f "tokens=*" %%V in ('reg query "HKLM\SOFTWARE\SolidWorks" 2^>nul ^| findstr /I /R "SOLIDWORKS [0-9][0-9][0-9][0-9]$"') do (
    reg delete "%%V\Addins\%CLSID%" /f >nul 2>&1
)

if /I "%ACTION%"=="unregister" (
    echo Unregistering %DLL%
    "%REGASM%" "%DLL%" /unregister
) else (
    echo Registering %DLL%
    "%REGASM%" "%DLL%" /codebase
)

echo.
echo Done. Restart SolidWorks, then check Tools ^> Add-Ins for "CADder Bridge".
pause
