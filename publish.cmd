@echo off
rem Builds then publishes to GitHub (dist folder): the friends' loaders pick up the new version.
cd /d "%~dp0"
call build.cmd || exit /b 1
set /p VERSION=<dist\version.txt
git add -A || exit /b 1
git diff --cached --quiet && echo Nothing to publish: no changes. && exit /b 0
git commit -m "v%VERSION%" || exit /b 1
git push || exit /b 1
rem Tell the running game (on a local feed) to announce the published version to the other players.
for %%D in ("E:\Games\Steam\steamapps\common\Valheim\MLLoader\UserData\BryansGoodies" "E:\Games\Steam\steamapps\common\Valheim\UserData\BryansGoodies") do if exist %%~D type nul > "%%~D\announce"
echo.
echo Published: v%VERSION%
