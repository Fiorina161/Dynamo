@echo off
cd /d "%~dp0"
rem Double-cliquez ici pour desinstaller Dynamo (voir LISEZMOI.txt).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Desinstaller-Dynamo.ps1"
