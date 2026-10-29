@echo off
rem Builds the driver and the test tool on Windows (.NET SDK 8 or later) and assembles dist\Sonix290Camera.
setlocal
cd /d "%~dp0"
dotnet build -c Release SxTest\SxTest.csproj || exit /b 1
set "OUT=dist\Sonix290Camera"
if exist dist rmdir /s /q dist
mkdir "%OUT%"
set "BIN=SxTest\bin\Release\net48"
for %%F in (ASCOM.Sonix290.Camera.dll ASCOM.Sonix290.Camera.pdb SxTest.exe SxTest.exe.config) do copy /y "%BIN%\%%F" "%OUT%\" >nul || exit /b 1
copy /y package\* "%OUT%\" >nul
copy /y ..\README.md "%OUT%\" >nul
copy /y ..\LICENSE "%OUT%\LICENSE.txt" >nul
powershell -NoProfile -Command "Compress-Archive -Force -Path '%OUT%' -DestinationPath 'dist\Sonix290Camera.zip'"
rem Setup .exe with Inno Setup 6 (winget install JRSoftware.InnoSetup), if it is installed.
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" (
  "%ISCC%" /Q installer\Sonix290Camera.iss || exit /b 1
) else echo Inno Setup 6 not found: no setup .exe built.
echo Built %OUT% and dist\Sonix290Camera.zip. Install: dist\Sonix290Camera-*-Setup.exe, or %OUT%\install.cmd as administrator.
