@echo off
setlocal
call "%~dp0Stop-Stayline.cmd"
if errorlevel 1 exit /b 1
call "%~dp0Start-Stayline.cmd"
if errorlevel 1 exit /b 1
endlocal
exit /b 0
