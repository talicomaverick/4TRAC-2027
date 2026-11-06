@echo off
rem Runs the 4TRAC test suite and writes test-run.log next to the solution.
cd /d "%~dp0.."
echo started %DATE% %TIME% > test-run.log
dotnet --version >> test-run.log 2>&1
dotnet test tests\4TRAC.Tests\4TRAC.Tests.csproj -c Release >> test-run.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> test-run.log
echo finished %DATE% %TIME% >> test-run.log
