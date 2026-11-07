@echo off
rem Moves stale builds and stray files into _to_delete\ (nothing is deleted). Review the folder, then delete it yourself.
rem Keeps: dist\4TRAC-app-win-x64-ui2 (the current build), data\profiles (unit tests may read it), rasters_COP30.tar.gz, tools\backup-*.
cd /d "%~dp0.."
set D=_to_delete
if not exist %D% mkdir %D%
if not exist %D%\dist mkdir %D%\dist
if not exist %D%\tests mkdir %D%\tests
if not exist %D%\data mkdir %D%\data
for %%F in ("4TRAC-app-win-x64" "4TRAC-app-win-x64-20261105" "4TRAC-app-win-x64-ui" "4TRAC-validation-win-x64" "4TRAC-validation-win-x64.zip" "publish-new" "publish-new2") do (
  if exist "dist\%%~F" move "dist\%%~F" "%D%\dist\" >nul
)
for %%F in ("New Text Document.txt" "return.txt" "permissions.txt" "23-10-2026 Build Prompt RGBS.md" "retrograde-ballistic-solver-planning-for-forensic-ballistics.json" "publish-run.log") do (
  if exist "%%~F" move "%%~F" "%D%\" >nul
)
if exist scenario-out move scenario-out "%D%\" >nul
if exist data\calibre_database_DRAFT.csv move data\calibre_database_DRAFT.csv "%D%\data\" >nul
if exist tests\4TRAC.Tests\ProbeTests.cs move tests\4TRAC.Tests\ProbeTests.cs "%D%\tests\" >nul
echo Moved. Now in %D%:
dir /b %D%
