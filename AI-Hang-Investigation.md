# Deadlock II: Shrine Wars — AI turn hang: investigation, fix and reporting notes

Date: 2026-09-18
Game: Deadlock II: Shrine Wars v1.20, GOG build 53593680394309429 (`DEADLOCK.EXE`, 1,738,752 bytes, Borland C++ build, image base 0x400000, no ASLR)
OS: Windows 11 Home 10.0.26200
Status: root cause found, 3-byte patch verified in-game, patch applied to the installed EXE.

---

## 1. Symptom

During an AI player's turn the game stops responding. One CPU core runs at 100% indefinitely.
Loading the save from just before the turn and ending the turn again reproduces it every time.
Windows reports the window as "not responding".

## 2. Tools used

| Tool | Where | Purpose |
|------|-------|---------|
| `cdb.exe` (x86) | `C:\Program Files\WindowsApps\Microsoft.WinDbg_<ver>_x64__8wekyb3d8bbwe\x86\cdb.exe` (Store WinDbg) | Attach to the hung 32-bit process, sample stacks, set breakpoints, patch memory |
| Python 3.14 + `capstone` + `pefile` | `C:\Python314\python.exe` | Static disassembly of the CODE section (`debug\full.asm`) |
| PowerShell | | Wrapper scripts, final patcher |

Gotchas learned:
- Use `.logopen <file>` inside the cdb command file. Redirecting cdb stdout from PowerShell or Bash produces UTF-16 with broken alignment.
- Inside `~*e` do not use `~.` (cdb reports "recursive thread command"). Select a thread with `~0s` instead.
- Attach with `-p <pid> -cf <cmdfile>` and end the command file with `qd` so the game keeps running after detach.
- `Get-Process DEADLOCK` gives `Responding` and `TotalProcessorTime`; a quick way to confirm a spin without a debugger.

All command files and logs are kept in `debug\`:

| File | Content |
|------|---------|
| `cdbcmds.txt`, `sample.ps1` | First pass: `!runaway`, all-thread stacks, disassembly around EIP, 4 samples |
| `s1.txt` .. `s4.txt` | Converted sample logs |
| `cdbtrace.txt`, `trace.log` | Breakpoint after each job dispatch; dumps the job node |
| `cdbtrace2.txt`, `trace2.log` | Full job list for race 2 before each dispatch |
| `cdbtrace3.txt`, `trace3.log` | Hardware write breakpoints on the done flags, breakpoints on job creation |
| `cdbpatch.txt`, `livepatch.log` | In-memory patch of the running game |
| `cdbcheck.txt`, `check.log` | Post-patch confirmation that the main thread left the loop |
| `full.asm` | Linear-sweep disassembly of the whole CODE section (capstone, skipdata mode) |

## 3. Method

1. `!runaway 7` across four samples: thread 0 (main thread) accumulated all user CPU time; all other threads idle.
2. Stack of thread 0 was always inside the same frames:
   `0x45F46C` → `0x408B2A` → **`0x405AAC`** (job scheduler) → `0x4059BC` (dispatch by job type) → a handler.
3. Breakpoint at `0x405AE2` (just after dispatch) with `dd ebx` showed the same three job nodes cycling forever, each with priority 5001 (0x1389).
4. Walked the whole job list before each dispatch and set hardware write breakpoints on the done flags of the two recurring nodes. That named the exact instruction that re-arms them.
5. Static disassembly of the handlers confirmed the semantics and that this is the only such site.
6. Patched the three bytes in the live process with `eb`. The window became responsive and the main thread returned to the `PeekMessage` loop. The user confirmed the turn completed and play continued.

## 4. Findings

### 4.1 Data structures

Per-race AI state: array of 7 structs, 0x44 bytes each, at `0x522280`. Offset +0x14 of each is the head pointer of that race's job list (so `0x522294 + race*0x44`).

Job node, 0x44 bytes, malloc'd:

| Offset | Meaning |
|--------|---------|
| +0x00 | job type (0..13; dispatch table at `0x4059DC`) |
| +0x04 | minister / sub-id |
| +0x08 | priority. > 5000 (0x1388) means "process again immediately" |
| +0x0C | **done this pass** flag |
| +0x10 | remove-me flag (scheduler unlinks and frees the node when set) |
| +0x14 | next |
| +0x18 | prev (head's prev points at the race struct) |
| +0x1C, +0x20, +0x24 | job parameters (building id, territory or -1, amount/resource, etc.) |

Job types seen: 2 = build building, 3 = (created by `0x408288`), 8 = obtain resource, 12 = (created by `0x408F58`).

### 4.2 Scheduler (`0x405AAC`, called from `0x408B2A` with the race index)

```
call 0x405920            ; clear done flag on every job of this race
loop:
  ebx = 0x40597C(race)   ; highest-priority job with done == 0, or NULL
  if !ebx -> exit
  call 0x4059BC(race, job)      ; dispatch to handler by type
  if job.done:                  ; handler finished it
      if job.prio > 5000: job.prio = default from race table
      else               : job.prio++
  else: job.prio -= 10          ; handler did not finish it
  if job.remove: unlink + free (0x405760)
  goto loop
exit:
  call 0x405948            ; purge all type-8 jobs of this race
```

The loop terminates only when every job is marked done or removed. New jobs created during the pass (done = 0) are processed in the same pass, which is intended. The loop has no iteration cap.

### 4.3 The cycle

- **Type 8 handler** (`0x408E3C`, "obtain resource N"): if stock + income < needed, tries to queue the producer building (`0x4B6444[N]`). If that fails, marks itself done and calls `0x407D60` to create a **type 2** job "build prerequisite building `0x4B649C[producer]`" at priority 5001.
- **Type 2 handler** (`0x407E78`, "build building B"): walks the building's resource costs; for each resource the race cannot produce it calls `0x408B58`, then sets its own remove flag.
- **`0x408B58`** ("request resource"): allocates a fresh type-8 node, then calls `0x4058E0` to look for an equivalent existing job. If one exists it **re-arms it**:

```
00408BA9  33 C0        xor  eax, eax
00408BAB  89 46 0C     mov  [esi+0Ch], eax      ; done = 0   <-- the bug
00408BAE  8B 55 18     mov  edx, [ebp+18h]
00408BB1  01 56 20     add  [esi+20h], edx      ; needed amount += request
...                                              ; prio = max(prio, 5001)
```

Observed instance (race 2): resource 4 and resource 8 jobs → "build building 14" / "build building 18" → those need resources 4 and 8 → re-arm. Building 14 needs resources whose producers require building 14 or 18, so no iteration can make progress and the pass never ends.

`grep` over `full.asm` for stores to `[reg+0xC]` in the job code shows `0x408BAB` is the only place that clears the done flag of an *existing* job. `0x407D60` and `0x408288` (the other create-or-merge routines) only raise the priority of an existing job.

## 5. The fix

Replace `89 46 0C` at VA `0x00408BAB` with `90 90 90` (three NOPs).

File offset: CODE section starts at VA 0x401000 / raw 0x800, so `0x408BAB - 0x401000 + 0x800 = 0x83AB`.

Effect: a re-armed job keeps its raised priority and the increased requested amount, but keeps done = 1, so it runs on the **next** turn rather than again in the same pass. The AI retries an impossible build once per turn, which is harmless, instead of hanging the game. Nothing else in the pass changes.

Verification:
- In-memory patch on the hung process: main thread left the scheduler within seconds, window responsive, turn completed, play continued.
- File patch applied via `Patch-DeadlockAIHang.ps1`; the game was restarted, the same save loaded, and the turn passed normally.

## 6. Using the patch script

`Patch-DeadlockAIHang.ps1` in the game folder. Backup is written to `DEADLOCK.EXE.orig` before the first patch.

| Command | What it does |
|---------|--------------|
| `powershell -ExecutionPolicy Bypass -File .\Patch-DeadlockAIHang.ps1` | Opens a window: status (patched / original / wrong build), Apply, Restore, Browse, log |
| `... -NoGui` | Patch from the console |
| `... -Restore` | Copy the backup back |
| `... -ExePath <path>` | Work on a different copy |

The script refuses to run while the game is open, checks the file size and the 8 code bytes before the patch site, and verifies the write. Logic lives in `Get-PatchState`, `Invoke-Patch`, `Invoke-Restore`.

## 7. Reporting to GOG

GOG has no per-game bug tracker. Routes:

1. **Support ticket**: support.gog.com → Contact us → game: Deadlock II: Shrine Wars → Technical issue. Attach a zip.
2. **Game forum** on gog.com: post the same report so other players can confirm and staff can see it.
3. **Rights holder**: whoever is listed as publisher on the store page. GOG needs their approval to change the EXE.

Zip contents:
- The save that reproduces the hang (from `Saves\` or `Campaign\`), plus `Saves\AUTOSAVE.SAV`.
- Reproduction steps, version info (v1.20, build 53593680394309429, Windows 11).
- This document or its sections 4–5, and `Patch-DeadlockAIHang.ps1`.
- The byte change stated plainly so it can be applied without the script.

Draft ticket text:

```
Deadlock II: Shrine Wars (GOG build 53593680394309429, game v1.20) hangs during an AI turn.
Reproduction: load the attached save, end the turn. The game stops responding and one core
runs at 100% until killed. It happens every time with this save.

I debugged the hung process. The AI job scheduler (function at 0x00405AAC) re-runs jobs
until none are left for this pass. The "request resource" routine (0x00408B58) re-arms an
already-processed job by clearing its done flag at 0x00408BAB (mov [esi+0Ch],eax). With a
circular building/resource prerequisite the pass never ends.

Proposed fix: replace the 3 bytes at file offset 0x83AB (89 46 0C) with 90 90 90. The
re-armed job then runs on the next turn instead of again in the same pass. Verified: the
patched EXE completes the turn and play continues normally. Patch script attached.
```

## 8. Reusable recipe for the next hang

1. `Get-Process DEADLOCK | select Responding, TotalProcessorTime` twice a few seconds apart. Growing CPU + not responding = spin.
2. Attach: `cdb -p <pid> -cf debug\cdbcmds.txt` (uses `.logopen`; ends with `qd`). Read `!runaway` to find the hot thread, then its stack.
3. Find the innermost frame that repeats across samples; disassemble it from `debug\full.asm` (regenerate with the capstone snippet in `debug\` if the EXE changes).
4. Put a breakpoint on the loop body, dump the loop's data each hit, and use `ba w4` on any field that unexpectedly resets to find the writer.
5. Test a candidate fix with `eb <addr> <bytes>` on the live process before touching the file.
6. File offset for the CODE section = VA − 0x400800.
