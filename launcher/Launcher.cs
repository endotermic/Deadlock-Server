// =====================================================================================================
//  Launcher.cs - starts Deadlock II: Shrine Wars v1.20 (GOG) with every known fix and a configurable
//                resolution applied in memory, leaving DEADLOCK.EXE on disk untouched
// =====================================================================================================
//
//  WHAT THIS PROGRAM DOES
//  ----------------------
//  1. Reads deadlock2.cfg (resolution, windowed, bpp, which bug fixes to apply, where the EXE is).
//  2. Verifies on disk that the EXE is the GOG v1.20 build the patch tables were written for.
//  3. Starts DEADLOCK.EXE *suspended*: the image is mapped at its fixed base 0x400000 but no code has
//     run yet.
//  4. Rewrites bytes in the process memory:
//       - the two bug fixes (same bytes as patcher\Patcher.cs; see "THE FIXES" below), unless the
//         EXE on disk already carries them, in which case they are detected and skipped;
//       - every hard-coded 640x480 constant, when the configured resolution differs (see
//         "THE RESOLUTION TABLE" below).
//  5. Verifies each write by reading it back, resumes the process, waits for it and returns its
//     exit code. If any check fails, the suspended process is terminated before it ever runs.
//
//  Why in memory instead of on disk: one tool carries every change, any resolution can be tried by
//  editing a line, and the GOG files stay pristine (no backup juggling, nothing for Galaxy to
//  re-verify). patcher\Patcher.cs remains for people who want the fixes permanently in the EXE; both
//  tools recognise each other's state.
//
//  Written for C# 5 / .NET Framework 4.0 so the compiler that ships with Windows can build it
//  (see launch.cmd). No string interpolation, no newer syntax.
//
//  =================================================================================================
//  THE FIXES                       (write-ups: ..\AI-Hang-Investigation.md, ..\Minimize-Crash-Investigation.md)
//  =================================================================================================
//
//  FIX 1 - AI turn hang (fix_aihang). The AI job scheduler (0x00405AAC) re-runs every job of a race
//  until none is left undone in this pass. The "request resource" routine (0x00408B58) re-arms an
//  already-processed "obtain resource" job by clearing its done flag at 0x00408BAB:
//        89 46 0C   mov dword ptr [esi+0Ch], eax      ; eax = 0
//  With a circular building/resource prerequisite the pass never ends and the game sits at 100% CPU.
//  Fix: three NOPs. The merged request keeps its raised priority and amount but is not re-run in the
//  same pass; such jobs are freed at the end of every pass anyway and recreated next turn.
//
//  FIX 2 - crash when the minimised game is restored (fix_minimize). On WM_ACTIVATE(inactive) the
//  software-cursor shutdown routine (0x00494FB6) frees the cursor's save-under bitmap but leaves the
//  "cursor is drawn" flag [0x51DC8C] set, because the hide call before it is skipped while the show
//  count is 0 (the menu paints the cursor directly). On WM_ACTIVATE(active) the redraw trusts the
//  flag and blits from the NULL pointer: exception 0xC000041D, black screen. Fix: rewrite the tail
//  of the shutdown routine in place so it also clears the flag; the three padding bytes after its
//  "ret" provide the room, and the early-exit jump's displacement moves by two (0x50 -> 0x52).
//        494FC0  74 50 -> 74 52
//        495003  33 C9 89 0D 9CDC5100 33 C0 A3 7CEC6500 B8 01000000 5D C3 00 00
//            ->  33 C0 A3 9CDC5100 A3 7CEC6500 A3 8CDC5100 B8 01000000 5D C3
//
//  =================================================================================================
//  THE RESOLUTION TABLE                                        (write-up: ..\Resolution-Investigation.md)
//  =================================================================================================
//
//  The game has no resolution setting; 640x480 is baked in as literal immediates: the DirectDraw
//  SetDisplayMode call, the window size, two back buffers, the cursor clip rectangle, ~30 pairs of
//  "whole screen" rectangles passed to redraw/clip helpers, a few coordinate clamps, and the 600x400
//  map viewport. The table at the bottom rewrites all of them. With a resolution other than 640x480
//  the game runs and the map renders across the whole new surface, but the HUD, dialogs and frame
//  art are still 640x480 sprites at their old positions and nothing paints the area beside them -
//  it is the platform for finishing a high-resolution mod, not the finished mod.
//
//  USAGE
//    Launcher.exe [settings.cfg] [/dry] [/scriptdir:<dir>]
//      settings.cfg  default: deadlock2.cfg next to launch.cmd, else in the current folder
//      /dry          print the resolved settings and everything that would be patched; start nothing
//    Exit code: the game's exit code, 1 on launcher error, 2 on bad arguments.
// =====================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Deadlock2Launcher
{
    /// <summary>A run of bytes to rewrite in the running game. Original and Patched have equal length.</summary>
    sealed class BytePatch
    {
        public string Group;     // which fix / table it belongs to (for messages)
        public uint Addr;        // VA of the first byte
        public byte[] Original;  // what the untouched GOG v1.20 EXE holds
        public byte[] Patched;   // what we write
        public bool SkipIfAlreadyPatched;   // bug fixes may already be in the EXE on disk; size constants never are
        public string Note;
    }

    /// <summary>A bug fix: a context check plus the byte runs it rewrites.</summary>
    sealed class Fix
    {
        public string Id, Name;
        public uint ContextAddr; public byte[] Context;   // must match on disk or the fix is not for this build
        public BytePatch[] Patches;
    }

    enum SizeKind { Width, Height, ViewportW, ViewportH }

    /// <summary>One 32-bit immediate that encodes 640, 480, 600 or 400.</summary>
    sealed class SizeSite
    {
        public uint Addr; public SizeKind Kind; public string Note;
        public SizeSite(uint addr, SizeKind kind, string note) { Addr = addr; Kind = kind; Note = note; }
        public int OriginalValue
        {
            get
            {
                switch (Kind) { case SizeKind.Width: return 640; case SizeKind.Height: return 480; case SizeKind.ViewportW: return 600; default: return 400; }
            }
        }
    }

    sealed class Settings
    {
        public int Width = 640, Height = 480;
        public int ViewportW = -1, ViewportH = -1;   // -1 = derive from the screen size
        public bool Windowed = false;
        public int Bpp = 0;          // 0 = pass no colour-depth switch (the game's own default), 16 = -16, 8 = -8
        public bool FixAiHang = true, FixMinimize = true;
        public string Exe;
        public string ExtraArgs = "";
    }

    static class Native
    {
        public const uint CREATE_SUSPENDED = 0x00000004;
        public const uint PAGE_EXECUTE_READWRITE = 0x40;
        public const uint INFINITE = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb; public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateProcess(string lpApplicationName, string lpCommandLine, IntPtr pa, IntPtr ta,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint ResumeThread(IntPtr hThread);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateProcess(IntPtr hProcess, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, UIntPtr size, uint newProtect, out uint oldProtect);
    }

    static class Program
    {
        const int ExpectedSize = 1738752;   // GOG DEADLOCK.EXE v1.20; every address below is for this build
        const uint FileDelta = 0x400800;    // file offset = VA - FileDelta (CODE section: VA 0x401000 at raw 0x800)

        // =========================================================================================
        //  Entry point
        // =========================================================================================

        static int Main(string[] args)
        {
            string cfgPath = null, scriptDir = null; bool dry = false;
            foreach (string a in args)
            {
                string l = a.ToLowerInvariant();
                if (l == "/dry" || l == "-dry" || l == "--dry") dry = true;
                else if (l.StartsWith("/scriptdir:")) scriptDir = a.Substring("/scriptdir:".Length).Trim('"');
                else if (l == "/?" || l == "-h" || l == "--help") { Usage(); return 2; }
                else if (cfgPath == null) cfgPath = a;
                else { Console.Error.WriteLine("Unexpected argument: " + a); Usage(); return 2; }
            }
            try
            {
                cfgPath = LocateCfg(cfgPath, scriptDir);
                Settings s = ReadSettings(cfgPath);
                s.Exe = LocateExe(s.Exe, scriptDir, Path.GetDirectoryName(cfgPath));
                if (s.ViewportW < 0) { s.ViewportW = s.Width - 40; s.ViewportH = s.Height - 80; }

                Console.WriteLine("config      : " + cfgPath);
                Console.WriteLine("DEADLOCK.EXE: " + s.Exe);
                Console.WriteLine("resolution  : {0}x{1}  viewport {2}x{3}  {4}  {5}", s.Width, s.Height,
                    s.ViewportW, s.ViewportH, s.Windowed ? "windowed" : "fullscreen", s.Bpp == 0 ? "default colour depth" : s.Bpp + " bpp");
                Console.WriteLine("fixes       : AI hang {0}, minimise crash {1}", s.FixAiHang ? "on" : "off", s.FixMinimize ? "on" : "off");

                byte[] file = ReadCheckedFile(s.Exe);
                List<BytePatch> plan = BuildPlan(s, file);
                string cmdline = BuildCommandLine(s);
                Console.WriteLine("command     : " + cmdline);

                if (dry)
                {
                    Console.WriteLine(plan.Count == 0 ? "nothing to patch." : "would patch " + plan.Count + " byte runs:");
                    foreach (BytePatch p in plan)
                        Console.WriteLine("  {0:X8}  {1} -> {2}  [{3}] {4}", p.Addr, Hex(p.Original), Hex(p.Patched), p.Group, p.Note);
                    return 0;
                }
                return Launch(s, cmdline, plan);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        static void Usage()
        {
            Console.WriteLine("Usage: Launcher.exe [settings.cfg] [/dry]");
            Console.WriteLine("  settings.cfg  default deadlock2.cfg next to launch.cmd or in the current folder");
            Console.WriteLine("  /dry          show the resolved settings and the patch plan, start nothing");
        }

        // =========================================================================================
        //  Configuration
        // =========================================================================================

        static string LocateCfg(string explicitPath, string scriptDir)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
                if (!File.Exists(explicitPath)) throw new FileNotFoundException("Settings file not found: " + explicitPath);
                return Path.GetFullPath(explicitPath);
            }
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(scriptDir)) candidates.Add(Path.Combine(scriptDir, "deadlock2.cfg"));
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "deadlock2.cfg"));
            foreach (string c in candidates) if (File.Exists(c)) return Path.GetFullPath(c);
            throw new FileNotFoundException("deadlock2.cfg not found. Looked in: " + string.Join("; ", candidates.ToArray()));
        }

        // Plain key=value lines, '#' or ';' comments, case-insensitive keys.
        static Settings ReadSettings(string path)
        {
            var s = new Settings();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) throw new FormatException("Bad line in " + path + ": " + raw);
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "resolution":   ParseSize(val, out s.Width, out s.Height); break;
                    case "viewport":     ParseSize(val, out s.ViewportW, out s.ViewportH); break;
                    case "windowed":     s.Windowed = ParseBool(val, key); break;
                    case "bpp":          s.Bpp = (val.ToLowerInvariant() == "auto" || val == "") ? 0 : int.Parse(val); break;
                    case "fix_aihang":   s.FixAiHang = ParseBool(val, key); break;
                    case "fix_minimize": s.FixMinimize = ParseBool(val, key); break;
                    case "exe":          s.Exe = val.Trim('"'); break;
                    case "args":         s.ExtraArgs = val; break;
                    default: throw new FormatException("Unknown setting '" + key + "' in " + path);
                }
            }
            if (s.Width < 640 || s.Height < 480) throw new FormatException("resolution must be at least 640x480 (the UI needs that much).");
            if (s.Width > 8192 || s.Height > 8192) throw new FormatException("resolution is unreasonably large.");
            if (s.Bpp != 0 && s.Bpp != 8 && s.Bpp != 16) throw new FormatException("bpp must be auto, 8 or 16.");
            return s;
        }

        static bool ParseBool(string val, string key)
        {
            string v = val.ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            throw new FormatException("Setting '" + key + "' must be 0 or 1, got '" + val + "'");
        }

        static void ParseSize(string val, out int w, out int h)
        {
            string[] parts = val.ToLowerInvariant().Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out w) || !int.TryParse(parts[1].Trim(), out h))
                throw new FormatException("Expected WIDTHxHEIGHT, got '" + val + "'");
        }

        // Candidate order: exe= from the config, the config's folder, launch.cmd's folder, the current
        // folder, the GOG installer's registry entry. The registry path can point at an old copy when
        // the game was moved, which is why it comes last.
        static string LocateExe(string explicitPath, string scriptDir, string cfgDir)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
                if (Directory.Exists(explicitPath)) explicitPath = Path.Combine(explicitPath, "DEADLOCK.EXE");
                if (!File.Exists(explicitPath)) throw new FileNotFoundException("File not found: " + explicitPath);
                return Path.GetFullPath(explicitPath);
            }
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(cfgDir)) candidates.Add(Path.Combine(cfgDir, "DEADLOCK.EXE"));
            if (!string.IsNullOrEmpty(scriptDir)) candidates.Add(Path.Combine(scriptDir, "DEADLOCK.EXE"));
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "DEADLOCK.EXE"));
            string gog = ReadGogPath();
            if (gog != null) candidates.Add(Path.Combine(gog, "DEADLOCK.EXE"));
            foreach (string c in candidates) if (File.Exists(c)) return Path.GetFullPath(c);
            throw new FileNotFoundException("DEADLOCK.EXE not found. Looked in: " + string.Join("; ", candidates.ToArray()) + ". Set exe= in the config.");
        }

        static string ReadGogPath()
        {
            try
            {
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                                            .OpenSubKey(@"SOFTWARE\GOG.com\Games\1207660543"))
                    return key == null ? null : key.GetValue("path") as string;
            }
            catch { return null; }
        }

        // The game's own switches: -w windowed (DDSCL_NORMAL, no SetDisplayMode), -8 / -16 colour depth.
        // bpp=auto passes nothing: the game then uses its built-in default. Note that "-8" makes the
        // GOG build crash at start-up (access violation in a row copy at 0x48E6FE) under the GOG
        // ddraw wrapper even without any patch, so it is never passed unless asked for explicitly.
        static string BuildCommandLine(Settings s)
        {
            string cmd = "\"" + s.Exe + "\"";
            if (s.Windowed) cmd += " -w";
            if (s.Bpp == 16) cmd += " -16"; else if (s.Bpp == 8) cmd += " -8";
            if (!string.IsNullOrEmpty(s.ExtraArgs)) cmd += " " + s.ExtraArgs;
            return cmd;
        }

        // =========================================================================================
        //  Planning: decide every byte run from the config and the state of the file on disk
        // =========================================================================================

        static byte[] ReadCheckedFile(string exe)
        {
            byte[] file = File.ReadAllBytes(exe);
            if (file.Length != ExpectedSize)
                throw new InvalidDataException(string.Format("DEADLOCK.EXE is {0} bytes, expected {1} (GOG v1.20). The patch tables do not fit this build.", file.Length, ExpectedSize));
            return file;
        }

        static List<BytePatch> BuildPlan(Settings s, byte[] file)
        {
            var plan = new List<BytePatch>();

            // --- bug fixes ------------------------------------------------------------------------
            foreach (Fix fix in Fixes)
            {
                bool enabled = fix.Id == "AIHang" ? s.FixAiHang : s.FixMinimize;
                if (!enabled) { Console.WriteLine("  {0}: disabled in config.", fix.Name); continue; }
                if (!FileHas(file, fix.ContextAddr, fix.Context))
                    throw new InvalidDataException(fix.Name + ": code around the patch site does not match this build. Refusing.");
                int patched = 0, original = 0;
                foreach (BytePatch p in fix.Patches)
                {
                    if (FileHas(file, p.Addr, p.Patched)) patched++;
                    else if (FileHas(file, p.Addr, p.Original)) original++;
                }
                if (patched == fix.Patches.Length) { Console.WriteLine("  {0}: already applied in the EXE on disk.", fix.Name); continue; }
                if (original != fix.Patches.Length)
                    throw new InvalidDataException(fix.Name + ": bytes at the patch site are neither original nor patched. Refusing.");
                plan.AddRange(fix.Patches);
            }

            // --- resolution -----------------------------------------------------------------------
            bool resize = s.Width != 640 || s.Height != 480 || s.ViewportW != 600 || s.ViewportH != 400;
            if (resize)
            {
                foreach (SizeSite site in SizeSites)
                {
                    int v = BitConverter.ToInt32(file, (int)(site.Addr - FileDelta));
                    if (v != site.OriginalValue)
                        throw new InvalidDataException(string.Format("Unexpected value {0} at VA 0x{1:X8} (expected {2}). {3}", v, site.Addr, site.OriginalValue, site.Note));
                    int nv;
                    switch (site.Kind) { case SizeKind.Width: nv = s.Width; break; case SizeKind.Height: nv = s.Height; break; case SizeKind.ViewportW: nv = s.ViewportW; break; default: nv = s.ViewportH; break; }
                    plan.Add(new BytePatch { Group = "resolution", Addr = site.Addr, Original = BitConverter.GetBytes(site.OriginalValue),
                                             Patched = BitConverter.GetBytes(nv), SkipIfAlreadyPatched = false, Note = site.Note });
                }
            }
            return plan;
        }

        static bool FileHas(byte[] file, uint va, byte[] expected)
        {
            int off = (int)(va - FileDelta);
            if (off < 0 || off + expected.Length > file.Length) return false;
            for (int i = 0; i < expected.Length; i++) if (file[off + i] != expected[i]) return false;
            return true;
        }

        static string Hex(byte[] b)
        {
            var parts = new string[b.Length];
            for (int i = 0; i < b.Length; i++) parts[i] = b[i].ToString("X2");
            return string.Join(" ", parts);
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // =========================================================================================
        //  Launch and patch
        // =========================================================================================

        static int Launch(Settings s, string cmdline, List<BytePatch> plan)
        {
            var si = new Native.STARTUPINFO(); si.cb = Marshal.SizeOf(typeof(Native.STARTUPINFO));
            Native.PROCESS_INFORMATION pi;
            bool suspend = plan.Count > 0;
            // CREATE_SUSPENDED: the image is mapped at 0x400000 but no code has run yet, so the bytes
            // can be rewritten before the game reads any of them.
            if (!Native.CreateProcess(null, cmdline, IntPtr.Zero, IntPtr.Zero, false,
                    suspend ? Native.CREATE_SUSPENDED : 0, IntPtr.Zero, Path.GetDirectoryName(s.Exe), ref si, out pi))
                throw new InvalidOperationException("CreateProcess failed: " + new System.ComponentModel.Win32Exception().Message);
            try
            {
                if (suspend)
                {
                    int written;
                    try { written = ApplyAll(pi.hProcess, plan); }
                    catch
                    {
                        Native.TerminateProcess(pi.hProcess, 1);   // never let a half-patched game run
                        throw;
                    }
                    Native.ResumeThread(pi.hThread);
                    Console.WriteLine("game started (PID {0}); {1} byte runs patched in memory; waiting for it to exit...", pi.dwProcessId, written);
                }
                else Console.WriteLine("game started (PID {0}) unmodified; waiting for it to exit...", pi.dwProcessId);

                Native.WaitForSingleObject(pi.hProcess, Native.INFINITE);
                uint code; Native.GetExitCodeProcess(pi.hProcess, out code);
                Console.WriteLine("game exited with code 0x{0:X}", code);
                return (int)code;
            }
            finally
            {
                Native.CloseHandle(pi.hThread);
                Native.CloseHandle(pi.hProcess);
            }
        }

        static int ApplyAll(IntPtr hProcess, List<BytePatch> plan)
        {
            int written = 0;
            foreach (BytePatch p in plan)
            {
                var addr = new IntPtr((long)p.Addr);
                IntPtr n;
                byte[] cur = new byte[p.Original.Length];
                if (!Native.ReadProcessMemory(hProcess, addr, cur, cur.Length, out n) || (int)n != cur.Length)
                    throw new InvalidOperationException(string.Format("ReadProcessMemory failed at 0x{0:X8}", p.Addr));
                if (SameBytes(cur, p.Patched) && p.SkipIfAlreadyPatched) continue;
                if (!SameBytes(cur, p.Original))
                    throw new InvalidOperationException(string.Format("Memory at 0x{0:X8} holds {1}, expected {2} [{3}] {4}", p.Addr, Hex(cur), Hex(p.Original), p.Group, p.Note));

                // Code pages are mapped read+execute; make the range writable for the write, then restore.
                uint old;
                if (!Native.VirtualProtectEx(hProcess, addr, new UIntPtr((uint)p.Patched.Length), Native.PAGE_EXECUTE_READWRITE, out old))
                    throw new InvalidOperationException(string.Format("VirtualProtectEx failed at 0x{0:X8}", p.Addr));
                if (!Native.WriteProcessMemory(hProcess, addr, p.Patched, p.Patched.Length, out n) || (int)n != p.Patched.Length)
                    throw new InvalidOperationException(string.Format("WriteProcessMemory failed at 0x{0:X8}", p.Addr));
                uint dummy; Native.VirtualProtectEx(hProcess, addr, new UIntPtr((uint)p.Patched.Length), old, out dummy);

                if (!Native.ReadProcessMemory(hProcess, addr, cur, cur.Length, out n) || !SameBytes(cur, p.Patched))
                    throw new InvalidOperationException(string.Format("Verification failed at 0x{0:X8}", p.Addr));
                written++;
            }
            return written;
        }

        // =========================================================================================
        //  Table 1: bug fixes (byte for byte the same as patcher\Patcher.cs)
        // =========================================================================================
        static readonly Fix[] Fixes = new[]
        {
            new Fix
            {
                Id = "AIHang", Name = "AI turn hang (job scheduler infinite loop)",
                // 8 bytes before the store: 8B F0 mov esi,eax / 85 F6 test esi,esi / 74 32 je / 33 C0 xor eax,eax
                ContextAddr = 0x00408BA3, Context = new byte[] { 0x8B, 0xF0, 0x85, 0xF6, 0x74, 0x32, 0x33, 0xC0 },
                Patches = new[]
                {
                    new BytePatch { Group = "fix_aihang", Addr = 0x00408BAB, SkipIfAlreadyPatched = true,
                        Original = new byte[] { 0x89, 0x46, 0x0C },     // mov [esi+0Ch], eax   ; job.done = 0  <- the bug
                        Patched  = new byte[] { 0x90, 0x90, 0x90 },     // nop nop nop
                        Note = "request-resource must not clear the done flag of an existing job" }
                }
            },
            new Fix
            {
                Id = "MinimizeCrash", Name = "Crash on restore after minimise (cursor save-under)",
                // start of the cursor shutdown routine 0x494FB6: push ebp / mov ebp,esp / cmp [51DC9C],0 / je ..
                ContextAddr = 0x00494FB6, Context = new byte[] { 0x55, 0x8B, 0xEC, 0x83, 0x3D, 0x9C, 0xDC, 0x51, 0x00, 0x00, 0x74 },
                Patches = new[]
                {
                    new BytePatch { Group = "fix_minimize", Addr = 0x00494FC1, SkipIfAlreadyPatched = true,
                        Original = new byte[] { 0x50 }, Patched = new byte[] { 0x52 },     // je rel8: 0x495012 -> 0x495014
                        Note = "early-exit jump follows the moved 'mov eax,1'" },
                    new BytePatch { Group = "fix_minimize", Addr = 0x00495003, SkipIfAlreadyPatched = true,
                        Original = new byte[] { 0x33,0xC9, 0x89,0x0D,0x9C,0xDC,0x51,0x00, 0x33,0xC0, 0xA3,0x7C,0xEC,0x65,0x00, 0xB8,0x01,0x00,0x00,0x00, 0x5D, 0xC3, 0x00,0x00 },
                        Patched  = new byte[] { 0x33,0xC0, 0xA3,0x9C,0xDC,0x51,0x00, 0xA3,0x7C,0xEC,0x65,0x00, 0xA3,0x8C,0xDC,0x51,0x00, 0xB8,0x01,0x00,0x00,0x00, 0x5D, 0xC3 },
                        Note = "shutdown tail also clears the 'cursor drawn' flag [51DC8C]" }
                }
            }
        };

        // =========================================================================================
        //  Table 2: 640x480 immediates. Addr is the VA of the 4-byte immediate, not of the instruction.
        //  Generated from a capstone sweep of the CODE section (debug\full.asm in the game folder) and
        //  reviewed by hand; the only 640 immediate deliberately left out is "sub ecx, 0x280" at
        //  0x473700, a switch-case value. Encodings: 68 imm32 (+1), C7 44 24 08 imm32 (+4),
        //  C7 45 F8 imm32 (+3), 81 F9/FA imm32 (+2), BA imm32 (+1), C7 05 addr32 imm32 (+6).
        // =========================================================================================
        static readonly SizeSite[] SizeSites = new[]
        {
            // --- display, window, buffers -------------------------------------------------------------
            new SizeSite(0x0046FCCC, SizeKind.Width,  "0046FCCB: push 0x280  ; DirectDraw init 0x48B623: SetDisplayMode(w,h,bpp), surfaces, screen objects 0x65E644/0x65E6F4"),
            new SizeSite(0x0046FCC7, SizeKind.Height, "0046FCC6: push 0x1e0  ; DirectDraw init 0x48B623 (height)"),
            new SizeSite(0x0046FE00, SizeKind.Width,  "0046FDFF: push 0x280  ; window creation 0x4A5E40(0,0,w,h)"),
            new SizeSite(0x0046FDFB, SizeKind.Height, "0046FDFA: push 0x1e0  ; window creation 0x4A5E40(0,0,w,h)"),
            new SizeSite(0x0046FE15, SizeKind.Width,  "0046FE14: push 0x280  ; main back buffer [0x4D5C28] = 0x48C28D(w,h,0)"),
            new SizeSite(0x0046FE10, SizeKind.Height, "0046FE0F: push 0x1e0  ; main back buffer [0x4D5C28]"),
            new SizeSite(0x00481210, SizeKind.Width,  "0048120F: push 0x280  ; second screen buffer [0x4DCC1C] = 0x48C28D(w,h,0)"),
            new SizeSite(0x0048120B, SizeKind.Height, "0048120A: push 0x1e0  ; second screen buffer [0x4DCC1C]"),

            // --- map viewport (main game screen setup 0x48149C) -------------------------------------------
            new SizeSite(0x004814BB, SizeKind.ViewportW, "004814BA: push 0x258  ; map bitmap allocation 0x48125C(.., w, h)"),
            new SizeSite(0x004814B6, SizeKind.ViewportH, "004814B5: push 0x190  ; map bitmap allocation (height)"),
            new SizeSite(0x004814DC, SizeKind.ViewportW, "004814D6: mov [0x4C5460], 0x258  ; map viewport width global (origin x stays 22 = [0x4C5458])"),
            new SizeSite(0x004814E6, SizeKind.ViewportH, "004814E0: mov [0x4C5464], 0x190  ; map viewport height global (origin y stays 24 = [0x4C545C])"),

            // --- intro / menu screen: clear-screen fills and clip rects ---------------------------------------
            new SizeSite(0x0043C160, SizeKind.Width,  "0043C15F: push 0x280  ; clear-screen fill 0x493784(0,0,w,h,2,0,1), menu path A"),
            new SizeSite(0x0043C15B, SizeKind.Height, "0043C15A: push 0x1e0  ; clear-screen fill, menu path A"),
            new SizeSite(0x0043C141, SizeKind.Width,  "0043C13E: mov [ebp-8], 0x280  ; clip rect for 0x49A9E7, menu path A"),
            new SizeSite(0x0043C13A, SizeKind.Height, "0043C137: mov [ebp-4], 0x1e0  ; clip rect, menu path A"),
            new SizeSite(0x0043C220, SizeKind.Width,  "0043C21F: push 0x280  ; clear-screen fill, menu path B"),
            new SizeSite(0x0043C21B, SizeKind.Height, "0043C21A: push 0x1e0  ; clear-screen fill, menu path B"),
            new SizeSite(0x0043C201, SizeKind.Width,  "0043C1FE: mov [ebp-8], 0x280  ; clip rect, menu path B"),
            new SizeSite(0x0043C1FA, SizeKind.Height, "0043C1F7: mov [ebp-4], 0x1e0  ; clip rect, menu path B"),

            // --- cursor ------------------------------------------------------------------------------------
            new SizeSite(0x004151AF, SizeKind.Width,  "004151AC: mov [ebp-8], 0x280  ; cursor clip rect set in 0x41515x"),
            new SizeSite(0x004151A8, SizeKind.Height, "004151A5: mov [ebp-4], 0x1e0  ; cursor clip rect"),

            // --- map code: coordinate clamps and edge scrolling -----------------------------------------------
            new SizeSite(0x0041863F, SizeKind.Width,  "0041863D: cmp ecx, 0x280  ; hex->pixel conversion: result must be on screen"),
            new SizeSite(0x0041864F, SizeKind.Height, "0041864D: cmp edx, 0x1e0  ; hex->pixel conversion (height)"),
            new SizeSite(0x0041BA50, SizeKind.Width,  "0041BA4E: cmp ecx, 0x280  ; hex->pixel conversion, second copy"),
            new SizeSite(0x0041BA60, SizeKind.Height, "0041BA5E: cmp edx, 0x1e0  ; hex->pixel conversion, second copy"),
            new SizeSite(0x00469E65, SizeKind.Width,  "00469E63: cmp edx, 0x280  ; edge-scroll direction from mouse x (>= width -> scroll right)"),
            new SizeSite(0x0048010C, SizeKind.Width,  "0048010B: mov edx, 0x280  ; right-anchored overlay: x = width - w - 4"),
            new SizeSite(0x00481355, SizeKind.Width,  "00481352: mov [ebp-4], 0x280  ; view size clamp against screen width (role uncertain)"),

            // --- whole-screen rectangles {0,0,640,480} passed to redraw 0x414EA4 or clip 0x49A9E7 ---------
            new SizeSite(0x00418D33, SizeKind.Width,  "00418D2F: mov [esp+8], 0x280"),   new SizeSite(0x00418D3B, SizeKind.Height, "00418D37: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x00419866, SizeKind.Width,  "00419863: mov [ebp-8], 0x280"),   new SizeSite(0x0041986D, SizeKind.Height, "0041986A: mov [ebp-4], 0x1e0"),
            new SizeSite(0x0041AC5B, SizeKind.Width,  "0041AC57: mov [esp+8], 0x280"),   new SizeSite(0x0041AC63, SizeKind.Height, "0041AC5F: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x0041B234, SizeKind.Width,  "0041B230: mov [esp+8], 0x280"),   new SizeSite(0x0041B23C, SizeKind.Height, "0041B238: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x0041C393, SizeKind.Width,  "0041C38F: mov [esp+8], 0x280"),   new SizeSite(0x0041C39B, SizeKind.Height, "0041C397: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x0041FF3F, SizeKind.Width,  "0041FF3B: mov [esp+8], 0x280"),   new SizeSite(0x0041FF47, SizeKind.Height, "0041FF43: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x00422607, SizeKind.Width,  "00422603: mov [esp+8], 0x280"),   new SizeSite(0x0042260F, SizeKind.Height, "0042260B: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x0042317C, SizeKind.Width,  "00423178: mov [esp+8], 0x280"),   new SizeSite(0x00423184, SizeKind.Height, "00423180: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x00425FD7, SizeKind.Width,  "00425FD4: mov [ebp-8], 0x280"),   new SizeSite(0x00425FD0, SizeKind.Height, "00425FCD: mov [ebp-4], 0x1e0"),
            new SizeSite(0x004261F9, SizeKind.Width,  "004261F5: mov [esp+8], 0x280"),   new SizeSite(0x004261F1, SizeKind.Height, "004261ED: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x004280A5, SizeKind.Width,  "004280A2: mov [ebp-8], 0x280"),   new SizeSite(0x004280AC, SizeKind.Height, "004280A9: mov [ebp-4], 0x1e0"),
            new SizeSite(0x0042F57B, SizeKind.Width,  "0042F577: mov [esp+8], 0x280"),   new SizeSite(0x0042F583, SizeKind.Height, "0042F57F: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x0042F6DE, SizeKind.Width,  "0042F6DA: mov [esp+8], 0x280"),   new SizeSite(0x0042F6E6, SizeKind.Height, "0042F6E2: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x00432B22, SizeKind.Width,  "00432B1E: mov [esp+8], 0x280"),   new SizeSite(0x00432B2A, SizeKind.Height, "00432B26: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x00432D42, SizeKind.Width,  "00432D3E: mov [esp+8], 0x280"),   new SizeSite(0x00432D4A, SizeKind.Height, "00432D46: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x00435EAA, SizeKind.Width,  "00435EA6: mov [esp+8], 0x280"),   new SizeSite(0x00435EB2, SizeKind.Height, "00435EAE: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x0043732B, SizeKind.Width,  "00437327: mov [esp+8], 0x280"),   new SizeSite(0x00437333, SizeKind.Height, "0043732F: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x004373DA, SizeKind.Width,  "004373D7: mov [ebp-8], 0x280"),   new SizeSite(0x004373E1, SizeKind.Height, "004373DE: mov [ebp-4], 0x1e0"),
            new SizeSite(0x0043823B, SizeKind.Width,  "00438237: mov [esp+8], 0x280"),   new SizeSite(0x00438243, SizeKind.Height, "0043823F: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x004388D4, SizeKind.Width,  "004388D0: mov [esp+8], 0x280"),   new SizeSite(0x004388DC, SizeKind.Height, "004388D8: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x0043A363, SizeKind.Width,  "0043A35F: mov [esp+8], 0x280"),   new SizeSite(0x0043A36B, SizeKind.Height, "0043A367: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x0043A429, SizeKind.Width,  "0043A426: mov [ebp-8], 0x280"),   new SizeSite(0x0043A430, SizeKind.Height, "0043A42D: mov [ebp-4], 0x1e0"),
            new SizeSite(0x0043C9E0, SizeKind.Width,  "0043C9DC: mov [esp+8], 0x280"),   new SizeSite(0x0043C9E8, SizeKind.Height, "0043C9E4: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x0043CADF, SizeKind.Width,  "0043CADB: mov [esp+8], 0x280"),   new SizeSite(0x0043CAE7, SizeKind.Height, "0043CAE3: mov [esp+0xc], 0x1e0"),
            new SizeSite(0x0043E5EB, SizeKind.Width,  "0043E5E8: mov [ebp-8], 0x280"),   new SizeSite(0x0043E5F2, SizeKind.Height, "0043E5EF: mov [ebp-4], 0x1e0"),
            new SizeSite(0x00468BA6, SizeKind.Width,  "00468BA2: mov [esp+8], 0x280"),   new SizeSite(0x00468B9E, SizeKind.Height, "00468B9A: mov [esp+0xc], 0x1e0"),
        };
    }
}
