@echo off
setlocal

set "PUBLISH_DIR=%~dp0artifacts\publish\RightMenuCheck"
set "APP_PATH=%PUBLISH_DIR%\RightMenuCheck.App.exe"

if not exist "%APP_PATH%" (
    echo RightMenuCheck has not been published yet.
    echo Run this command from the repository root:
    echo pwsh -NoLogo -NoProfile -File .\scripts\publish.ps1 -Version 0.1.0
    pause
    exit /b 1
)

start "" /D "%PUBLISH_DIR%" "%APP_PATH%" %*
exit /b %ERRORLEVEL%
