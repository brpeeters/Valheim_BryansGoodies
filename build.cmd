@echo off
rem Builds the core (BryansGoodies.Core.dll) then the loader (BryansGoodies.dll).
rem Each project copies its files: UserData\BryansGoodies, Mods, and dist\ (published files).
rem Version number: <ModVersion> in BryansGoodies.csproj. To publish to friends: publish.cmd.
cd /d "%~dp0"
dotnet build -c Release || exit /b 1
dotnet build Loader -c Release || exit /b 1
echo.
echo Built version:
type dist\version.txt
