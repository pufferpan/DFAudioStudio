@echo off
chcp 65001 >nul
title DFAudioStudio - Fetch Whisper Model
echo ============================================
echo  DFAudioStudio Whisper model fetcher
echo  (openaipublic.azureedge.net + ghproxy + USTC PyPI)
echo ============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\fetch-whisper-model.ps1"
echo.
echo Done. Model files are in: %LOCALAPPDATA%\DFAudioStudio\models
echo Restart DFAudioStudio and it will pick the model up automatically.
pause
