// =====================================================================================================
//  Patcher.cs - byte patcher for Deadlock II: Shrine Wars v1.20 (the GOG build of DEADLOCK.EXE)
// =====================================================================================================
//
//  WHAT THIS PROGRAM IS
//  --------------------
//  Deadlock II is a 1998 Borland C++ program. There is no source code, so bugs are fixed by changing
//  a handful of machine-code bytes directly in DEADLOCK.EXE. This file is the complete list of those
//  changes plus the checks that make applying them safe. installer.cmd compiles it with the C# compiler
//  that is part of every Windows installation (%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe)
//  and runs the result, so nothing has to be installed and the reader can see exactly what is written.
//
//  The code deliberately sticks to C# 5 / .NET Framework 4.0 syntax (no string interpolation, no
//  "nameof", no expression-bodied members, no tuples): that is the newest language the bundled
//  compiler understands.
//
//  HOW A PATCH IS DESCRIBED
//  ------------------------
//  Each Patch has
//    * a Context: a run of bytes that must be found at a fixed offset before the patch is considered
//      at all. It proves that this really is the v1.20 GOG build at the expected code layout, and not
//      some other version where the same offsets hold different instructions.
//    * one or more Sites: a file offset and two equally long byte runs, Original and Patched. A site
//      is "applied" when it holds Patched and "not applied" when it holds Original; anything else
//      means the file was modified by something we do not know about, and we refuse to touch it.
//
//  Addresses are given both as virtual addresses (VA, what a debugger or disassembler shows while the
//  game runs) and as file offsets (where the bytes sit inside DEADLOCK.EXE). The CODE section is
//  mapped at VA 0x00401000 and starts at raw file offset 0x800, so for every code byte
//
//        file offset = VA - 0x00401000 + 0x800 = VA - 0x400800
//
//  The EXE has no relocations and is always loaded at image base 0x00400000, so VAs are stable.
//
//  =================================================================================================
//  BUG 1 - THE AI TURN THAT NEVER ENDS                     (write-up: ..\AI-Hang-Investigation.md)
//  =================================================================================================
//
//  Symptom: during an AI player's turn the game stops responding and one CPU core sits at 100%
//  forever. Loading the autosave and ending the turn again reproduces it every time.
//
//  Mechanism: each AI race keeps a linked list of "jobs" (build building B, obtain resource R, ...).
//  The scheduler at VA 0x00405AAC first clears a "done this pass" flag on every job and then loops:
//  pick the highest-priority job whose done flag is clear, run its handler, repeat until no undone
//  job is left. Jobs created while the pass runs are processed in the same pass. That is intended -
//  it lets the AI go from "I am short of resource R" to "queue R's producer building" to "request
//  the resources that building costs" within a single turn. The loop has no iteration cap; it relies
//  on the prerequisite graph being acyclic.
//
//  The "request resource" routine at VA 0x00408B58 is called by the "build building" handler for
//  every resource cost the race cannot produce. If an "obtain resource" job for that resource already
//  exists, the routine merges the request into it: adds the amount, raises the priority to 5001
//  ("process again immediately") and, at VA 0x00408BAB, clears the job's done flag:
//
//        00408BA9  33 C0        xor eax, eax
//        00408BAB  89 46 0C     mov dword ptr [esi+0Ch], eax      ; esi = existing job, +0Ch = done flag
//        00408BAE  8B 55 18     mov edx, dword ptr [ebp+18h]
//        00408BB1  01 56 20     add dword ptr [esi+20h], edx      ; needed amount += request
//
//  This is the ONLY place in the job system that marks an already-processed job as not done (the two
//  other create-or-merge routines, 0x00407D60 and 0x00408288, only raise the priority). When the
//  prerequisite chain is circular - building 14 needs resources whose producers need building 14 or 18
//  in the captured save - the "obtain resource" job spawns a "build prerequisite" job, that job
//  re-arms the "obtain resource" job in the same pass, and the pass never ends. The scheduler's own
//  anti-spin rule (a job that reports "not done" loses 10 priority each time) never fires, because
//  every job in the cycle reports done.
//
//  The fix (PATCH 1): replace the 3-byte store with three NOPs. The merged job still gets the larger
//  amount and the raised priority, but it keeps done = 1 and is not run again in this pass. All
//  "obtain resource" jobs are freed at the end of every pass anyway (routine 0x00405948), and the
//  planner recreates the request on the next turn. Net effect on AI decisions: none on turns without
//  a mid-pass prerequisite chain; a one-turn delay on second-order production decisions otherwise;
//  and a once-per-turn harmless retry instead of a hang when the chain is circular.
//
//  =================================================================================================
//  BUG 2 - CRASH WHEN THE MINIMISED GAME IS RESTORED  (write-up: ..\Minimize-Crash-Investigation.md)
//  =================================================================================================
//
//  Symptom: Alt-Tab or the Win key away from the full-screen game and back. The screen stays black
//  and the game looks hung. It is actually dead: the Application event log shows exception
//  0xC000041D (STATUS_FATAL_USER_CALLBACK_EXCEPTION, "an access violation escaped from a window
//  procedure") at fault offset 0x0008C969. The Windows Error Reporting dialog sits behind the dead
//  full-screen window, which is why it looks like a hang. Reproduces every time, already at the main
//  menu.
//
//  Mechanism: the game draws its own mouse cursor into the DirectDraw surface and keeps the
//  background under it in a "save-under" bitmap, pointer at [0x0065ECB8]. A flag at [0x0051DC8C]
//  says "a cursor is currently drawn and the save-under is valid". The relevant routines:
//
//        0x004943AB  save under:  allocate the bitmap, copy the screen into it, set drawn = 1
//        0x00494B6B  restore:     blit the bitmap back, drawn = 0  (only if the pointer is non-NULL)
//        0x00494B06  hide cursor: returns early unless a shape is set, the module is initialised
//                                 AND the cursor show-count [0x0065EC7C] is not zero
//        0x00494449  draw at the new position: if drawn = 1 and the old and new cursor rectangles
//                                 overlap, merge the old save-under into the new one. It reads
//                                 [0x0065ECB8] WITHOUT a NULL check.
//        0x00494FB6  shutdown:    hide cursor, stop the cursor thread, free the save-under and the
//                                 scratch bitmap, set the pointers to NULL, initialised = 0, count = 0
//
//  WM_ACTIVATE(inactive), i.e. minimise, calls shutdown. WM_ACTIVATE(active), i.e. restore,
//  re-initialises the module and shows the cursor, which ends in the draw routine.
//
//  The main menu paints the cursor itself by calling "save under" directly (from 0x00444916) while
//  the show-count stays 0. So at minimise time the state is: drawn = 1, save-under allocated,
//  show-count = 0. Shutdown's hide call sees count = 0 and does nothing, so the save-under is never
//  restored and drawn stays 1. Shutdown then frees the bitmap and NULLs the pointer. On restore the
//  draw routine trusts drawn = 1, finds that the stale drawn rectangle overlaps the new one, takes the
//  merge path and blits from the NULL pointer:
//
//        0048C969  3B 56 04     cmp edx, dword ptr [esi+4]        ; esi = source bitmap = NULL -> AV
//
//  The fix (PATCH 2): make shutdown clear the drawn flag when it frees the save-under. "Nothing is
//  saved" must imply "nothing is drawn". The tail of the shutdown routine is rewritten in place; the
//  three padding bytes after its "ret" give exactly the room needed for one more 5-byte store:
//
//        VA        before                                 after
//        00494FC0  74 50  je 00495012                      74 52  je 00495014   (SITE A, 1 byte)
//        ...
//        00495003  33 C9           xor ecx, ecx            33 C0           xor eax, eax      (SITE B,
//        00495005  89 0D 9CDC5100  mov [51DC9C], ecx       A3 9CDC5100     mov [51DC9C], eax   24 bytes)
//        0049500B  33 C0           xor eax, eax            A3 7CEC6500     mov [65EC7C], eax
//        0049500D  A3 7CEC6500     mov [65EC7C], eax       A3 8CDC5100     mov [51DC8C], eax   <- new
//        00495012  B8 01000000     mov eax, 1              B8 01000000     mov eax, 1          (now 495014)
//        00495017  5D              pop ebp                 5D              pop ebp
//        00495018  C3              ret                     C3              ret
//        00495019  00 00 00        padding                 00              padding
//
//  Byte by byte:
//    * "mov [imm32], ecx" is a 6-byte instruction (89 0D + address) while "mov [imm32], eax" is a
//      5-byte short form (A3 + address). Using eax for all three stores saves one byte and makes the
//      second "xor" unnecessary, which saves two more. Those three bytes plus the three padding bytes
//      pay for the new 5-byte store and the one extra byte the rewritten tail needs.
//    * The function's early exit ("not initialised -> return 1") jumps straight to "mov eax, 1".
//      That instruction moves from 0x00495012 to 0x00495014, so the jump's 8-bit displacement at
//      0x00494FC1 grows by 2: 0x50 -> 0x52. It is the only jump into this tail (checked with a full
//      disassembly of the CODE section), and the next function still starts at 0x0049501C.
//    * Everything else - what is freed, the mutex, the thread shutdown, the return value - is
//      unchanged. After a restore the draw routine now takes its normal "no cursor on screen" branch:
//      copy the screen into the scratch bitmap, allocate a fresh save-under, draw.
//
//  Verified by starting the patched game under a debugger that logs every first-chance exception and
//  minimising/restoring it twice: no exception, the menu redrawn with the cursor in place.
//
//  =================================================================================================
//  BUG 3 - THE MINIMISED GAME CANNOT BE CLOSED    (write-up: ..\Close-While-Minimized-Investigation.md)
//  =================================================================================================
//
//  Symptom: with the game minimised, "Close window" from the taskbar or Alt+F4 does nothing; the
//  process has to be killed.
//
//  Mechanism: the WM_CLOSE handler at 0x00472DA4 always pops up an in-game confirmation dialog
//  ("Are you sure you want to exit Deadlock 2?" in the menu, a quit/save dialog during a match). The
//  dialog is drawn inside the DirectDraw surface, so while the window is minimised it is invisible,
//  and the request is effectively swallowed. Setting the game's quit flags instead of showing the
//  dialog does not help: while minimised the GOG ddraw wrapper parks the main thread in its own
//  message-pumping wait loop that never returns to the game, and after a forced restore the main
//  menu ignores those flags anyway (its only working exit is its own X button).
//
//  The fix (PATCH 3): the handler's first instruction (5 bytes) becomes a jump into a 37-byte code
//  cave in the zero-filled tail of the CODE section (0x004B4434; the import thunk table ends at
//  0x004B4430 and nothing references the padding). The cave asks Windows whether the game's main
//  window ([0x58F1A4]) is minimised - GetWindowLongA(hwnd, GWL_STYLE) & WS_MINIMIZE, through the
//  game's own import thunks - and if so calls ExitProcess(0). Otherwise it executes the displaced
//  instruction and jumps back, so with the window visible the dialog appears exactly as before.
//  Nothing is lost that "quit without saving" would keep: AUTOSAVE.SAV is rewritten every turn.
//
//        00472DA4  A1 54F15900           mov eax,[59F154]      ->  E9 8B160400   jmp 004B4434
//        004B4434  6A F0                 push -16              ; GWL_STYLE
//        004B4436  FF35 A4F15800         push [58F1A4]         ; main window handle
//        004B443C  E8 1AFDFFFF           call 004B415B         ; thunk -> USER32!GetWindowLongA
//        004B4441  A9 00000020           test eax, 20000000h   ; WS_MINIMIZE
//        004B4446  74 07                 jz 004B444F
//        004B4448  6A 00                 push 0
//        004B444A  E8 56FBFFFF           call 004B3FA5         ; thunk -> KERNEL32!ExitProcess
//        004B444F  A1 54F15900           mov eax,[59F154]      ; displaced instruction
//        004B4454  E9 50E9FBFF           jmp 00472DA9          ; back into the handler
//
//  =================================================================================================
//  USAGE
//  =================================================================================================
//
//    Patcher.exe [path\to\DEADLOCK.EXE] [/status] [/restore] [/scriptdir:<dir>]
//
//      (no switch)  apply every patch the file is still missing; back up to DEADLOCK.EXE.orig first
//      /status      only report which patches are applied, change nothing
//      /restore     copy DEADLOCK.EXE.orig back over DEADLOCK.EXE
//      /scriptdir   an extra folder to look for DEADLOCK.EXE in (installer.cmd passes its own folder)
//
//    Exit code 0 = done or nothing to do, 1 = refused or failed, 2 = bad arguments.
// =====================================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DeadlockPatcher
{
    /// <summary>One contiguous run of bytes that a patch changes.</summary>
    sealed class Site
    {
        public uint VirtualAddr;   // where the bytes are while the game runs (for the log line)
        public int Offset;         // where the bytes are inside the file (= VirtualAddr - 0x400800)
        public byte[] Original;    // what the untouched GOG v1.20 EXE holds here
        public byte[] Patched;     // what we write; always the same length as Original, so nothing moves
    }

    /// <summary>One bug fix: a context check plus the sites it rewrites.</summary>
    sealed class Patch
    {
        public string Id;
        public string Name;
        public int ContextOffset;  // the Context bytes must be found here, or the patch does not apply
        public byte[] Context;     // proves the surrounding code is the one the patch was written for
        public Site[] Sites;
    }

    enum PatchState
    {
        WrongCode,   // the context bytes do not match: wrong build, do not touch
        Patched,     // every site already holds the patched bytes
        Original,    // every site still holds the original bytes: safe to apply
        Unknown      // sites are mixed or hold something else: somebody else changed this file
    }

    static class Program
    {
        // The GOG v1.20 DEADLOCK.EXE is exactly this long. A different size means a different build
        // (or a file that is not Deadlock II at all), and every offset below would be meaningless.
        const int ExpectedSize = 1738752;

        static readonly Patch[] Patches = new[]
        {
            // -----------------------------------------------------------------------------------------
            // PATCH 1 - AI turn hang. See "BUG 1" at the top of the file.
            // -----------------------------------------------------------------------------------------
            new Patch
            {
                Id = "AIHang",
                Name = "AI turn hang (job scheduler infinite loop)",

                // Context: the 8 bytes immediately before the store we remove, i.e. the tail of the
                // "does an equivalent job already exist?" test in the request-resource routine:
                //     00408BA3  8B F0     mov  esi, eax       ; esi = existing job found by 0x4058E0, or 0
                //     00408BA5  85 F6     test esi, esi
                //     00408BA7  74 32     je   00408BDB       ; no existing job -> create a new one
                //     00408BA9  33 C0     xor  eax, eax       ; eax = 0, about to be stored into the flag
                ContextOffset = 0x83A3,
                Context = new byte[] { 0x8B, 0xF0, 0x85, 0xF6, 0x74, 0x32, 0x33, 0xC0 },

                Sites = new[]
                {
                    new Site
                    {
                        // 00408BAB  89 46 0C   mov dword ptr [esi+0Ch], eax   ; job.done = 0  <- the bug
                        // Three NOPs keep every other instruction where it is. The following
                        // "add [esi+20h], edx" (merge the requested amount) and the priority raise
                        // still run, so the merged request is not lost, only not re-run this pass.
                        VirtualAddr = 0x00408BAB, Offset = 0x83AB,
                        Original = new byte[] { 0x89, 0x46, 0x0C },
                        Patched  = new byte[] { 0x90, 0x90, 0x90 }
                    }
                }
            },

            // -----------------------------------------------------------------------------------------
            // PATCH 2 - crash on restore after minimise. See "BUG 2" at the top of the file.
            // -----------------------------------------------------------------------------------------
            new Patch
            {
                Id = "MinimizeCrash",
                Name = "Crash on restore after minimise (cursor save-under)",

                // Context: the start of the cursor shutdown routine at VA 0x00494FB6, up to and
                // including the opcode of the jump whose displacement SITE A changes:
                //     00494FB6  55                   push ebp
                //     00494FB7  8B EC                mov  ebp, esp
                //     00494FB9  83 3D 9CDC5100 00    cmp  dword ptr [51DC9C], 0   ; module initialised?
                //     00494FC0  74 ..                je   <return 1>              ; not initialised -> nothing to free
                // The displacement byte itself is left out of the context because it is what we patch.
                ContextOffset = 0x947B6,
                Context = new byte[] { 0x55, 0x8B, 0xEC, 0x83, 0x3D, 0x9C, 0xDC, 0x51, 0x00, 0x00, 0x74 },

                Sites = new[]
                {
                    new Site
                    {
                        // SITE A: the "not initialised" early exit jumps to "mov eax, 1". SITE B moves
                        // that instruction two bytes further down (0x495012 -> 0x495014), so the
                        // 8-bit displacement grows from 0x50 to 0x52. Without this byte the early
                        // exit would land in the middle of the new "mov [51DC8C], eax" encoding.
                        VirtualAddr = 0x00494FC1, Offset = 0x947C1,
                        Original = new byte[] { 0x50 },
                        Patched  = new byte[] { 0x52 }
                    },
                    new Site
                    {
                        // SITE B: the tail of the shutdown routine, from the point where both bitmaps
                        // have already been freed to the end of the function including the padding.
                        VirtualAddr = 0x00495003, Offset = 0x94803,

                        Original = new byte[] {
                            0x33, 0xC9,                                 // 495003  xor ecx, ecx
                            0x89, 0x0D, 0x9C, 0xDC, 0x51, 0x00,         // 495005  mov [51DC9C], ecx   ; initialised = 0
                            0x33, 0xC0,                                 // 49500B  xor eax, eax
                            0xA3, 0x7C, 0xEC, 0x65, 0x00,               // 49500D  mov [65EC7C], eax   ; show count = 0
                            0xB8, 0x01, 0x00, 0x00, 0x00,               // 495012  mov eax, 1          ; return value
                            0x5D, 0xC3,                                 // 495017  pop ebp / ret
                            0x00, 0x00 },                               // 495019  2 of the 3 padding bytes before the next function

                        Patched = new byte[] {
                            0x33, 0xC0,                                 // 495003  xor eax, eax        ; one zero register is enough
                            0xA3, 0x9C, 0xDC, 0x51, 0x00,               // 495005  mov [51DC9C], eax   ; initialised = 0 (5-byte form, was 6)
                            0xA3, 0x7C, 0xEC, 0x65, 0x00,               // 49500A  mov [65EC7C], eax   ; show count = 0
                            0xA3, 0x8C, 0xDC, 0x51, 0x00,               // 49500F  mov [51DC8C], eax   ; drawn = 0  <- THE FIX:
                                                                        //         the save-under was just freed, so no
                                                                        //         cursor can be "drawn" any more
                            0xB8, 0x01, 0x00, 0x00, 0x00,               // 495014  mov eax, 1          ; same return value as before
                            0x5D, 0xC3 }                                // 495019  pop ebp / ret       ; third padding byte at 49501B untouched
                    }
                }
            },

            // -----------------------------------------------------------------------------------------
            // PATCH 3 - the minimised game cannot be closed. See "BUG 3" at the top of the file.
            // -----------------------------------------------------------------------------------------
            new Patch
            {
                Id = "CloseWhileMinimized",
                Name = "Close request ignored while minimised (WM_CLOSE dialog)",

                // Context: the WM_CLOSE handler's instructions right after the one we replace. The patched
                // instruction itself must not be part of the context, or an already-patched file would be
                // reported as a foreign build:
                //     00472DA9  85 C0          test eax, eax
                //     00472DAB  7E 2D          jle  00472DDA        ; no match running -> plain "really quit?" box
                //     00472DAD  BA C07A4500    mov  edx, 0x457AC0   ; main game screen handler
                ContextOffset = 0x725A9,
                Context = new byte[] { 0x85, 0xC0, 0x7E, 0x2D, 0xBA, 0xC0, 0x7A, 0x45, 0x00 },

                Sites = new[]
                {
                    new Site
                    {
                        // SITE A: the handler's first instruction becomes the jump into the cave.
                        VirtualAddr = 0x00472DA4, Offset = 0x725A4,
                        Original = new byte[] { 0xA1, 0x54, 0xF1, 0x59, 0x00 },     // mov eax, [0x59F154]
                        Patched  = new byte[] { 0xE9, 0x8B, 0x16, 0x04, 0x00 }      // jmp 0x004B4434
                    },
                    new Site
                    {
                        // SITE B: the cave, in the zero padding after the import thunks (0x4B4431..0x4B45FF).
                        VirtualAddr = 0x004B4434, Offset = 0xB3C34,
                        Original = new byte[37],                                    // 37 zero bytes
                        Patched  = new byte[] {
                            0x6A, 0xF0,                                 // push -16                   ; GWL_STYLE
                            0xFF, 0x35, 0xA4, 0xF1, 0x58, 0x00,         // push dword ptr [58F1A4]    ; main window handle
                            0xE8, 0x1A, 0xFD, 0xFF, 0xFF,               // call 004B415B              ; -> GetWindowLongA
                            0xA9, 0x00, 0x00, 0x00, 0x20,               // test eax, 20000000h        ; WS_MINIMIZE
                            0x74, 0x07,                                 // jz  +7                     ; not minimised
                            0x6A, 0x00,                                 // push 0
                            0xE8, 0x56, 0xFB, 0xFF, 0xFF,               // call 004B3FA5              ; -> ExitProcess, never returns
                            0xA1, 0x54, 0xF1, 0x59, 0x00,               // mov eax, [59F154]          ; displaced instruction
                            0xE9, 0x50, 0xE9, 0xFB, 0xFF }              // jmp 00472DA9               ; back into the handler
                    }
                }
            }
        };

        // =========================================================================================
        //  Entry point and argument handling
        // =========================================================================================

        static int Main(string[] args)
        {
            string exePath = null, scriptDir = null;
            bool statusOnly = false, restore = false;

            // Accept /switch, -switch and --switch spellings; the one non-switch argument is the EXE.
            foreach (string a in args)
            {
                string lower = a.ToLowerInvariant();
                if (lower == "/status" || lower == "-status" || lower == "--status") statusOnly = true;
                else if (lower == "/restore" || lower == "-restore" || lower == "--restore") restore = true;
                else if (lower.StartsWith("/scriptdir:")) scriptDir = a.Substring("/scriptdir:".Length).Trim('"');
                else if (lower == "/?" || lower == "-h" || lower == "--help" || lower == "/help") { Usage(); return 2; }
                else if (exePath == null) exePath = a;
                else { Console.Error.WriteLine("Unexpected argument: " + a); Usage(); return 2; }
            }

            try
            {
                exePath = LocateExe(exePath, scriptDir);
                Console.WriteLine("DEADLOCK.EXE: " + exePath);
                if (restore) return Restore(exePath);
                if (statusOnly) { ReportStatus(exePath); return 0; }
                return Apply(exePath);
            }
            catch (Exception ex)
            {
                // Every refusal below is thrown as an exception with a human-readable message, so one
                // handler turns them all into "ERROR: ..." plus exit code 1 for the calling script.
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        static void Usage()
        {
            Console.WriteLine("Usage: Patcher.exe [path\\to\\DEADLOCK.EXE] [/status] [/restore]");
            Console.WriteLine("  (no switch)  apply the patches the file is still missing; backup to DEADLOCK.EXE.orig");
            Console.WriteLine("  /status      report which patches are applied, change nothing");
            Console.WriteLine("  /restore     copy DEADLOCK.EXE.orig back over DEADLOCK.EXE");
        }

        // Finds the EXE to work on. Candidate order: explicit argument, the folder installer.cmd lives
        // in (people drop the patcher into the game folder), the current folder, and finally the GOG
        // installer's registry entry. The registry path can be stale when the game was moved, which is
        // why it comes last and why every candidate is checked with File.Exists.
        static string LocateExe(string explicitPath, string scriptDir)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
                // Allow passing the game folder instead of the file.
                if (Directory.Exists(explicitPath)) explicitPath = Path.Combine(explicitPath, "DEADLOCK.EXE");
                if (!File.Exists(explicitPath)) throw new FileNotFoundException("File not found: " + explicitPath);
                return Path.GetFullPath(explicitPath);
            }
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(scriptDir)) candidates.Add(Path.Combine(scriptDir, "DEADLOCK.EXE"));
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "DEADLOCK.EXE"));
            string gogDir = ReadGogPath();
            if (gogDir != null) candidates.Add(Path.Combine(gogDir, "DEADLOCK.EXE"));

            foreach (string c in candidates)
                if (File.Exists(c)) return Path.GetFullPath(c);

            throw new FileNotFoundException(
                "DEADLOCK.EXE not found. Looked in: " + string.Join("; ", candidates.ToArray()) +
                ". Pass the path as the first argument.");
        }

        // GOG Galaxy / the GOG installer records the install folder under the game's product id
        // (1207660543 = Deadlock II: Shrine Wars). The game is 32-bit, so the key lives in the 32-bit
        // registry view; RegistryView.Registry32 finds it from either a 32- or a 64-bit process.
        static string ReadGogPath()
        {
            try
            {
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                                            .OpenSubKey(@"SOFTWARE\GOG.com\Games\1207660543"))
                {
                    if (key == null) return null;
                    return key.GetValue("path") as string;
                }
            }
            catch { return null; }   // no GOG install, or no permission: simply not a candidate
        }

        // =========================================================================================
        //  Checks
        // =========================================================================================

        static bool BytesEqual(byte[] a, int offset, byte[] b)
        {
            if (offset < 0 || offset + b.Length > a.Length) return false;
            for (int i = 0; i < b.Length; i++) if (a[offset + i] != b[i]) return false;
            return true;
        }

        static string Hex(byte[] b, int offset, int len)
        {
            var parts = new string[len];
            for (int i = 0; i < len; i++) parts[i] = b[offset + i].ToString("X2");
            return string.Join(" ", parts);
        }

        // Classifies one patch against the loaded file. The context check comes first because if the
        // surrounding code is not what we expect, the Original/Patched comparison would be meaningless:
        // random bytes could coincidentally equal "90 90 90".
        static PatchState StateOf(byte[] file, Patch p, out string detail)
        {
            if (!BytesEqual(file, p.ContextOffset, p.Context))
            {
                detail = string.Format("code at 0x{0:X} does not match: {1}", p.ContextOffset,
                                       Hex(file, p.ContextOffset, p.Context.Length));
                return PatchState.WrongCode;
            }
            int patched = 0, original = 0;
            foreach (Site s in p.Sites)
            {
                if (BytesEqual(file, s.Offset, s.Patched)) patched++;
                else if (BytesEqual(file, s.Offset, s.Original)) original++;
            }
            // A multi-site patch must be all-or-nothing. Half of PATCH 2 (the jump fixed but not the
            // tail, or the other way round) would crash on the "not initialised" exit, so a mixed
            // state is reported as Unknown and never "completed" automatically.
            if (patched == p.Sites.Length) { detail = "applied"; return PatchState.Patched; }
            if (original == p.Sites.Length) { detail = "not applied"; return PatchState.Original; }
            detail = "bytes at the patch site(s) are neither original nor patched";
            return PatchState.Unknown;
        }

        // Reads the whole file (1.7 MB, trivially small) and enforces the size check up front.
        static byte[] ReadChecked(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            if (file.Length != ExpectedSize)
                throw new InvalidDataException(string.Format(
                    "File is {0} bytes, expected {1} (GOG v1.20 build). Refusing to touch it.", file.Length, ExpectedSize));
            return file;
        }

        // Windows locks the image of a running process, so writing DEADLOCK.EXE while the game is open
        // would fail with an unhelpful sharing-violation error (or, with /restore, half-succeed).
        // Check up front and say so plainly. If we cannot read a DEADLOCK process's path (different
        // user, elevation), err on the side of refusing.
        static void EnsureGameClosed(string path)
        {
            string full = Path.GetFullPath(path);
            foreach (Process pr in Process.GetProcessesByName("DEADLOCK"))
            {
                string running = null;
                try { running = pr.MainModule.FileName; } catch { }
                if (running == null || string.Equals(Path.GetFullPath(running), full, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("DEADLOCK.EXE is running (PID " + pr.Id + "). Close the game first.");
            }
        }

        // =========================================================================================
        //  Actions
        // =========================================================================================

        // /status: read-only, so it does not care whether the game is running.
        static void ReportStatus(string path)
        {
            byte[] file = ReadChecked(path);
            foreach (Patch p in Patches)
            {
                string detail;
                PatchState st = StateOf(file, p, out detail);
                Console.WriteLine("  [{0}] {1}: {2}", st == PatchState.Patched ? "x" : " ", p.Name, detail);
            }
            Console.WriteLine(File.Exists(path + ".orig") ? "Backup present: " + path + ".orig" : "No backup (DEADLOCK.EXE.orig) yet.");
        }

        static int Apply(string path)
        {
            EnsureGameClosed(path);
            byte[] file = ReadChecked(path);

            // Decide per patch. Already-applied patches are skipped so the program can be re-run after
            // a new patch is added to this file; anything unexpected aborts before a single byte moves.
            var todo = new List<Patch>();
            foreach (Patch p in Patches)
            {
                string detail;
                PatchState st = StateOf(file, p, out detail);
                switch (st)
                {
                    case PatchState.Patched:  Console.WriteLine("  {0}: already applied.", p.Name); break;
                    case PatchState.Original: todo.Add(p); break;
                    default: throw new InvalidDataException(p.Name + ": " + detail + ". Refusing to patch.");
                }
            }
            if (todo.Count == 0) { Console.WriteLine("All patches already applied. Nothing to do."); return 0; }

            // The backup is written once and never overwritten: the first run sees the pristine GOG
            // file, and later runs (adding a newer patch) must not replace that pristine copy with an
            // already-patched one, or /restore would stop meaning "back to the original".
            string backup = path + ".orig";
            if (!File.Exists(backup))
            {
                File.Copy(path, backup);
                Console.WriteLine("Backup written to " + backup);
            }
            else Console.WriteLine("Backup already exists at " + backup + " (kept).");

            // Patch in memory, then write the whole file once. Patched and Original are always the
            // same length, so no byte outside the listed sites changes.
            foreach (Patch p in todo)
                foreach (Site s in p.Sites)
                    Array.Copy(s.Patched, 0, file, s.Offset, s.Patched.Length);
            File.WriteAllBytes(path, file);

            // Read back and confirm every site landed. Guards against a write that silently went to
            // an antivirus-quarantined or cloud-synced placeholder instead of the real file.
            byte[] written = File.ReadAllBytes(path);
            foreach (Patch p in todo)
                foreach (Site s in p.Sites)
                {
                    if (!BytesEqual(written, s.Offset, s.Patched))
                        throw new IOException(string.Format("Verification failed after write at offset 0x{0:X}.", s.Offset));
                    Console.WriteLine("  {0}: patched file offset 0x{1:X} (VA 0x{2:X8}): {3} -> {4}",
                        p.Name, s.Offset, s.VirtualAddr, Hex(s.Original, 0, s.Original.Length), Hex(s.Patched, 0, s.Patched.Length));
                }
            Console.WriteLine("Done.");
            return 0;
        }

        // /restore: the backup is the untouched GOG file, so copying it back removes every patch at
        // once. All the bugs come back until the patcher is run again.
        static int Restore(string path)
        {
            EnsureGameClosed(path);
            string backup = path + ".orig";
            if (!File.Exists(backup)) throw new FileNotFoundException("No backup found at " + backup);
            File.Copy(backup, path, true);
            Console.WriteLine("Restored original from " + backup);
            return 0;
        }
    }
}
