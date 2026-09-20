@echo off
setlocal
set "METER_EXE=%~dp0..\..\..\task-token-meter.exe"
if not exist "%METER_EXE%" exit /b 0
"%METER_EXE%" hook run --provider claude --managed-by task-token-meter-v1
exit /b 0
