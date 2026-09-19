<#
.SYNOPSIS
  Starts Deadlock II the way NetAccolade did: writes deadlock.ini and runs DEADLOCK.EXE -ms.

.DESCRIPTION
  Host:  .\Start-OnlineWar.ps1 -HostGame [-Players 2] [-Name "Online War"] [-UserName Nika]
  Join:  .\Start-OnlineWar.ps1 -Join 10.147.17.5 [-UserName Nika]
  Custom file: .\Start-OnlineWar.ps1 -IniFile .\my-launch.ini

  The script copies the chosen template to <game dir>\deadlock.ini, backs up any existing
  deadlock.ini, launches the game, waits for it to exit and then removes the file again.
  See NetAccolade-Investigation.md for the file format and the network requirements.
#>
[CmdletBinding(DefaultParameterSetName = 'Host')]
param(
    [Parameter(ParameterSetName = 'Host')] [switch] $HostGame,
    [Parameter(ParameterSetName = 'Join', Mandatory)] [string] $Join,
    [Parameter(ParameterSetName = 'File', Mandatory)] [string] $IniFile,
    [ValidateRange(2, 7)] [int] $Players = 2,
    [string] $Name = 'Online War',
    [string] $UserName = $env:USERNAME,
    [string] $GameDir = (Split-Path -Parent $PSScriptRoot)
)

$exe = Join-Path $GameDir 'DEADLOCK.EXE'
if (-not (Test-Path $exe)) { throw "DEADLOCK.EXE not found in $GameDir" }
if ($UserName.Length -gt 31) { $UserName = $UserName.Substring(0, 31) }
if ($Name.Length -gt 31) { $Name = $Name.Substring(0, 31) }

switch ($PSCmdlet.ParameterSetName) {
    'Host' { $template = Join-Path $PSScriptRoot 'host.ini' }
    'Join' { $template = Join-Path $PSScriptRoot 'join.ini' }
    'File' { $template = $IniFile }
}
$text = Get-Content -Raw -Path $template

if ($PSCmdlet.ParameterSetName -ne 'File') {
    $text = $text -replace '(?m)^Name=.*$', "Name=$Name"
    $text = $text -replace '(?m)^User Name=.*$', "User Name=$UserName"
    $text = $text -replace '(?m)^(\[Startup\][\s\S]*?)^Players=.*$', "`${1}Players=$Players"
    if ($PSCmdlet.ParameterSetName -eq 'Join') {
        $text = $text -replace '(?m)^Master Address=.*$', "Master Address=$Join"
    }
}

$target = Join-Path $GameDir 'deadlock.ini'
$backup = $null
if (Test-Path $target) {
    $backup = "$target.bak"
    Copy-Item $target $backup -Force
    Write-Host "Existing deadlock.ini backed up to $backup"
}
Set-Content -Path $target -Value $text -Encoding ASCII
Write-Host "Launch file written; starting DEADLOCK.EXE -ms ($($PSCmdlet.ParameterSetName))"

try {
    $proc = Start-Process -FilePath $exe -ArgumentList '-ms' -WorkingDirectory $GameDir -PassThru
    $proc.WaitForExit()
    Write-Host "Game exited with code $($proc.ExitCode)"
}
finally {
    Remove-Item $target -Force -ErrorAction SilentlyContinue
    if ($backup) { Move-Item $backup $target -Force }
}
