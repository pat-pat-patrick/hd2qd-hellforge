@echo off
rem Tests, then builds release\HD2QD.HellForge.dll, a plugin of HD2 Mod Quick Deploy (added on
rem its Plugins page). Needs the .NET SDK (8 or later): dotnet on PATH, or set DOTNET to its
rem dotnet.exe.
cd /d "%~dp0"
if "%DOTNET%"=="" set DOTNET=dotnet
"%DOTNET%" test tests\HellForge.Tests.csproj || exit /b 1
"%DOTNET%" build src\HD2QD.HellForge.csproj -c Release || exit /b 1
if not exist release mkdir release
copy /y "src\bin\Release\net48\HD2QD.HellForge.dll" "release\HD2QD.HellForge.dll" >nul || exit /b 1
rem the maintainer's signature: Quick Deploy then shows this build as an official plugin, whatever
rem its version. Where the signing tool or its key is not (someone else's build), it says so and
rem leaves the file unsigned
if exist "%~dp0..\hd2-mod-cli\tools\sign_plugin.py" python "%~dp0..\hd2-mod-cli\tools\sign_plugin.py" "release\HD2QD.HellForge.dll" || exit /b 1
echo.
echo Built: release\HD2QD.HellForge.dll
