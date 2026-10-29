@echo off
rem Installs the Sonix IMX290 ASCOM camera driver. Run as administrator.
setlocal
net session >nul 2>&1
if errorlevel 1 (
  echo Run this as administrator: right-click install.cmd, "Run as administrator".
  pause
  exit /b 1
)
set "DEST=%ProgramFiles%\Sonix290Camera"
echo Copying to %DEST%
if not exist "%DEST%" mkdir "%DEST%"
copy /y "%~dp0ASCOM.Sonix290.Camera.dll" "%DEST%\" >nul || goto :fail
copy /y "%~dp0ASCOM.Sonix290.Camera.pdb" "%DEST%\" >nul
copy /y "%~dp0SxTest.exe" "%DEST%\" >nul
copy /y "%~dp0SxTest.exe.config" "%DEST%\" >nul
copy /y "%~dp0comtest.vbs" "%DEST%\" >nul
rem Files from a downloaded zip carry a "blocked" mark that stops .NET from loading them.
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%DEST%' | Unblock-File"
echo Registering for 64-bit clients (N.I.N.A., SharpCap 64, ConformU)
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\regasm.exe" /nologo /codebase "%DEST%\ASCOM.Sonix290.Camera.dll" || goto :fail
echo Registering for 32-bit clients (PHD2)
"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\regasm.exe" /nologo /codebase "%DEST%\ASCOM.Sonix290.Camera.dll" || goto :fail
echo.
echo Installed. It appears in the ASCOM Chooser as "Sonix IMX290".
pause
exit /b 0
:fail
echo INSTALL FAILED
pause
exit /b 1
