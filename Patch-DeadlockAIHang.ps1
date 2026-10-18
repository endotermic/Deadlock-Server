<#
.SYNOPSIS
  Fixes three bugs in Deadlock II: Shrine Wars v1.20 (GOG) by patching a few bytes in DEADLOCK.EXE:
  the "AI turn never ends" hang, the crash when the minimised game is restored, and the close
  request that was ignored while the game was minimised.
  Runs as a small window by default, or from the command line.

.DESCRIPTION
  PATCH 1 - AI turn hang (3 bytes at file offset 0x83AB)

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
      Replace the 3-byte store with three NOPs (90 90 90). The re-armed job still gets its
      boosted priority and updated resource amount but is not re-run in the same pass. All
      "get resource" jobs are purged at the end of the pass anyway (0x00405948), and the AI
      planner recreates the request on the next turn. The AI therefore retries an impossible
      build once per turn (harmless) instead of hanging the game. Decisions on turns without
      a mid-pass prerequisite chain are byte-for-byte identical to the unpatched game.

  PATCH 2 - crash on restore after minimise (25 bytes at file offsets 0x947C1 and 0x94803)

    SYMPTOM
      Alt-Tab or Win key away from the full-screen game and back: the screen stays black and
      the game looks hung. The Application event log shows exception 0xC000041D at fault
      offset 0x0008C969, i.e. an access violation inside the window procedure.

    ROOT CAUSE
      The game draws its own mouse cursor and keeps the background under it in a "save-under"
      bitmap ([0x65ECB8]). On WM_ACTIVATE(inactive) the cursor shutdown routine at VA 0x00494FB6
      frees that bitmap, but the hide call just before it is skipped while the cursor show-count
      is 0 (the main menu paints the cursor directly), so the "cursor is drawn" flag at
      [0x51DC8C] stays set. On WM_ACTIVATE(active) the redraw at VA 0x00494449 trusts the flag
      and blits from the NULL pointer.

    THE FIX
      Rewrite the tail of the shutdown routine so it also clears the drawn flag, using the three
      padding bytes after its ret:

          494FC0  74 50  je 495012                  ->  74 52  je 495014
          495003  xor ecx,ecx                        ->  xor eax,eax
                  mov [51DC9C],ecx ; initialised=0       mov [51DC9C],eax ; initialised = 0
                  xor eax,eax                            mov [65EC7C],eax ; show count = 0
                  mov [65EC7C],eax ; show count=0        mov [51DC8C],eax ; drawn = 0  (new)
                  mov eax,1 ; pop ebp ; ret              mov eax,1 ; pop ebp ; ret

      Nothing outside minimise/restore changes; after a restore the cursor code takes its normal
      "no cursor on screen" path and re-creates the save-under.

  PATCH 3 - the minimised game cannot be closed (42 bytes at file offsets 0x725A4 and 0xB3C34)

    SYMPTOM
      With the game minimised, "Close window" from the taskbar or Alt+F4 does nothing.

    ROOT CAUSE
      The WM_CLOSE handler at VA 0x00472DA4 always shows an in-game confirmation dialog. It is drawn
      inside the DirectDraw surface, so while minimised it is invisible, and the GOG ddraw wrapper
      keeps the main thread in its own wait loop in that state, so no game code could act anyway.

    THE FIX
      The handler's first instruction becomes a jump into a 37-byte code cave in the zero padding at
      the end of the CODE section (VA 0x004B4434). The cave checks GetWindowLongA(hwnd, GWL_STYLE)
      & WS_MINIMIZE through the game's own imports; if the window is minimised it calls
      ExitProcess(0), otherwise it runs the displaced instruction and jumps back, so the dialog
      still appears while the window is visible.

  FILE LOCATIONS
    The CODE section starts at VA 0x00401000 / raw file offset 0x800, so file offset = VA - 0x400800.

  SAFETY
    The script refuses to touch the file unless the size matches the GOG v1.20 build and the code
    around each patch site matches the expected instructions. The first run writes a backup next
    to the EXE as DEADLOCK.EXE.orig, and -Restore copies it back. Patches that are already applied
    are left alone, so the script can be re-run after a new patch is added.

.PARAMETER ExePath
  Path to DEADLOCK.EXE. Defaults to the file next to this script.

.PARAMETER Restore
  Command-line mode: copy DEADLOCK.EXE.orig back over DEADLOCK.EXE.

.PARAMETER NoGui
  Command-line mode: apply all patches and print results to the console instead of
  opening the window. -Restore implies -NoGui.

.EXAMPLE
  .\Patch-DeadlockAIHang.ps1
  Opens the window. Shows which patches are applied and offers Apply / Restore buttons.

.EXAMPLE
  .\Patch-DeadlockAIHang.ps1 -NoGui -ExePath 'C:\Games\Deadlock2\DEADLOCK.EXE'
  Applies both patches from the command line.

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
# Patch definitions. Everything the checks and the writes need is in one place.
# Each patch has a Context (bytes that must match before anything is touched)
# and one or more Sites (Original -> Patched byte runs of equal length).
# ---------------------------------------------------------------------------
$script:ExpectedSize = 1738752                                  # GOG DEADLOCK.EXE v1.20

$script:Patches = @(
    [pscustomobject]@{
        Id      = 'AIHang'
        Name    = 'AI turn hang (job scheduler infinite loop)'
        Context = [pscustomobject]@{
            Offset = 0x83A3
            Bytes  = [byte[]](0x8B,0xF0,0x85,0xF6,0x74,0x32,0x33,0xC0)
            # the 8 bytes immediately before the patch site:
            #   8B F0        mov  esi, eax        ; esi = existing job (or 0)
            #   85 F6        test esi, esi
            #   74 32        je   +0x32           ; no existing job -> create new
            #   33 C0        xor  eax, eax        ; eax = 0
        }
        Sites = @(
            [pscustomobject]@{
                VirtualAddr = 0x00408BAB
                Offset      = 0x83AB
                Original    = [byte[]](0x89,0x46,0x0C)              # mov [esi+0Ch], eax  (done = 0)
                Patched     = [byte[]](0x90,0x90,0x90)              # nop nop nop
            }
        )
    },
    [pscustomobject]@{
        Id      = 'MinimizeCrash'
        Name    = 'Crash on restore after minimise (cursor save-under)'
        Context = [pscustomobject]@{
            Offset = 0x947B6
            Bytes  = [byte[]](0x55,0x8B,0xEC,0x83,0x3D,0x9C,0xDC,0x51,0x00,0x00,0x74)
            # start of the cursor shutdown routine at VA 0x00494FB6:
            #   55                   push ebp
            #   8B EC                mov  ebp, esp
            #   83 3D 9CDC5100 00    cmp  dword ptr [51DC9C], 0   ; initialised?
            #   74 ..                je   <return 1>
        }
        Sites = @(
            [pscustomobject]@{
                VirtualAddr = 0x00494FC1
                Offset      = 0x947C1
                Original    = [byte[]](0x50)                        # je rel8 -> 0x495012
                Patched     = [byte[]](0x52)                        # je rel8 -> 0x495014
            },
            [pscustomobject]@{
                VirtualAddr = 0x00495003
                Offset      = 0x94803
                Original    = [byte[]](0x33,0xC9, 0x89,0x0D,0x9C,0xDC,0x51,0x00, 0x33,0xC0, 0xA3,0x7C,0xEC,0x65,0x00,
                                       0xB8,0x01,0x00,0x00,0x00, 0x5D, 0xC3, 0x00,0x00)
                Patched     = [byte[]](0x33,0xC0, 0xA3,0x9C,0xDC,0x51,0x00, 0xA3,0x7C,0xEC,0x65,0x00, 0xA3,0x8C,0xDC,0x51,0x00,
                                       0xB8,0x01,0x00,0x00,0x00, 0x5D, 0xC3)
            }
        )
    },
    [pscustomobject]@{
        Id      = 'CloseWhileMinimized'
        Name    = 'Close request ignored while minimised (WM_CLOSE dialog)'
        Context = [pscustomobject]@{
            Offset = 0x725A9
            Bytes  = [byte[]](0x85,0xC0,0x7E,0x2D,0xBA,0xC0,0x7A,0x45,0x00)
            # the WM_CLOSE handler's instructions right after the patched one (VA 0x00472DA9); the
            # patched instruction itself is left out so an already-patched file still matches:
            #   85 C0         test eax, eax
            #   7E 2D         jle  <plain "really quit?" box>
            #   BA C07A4500   mov  edx, 0x457AC0
        }
        Sites = @(
            [pscustomobject]@{
                VirtualAddr = 0x00472DA4
                Offset      = 0x725A4
                Original    = [byte[]](0xA1,0x54,0xF1,0x59,0x00)              # mov eax, [0x59F154]
                Patched     = [byte[]](0xE9,0x8B,0x16,0x04,0x00)              # jmp 0x004B4434 (the cave)
            },
            [pscustomobject]@{
                VirtualAddr = 0x004B4434
                Offset      = 0xB3C34
                Original    = [byte[]](@(0) * 37)                             # zero padding after the import thunks
                Patched     = [byte[]](0x6A,0xF0,                             # push -16            ; GWL_STYLE
                                       0xFF,0x35,0xA4,0xF1,0x58,0x00,         # push [58F1A4]       ; main window handle
                                       0xE8,0x1A,0xFD,0xFF,0xFF,              # call 004B415B       ; -> GetWindowLongA
                                       0xA9,0x00,0x00,0x00,0x20,              # test eax, WS_MINIMIZE
                                       0x74,0x07,                             # jz +7               ; not minimised
                                       0x6A,0x00,                             # push 0
                                       0xE8,0x56,0xFB,0xFF,0xFF,              # call 004B3FA5       ; -> ExitProcess
                                       0xA1,0x54,0xF1,0x59,0x00,              # mov eax, [59F154]   ; displaced instruction
                                       0xE9,0x50,0xE9,0xFB,0xFF)              # jmp 00472DA9        ; back into the handler
            }
        )
    }
)

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

# State of one patch inside an already-loaded byte array:
#   WrongCode - the context bytes do not match (not the build this patch was made for)
#   Patched   - every site holds the patched bytes
#   Original  - every site holds the original bytes
#   Unknown   - sites are mixed or hold something else
function Get-OnePatchState([byte[]]$bytes, $patch) {
    $ctxEnd = $patch.Context.Offset + $patch.Context.Bytes.Length - 1
    $ctx = $bytes[$patch.Context.Offset..$ctxEnd]
    if (-not (Test-BytesEqual $ctx $patch.Context.Bytes)) {
        return [pscustomobject]@{ Id = $patch.Id; Name = $patch.Name; State = 'WrongCode'
            Detail = ("code at 0x{0:X} does not match: {1}" -f $patch.Context.Offset, (Format-Hex $ctx)) }
    }
    $patchedSites = 0; $originalSites = 0
    foreach ($site in $patch.Sites) {
        $cur = $bytes[$site.Offset..($site.Offset + $site.Original.Length - 1)]
        if     (Test-BytesEqual $cur $site.Patched)  { $patchedSites++ }
        elseif (Test-BytesEqual $cur $site.Original) { $originalSites++ }
    }
    $n = $patch.Sites.Count
    if ($patchedSites -eq $n) {
        $state = 'Patched';  $detail = "applied"
    } elseif ($originalSites -eq $n) {
        $state = 'Original'; $detail = "not applied"
    } else {
        $state = 'Unknown';  $detail = "bytes at the patch site(s) are neither original nor patched"
    }
    return [pscustomobject]@{ Id = $patch.Id; Name = $patch.Name; State = $state; Detail = $detail }
}

# Inspects the file and reports one of:
#   Missing     - file does not exist
#   WrongSize   - not the v1.20 build these patches were made for
#   WrongCode   - size matches but the code around a patch site differs
#   Original    - no patch applied, safe to patch
#   Partial     - some patches applied, the rest can be applied
#   Patched     - every patch applied
#   Unknown     - a patch site holds bytes that are neither original nor patched
# PerPatch holds the per-patch results; Detail is a multi-line summary.
function Get-PatchState([string]$Path) {
    $result = [pscustomobject]@{ State = ''; Detail = ''; HasBackup = $false; PerPatch = @() }
    if (-not (Test-Path -LiteralPath $Path)) {
        $result.State = 'Missing'; $result.Detail = "File not found: $Path"; return $result
    }
    $result.HasBackup = Test-Path -LiteralPath (Get-BackupPath $Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ne $script:ExpectedSize) {
        $result.State = 'WrongSize'
        $result.Detail = "File is $($bytes.Length) bytes, expected $script:ExpectedSize (GOG v1.20 build)."
        return $result
    }

    $result.PerPatch = @(foreach ($p in $script:Patches) { Get-OnePatchState $bytes $p })
    $states = $result.PerPatch | ForEach-Object State

    if     ($states -contains 'WrongCode')                      { $result.State = 'WrongCode' }
    elseif ($states -contains 'Unknown')                        { $result.State = 'Unknown' }
    elseif (($states | Where-Object { $_ -ne 'Patched' }).Count -eq 0)  { $result.State = 'Patched' }
    elseif (($states | Where-Object { $_ -ne 'Original' }).Count -eq 0) { $result.State = 'Original' }
    else                                                        { $result.State = 'Partial' }

    $result.Detail = ($result.PerPatch | ForEach-Object { "$($_.Name): $($_.Detail)" }) -join "`r`n"
    return $result
}

# Applies every patch that is not yet applied. Writes a backup first (never overwrites an
# existing backup). Throws on any precondition failure; returns a message on success.
function Invoke-Patch([string]$Path) {
    if (Test-GameRunning $Path) { throw "DEADLOCK.EXE is running from $Path. Close the game first." }
    $state = Get-PatchState $Path
    switch ($state.State) {
        'Patched'  { return "All patches already applied. Nothing to do." }
        'Original' { }   # fall through to the write below
        'Partial'  { }
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
    $todo = @()
    foreach ($p in $script:Patches) {
        $ps = $state.PerPatch | Where-Object Id -eq $p.Id
        if ($ps.State -eq 'Patched') { $log += "$($p.Name): already applied."; continue }
        $todo += $p
        foreach ($site in $p.Sites) {
            for ($i = 0; $i -lt $site.Patched.Length; $i++) { $bytes[$site.Offset + $i] = $site.Patched[$i] }
        }
    }
    [System.IO.File]::WriteAllBytes($Path, $bytes)

    # Read back and confirm the bytes landed.
    $written = [System.IO.File]::ReadAllBytes($Path)
    foreach ($p in $todo) {
        foreach ($site in $p.Sites) {
            $verify = $written[$site.Offset..($site.Offset + $site.Patched.Length - 1)]
            if (-not (Test-BytesEqual $verify $site.Patched)) { throw "Verification failed after write at offset 0x{0:X}." -f $site.Offset }
            $log += ("{0}: patched file offset 0x{1:X} (VA 0x{2:X8}): {3} -> {4}" -f
                $p.Name, $site.Offset, $site.VirtualAddr, (Format-Hex $site.Original), (Format-Hex $site.Patched))
        }
    }
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
$form.Text            = 'Deadlock II - EXE patches'
$form.StartPosition   = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox     = $false
$form.MinimizeBox     = $false
$form.ClientSize      = New-Object System.Drawing.Size(560, 450)
$form.Font            = New-Object System.Drawing.Font('Segoe UI', 9)

# --- description -----------------------------------------------------------
$lblInfo = New-Object System.Windows.Forms.Label
$lblInfo.Location = New-Object System.Drawing.Point(12, 10)
$lblInfo.Size     = New-Object System.Drawing.Size(536, 84)
$lblInfo.Text     = "Three byte patches for DEADLOCK.EXE (GOG v1.20):`r`n" +
                    "1. AI turn hang: an AI turn never finishes at 100% CPU (3 bytes at 0x83AB).`r`n" +
                    "2. Minimise crash: Alt-Tab away and back leaves a black screen, 0xC000041D (25 bytes at 0x947C1/0x94803).`r`n" +
                    "3. Close while minimised: taskbar Close / Alt+F4 did nothing while minimised (42 bytes at 0x725A4/0xB3C34).`r`n" +
                    "A backup (DEADLOCK.EXE.orig) is written before the first patch."

# --- EXE path row ------------------------------------------------------------
$lblPath = New-Object System.Windows.Forms.Label
$lblPath.Location = New-Object System.Drawing.Point(12, 100)
$lblPath.AutoSize = $true
$lblPath.Text     = 'DEADLOCK.EXE:'

$txtPath = New-Object System.Windows.Forms.TextBox
$txtPath.Location = New-Object System.Drawing.Point(12, 120)
$txtPath.Size     = New-Object System.Drawing.Size(450, 23)
$txtPath.Text     = $ExePath

$btnBrowse = New-Object System.Windows.Forms.Button
$btnBrowse.Location = New-Object System.Drawing.Point(468, 118)
$btnBrowse.Size     = New-Object System.Drawing.Size(80, 26)
$btnBrowse.Text     = 'Browse...'

# --- status row ------------------------------------------------------------
$lblStatusCaption = New-Object System.Windows.Forms.Label
$lblStatusCaption.Location = New-Object System.Drawing.Point(12, 154)
$lblStatusCaption.AutoSize = $true
$lblStatusCaption.Text     = 'Status:'

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Location = New-Object System.Drawing.Point(60, 154)
$lblStatus.Size     = New-Object System.Drawing.Size(488, 20)
$lblStatus.Font     = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)

$lblDetail = New-Object System.Windows.Forms.Label
$lblDetail.Location = New-Object System.Drawing.Point(60, 174)
$lblDetail.Size     = New-Object System.Drawing.Size(488, 54)
$lblDetail.ForeColor = [System.Drawing.Color]::DimGray

# --- buttons ---------------------------------------------------------------
$btnPatch = New-Object System.Windows.Forms.Button
$btnPatch.Location = New-Object System.Drawing.Point(12, 234)
$btnPatch.Size     = New-Object System.Drawing.Size(130, 30)
$btnPatch.Text     = 'Apply patches'

$btnRestore = New-Object System.Windows.Forms.Button
$btnRestore.Location = New-Object System.Drawing.Point(150, 234)
$btnRestore.Size     = New-Object System.Drawing.Size(130, 30)
$btnRestore.Text     = 'Restore original'

$btnRefresh = New-Object System.Windows.Forms.Button
$btnRefresh.Location = New-Object System.Drawing.Point(288, 234)
$btnRefresh.Size     = New-Object System.Drawing.Size(90, 30)
$btnRefresh.Text     = 'Re-check'

$btnClose = New-Object System.Windows.Forms.Button
$btnClose.Location = New-Object System.Drawing.Point(468, 234)
$btnClose.Size     = New-Object System.Drawing.Size(80, 30)
$btnClose.Text     = 'Close'
$btnClose.DialogResult = [System.Windows.Forms.DialogResult]::Cancel

# --- log -------------------------------------------------------------------
$txtLog = New-Object System.Windows.Forms.TextBox
$txtLog.Location   = New-Object System.Drawing.Point(12, 274)
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
        $state = [pscustomobject]@{ State = 'Error'; Detail = $_.Exception.Message; HasBackup = $false; PerPatch = @() }
    }

    $running = $false
    if ($state.State -ne 'Missing' -and $state.State -ne 'Error') {
        try { $running = Test-GameRunning $path } catch { $running = $false }
    }

    switch ($state.State) {
        'Patched'   { $lblStatus.Text = 'PATCHED - all fixes are applied';           $lblStatus.ForeColor = [System.Drawing.Color]::ForestGreen }
        'Partial'   { $lblStatus.Text = 'PARTLY PATCHED - a fix is still missing';   $lblStatus.ForeColor = [System.Drawing.Color]::DarkOrange }
        'Original'  { $lblStatus.Text = 'NOT PATCHED - original v1.20 EXE';          $lblStatus.ForeColor = [System.Drawing.Color]::DarkOrange }
        'Missing'   { $lblStatus.Text = 'FILE NOT FOUND';                            $lblStatus.ForeColor = [System.Drawing.Color]::Firebrick }
        default     { $lblStatus.Text = "CANNOT PATCH ($($state.State))";             $lblStatus.ForeColor = [System.Drawing.Color]::Firebrick }
    }
    $detail = $state.Detail
    if ($running) { $detail = "The game is running. Close it before patching or restoring.`r`n$detail" }
    $lblDetail.Text = $detail

    $btnPatch.Enabled   = ($state.State -eq 'Original' -or $state.State -eq 'Partial') -and -not $running
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
        "Copy DEADLOCK.EXE.orig back over DEADLOCK.EXE?`r`nAll three bugs will come back until you patch again.",
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
    $lblDetail.Text -split "`r`n" | ForEach-Object { Write-GuiLog $_ }
})

[void]$form.ShowDialog()
$form.Dispose()
