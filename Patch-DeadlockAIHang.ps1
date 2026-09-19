<#
.SYNOPSIS
  Fixes the "AI turn never ends" hang in Deadlock II: Shrine Wars v1.20 by patching
  three bytes in DEADLOCK.EXE. Runs as a small window by default, or from the command line.

.DESCRIPTION
  SYMPTOM
    During an AI player's turn the game stops responding and one CPU core sits at 100%.
    Loading the autosave and ending the turn again reproduces it every time.

  ROOT CAUSE (found with WinDbg attached to the hung process)
    Each AI race keeps a linked list of "jobs" (build this, research that, get resource X).
    Once per turn the scheduler at VA 0x00405AAC clears every job's "done this pass" flag
    and then repeatedly runs the highest-priority job that is not yet done, until none are
    left. Handlers may create new jobs while the pass runs; those run in the same pass.

    The "request resource" routine at VA 0x00408B58 is called when a building the AI wants
    needs resources the race cannot produce. If a "get resource" job for that resource
    already exists, the routine re-arms it: it raises the job's priority to 5001 and, at
    VA 0x00408BAB, clears the job's done flag with

        89 46 0C    mov dword ptr [esi+0Ch], eax     ; eax = 0

    That store is the only place in the job system that marks an already-processed job as
    not done. When the prerequisite chain is circular (building A needs resource R, R's
    producer building needs A), the pass cycles forever:

        get resource R  ->  spawn "build A"  ->  "build A" re-arms "get resource R"  -> ...

  THE FIX
    Replace the 3-byte store with three NOPs (90 90 90). The re-armed job keeps its boosted
    priority and its updated resource amount, but runs on the next turn instead of again in
    the same pass. The AI therefore retries an impossible build once per turn (harmless)
    instead of hanging the game.

  FILE LOCATION
    VA 0x00408BAB lies in the CODE section (virtual address 0x00401000, raw file offset
    0x800), so the file offset is 0x00408BAB - 0x00401000 + 0x800 = 0x83AB.

  SAFETY
    The script refuses to touch the file unless the size matches the GOG v1.20 build and the
    8 bytes of code just before the patch site match the expected instructions. The first
    run writes a backup next to the EXE as DEADLOCK.EXE.orig, and -Restore copies it back.

.PARAMETER ExePath
  Path to DEADLOCK.EXE. Defaults to the file next to this script.

.PARAMETER Restore
  Command-line mode: copy DEADLOCK.EXE.orig back over DEADLOCK.EXE.

.PARAMETER NoGui
  Command-line mode: apply the patch and print results to the console instead of
  opening the window. -Restore implies -NoGui.

.EXAMPLE
  .\Patch-DeadlockAIHang.ps1
  Opens the window. Shows whether the EXE is patched and offers Patch / Restore buttons.

.EXAMPLE
  .\Patch-DeadlockAIHang.ps1 -NoGui
  Applies the patch from the command line.

.EXAMPLE
  .\Patch-DeadlockAIHang.ps1 -Restore
  Puts the original EXE back.
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [switch]$Restore,
    [switch]$NoGui
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Locate the EXE. $PSScriptRoot can be empty when the script is started with a
# relative path from some hosts, so fall back to the invocation path.
# ---------------------------------------------------------------------------
if (-not $ExePath) {
    $root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $ExePath = Join-Path $root 'DEADLOCK.EXE'
}

# ---------------------------------------------------------------------------
# Patch definition. Everything the checks and the write need is in one place.
# ---------------------------------------------------------------------------
$script:Patch = [pscustomobject]@{
    Name         = 'AI job scheduler infinite loop'
    VirtualAddr  = 0x00408BAB
    Offset       = 0x83AB                                   # file offset of the store
    Context      = [byte[]](0x8B,0xF0,0x85,0xF6,0x74,0x32,0x33,0xC0)
                   # the 8 bytes immediately before the patch site:
                   #   8B F0        mov  esi, eax        ; esi = existing job (or 0)
                   #   85 F6        test esi, esi
                   #   74 32        je   +0x32           ; no existing job -> create new
                   #   33 C0        xor  eax, eax        ; eax = 0
    Original     = [byte[]](0x89,0x46,0x0C)                 # mov [esi+0Ch], eax  (done = 0)
    Patched      = [byte[]](0x90,0x90,0x90)                 # nop nop nop
    ExpectedSize = 1738752                                  # GOG DEADLOCK.EXE v1.20
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Test-BytesEqual([byte[]]$a, [byte[]]$b) {
    if ($a.Length -ne $b.Length) { return $false }
    for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { return $false } }
    return $true
}

function Format-Hex([byte[]]$bytes) {
    ($bytes | ForEach-Object { $_.ToString('X2') }) -join ' '
}

function Get-BackupPath([string]$Path) { "$Path.orig" }

# Returns $true if a DEADLOCK.EXE process is running from exactly this file.
# Windows locks a running image, so a write would fail anyway; this gives a clear message.
function Test-GameRunning([string]$Path) {
    $target = (Resolve-Path -LiteralPath $Path).Path
    $procs = Get-Process -Name DEADLOCK -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ($_.Path -ieq $target) }
    return [bool]$procs
}

# Inspects the file and reports one of:
#   Missing     - file does not exist
#   WrongSize   - not the v1.20 build this patch was made for
#   WrongCode   - size matches but the code before the patch site differs
#   Original    - unpatched, safe to patch
#   Patched     - already patched
#   Unknown     - context matches but the 3 bytes are neither original nor patched
function Get-PatchState([string]$Path) {
    $result = [pscustomobject]@{ State = ''; Detail = ''; HasBackup = $false }
    if (-not (Test-Path -LiteralPath $Path)) {
        $result.State = 'Missing'; $result.Detail = "File not found: $Path"; return $result
    }
    $result.HasBackup = Test-Path -LiteralPath (Get-BackupPath $Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ne $Patch.ExpectedSize) {
        $result.State = 'WrongSize'
        $result.Detail = "File is $($bytes.Length) bytes, expected $($Patch.ExpectedSize) (GOG v1.20 build)."
        return $result
    }
    $ctx = $bytes[($Patch.Offset - $Patch.Context.Length)..($Patch.Offset - 1)]
    if (-not (Test-BytesEqual $ctx $Patch.Context)) {
        $result.State = 'WrongCode'
        $result.Detail = ("Code before offset 0x{0:X} does not match: {1}" -f $Patch.Offset, (Format-Hex $ctx))
        return $result
    }
    $cur = $bytes[$Patch.Offset..($Patch.Offset + $Patch.Original.Length - 1)]
    if (Test-BytesEqual $cur $Patch.Patched) {
        $result.State = 'Patched'
        $result.Detail = ("Bytes at 0x{0:X} are {1} (patched)." -f $Patch.Offset, (Format-Hex $cur))
    } elseif (Test-BytesEqual $cur $Patch.Original) {
        $result.State = 'Original'
        $result.Detail = ("Bytes at 0x{0:X} are {1} (original, unpatched)." -f $Patch.Offset, (Format-Hex $cur))
    } else {
        $result.State = 'Unknown'
        $result.Detail = ("Bytes at 0x{0:X} are {1}: neither original nor patched." -f $Patch.Offset, (Format-Hex $cur))
    }
    return $result
}

# Applies the patch. Writes a backup first (never overwrites an existing backup).
# Throws on any precondition failure; returns a message on success.
function Invoke-Patch([string]$Path) {
    if (Test-GameRunning $Path) { throw "DEADLOCK.EXE is running from $Path. Close the game first." }
    $state = Get-PatchState $Path
    switch ($state.State) {
        'Patched'  { return "Already patched. Nothing to do." }
        'Original' { }   # fall through to the write below
        default    { throw "Refusing to patch. $($state.Detail)" }
    }

    $backup = Get-BackupPath $Path
    $log = @()
    if (-not (Test-Path -LiteralPath $backup)) {
        Copy-Item -LiteralPath $Path -Destination $backup
        $log += "Backup written to $backup"
    } else {
        $log += "Backup already exists at $backup (kept)."
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    for ($i = 0; $i -lt $Patch.Patched.Length; $i++) { $bytes[$Patch.Offset + $i] = $Patch.Patched[$i] }
    [System.IO.File]::WriteAllBytes($Path, $bytes)

    # Read back and confirm the bytes landed.
    $verify = [System.IO.File]::ReadAllBytes($Path)[$Patch.Offset..($Patch.Offset + $Patch.Patched.Length - 1)]
    if (-not (Test-BytesEqual $verify $Patch.Patched)) { throw "Verification failed after write." }

    $log += ("Patched {0} at file offset 0x{1:X} (VA 0x{2:X8}): {3} -> {4}" -f
        $Path, $Patch.Offset, $Patch.VirtualAddr, (Format-Hex $Patch.Original), (Format-Hex $Patch.Patched))
    return ($log -join "`r`n")
}

# Copies the backup back over the EXE.
function Invoke-Restore([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "File not found: $Path" }
    if (Test-GameRunning $Path) { throw "DEADLOCK.EXE is running from $Path. Close the game first." }
    $backup = Get-BackupPath $Path
    if (-not (Test-Path -LiteralPath $backup)) { throw "No backup found at $backup" }
    Copy-Item -LiteralPath $backup -Destination $Path -Force
    return "Restored original from $backup"
}

# ---------------------------------------------------------------------------
# Command-line mode
# ---------------------------------------------------------------------------
if ($Restore -or $NoGui) {
    if ($Restore) { Write-Host (Invoke-Restore $ExePath) }
    else          { Write-Host (Invoke-Patch   $ExePath) }
    return
}

# ---------------------------------------------------------------------------
# GUI mode (Windows Forms)
# ---------------------------------------------------------------------------
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$form = New-Object System.Windows.Forms.Form
$form.Text            = 'Deadlock II - AI hang patch'
$form.StartPosition   = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox     = $false
$form.MinimizeBox     = $false
$form.ClientSize      = New-Object System.Drawing.Size(560, 420)
$form.Font            = New-Object System.Drawing.Font('Segoe UI', 9)

# --- description -----------------------------------------------------------
$lblInfo = New-Object System.Windows.Forms.Label
$lblInfo.Location = New-Object System.Drawing.Point(12, 10)
$lblInfo.Size     = New-Object System.Drawing.Size(536, 70)
$lblInfo.Text     = "Fixes the hang where an AI turn never finishes and the game sits at 100% CPU.`r`n" +
                    "Cause: the AI job scheduler lets 'get resource' and 'build prerequisite' jobs re-arm " +
                    "each other endlessly within one turn. The patch changes 3 bytes in DEADLOCK.EXE " +
                    "(file offset 0x83AB) so a re-armed job waits for the next turn. A backup " +
                    "(DEADLOCK.EXE.orig) is written before the first patch."

# --- EXE path row ------------------------------------------------------------
$lblPath = New-Object System.Windows.Forms.Label
$lblPath.Location = New-Object System.Drawing.Point(12, 88)
$lblPath.AutoSize = $true
$lblPath.Text     = 'DEADLOCK.EXE:'

$txtPath = New-Object System.Windows.Forms.TextBox
$txtPath.Location = New-Object System.Drawing.Point(12, 108)
$txtPath.Size     = New-Object System.Drawing.Size(450, 23)
$txtPath.Text     = $ExePath

$btnBrowse = New-Object System.Windows.Forms.Button
$btnBrowse.Location = New-Object System.Drawing.Point(468, 106)
$btnBrowse.Size     = New-Object System.Drawing.Size(80, 26)
$btnBrowse.Text     = 'Browse...'

# --- status row ------------------------------------------------------------
$lblStatusCaption = New-Object System.Windows.Forms.Label
$lblStatusCaption.Location = New-Object System.Drawing.Point(12, 142)
$lblStatusCaption.AutoSize = $true
$lblStatusCaption.Text     = 'Status:'

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Location = New-Object System.Drawing.Point(60, 142)
$lblStatus.Size     = New-Object System.Drawing.Size(488, 20)
$lblStatus.Font     = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)

$lblDetail = New-Object System.Windows.Forms.Label
$lblDetail.Location = New-Object System.Drawing.Point(60, 162)
$lblDetail.Size     = New-Object System.Drawing.Size(488, 36)
$lblDetail.ForeColor = [System.Drawing.Color]::DimGray

# --- buttons ---------------------------------------------------------------
$btnPatch = New-Object System.Windows.Forms.Button
$btnPatch.Location = New-Object System.Drawing.Point(12, 204)
$btnPatch.Size     = New-Object System.Drawing.Size(130, 30)
$btnPatch.Text     = 'Apply patch'

$btnRestore = New-Object System.Windows.Forms.Button
$btnRestore.Location = New-Object System.Drawing.Point(150, 204)
$btnRestore.Size     = New-Object System.Drawing.Size(130, 30)
$btnRestore.Text     = 'Restore original'

$btnRefresh = New-Object System.Windows.Forms.Button
$btnRefresh.Location = New-Object System.Drawing.Point(288, 204)
$btnRefresh.Size     = New-Object System.Drawing.Size(90, 30)
$btnRefresh.Text     = 'Re-check'

$btnClose = New-Object System.Windows.Forms.Button
$btnClose.Location = New-Object System.Drawing.Point(468, 204)
$btnClose.Size     = New-Object System.Drawing.Size(80, 30)
$btnClose.Text     = 'Close'
$btnClose.DialogResult = [System.Windows.Forms.DialogResult]::Cancel

# --- log -------------------------------------------------------------------
$txtLog = New-Object System.Windows.Forms.TextBox
$txtLog.Location   = New-Object System.Drawing.Point(12, 244)
$txtLog.Size       = New-Object System.Drawing.Size(536, 164)
$txtLog.Multiline  = $true
$txtLog.ReadOnly   = $true
$txtLog.ScrollBars = 'Vertical'
$txtLog.Font       = New-Object System.Drawing.Font('Consolas', 9)
$txtLog.BackColor  = [System.Drawing.Color]::White

$form.Controls.AddRange(@($lblInfo, $lblPath, $txtPath, $btnBrowse, $lblStatusCaption, $lblStatus,
                          $lblDetail, $btnPatch, $btnRestore, $btnRefresh, $btnClose, $txtLog))
$form.CancelButton = $btnClose

# Appends a timestamped line to the log box.
function Write-GuiLog([string]$Message) {
    $stamp = (Get-Date).ToString('HH:mm:ss')
    $txtLog.AppendText("[$stamp] $Message`r`n")
}

# Re-reads the EXE, updates the status labels and enables only the buttons that make sense.
function Update-Status {
    $path = $txtPath.Text
    try {
        $state = Get-PatchState $path
    } catch {
        $state = [pscustomobject]@{ State = 'Error'; Detail = $_.Exception.Message; HasBackup = $false }
    }

    $running = $false
    if ($state.State -ne 'Missing' -and $state.State -ne 'Error') {
        try { $running = Test-GameRunning $path } catch { $running = $false }
    }

    switch ($state.State) {
        'Patched'   { $lblStatus.Text = 'PATCHED - the hang fix is applied';   $lblStatus.ForeColor = [System.Drawing.Color]::ForestGreen }
        'Original'  { $lblStatus.Text = 'NOT PATCHED - original v1.20 EXE';    $lblStatus.ForeColor = [System.Drawing.Color]::DarkOrange }
        'Missing'   { $lblStatus.Text = 'FILE NOT FOUND';                      $lblStatus.ForeColor = [System.Drawing.Color]::Firebrick }
        default     { $lblStatus.Text = "CANNOT PATCH ($($state.State))";       $lblStatus.ForeColor = [System.Drawing.Color]::Firebrick }
    }
    $detail = $state.Detail
    if ($running) { $detail = "The game is running. Close it before patching or restoring.`r`n$detail" }
    $lblDetail.Text = $detail

    $btnPatch.Enabled   = ($state.State -eq 'Original') -and -not $running
    $btnRestore.Enabled = $state.HasBackup -and -not $running -and ($state.State -ne 'Missing')
}

$btnBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title  = 'Select DEADLOCK.EXE'
    $dlg.Filter = 'Deadlock II executable (DEADLOCK.EXE)|DEADLOCK.EXE|Executables (*.exe)|*.exe|All files (*.*)|*.*'
    if (Test-Path -LiteralPath $txtPath.Text) {
        $dlg.InitialDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $txtPath.Text).Path
    }
    if ($dlg.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $txtPath.Text = $dlg.FileName
        Write-GuiLog "Selected $($dlg.FileName)"
        Update-Status
    }
})

$btnPatch.Add_Click({
    try {
        $msg = Invoke-Patch $txtPath.Text
        $msg -split "`r`n" | ForEach-Object { Write-GuiLog $_ }
    } catch {
        Write-GuiLog "ERROR: $($_.Exception.Message)"
        [System.Windows.Forms.MessageBox]::Show($form, $_.Exception.Message, 'Patch failed',
            [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
    Update-Status
})

$btnRestore.Add_Click({
    $answer = [System.Windows.Forms.MessageBox]::Show($form,
        "Copy DEADLOCK.EXE.orig back over DEADLOCK.EXE?`r`nThe hang will come back until you patch again.",
        'Restore original', [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
    try {
        Write-GuiLog (Invoke-Restore $txtPath.Text)
    } catch {
        Write-GuiLog "ERROR: $($_.Exception.Message)"
        [System.Windows.Forms.MessageBox]::Show($form, $_.Exception.Message, 'Restore failed',
            [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
    Update-Status
})

$btnRefresh.Add_Click({ Update-Status; Write-GuiLog "Re-checked. $($lblStatus.Text)" })
$txtPath.Add_Leave({ Update-Status })

$form.Add_Shown({
    Update-Status
    Write-GuiLog "Checked $($txtPath.Text)"
    Write-GuiLog $lblStatus.Text
})

[void]$form.ShowDialog()
$form.Dispose()
