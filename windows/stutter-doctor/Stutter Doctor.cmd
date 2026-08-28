@echo off
REM Builds the latest source, then launches Stutter Doctor elevated so PresentMon can open an
REM ETW session for frametime capture. Everything except frametimes works unelevated.
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%bin\Release\net8.0-windows\StutterDoctor.exe"

REM A running instance holds a lock on the exe, which would make the build fail with a confusing
REM MSB3027. Detect it and bail out cleanly instead.
tasklist /FI "IMAGENAME eq StutterDoctor.exe" 2>nul | find /I "StutterDoctor.exe" >nul
if not errorlevel 1 (
  echo.
  echo Stutter Doctor is already running - look for its window, or check the taskbar.
  echo.
  echo If you want to restart it ^(e.g. to pick up code changes^), close it first.
  echo Note: closing it discards an in-progress session, so Export the report first.
  echo.
  pause
  exit /b 0
)

echo Building latest source...
pushd "%ROOT%"
dotnet build -c Release -v minimal --nologo
set "BUILD_RC=%ERRORLEVEL%"
popd

if not "%BUILD_RC%"=="0" (
  echo.
  echo Build failed. Check that the .NET 8 SDK is present:  dotnet --list-sdks
  echo.
  pause
  exit /b 1
)

if not exist "%EXE%" (
  echo.
  echo Build reported success but %EXE% is missing.
  echo.
  pause
  exit /b 1
)

echo Launching elevated - accept the UAC prompt...
if "%~1"=="" (
  powershell -NoProfile -Command "Start-Process -FilePath '%EXE%' -Verb RunAs"
) else (
  powershell -NoProfile -Command "Start-Process -FilePath '%EXE%' -Verb RunAs -ArgumentList '%*'"
)
endlocal
