@echo off
cd /d "%~dp0"
rem Double-cliquez ici pour installer Dynamo (voir LISEZMOI.txt).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer-Dynamo.ps1"
