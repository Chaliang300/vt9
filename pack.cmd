@echo off
rem VT9Status packaging wrapper (double-click friendly).
rem Passes every argument straight to pack.ps1, e.g.  pack.cmd -Preview -Zip -Archive -Install
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack.ps1" %*
echo.
pause
