@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Stop-Stayline.ps1"
if errorlevel 1 goto failed
endlocal
exit /b 0
:failed
pause
endlocal
exit /b 1
