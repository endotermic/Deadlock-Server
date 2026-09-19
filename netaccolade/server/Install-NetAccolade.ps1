<#
.SYNOPSIS
  Makes Deadlock II's "NetAccolade" menu button start the local Node.js lobby.

.DESCRIPTION
  1. Compiles MClient.exe (the trampoline the game spawns) with the .NET Framework C# compiler.
  2. Writes the registry value the game reads:
       HKLM\SOFTWARE\Accolade\NetAccolade  PATH = <this folder>
     (32-bit view, i.e. HKLM\SOFTWARE\WOW6432Node\... on 64-bit Windows).  This step needs
     administrator rights; the script re-launches itself elevated for just that step.

  .\Install-NetAccolade.ps1            install (compile + registry)
  .\Install-NetAccolade.ps1 -Status    show what is installed
  .\Install-NetAccolade.ps1 -Uninstall remove the registry key (files are left alone)
#>
[CmdletBinding()]
param(
    [switch] $Status,
    [switch] $Uninstall,
    [switch] $AutostartOnly, # only (re)create the Startup shortcut and start the server, no compile/registry
    [switch] $RegistryOnly   # internal: used by the elevated re-launch
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$regPath = if ([Environment]::Is64BitOperatingSystem) { 'HKLM:\SOFTWARE\WOW6432Node\Accolade\NetAccolade' } else { 'HKLM:\SOFTWARE\Accolade\NetAccolade' }

function Test-Admin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }

$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'NetAccolade Lobby.lnk'

function Install-Autostart {
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut($startupLink)
    $lnk.TargetPath = Join-Path $env:WINDIR 'System32\wscript.exe'
    $lnk.Arguments = '"' + (Join-Path $here 'run-server.vbs') + '"'
    $lnk.WorkingDirectory = $here
    $lnk.Description = 'Deadlock II NetAccolade lobby server (hidden, http://127.0.0.1:7624)'
    $lnk.Save()
    Write-Host "autostart : $startupLink"
}

function Start-ServerNow {
    try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 http://127.0.0.1:7624/api/ping | Out-Null; Write-Host "server    : already running"; return } catch { }
    Start-Process -FilePath (Join-Path $env:WINDIR 'System32\wscript.exe') -ArgumentList ('"' + (Join-Path $here 'run-server.vbs') + '"') -WorkingDirectory $here
    Start-Sleep -Seconds 2
    try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 http://127.0.0.1:7624/api/ping | Out-Null; Write-Host "server    : started (hidden), http://127.0.0.1:7624" } catch { Write-Warning "server did not answer on port 7624; see $here\server.log" }
}

function Show-Status {
    $exe = Join-Path $here 'MClient.exe'
    Write-Host ("autostart   : " + $(if (Test-Path $startupLink) { $startupLink } else { 'not installed' }))
    $up = $false; try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 http://127.0.0.1:7624/api/ping | Out-Null; $up = $true } catch { }
    Write-Host ("server      : " + $(if ($up) { 'running on http://127.0.0.1:7624' } else { 'not running' }))
    Write-Host ("MClient.exe : " + $(if (Test-Path $exe) { "present ($((Get-Item $exe).Length) bytes)" } else { 'missing (run install)' }))
    $node = Get-Command node -ErrorAction SilentlyContinue
    Write-Host ("node.exe    : " + $(if ($node) { "$($node.Source) ($(& node --version))" } else { 'NOT FOUND on PATH' }))
    $v = Get-ItemProperty -Path $regPath -Name PATH -ErrorAction SilentlyContinue
    Write-Host ("registry    : " + $(if ($v) { "$regPath PATH=$($v.PATH)" } else { "$regPath not set" }))
    if ($v -and $v.PATH -ne $here) { Write-Warning "registry PATH does not point at this folder ($here)" }
}

function Build-Client {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }
    $src = Join-Path $here 'MClient.cs'; $out = Join-Path $here 'MClient.exe'
    & $csc /nologo /target:winexe /optimize+ "/out:$out" /r:System.Windows.Forms.dll $src
    if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
    Write-Host "built $out"
}

function Set-Registry {
    New-Item -Path $regPath -Force | Out-Null
    Set-ItemProperty -Path $regPath -Name PATH -Value $here -Type String
    Write-Host "registry set: $regPath PATH=$here"
}

function Invoke-Elevated([string] $extraArgs) {
    $ps = (Get-Process -Id $PID).Path
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" $extraArgs"
    Write-Host "Requesting administrator rights for the registry step..."
    $p = Start-Process -FilePath $ps -ArgumentList $argList -Verb RunAs -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "elevated step failed with exit code $($p.ExitCode)" }
}

if ($Status) { Show-Status; return }

if ($Uninstall) {
    if ($RegistryOnly -or (Test-Admin)) { Remove-Item -Path $regPath -Recurse -Force -ErrorAction SilentlyContinue; Write-Host "removed $regPath" }
    else { Invoke-Elevated '-Uninstall -RegistryOnly' }
    if (-not $RegistryOnly) {
        Remove-Item $startupLink -Force -ErrorAction SilentlyContinue; Write-Host "removed autostart shortcut"
        Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.CommandLine -match 'server\.js' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Write-Host "stopped server pid $($_.ProcessId)" }
    }
    return
}

if ($RegistryOnly) { Set-Registry; return }
if ($AutostartOnly) { Install-Autostart; Start-ServerNow; Show-Status; return }

Build-Client
if (Test-Admin) { Set-Registry } else { Invoke-Elevated '-RegistryOnly' }
Install-Autostart
Start-ServerNow
Show-Status
Write-Host ""
Write-Host "Done. The lobby server now runs hidden in the background and starts again at every logon."
Write-Host "In Deadlock II choose 'NetAccolade' from the main menu, or join TCP/IP games directly: the fake sessions"
Write-Host "from server.json (fakeGames) are always advertised on this PC."
