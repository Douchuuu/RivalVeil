@echo off
chcp 65001 >nul
title RivalVeil Server Launcher

:: ============================================
:: RivalVeil Server Launcher v2.4
:: ============================================

setlocal EnableDelayedExpansion

:: Colors for output
set "GREEN=[92m"
set "YELLOW=[93m"
set "RED=[91m"
set "RESET=[0m"

echo %GREEN%========================================%RESET%
echo %GREEN%  RivalVeil Server Launcher v2.4%RESET%
echo %GREEN%========================================%RESET%
echo.

:: ============================================
:: Pre-flight checks
:: ============================================

:: Check Python
echo %YELLOW%[1/5] Checking Python...%RESET%
python --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo %RED%ERROR: Python not found!%RESET%
    pause
    exit /b 1
)
echo %GREEN%  Python OK%RESET%

:: Check uvicorn
echo %YELLOW%[2/5] Checking uvicorn...%RESET%
python -m uvicorn --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo %RED%ERROR: uvicorn not found!%RESET%
    pause
    exit /b 1
)
echo %GREEN%  uvicorn OK%RESET%

:: Check cloudflared
echo %YELLOW%[3/5] Checking cloudflared...%RESET%
cloudflared --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo %RED%ERROR: cloudflared not found!%RESET%
    pause
    exit /b 1
)
echo %GREEN%  cloudflared OK%RESET%

:: Check .env file
echo %YELLOW%[4/5] Checking .env file...%RESET%
if not exist ".env" (
    echo %RED%ERROR: .env file not found!%RESET%
    echo %YELLOW%Please copy .env.example to .env and configure it:%RESET%
    pause
    exit /b 1
)
echo %GREEN%  .env OK%RESET%

:: ============================================
:: Load environment variables from .env
:: ============================================
echo %YELLOW%[5/5] Loading configuration from .env...%RESET%

:: Use Python to load .env and show status
python -c "from dotenv import load_dotenv; load_dotenv(); import os; print('  DB_HOST:', os.getenv('DB_HOST', 'localhost')); print('  DB_USER:', os.getenv('DB_USER', 'root')); print('  DB_PASSWORD:', '*' * len(os.getenv('DB_PASSWORD', '')) if os.getenv('DB_PASSWORD') else 'NOT SET'); print('  DB_NAME:', os.getenv('DB_NAME', 'rivalveil')); print('  GIST_TOKEN:', 'SET' if os.getenv('GIST_TOKEN') else 'NOT SET')" 2>nul

if %ERRORLEVEL% NEQ 0 (
    echo %RED%ERROR: Failed to load .env!%RESET%
    pause
    exit /b 1
)

echo %GREEN%Configuration loaded!%RESET%
echo.

:: ============================================
:: Check database connection
:: ============================================
echo %YELLOW%Checking database connection...%RESET%
python test_db.py >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo %RED%ERROR: Cannot connect to database!%RESET%
    echo %YELLOW%Please check your DB_PASSWORD in .env file%RESET%
    echo.
    echo Run test to see details:
    echo   python test_db.py
    echo.
    pause
    exit /b 1
)
echo %GREEN%Database connection OK!%RESET%
echo.

:: Set directories
set "FASTAPI_DIR=%CD%"

:: Create logs directory
if not exist "%FASTAPI_DIR%\logs" mkdir "%FASTAPI_DIR%\logs"

:: ============================================
:: Start FastAPI Server
:: ============================================
echo %GREEN%========================================%RESET%
echo %GREEN%  Starting Services%RESET%
echo %GREEN%========================================%RESET%
echo.
echo %GREEN%[1/2] Starting FastAPI server...%RESET%
echo   URL: http://localhost:8000
echo   Docs: http://localhost:8000/docs
echo.

start "FastAPI Server" cmd /k "cd /d "%FASTAPI_DIR%" && echo Starting FastAPI... && python -m uvicorn main:app --host 0.0.0.0 --port 8000"

:: Wait for FastAPI to start
echo %YELLOW%Waiting for FastAPI to start (5 sec)...%RESET%
timeout /t 5 /nobreak >nul

:: Test if FastAPI is running
curl -s http://localhost:8000/health >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo %YELLOW%Warning: FastAPI may not be ready yet%RESET%
) else (
    echo %GREEN%FastAPI is running!%RESET%
)
echo.

:: ============================================
:: Start Cloudflare Tunnel + URL Updater
:: ============================================
echo %GREEN%[2/2] Starting Cloudflare tunnel + URL updater...%RESET%
echo %YELLOW%IMPORTANT: Wait for URL to appear in this window!%RESET%
echo %YELLOW%Do NOT close it until you see: [url_updater] Gist updated%RESET%
echo.

start "Cloudflare Tunnel + URL Updater" cmd /k "cd /d "%FASTAPI_DIR%" && python url_updater.py"

:: Wait for tunnel
echo %YELLOW%Waiting for Cloudflare tunnel (10 sec)...%RESET%
timeout /t 10 /nobreak >nul

:: ============================================
:: Done
:: ============================================
echo.
echo %GREEN%========================================%RESET%
echo %GREEN%  Core services started!%RESET%
echo %GREEN%========================================%RESET%
echo.
echo %YELLOW%Services running:%RESET%
echo   - FastAPI:     http://localhost:8000
echo   - Cloudflare:  (see "Cloudflare Tunnel" window)
echo   - Gist URL:    (see "Cloudflare Tunnel" window)
echo.
echo %YELLOW%IMPORTANT:%RESET%
echo   1. Check "Cloudflare Tunnel" window for the public URL
echo   2. Wait for: [url_updater] Gist updated: https://xxx.trycloudflare.com
echo   3. Then Unity clients can connect!
echo.
echo %YELLOW%Press any key to stop all services...%RESET%
pause >nul

:: Cleanup
echo.
echo %YELLOW%Stopping all services...%RESET%
taskkill /FI "WINDOWTITLE eq FastAPI Server" /F >nul 2>&1
taskkill /FI "WINDOWTITLE eq Cloudflare Tunnel*" /F >nul 2>&1
taskkill /IM cloudflared.exe /F >nul 2>&1

echo %GREEN%All services stopped.%RESET%
timeout /t 2 /nobreak >nul
