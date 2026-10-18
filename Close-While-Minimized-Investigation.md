# Deadlock II: Shrine Wars — the minimised game cannot be closed: investigation and fix

Date: 2026-10-18
Game: Deadlock II: Shrine Wars v1.20, GOG build (`DEADLOCK.EXE`, 1,738,752 bytes), through GOG's bundled `ddraw.dll` wrapper.
Status: root cause found, 42-byte patch verified (menu and in-game), carried by `launcher\`, `patcher\` and `Patch-DeadlockAIHang.ps1`.

Companion to `AI-Hang-Investigation.md` (tooling) and `Minimize-Crash-Investigation.md` (the minimise/restore crash that had to be fixed first to make this one visible).

---

## 1. Symptom

Minimise the game. "Close window" from the taskbar, or Alt+F4, does nothing. The only way out is to kill the process.

## 2. What the close request does

`WM_CLOSE` (and `WM_SYSCOMMAND SC_CLOSE`, which Windows turns into `WM_CLOSE`) reaches the game's window
procedure through the wrapper's subclass (`DDRAW.dll` → `CallWindowProcA` → `MainWndProc 0x473940`).
The jump table at `0x47396C` sends message `0x10` to the handler at `0x472DA4`:

```
00472DA4  mov eax,[0x59F154]            ; a match is in progress?
          test eax,eax ; jle menu_case
          cmp [0x4D02B8],0x457AC0       ; and the current screen is the main game screen?
          jne menu_case
          call 0x423D84                 ; in-game "quit? / save?" dialog: 2 = cancel, 1 = save first, else quit
          ...
menu_case:
          call 0x42836C(...,0x18,0,4)   ; "Are you sure you want to exit Deadlock 2?"  2 = no
          cmp eax,2 ; je done
          mov [0x58F1EC],1              ; the quit flag
done:     ret
```

Both dialogs are drawn inside the DirectDraw surface. While the window is minimised they are invisible,
so the request is effectively swallowed.

## 3. Why the gentle fixes did not work

Everything below was traced with the x86 `cdb` attached to the running game (breakpoints inside the
handler and the cave, non-invasive stack samples afterwards; command files in `debug\`).

1. **The game's own "minimised" flag is dead under the wrapper.** The `WM_SIZE` handler (`0x472F2C`)
   stores `wParam == SIZE_MINIMIZED` at `[0x4D63D4]`, and two places in the main loop read it. But the
   GOG wrapper never delivers `WM_SIZE(SIZE_MINIMIZED)` to the game's window procedure, so the flag
   stays 0 and a check on it does nothing.
2. **Setting the quit flag `[0x58F1EC] = 1` (what the dialog's "yes" does) is never acted on.** While
   minimised the wrapper parks the main thread in its own wait loop that pumps messages (that is how
   the handler ran at all) but never returns to the game loop.
3. **Setting the flag and restoring the window with `ShowWindow(SW_RESTORE)`**: the game loop resumes,
   and the main menu ignores the flag and carries on. The flag is only consumed by the in-game screen
   loop and by various wait loops; the menu loop exits only through its own button codes (its X button
   returns 8 → `0x43611C` → `main` returns).
4. **Setting both quit flags (`[0x58F1EC]` and `[0x58F1F0]`, as `WM_QUIT` and `WM_DESTROY` do) plus
   restore**: same result.

A side finding confirms the picture: with the window *visible*, posting `WM_CLOSE` in the main menu
shows the "Are you sure you want to exit Deadlock 2?" dialog, and answering **yes** closes the dialog,
sets the flag, and leaves the game sitting in the menu. The menu never honoured that path in this
build; only its X button quits.

## 4. The fix

The handler's first instruction becomes a jump into a code cave; the cave asks Windows whether the game
window is minimised and, if so, exits the process. Otherwise it runs the displaced instruction and
jumps back, so with the window visible everything behaves as before.

```
VA        before                           after
00472DA4  A1 54F15900  mov eax,[59F154]    E9 8B160400   jmp 004B4434

004B4434  (zero padding)                   6A F0            push -16                 ; GWL_STYLE
004B4436                                   FF35 A4F15800    push [58F1A4]            ; the game's main window handle
004B443C                                   E8 1AFDFFFF      call 004B415B            ; thunk -> USER32!GetWindowLongA
004B4441                                   A9 00000020      test eax, 20000000h      ; WS_MINIMIZE
004B4446                                   74 07            jz 004B444F
004B4448                                   6A 00            push 0
004B444A                                   E8 56FBFFFF      call 004B3FA5            ; thunk -> KERNEL32!ExitProcess
004B444F                                   A1 54F15900      mov eax,[59F154]         ; displaced instruction
004B4454                                   E9 50E9FBFF      jmp 00472DA9             ; back into the handler
```

| File offset | Length | Original | Patched |
|-------------|--------|----------|---------|
| `0x725A4` | 5 | `A1 54 F1 59 00` | `E9 8B 16 04 00` |
| `0xB3C34` | 37 | 37 × `00` | `6A F0 FF 35 A4 F1 58 00 E8 1A FD FF FF A9 00 00 00 20 74 07 6A 00 E8 56 FB FF FF A1 54 F1 59 00 E9 50 E9 FB FF` |

Notes:
- The cave sits in the zero-filled tail of the CODE section (`0x4B4431..0x4B45FF`, inside the file's raw
  section data). The import thunk table ends at `0x4B4430`; a disassembly sweep finds no reference to
  the padding. The cave starts at the next 4-byte boundary.
- Both calls go through the game's own import thunks (`GetWindowLongA` at `0x4B415B`, `ExitProcess` at
  `0x4B3FA5`), so no import table change is needed. `[0x58F1A4]` is the main window handle, written at
  window creation (`0x46CCE2`) and zeroed on `WM_DESTROY`.
- `ExitProcess` skips the game's own shutdown. Nothing is lost that "quit without saving" would keep:
  `AUTOSAVE.SAV` is rewritten at the end of every turn, and the profile is written when it changes.
- The handler is a parameterless routine called from `MainWndProc` with `call 0x472DA4` followed by
  `xor eax,eax`, so the cave may clobber `eax/ecx/edx` freely and `ret` straight back.

## 5. Verification

Through `launcher\launch.cmd` with `fix_close=1` (the fix applied in memory):

| Case | Result |
|------|--------|
| Main menu, minimised, `WM_CLOSE` posted | process exits within 1 s, code 0 |
| In a match (Quick Start, landed), minimised, `WM_CLOSE` posted | process exits within 1 s, code 0 |
| Main menu, window visible, `WM_CLOSE` posted | "Are you sure you want to exit Deadlock 2?" dialog, process keeps running (unchanged) |

The same bytes are written on disk by `patcher\installer.cmd` and `Patch-DeadlockAIHang.ps1`; the three
tools recognise each other's state.
