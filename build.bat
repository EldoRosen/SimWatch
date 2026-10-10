@echo off
setlocal EnableExtensions

rem Re-launch in a window that stays open, so any error can always be read.
if /i "%~1"=="run" goto :main
start "SimWatch build" cmd /k ""%~f0" run"
exit /b

:main
cd /d "%~dp0"
set "LOG=%~dp0build-log.txt"

echo ==== SimWatch build ====
echo.

if not exist "%~dp0SimWatch.csproj" goto :noproject

where dotnet >nul 2>nul
if errorlevel 1 goto :nodotnet
for /f "delims=" %%V in ('dotnet --version') do set "DOTNET_VERSION=%%V"
echo .NET SDK: %DOTNET_VERSION%

rem ---- paths: saved from a previous run, or ask ----
set "TORCH_DIR="
set "GAME_DIR="
if exist "%~dp0build-paths.bat" call "%~dp0build-paths.bat"

if defined TORCH_DIR goto :havetorch
if exist "%~dp0TorchBinaries\Torch.dll" set "TORCH_DIR=%~dp0TorchBinaries"
if defined TORCH_DIR goto :havetorch
echo.
echo Enter the folder that contains Torch.dll, your Torch.Server folder.
echo Example: C:\Torch
set /p "TORCH_DIR=Torch folder: "
:havetorch
if not defined TORCH_DIR goto :nopaths
set "TORCH_DIR=%TORCH_DIR:"=%"
if "%TORCH_DIR:~-1%"=="\" set "TORCH_DIR=%TORCH_DIR:~0,-1%"

if defined GAME_DIR goto :havegame
if exist "%TORCH_DIR%\DedicatedServer64\Sandbox.Game.dll" set "GAME_DIR=%TORCH_DIR%\DedicatedServer64"
if defined GAME_DIR goto :havegame
if exist "%~dp0GameBinaries\Sandbox.Game.dll" set "GAME_DIR=%~dp0GameBinaries"
if defined GAME_DIR goto :havegame
echo.
echo Enter the folder that contains Sandbox.Game.dll, normally DedicatedServer64.
set /p "GAME_DIR=Game folder: "
:havegame
if not defined GAME_DIR goto :nopaths
set "GAME_DIR=%GAME_DIR:"=%"
if "%GAME_DIR:~-1%"=="\" set "GAME_DIR=%GAME_DIR:~0,-1%"

rem ---- check the DLLs are really there ----
set "MISSING="
for %%F in (Torch.dll Torch.API.dll NLog.dll) do if not exist "%TORCH_DIR%\%%F" call :missing "%TORCH_DIR%\%%F"
for %%F in (Sandbox.Game.dll Sandbox.Common.dll VRage.dll VRage.Game.dll VRage.Library.dll VRage.Math.dll SpaceEngineers.Game.dll SpaceEngineers.ObjectBuilders.dll) do if not exist "%GAME_DIR%\%%F" call :missing "%GAME_DIR%\%%F"
if defined MISSING goto :wrongpaths

rem remember the paths for next time
>"%~dp0build-paths.bat" echo set "TORCH_DIR=%TORCH_DIR%"
>>"%~dp0build-paths.bat" echo set "GAME_DIR=%GAME_DIR%"

echo.
echo Torch folder: %TORCH_DIR%
echo Game folder:  %GAME_DIR%
echo.
echo Building, please wait. The first build downloads a small package and can take a minute...

dotnet build "%~dp0SimWatch.csproj" -c Release "-p:TorchDir=%TORCH_DIR%" "-p:GameDir=%GAME_DIR%" >"%LOG%" 2>&1
set "BUILD_RESULT=%errorlevel%"
type "%LOG%"
if not "%BUILD_RESULT%"=="0" goto :buildfailed

set "ZIP=%~dp0bin\Release\net48\SimWatch.zip"
if not exist "%ZIP%" goto :nozip

copy /y "%ZIP%" "%~dp0SimWatch.zip" >nul
echo.
echo ==== SUCCESS ====
echo Plugin package: %~dp0SimWatch.zip
echo Copy it into your Torch Plugins folder, enable SimWatch in Torch, and restart.
echo.
if not exist "%TORCH_DIR%\Plugins" goto :end
choice /c YN /m "Copy it to the Torch Plugins folder now"
if errorlevel 2 goto :end
copy /y "%~dp0SimWatch.zip" "%TORCH_DIR%\Plugins\SimWatch.zip" >nul
echo Copied.
goto :end

:missing
echo Missing: %~1
set "MISSING=1"
exit /b

:noproject
echo ERROR: SimWatch.csproj was not found next to build.bat.
echo Extract the whole zip to a normal folder first, do not run this from inside the zip.
goto :end

:nodotnet
echo ERROR: the .NET SDK was not found.
echo Install the .NET SDK 6.0 or newer from https://dotnet.microsoft.com/download
echo then close this window and run build.bat again.
goto :end

:nopaths
echo ERROR: no folder was entered.
goto :end

:wrongpaths
echo.
echo ERROR: required DLLs are missing, so one of the folders is wrong.
echo Delete build-paths.bat and run build.bat again to re-enter them.
goto :end

:buildfailed
echo.
echo ==== BUILD FAILED ====
echo Error lines:
findstr /i /c:": error " "%LOG%"
echo.
echo The full output is saved in: %LOG%
echo Send me that file or the error lines above.
goto :end

:nozip
echo ERROR: the build succeeded but %ZIP% was not created.
echo See %LOG%
goto :end

:end
echo.
echo You can close this window.
exit /b
