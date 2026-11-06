@echo off
rem Runs the tests, then publishes the single-file app to dist\4TRAC-app-win-x64-ui2. Logs: build-run.log
cd /d "%~dp0.."
echo started %DATE% %TIME% > build-run.log
dotnet test tests\4TRAC.Tests\4TRAC.Tests.csproj -c Release >> build-run.log 2>&1
echo TEST_EXITCODE=%ERRORLEVEL% >> build-run.log
dotnet publish src\4TRAC.App\4TRAC.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist\4TRAC-app-win-x64-ui2 >> build-run.log 2>&1
echo PUBLISH_EXITCODE=%ERRORLEVEL% >> build-run.log
echo finished %DATE% %TIME% >> build-run.log
