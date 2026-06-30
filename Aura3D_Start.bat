@echo off
title Aura3D Local Server Starter
echo ==================================================
echo  Aura3D AI 3D Model Generator - Local Server
echo ==================================================
cd /d "%~dp0"
echo [System] Starting Aura3D local server...
echo [System] Opening browser at http://localhost:8080 ...
start http://localhost:8080
node server.js
pause
