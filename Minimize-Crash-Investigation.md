# Deadlock II: Shrine Wars — crash when restoring the minimised game: investigation and fix

Date: 2026-10-18
Game: Deadlock II: Shrine Wars v1.20, GOG build (`DEADLOCK.EXE`, 1,738,752 bytes, Borland C++ build, image base 0x400000, no ASLR), running through GOG's bundled `ddraw.dll` wrapper ("GOG.com DirectX 1-7 wrapper" 1.0.0.9270, `dxcfg.ini`: `presentation=application`, `display=desktop`).
OS: Windows 11 Home 10.0.26300
Status: root cause found, 25-byte patch verified in-game, patch applied to the installed EXE.

Companion to `AI-Hang-Investigation.md`, which covers the tooling in more detail. Both fixes are applied by `Patch-DeadlockAIHang.ps1`.

---

## 1. Symptom

Minimise the full-screen game (Alt-Tab, Win key, or a programmatic `ShowWindow(SW_MINIMIZE)`), then bring it back.
The game never redraws. From the player's side it looks like a hang: the screen stays black and the window does not respond.

What actually happens is a crash. Without a debugger the Windows Application log records

```
Faulting application name: DEADLOCK.EXE   Exception code: 0xc000041d   Fault offset: 0x0008c969
```

`0xC000041D` is `STATUS_FATAL_USER_CALLBACK_EXCEPTION`: an access violation escaped from a window procedure
that USER32 had called. Windows Error Reporting then holds the process while it decides what to do, and its
dialog is hidden behind the dead full-screen window, which is why it looks like a hang rather than a crash.
It reproduces every time, already at the main menu. Alt-Tab and the programmatic minimise go through the same
`WM_ACTIVATE` path.

## 2. Reproduction without touching the keyboard

```powershell
$p = Get-Process DEADLOCK; $h = $p.MainWindowHandle
[W]::ShowWindow($h, 6)                      # SW_MINIMIZE
Start-Sleep 3
[W]::ShowWindow($h, 9); [W]::SetForegroundWindow($h)   # SW_RESTORE
Start-Sleep 5; $p.Refresh(); $p.HasExited   # True on the unpatched EXE
```

(`[W]` is a two-line `Add-Type` P/Invoke wrapper for `user32!ShowWindow` / `SetForegroundWindow`.) The game was
started under the x86 `cdb.exe` with `debug\hang2cmd.txt`, which logs first-chance exceptions with registers and
stacks, breaks on the second chance, and writes a full dump. `debug\hang3cmd.txt` is the follow-up trace that
dumps the cursor globals at every relevant routine; its output is `debug\hang3.log`.

## 3. Crash site

```
eax=00000000 ebx=05606128 ecx=00000000 edx=00000015 esi=00000000 edi=00b0f1bc
0048c969 3b5604   cmp edx, dword ptr [esi+4]      ; esi = source bitmap = NULL

00b0f1f8 00494653  Deadlock2+0x8c969   generic bitmap blit 0x48C85E(src, dst, srcRect, dstRect, clip, 0, 0)
00b0f25c 00494993  0x494449            draw software cursor at its current position
00b0f278 00494e41  0x49497A            draw cursor on the current target surface
00b0f288 00472f14  0x494DEF(1)         cursor show-count++ ; became 1 -> draw
00b0f294 00473aaa  0x472ED0            WM_ACTIVATE handler, wParam = 1 (WA_ACTIVE)
00b0f2b0 758a9cc3  MainWndProc (0x473940)
         ...       USER32!CallWindowProcA  <- DDRAW.dll (GOG wrapper subclasses the window)
```

`0x48C85E` is the game's general blit (bitmap → bitmap with rect clipping). It reads the source bitmap's width
at `+4` and height at `+8`. The caller at `0x49464E` passed `[0x65ECB8]`, the cursor's save-under bitmap, and
that pointer was NULL.

## 4. The software cursor module

The game draws its own cursor into the DirectDraw surface and keeps the background under it in a "save-under"
bitmap. A dedicated thread (`0x494E62`, created in `0x494EBF`) follows the mouse and redraws; a mutex at
`0x65E558` (lock `0x4B3FAB`, unlock `0x4B3F2D`) guards the state.

Globals:

| Address | Meaning |
|---------|---------|
| `0x51DC84` | cursor thread stop flag |
| `0x51DC88` | Windows cursor hidden |
| `0x51DC8C` | **software cursor is drawn; save-under is valid** |
| `0x51DC94` / `0x51DC90` | optional callback + argument (hardware-cursor path) |
| `0x51DC98` | cursor thread handle |
| `0x51DC9C` | cursor module initialised |
| `0x65EC7C` | show count |
| `0x65EC80` | current cursor shape object |
| `0x65EC84`, `0x65EC88` | cursor position |
| `0x65EC8C..0x65EC98` | current cursor rect |
| `0x65EC9C..0x65ECA8` | rect where the cursor is currently drawn |
| `0x65ECAC` | target surface (`0x65E644` = main screen) |
| `0x65ECB0` | current animation frame |
| `0x65ECB8` | **save-under bitmap** |
| `0x65ECBC` | scratch bitmap (2× cursor size) |

Routines:

| VA | Role |
|----|------|
| `0x4943AB` | save under: allocate `[0x65ECB8]`, copy the screen into it, set drawn = 1, remember the rect in `0x65EC9C` |
| `0x494B6B` | restore: blit `[0x65ECB8]` back, drawn = 0. Guarded by `[0x65ECB8] != 0` |
| `0x494B06` | hide cursor. Returns early unless shape, initialised and **show count ≠ 0** |
| `0x494449` | draw at new position. If drawn = 1 and the old and new rects intersect, it merges the old save-under into the new one, reading `[0x65ECB8]` **without a NULL check** |
| `0x494DEF(n)` | show (n ≠ 0) / hide (0): adjusts the show count, draws on 0 → 1, hides on 1 → 0 |
| `0x49497A` | draw onto the current target (main screen → `0x494449`) |
| `0x494F62` | init cursor module |
| `0x494FB6` | **shutdown**: hide, stop the thread (`0x494F0F`), free both bitmaps, initialised = 0, count = 0 |
| `0x472ED0` | `WM_ACTIVATE`. Inactive: `0x472E68`, then `0x4152C0` = shape(0) + `0x494FB6`, then `0x48E0F7`. Active: `0x472E4C`, `0x415274` = shape + `0x494F62`, then `0x494DEF(1)` |
| `0x444916` | menu compositor: redraws the screen and calls `0x4943AB` **directly**, with the show count still 0 |

## 5. The sequence (from `debug\hang3.log`)

Steady state at the main menu: show count = 0, drawn = 1, save-under allocated, drawn rect = (0x176,0x197,0x18B,0x1AC).
The menu paints the cursor itself through the save-under routine; the show count is not involved.

1. **Minimise** → `WM_ACTIVATE(0)` → `0x4152C0`. The hide call `0x494B06` returns immediately because the show
   count is 0, so the save-under is never restored and **drawn stays 1**. `0x494FB6` then frees both bitmaps,
   sets `[0x65ECB8] = [0x65ECBC] = 0`, initialised = 0, count = 0. The drawn flag and the drawn rect survive.
2. **Restore** → `WM_ACTIVATE(1)` → re-init, `0x494DEF(1)`: count 0 → 1 → `0x494449`. It allocates a fresh
   scratch bitmap, sees drawn = 1, computes the new rect (same position, so it intersects the stale drawn rect),
   takes the merge path and blits from `[0x65ECB8] = NULL`. Access violation inside the window procedure →
   `0xC000041D`.

The first `WM_ACTIVATE(1)` after a restore sometimes gets away with it because the scratch allocation fails
while the surface is still lost; the second one crashes. Either way it never survives a restore.

## 6. The fix

Make the shutdown routine clear the drawn flag when it frees the save-under. "Nothing is saved" must imply
"nothing is drawn". The tail of `0x494FB6` is rewritten in place; the three padding bytes after its `ret`
provide the room.

```
VA        before                                   after
00494FC0  74 50        je 0x495012                  74 52        je 0x495014
...
00495003  33 C9        xor ecx, ecx                 33 C0        xor eax, eax
00495005  89 0D 9CDC5100  mov [51DC9C], ecx         A3 9CDC5100  mov [51DC9C], eax   ; initialised = 0
0049500B  33 C0        xor eax, eax                 A3 7CEC6500  mov [65EC7C], eax   ; show count = 0
0049500D  A3 7CEC6500  mov [65EC7C], eax            A3 8CDC5100  mov [51DC8C], eax   ; drawn = 0   (new)
00495012  B8 01000000  mov eax, 1                   B8 01000000  mov eax, 1          (now at 00495014)
00495017  5D           pop ebp                      5D           pop ebp
00495018  C3           ret                          C3           ret
00495019  00 00 00     padding                      00           padding
```

File offsets (CODE section VA 0x401000 = raw 0x800, so offset = VA − 0x400800):

| File offset | Length | Original | Patched |
|-------------|--------|----------|---------|
| `0x947C1` | 1 | `50` | `52` |
| `0x94803` | 24 | `33 C9 89 0D 9C DC 51 00 33 C0 A3 7C EC 65 00 B8 01 00 00 00 5D C3 00 00` | `33 C0 A3 9C DC 51 00 A3 7C EC 65 00 A3 8C DC 51 00 B8 01 00 00 00 5D C3` |

The only jump into the rewritten tail is the routine's own `je` at `0x494FC0`; `0x49501C` (the next function)
is untouched. With drawn = 0 the restore path in `0x494449` takes its normal "no cursor on screen" branch:
copy the screen into the scratch bitmap, allocate a new save-under, draw.

### What else changes

Nothing outside minimise/restore. The shutdown routine runs only from the `WM_ACTIVATE(0)` handler and at
exit, and the only behavioural difference is that after a shutdown the cursor module no longer believes a
cursor is on screen. Cursor drawing, the cursor thread, the save-under logic and the menu compositor are
unchanged.

## 7. Verification

- Unpatched EXE, no debugger: minimise + restore → process gone within 5 s, Event ID 1000 with the fault above.
- Patched EXE under `cdb` with all first-chance exceptions logged: two minimise/restore cycles, no exception of
  any kind, `Responding = True`, screenshot shows the main menu redrawn with the cursor in place.
- Not yet exercised: minimise during a running match (as opposed to the main menu). The same handler and the
  same cursor module are involved, so it is expected to behave the same.

## 8. Reporting to GOG

Same routes as in `AI-Hang-Investigation.md` §7. Draft text:

```
Deadlock II: Shrine Wars (GOG build, game v1.20) crashes when the full-screen game is minimised and
restored (Alt-Tab or Win key). The screen stays black; the Application event log shows exception
0xC000041D at fault offset 0x0008C969 in DEADLOCK.EXE.

Cause: on WM_ACTIVATE(inactive) the game frees its software-cursor save-under bitmap (routine at
0x00494FB6) but leaves the "cursor is drawn" flag at 0x0051DC8C set, because the preceding hide call
is skipped while the cursor show-count is 0 (the main menu paints the cursor directly). On
WM_ACTIVATE(active) the redraw at 0x00494449 trusts that flag and blits from the NULL pointer.

Fix: clear the flag in the shutdown routine. Change file offset 0x947C1 from 50 to 52 and the 24 bytes
at file offset 0x94803 from
  33 C9 89 0D 9C DC 51 00 33 C0 A3 7C EC 65 00 B8 01 00 00 00 5D C3 00 00
to
  33 C0 A3 9C DC 51 00 A3 7C EC 65 00 A3 8C DC 51 00 B8 01 00 00 00 5D C3
Verified: the patched EXE survives repeated minimise/restore and redraws correctly.
```
