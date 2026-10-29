@echo off
rem Removes the Sonix IMX290 ASCOM camera driver. Run as administrator.
setlocal
set "DEST=%ProgramFiles%\Sonix290Camera"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\regasm.exe" /nologo /unregister "%DEST%\ASCOM.Sonix290.Camera.dll"
"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\regasm.exe" /nologo /unregister "%DEST%\ASCOM.Sonix290.Camera.dll"
rmdir /s /q "%DEST%"
echo Uninstalled.
pause
