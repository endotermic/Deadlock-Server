@echo off
rem launch.cmd - compiles Launcher.cs with the C# compiler that ships with Windows and starts
rem Deadlock II through it: the bug fixes (AI turn hang, crash on restore after minimise) and
rem the resolution from deadlock2.cfg (next to this script) are applied in memory at start-up.
rem
rem   launch.cmd                     use deadlock2.cfg next to this script
rem   launch.cmd my-settings.cfg     use another settings file
rem   launch.cmd /dry                read the config and report what would be patched, start nothing
rem
rem DEADLOCK.EXE on disk is never modified; the bytes are rewritten in the running process
rem only. Needs the .NET Framework 4 that is part of Windows.
setlocal

set "SRC=%~dp0Launcher.cs"
set "OUT=%TEMP%\Deadlock2Launcher-%RANDOM%.exe"

set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo C# compiler not found in %WINDIR%\Microsoft.NET\Framework\v4.0.30319 or Framework64.
    set RC=1
    goto :end
)

"%CSC%" /nologo /optimize+ /target:exe /platform:anycpu "/out:%OUT%" "%SRC%"
if errorlevel 1 (
    echo Compilation failed.
    set RC=1
    goto :end
)

"%OUT%" "/scriptdir:%~dp0." %*
set RC=%ERRORLEVEL%
del /q "%OUT%" >nul 2>&1

:end
if not "%RC%"=="0" pause
exit /b %RC%
