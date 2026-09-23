@echo off
REM Register (or unregister) the CADder Bridge add-in with every installed
REM SolidWorks version. Needs administrator rights: the add-in registry keys
REM live under HKLM\SOFTWARE\SolidWorks\<version>\Addins.
REM
REM   Register-Addin.bat            register the Release build
REM   Register-Addin.bat Debug      register the Debug build, which is the
REM                                 only one that carries the test harness
REM   Register-Addin.bat /u         unregister
REM
REM In the release zip the DLL sits next to this script, with no bin
REM folder, and the script registers that DLL.

setlocal
set CONFIG=Release
set ACTION=register
for %%A in (%*) do (
    if /I "%%~A"=="Debug" set CONFIG=Debug
    if /I "%%~A"=="Release" set CONFIG=Release
    if /I "%%~A"=="/u" set ACTION=unregister
)
set DLL=%~dp0bin\%CONFIG%\Peak.Cadder.dll
if not exist "%DLL%" if exist "%~dp0Peak.Cadder.dll" set DLL=%~dp0Peak.Cadder.dll
set REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe

REM Self-elevate if not already running as administrator. PowerShell
REM refuses an empty -ArgumentList, so a double-click, which gives no
REM arguments, must not pass one.
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator rights...
    if "%~1"=="" (
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    ) else (
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
    )
    if errorlevel 1 (
        echo ERROR: The script did not get administrator rights. It registered nothing.
        pause
    )
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
