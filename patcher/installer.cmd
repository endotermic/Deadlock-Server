@echo off
rem installer.cmd - compiles Patcher.cs with the C# compiler that ships with Windows and runs it.
rem
rem   installer.cmd                              patch DEADLOCK.EXE found next to this script,
rem                                              in the current folder, or at the GOG install path
rem   installer.cmd C:\Games\Deadlock2\DEADLOCK.EXE   patch that file
rem   installer.cmd [path] /status               only report which patches are applied
rem   installer.cmd [path] /restore              put DEADLOCK.EXE.orig back
rem
rem Needs the .NET Framework 4 that is part of Windows (csc.exe in %WINDIR%\Microsoft.NET).
rem Nothing is installed; the compiled Patcher.exe lives in %TEMP% and is deleted afterwards.
setlocal

set "SRC=%~dp0Patcher.cs"
set "OUT=%TEMP%\DeadlockPatcher-%RANDOM%.exe"

set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo C# compiler not found in %WINDIR%\Microsoft.NET\Framework\v4.0.30319 or Framework64.
    echo Enable ".NET Framework 4.x" in "Turn Windows features on or off" and run this again.
    set RC=1
    goto :end
)
if not exist "%SRC%" (
    echo Patcher.cs not found next to this script: %SRC%
    set RC=1
    goto :end
)

echo Compiling Patcher.cs ...
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
rem Keep the window open when started by double-click (no arguments given).
if "%~1"=="" pause
exit /b %RC%
