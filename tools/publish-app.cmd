@echo off
rem Publishes the single-file self-contained WPF app to a NEW folder (the old dist is left untouched).
cd /d "%~dp0.."
echo started %DATE% %TIME% > publish-run.log
dotnet publish src\4TRAC.App\4TRAC.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist\4TRAC-app-win-x64-20261105 >> publish-run.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> publish-run.log
echo finished %DATE% %TIME% >> publish-run.log
