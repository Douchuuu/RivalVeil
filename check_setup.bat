@echo off
chcp 65001 >nul
title RivalVeil Setup Check

:: Colors
set "GREEN=[92m"
set "YELLOW=[93m"
set "RED=[91m"
set "RESET=[0m"

echo %GREEN%========================================%RESET%
echo %GREEN%  RivalVeil Setup Checker%RESET%
echo %GREEN%========================================%RESET%
echo.

set "ALL_OK=1"

:: Check Python
echo [1/6] Checking Python...
python --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo %RED%  X Python not found!%RESET%
    echo %YELLOW%    Install from: https://python.org%RESET%
    set "ALL_OK=0"
) else (
    for /f "tokens=*" %%a in ('python --version') do echo %GREEN%  + %%a%RESET%
)

:: Check pip
echo [2/6] Checking pip...
pip --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo %RED%  X pip not found!%RESET%
    set "ALL_OK=0"
) else (
    for /f "tokens=*" %%a in ('pip --version') do echo %GREEN%  + %%a%RESET%
)

:: Check uvicorn
echo [3/6] Checking uvicorn...
uvicorn --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo %RED%  X uvicorn not found!%RESET%
    echo %YELLOW%    Run: pip install uvicorn[standard]%RESET%
    set "ALL_OK=0"
) else (
    for /f "tokens=*" %%a in ('uvicorn --version') do echo %GREEN%  + uvicorn %%a%RESET%
)

:: Check cloudflared
echo [4/6] Checking cloudflared...
cloudflared --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo %RED%  X cloudflared not found!%RESET%
    echo %YELLOW%    Install from: https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/install-and-setup/installation/%RESET%
    set "ALL_OK=0"
) else (
    for /f "tokens=*" %%a in ('cloudflared --version') do echo %GREEN%  + %%a%RESET%
)

:: Check .env
echo [5/6] Checking .env file...
if not exist ".env" (
    echo %RED%  X .env not found!%RESET%
    echo %YELLOW%    Run: copy .env.example .env%RESET%
    set "ALL_OK=0"
) else (
    echo %GREEN%  + .env exists%RESET%
)

:: Check required Python packages
echo [6/6] Checking Python packages...
python -c "import fastapi" >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo %RED%  X fastapi not installed!%RESET%
    echo %YELLOW%    Run: pip install -r requirements.txt%RESET%
    set "ALL_OK=0"
) else (
    echo %GREEN%  + fastapi installed%RESET%
)

python -c "import mysql" >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo %RED%  X mysql-connector not installed!%RESET%
    echo %YELLOW%    Run: pip install -r requirements.txt%RESET%
    set "ALL_OK=0"
) else (
    echo %GREEN%  + mysql-connector installed%RESET%
)

python -c "import bcrypt" >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo %RED%  X bcrypt not installed!%RESET%
    echo %YELLOW%    Run: pip install -r requirements.txt%RESET%
    set "ALL_OK=0"
) else (
    echo %GREEN%  + bcrypt installed%RESET%
)

echo.
echo %GREEN%========================================%RESET%

if %ALL_OK%==1 (
    echo %GREEN%  All checks passed!%RESET%
    echo %GREEN%  You can run: start_all.bat%RESET%
) else (
    echo %RED%  Some checks failed!%RESET%
    echo %YELLOW%  Please fix the issues above.%RESET%
)

echo %GREEN%========================================%RESET%
echo.
pause
