@echo off
rem Runs the flat-model validation matrix and writes validation-out\<suite>\report.md plus validation-run.log.
rem Usage:  tools\run-validation.cmd [suite] [trials] [hypotheses]
rem   suite       legacy | spin | steep | down | all   (default spin)
rem   trials      per cell                      (default 20, a quick first look)
rem   hypotheses  per trial                     (default 500, the calibrated minimum)
cd /d "%~dp0.."
set SUITE=%1
if "%SUITE%"=="" set SUITE=spin
set TRIALS=%2
if "%TRIALS%"=="" set TRIALS=20
set HYPS=%3
if "%HYPS%"=="" set HYPS=500
echo started %DATE% %TIME% suite=%SUITE% trials=%TRIALS% hypotheses=%HYPS% > validation-run.log
dotnet run --project src\4TRAC.Validation\4TRAC.Validation.csproj -c Release -- --mode flat --suite %SUITE% --trials %TRIALS% --hypotheses %HYPS% --out validation-out\%SUITE% >> validation-run.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> validation-run.log
echo finished %DATE% %TIME% >> validation-run.log
