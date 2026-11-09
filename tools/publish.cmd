@echo off
rem Doppio clic: publish.ps1 con la finestra che resta aperta per leggere esito ed errori.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
if errorlevel 1 (echo. & echo PUBLISH FALLITO) else (echo. & echo Publish completato)
pause
