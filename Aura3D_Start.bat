@echo off
title Aura3D Local Server Starter
echo ==================================================
echo  Aura3D AI 3D Model Generator - Local Server
echo ==================================================
cd /d "%~dp0"
echo [System] Starting Aura3D local server...
npm run dev
pause
