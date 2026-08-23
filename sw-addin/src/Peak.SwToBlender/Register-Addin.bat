@echo off
REM Register (or unregister) the SW To Blender add-in with every installed
REM SolidWorks version. Needs administrator rights: the add-in registry keys
REM live under HKLM\SOFTWARE\SolidWorks\<version>\Addins.
REM
REM   Register-Addin.bat            register
REM   Register-Addin.bat /u         unregister

setlocal
set DLL=%~dp0bin\Release\Peak.SwToBlender.dll
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
    echo     dotnet build -c Release
    pause
    exit /b 1
)

if /I "%~1"=="/u" (
    echo Unregistering %DLL%
    "%REGASM%" "%DLL%" /unregister
) else (
    echo Registering %DLL%
    "%REGASM%" "%DLL%" /codebase
)

echo.
echo Done. Restart SolidWorks, then check Tools ^> Add-Ins for "SW To Blender".
pause
