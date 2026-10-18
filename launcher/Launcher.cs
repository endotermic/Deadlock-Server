// =====================================================================================================
//  Launcher.cs - starts Deadlock II: Shrine Wars v1.20 (GOG) at a configurable resolution
// =====================================================================================================
//
//  WHY A LAUNCHER
//  --------------
//  The game has no resolution setting. 640x480 is baked into DEADLOCK.EXE as literal numbers inside
//  instructions: the DirectDraw SetDisplayMode call, the window size, two back buffers, the cursor
//  clip rectangle, ~36 "whole screen" rectangles passed to redraw/clip helpers, a few coordinate
//  clamps, and the 600x400 map viewport. (Resolution-Investigation.md lists how they were found.)
//
//  Instead of rewriting the EXE on disk for every resolution, this program reads deadlock2.cfg,
//  starts DEADLOCK.EXE *suspended*, overwrites those immediates in the process memory with the
//  configured width/height, verifies them, and resumes the process. The file on disk is untouched,
//  so any resolution can be tried by editing one line and the patcher's fixes stay independent.
//  The image base is fixed (no ASLR, no relocation applied to a non-DLL at 0x400000), so the
//  addresses below are stable.
//
//  WHAT IS PATCHED (table at the bottom of this file)
//    Width sites  : every instruction immediate that is 640 and means "screen width"
//    Height sites : every instruction immediate that is 480 and means "screen height"
//    Viewport     : the map window's 600x400 size (allocation and the two layout globals); it grows
//                   with the screen while keeping the 22/24 px origin and the 18/56 px borders,
//                   unless viewport=WxH overrides it in the config
//
//  WHAT IS NOT PATCHED (so the picture is unfinished at anything but 640x480)
//    The frame art, the bottom bar and every panel are sprites placed by rectangles from the UI
//    resources. They still sit where a 640x480 frame puts them. Dialog centring and mouse
//    hit-testing of those panels may be off. This launcher is the experiment platform for fixing
//    that, not the fix itself.
//
//  Written for C# 5 / .NET Framework 4.0 so the compiler that ships with Windows can build it
//  (see launch.cmd).
//
//  USAGE
//    Launcher.exe [settings.cfg] [/dry] [/scriptdir:<dir>]
//      settings.cfg  default: deadlock2.cfg next to launch.cmd, else in the current folder
//      /dry          print the resolved settings and the patch table; start nothing
//    Exit code: the game's exit code, 1 on launcher error, 2 on bad arguments.
// =====================================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Deadlock2Launcher
{
    enum Kind { Width, Height, ViewportW, ViewportH }

    /// <summary>One 32-bit immediate inside an instruction of DEADLOCK.EXE.</summary>
    sealed class Site
    {
        public uint Addr;      // VA of the 4-byte immediate (not of the instruction)
        public Kind Kind;
        public string Note;    // "<instruction VA>: <instruction> ; <role>"
        public Site(uint addr, Kind kind, string note) { Addr = addr; Kind = kind; Note = note; }
        public int OriginalValue
        {
            get
            {
                switch (Kind)
                {
                    case Kind.Width: return 640;
                    case Kind.Height: return 480;
                    case Kind.ViewportW: return 600;
                    default: return 400;
                }
            }
        }
    }

    sealed class Settings
    {
        public int Width = 640, Height = 480;
        public int ViewportW = -1, ViewportH = -1;   // -1 = derive from the screen size
        public bool Windowed = true;
        public int Bpp = 16;
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
        const int ExpectedSize = 1738752;   // GOG DEADLOCK.EXE v1.20; the table only fits this build

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

                Console.WriteLine("config     : " + cfgPath);
                Console.WriteLine("DEADLOCK.EXE: " + s.Exe);
                Console.WriteLine("resolution : {0}x{1}  viewport {2}x{3}  {4}  {5} bpp", s.Width, s.Height,
                    s.ViewportW, s.ViewportH, s.Windowed ? "windowed" : "fullscreen", s.Bpp);

                CheckExeFile(s.Exe);
                string cmdline = BuildCommandLine(s);
                Console.WriteLine("command    : " + cmdline);

                bool needPatch = s.Width != 640 || s.Height != 480 || s.ViewportW != 600 || s.ViewportH != 400;
                if (dry)
                {
                    Console.WriteLine(needPatch ? "patch table (" + Sites.Length + " sites):" : "640x480: nothing would be patched.");
                    if (needPatch) foreach (Site site in Sites) Console.WriteLine("  {0:X8} <- {1,5}  {2}", site.Addr, NewValue(site, s), site.Note);
                    return 0;
                }
                return Launch(s, cmdline, needPatch);
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
            Console.WriteLine("  /dry          show the resolved settings and the patch table, start nothing");
        }

        // ------------------------------------------------------------------ configuration

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
                    case "resolution": ParseSize(val, out s.Width, out s.Height); break;
                    case "viewport":   ParseSize(val, out s.ViewportW, out s.ViewportH); break;
                    case "windowed":   s.Windowed = val == "1" || val.ToLowerInvariant() == "true" || val.ToLowerInvariant() == "yes"; break;
                    case "bpp":        s.Bpp = int.Parse(val); break;
                    case "exe":        s.Exe = val.Trim('"'); break;
                    case "args":       s.ExtraArgs = val; break;
                    default: throw new FormatException("Unknown setting '" + key + "' in " + path);
                }
            }
            if (s.Width < 640 || s.Height < 480) throw new FormatException("resolution must be at least 640x480 (the UI needs that much).");
            if (s.Width > 8192 || s.Height > 8192) throw new FormatException("resolution is unreasonably large.");
            if (s.Bpp != 8 && s.Bpp != 16) throw new FormatException("bpp must be 8 or 16.");
            return s;
        }

        static void ParseSize(string val, out int w, out int h)
        {
            string[] parts = val.ToLowerInvariant().Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out w) || !int.TryParse(parts[1].Trim(), out h))
                throw new FormatException("Expected WIDTHxHEIGHT, got '" + val + "'");
        }

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
        static string BuildCommandLine(Settings s)
        {
            string cmd = "\"" + s.Exe + "\"";
            if (s.Windowed) cmd += " -w";
            cmd += s.Bpp == 16 ? " -16" : " -8";
            if (!string.IsNullOrEmpty(s.ExtraArgs)) cmd += " " + s.ExtraArgs;
            return cmd;
        }

        // ------------------------------------------------------------------ checks

        // Before starting anything, prove on disk that this is the build the table was made for:
        // right size, and every site holds its original value (640/480/600/400) in the file.
        static void CheckExeFile(string exe)
        {
            byte[] file = File.ReadAllBytes(exe);
            if (file.Length != ExpectedSize)
                throw new InvalidDataException(string.Format("DEADLOCK.EXE is {0} bytes, expected {1} (GOG v1.20). The patch table does not fit this build.", file.Length, ExpectedSize));
            foreach (Site site in Sites)
            {
                int off = (int)(site.Addr - 0x400800);   // CODE section: VA 0x401000 at file offset 0x800
                int v = BitConverter.ToInt32(file, off);
                if (v != site.OriginalValue)
                    throw new InvalidDataException(string.Format("Unexpected value {0} at VA 0x{1:X8} (expected {2}). {3}", v, site.Addr, site.OriginalValue, site.Note));
            }
        }

        static int NewValue(Site site, Settings s)
        {
            switch (site.Kind)
            {
                case Kind.Width: return s.Width;
                case Kind.Height: return s.Height;
                case Kind.ViewportW: return s.ViewportW;
                default: return s.ViewportH;
            }
        }

        // ------------------------------------------------------------------ launch

        static int Launch(Settings s, string cmdline, bool needPatch)
        {
            var si = new Native.STARTUPINFO(); si.cb = Marshal.SizeOf(typeof(Native.STARTUPINFO));
            Native.PROCESS_INFORMATION pi;
            // CREATE_SUSPENDED: the image is mapped at 0x400000 but no code has run yet, so the
            // immediates can be rewritten before the game reads any of them.
            if (!Native.CreateProcess(null, cmdline, IntPtr.Zero, IntPtr.Zero, false,
                    needPatch ? Native.CREATE_SUSPENDED : 0, IntPtr.Zero, Path.GetDirectoryName(s.Exe), ref si, out pi))
                throw new InvalidOperationException("CreateProcess failed: " + new System.ComponentModel.Win32Exception().Message);
            try
            {
                if (needPatch)
                {
                    try { PatchAll(pi.hProcess, s); }
                    catch
                    {
                        Native.TerminateProcess(pi.hProcess, 1);   // never let a half-patched game run
                        throw;
                    }
                    Native.ResumeThread(pi.hThread);
                }
                Console.WriteLine("game started (PID {0}){1}; waiting for it to exit...", pi.dwProcessId, needPatch ? ", " + Sites.Length + " sites patched" : "");
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

        static void PatchAll(IntPtr hProcess, Settings s)
        {
            foreach (Site site in Sites)
            {
                var addr = new IntPtr((long)site.Addr);
                IntPtr n;
                byte[] cur = new byte[4];
                if (!Native.ReadProcessMemory(hProcess, addr, cur, 4, out n) || (int)n != 4)
                    throw new InvalidOperationException(string.Format("ReadProcessMemory failed at 0x{0:X8}", site.Addr));
                int v = BitConverter.ToInt32(cur, 0);
                if (v != site.OriginalValue)
                    throw new InvalidOperationException(string.Format("Memory at 0x{0:X8} holds {1}, expected {2}: {3}", site.Addr, v, site.OriginalValue, site.Note));

                // Code pages are mapped read+execute; make this one writable for the write.
                uint old;
                if (!Native.VirtualProtectEx(hProcess, addr, new UIntPtr(4u), Native.PAGE_EXECUTE_READWRITE, out old))
                    throw new InvalidOperationException(string.Format("VirtualProtectEx failed at 0x{0:X8}", site.Addr));
                byte[] nw = BitConverter.GetBytes(NewValue(site, s));
                if (!Native.WriteProcessMemory(hProcess, addr, nw, 4, out n) || (int)n != 4)
                    throw new InvalidOperationException(string.Format("WriteProcessMemory failed at 0x{0:X8}", site.Addr));
                uint dummy; Native.VirtualProtectEx(hProcess, addr, new UIntPtr(4u), old, out dummy);

                if (!Native.ReadProcessMemory(hProcess, addr, cur, 4, out n) || BitConverter.ToInt32(cur, 0) != NewValue(site, s))
                    throw new InvalidOperationException(string.Format("Verification failed at 0x{0:X8}", site.Addr));
            }
        }

        // ------------------------------------------------------------------ the patch table
        //
        // Addr is the VA of the 4-byte immediate. The note gives the instruction's own VA, the
        // instruction, and what the value is used for. Generated from a capstone sweep of the CODE
        // section (debug\full.asm in the game folder) and reviewed by hand; the only 640 immediate
        // deliberately left out is "sub ecx, 0x280" at 0x473700, a switch-case value.
        //
        // Instruction encodings and where the immediate sits:
        //   68 imm32                   push imm32                      -> +1
        //   C7 44 24 08 imm32          mov [esp+8], imm32              -> +4
        //   C7 45 F8 imm32             mov [ebp-8], imm32              -> +3
        //   81 F9 imm32 / 81 FA imm32  cmp ecx, imm32 / cmp edx, imm32 -> +2
        //   BA imm32                   mov edx, imm32                  -> +1
        //   C7 05 addr32 imm32         mov [addr], imm32               -> +6
        static readonly Site[] Sites = new[]
        {
            // --- display, window, buffers -------------------------------------------------------------
            new Site(0x0046FCCC, Kind.Width,  "0046FCCB: push 0x280  ; DirectDraw init 0x48B623: SetDisplayMode(w,h,bpp), primary/back surfaces, screen objects 0x65E644/0x65E6F4"),
            new Site(0x0046FCC7, Kind.Height, "0046FCC6: push 0x1e0  ; DirectDraw init 0x48B623 (height)"),
            new Site(0x0046FE00, Kind.Width,  "0046FDFF: push 0x280  ; window creation 0x4A5E40(0,0,w,h)"),
            new Site(0x0046FDFB, Kind.Height, "0046FDFA: push 0x1e0  ; window creation 0x4A5E40(0,0,w,h)"),
            new Site(0x0046FE15, Kind.Width,  "0046FE14: push 0x280  ; main back buffer [0x4D5C28] = 0x48C28D(w,h,0)"),
            new Site(0x0046FE10, Kind.Height, "0046FE0F: push 0x1e0  ; main back buffer [0x4D5C28]"),
            new Site(0x00481210, Kind.Width,  "0048120F: push 0x280  ; second screen buffer [0x4DCC1C] = 0x48C28D(w,h,0)"),
            new Site(0x0048120B, Kind.Height, "0048120A: push 0x1e0  ; second screen buffer [0x4DCC1C]"),

            // --- map viewport (main game screen setup 0x48149C) -------------------------------------------
            new Site(0x004814BB, Kind.ViewportW, "004814BA: push 0x258  ; map bitmap allocation 0x48125C(.., w, h)"),
            new Site(0x004814B6, Kind.ViewportH, "004814B5: push 0x190  ; map bitmap allocation (height)"),
            new Site(0x004814DC, Kind.ViewportW, "004814D6: mov [0x4C5460], 0x258  ; map viewport width global (origin x stays 22 = [0x4C5458])"),
            new Site(0x004814E6, Kind.ViewportH, "004814E0: mov [0x4C5464], 0x190  ; map viewport height global (origin y stays 24 = [0x4C545C])"),

            // --- intro / menu screen: clear-screen fills and clip rects ---------------------------------------
            new Site(0x0043C160, Kind.Width,  "0043C15F: push 0x280  ; clear-screen fill 0x493784(0,0,w,h,2,0,1), menu path A"),
            new Site(0x0043C15B, Kind.Height, "0043C15A: push 0x1e0  ; clear-screen fill, menu path A"),
            new Site(0x0043C141, Kind.Width,  "0043C13E: mov [ebp-8], 0x280  ; clip rect for 0x49A9E7, menu path A"),
            new Site(0x0043C13A, Kind.Height, "0043C137: mov [ebp-4], 0x1e0  ; clip rect, menu path A"),
            new Site(0x0043C220, Kind.Width,  "0043C21F: push 0x280  ; clear-screen fill, menu path B"),
            new Site(0x0043C21B, Kind.Height, "0043C21A: push 0x1e0  ; clear-screen fill, menu path B"),
            new Site(0x0043C201, Kind.Width,  "0043C1FE: mov [ebp-8], 0x280  ; clip rect, menu path B"),
            new Site(0x0043C1FA, Kind.Height, "0043C1F7: mov [ebp-4], 0x1e0  ; clip rect, menu path B"),

            // --- cursor ------------------------------------------------------------------------------------
            new Site(0x004151AF, Kind.Width,  "004151AC: mov [ebp-8], 0x280  ; cursor clip rect set in 0x41515x"),
            new Site(0x004151A8, Kind.Height, "004151A5: mov [ebp-4], 0x1e0  ; cursor clip rect"),

            // --- map code: coordinate clamps and edge scrolling -----------------------------------------------
            new Site(0x0041863F, Kind.Width,  "0041863D: cmp ecx, 0x280  ; hex->pixel conversion: result must be on screen"),
            new Site(0x0041864F, Kind.Height, "0041864D: cmp edx, 0x1e0  ; hex->pixel conversion (height)"),
            new Site(0x0041BA50, Kind.Width,  "0041BA4E: cmp ecx, 0x280  ; hex->pixel conversion, second copy"),
            new Site(0x0041BA60, Kind.Height, "0041BA5E: cmp edx, 0x1e0  ; hex->pixel conversion, second copy"),
            new Site(0x00469E65, Kind.Width,  "00469E63: cmp edx, 0x280  ; edge-scroll direction from mouse x (>= width -> scroll right)"),
            new Site(0x0048010C, Kind.Width,  "0048010B: mov edx, 0x280  ; right-anchored overlay: x = width - w - 4"),
            new Site(0x00481355, Kind.Width,  "00481352: mov [ebp-4], 0x280  ; view size clamp against screen width (role uncertain)"),

            // --- whole-screen rectangles {0,0,640,480} passed to redraw 0x414EA4 or clip 0x49A9E7 ---------
            new Site(0x00418D33, Kind.Width,  "00418D2F: mov [esp+8], 0x280"),   new Site(0x00418D3B, Kind.Height, "00418D37: mov [esp+0xc], 0x1e0"),
            new Site(0x00419866, Kind.Width,  "00419863: mov [ebp-8], 0x280"),   new Site(0x0041986D, Kind.Height, "0041986A: mov [ebp-4], 0x1e0"),
            new Site(0x0041AC5B, Kind.Width,  "0041AC57: mov [esp+8], 0x280"),   new Site(0x0041AC63, Kind.Height, "0041AC5F: mov [esp+0xc], 0x1e0"),
            new Site(0x0041B234, Kind.Width,  "0041B230: mov [esp+8], 0x280"),   new Site(0x0041B23C, Kind.Height, "0041B238: mov [esp+0xc], 0x1e0"),
            new Site(0x0041C393, Kind.Width,  "0041C38F: mov [esp+8], 0x280"),   new Site(0x0041C39B, Kind.Height, "0041C397: mov [esp+0xc], 0x1e0"),
            new Site(0x0041FF3F, Kind.Width,  "0041FF3B: mov [esp+8], 0x280"),   new Site(0x0041FF47, Kind.Height, "0041FF43: mov [esp+0xc], 0x1e0"),
            new Site(0x00422607, Kind.Width,  "00422603: mov [esp+8], 0x280"),   new Site(0x0042260F, Kind.Height, "0042260B: mov [esp+0xc], 0x1e0"),
            new Site(0x0042317C, Kind.Width,  "00423178: mov [esp+8], 0x280"),   new Site(0x00423184, Kind.Height, "00423180: mov [esp+0xc], 0x1e0"),
            new Site(0x00425FD7, Kind.Width,  "00425FD4: mov [ebp-8], 0x280"),   new Site(0x00425FD0, Kind.Height, "00425FCD: mov [ebp-4], 0x1e0"),
            new Site(0x004261F9, Kind.Width,  "004261F5: mov [esp+8], 0x280"),   new Site(0x004261F1, Kind.Height, "004261ED: mov [esp+0xc], 0x1e0"),
            new Site(0x004280A5, Kind.Width,  "004280A2: mov [ebp-8], 0x280"),   new Site(0x004280AC, Kind.Height, "004280A9: mov [ebp-4], 0x1e0"),
            new Site(0x0042F57B, Kind.Width,  "0042F577: mov [esp+8], 0x280"),   new Site(0x0042F583, Kind.Height, "0042F57F: mov [esp+0xc], 0x1e0"),
            new Site(0x0042F6DE, Kind.Width,  "0042F6DA: mov [esp+8], 0x280"),   new Site(0x0042F6E6, Kind.Height, "0042F6E2: mov [esp+0xc], 0x1e0"),
            new Site(0x00432B22, Kind.Width,  "00432B1E: mov [esp+8], 0x280"),   new Site(0x00432B2A, Kind.Height, "00432B26: mov [esp+0xc], 0x1e0"),
            new Site(0x00432D42, Kind.Width,  "00432D3E: mov [esp+8], 0x280"),   new Site(0x00432D4A, Kind.Height, "00432D46: mov [esp+0xc], 0x1e0"),
            new Site(0x00435EAA, Kind.Width,  "00435EA6: mov [esp+8], 0x280"),   new Site(0x00435EB2, Kind.Height, "00435EAE: mov [esp+0xc], 0x1e0"),
            new Site(0x0043732B, Kind.Width,  "00437327: mov [esp+8], 0x280"),   new Site(0x00437333, Kind.Height, "0043732F: mov [esp+0xc], 0x1e0"),
            new Site(0x004373DA, Kind.Width,  "004373D7: mov [ebp-8], 0x280"),   new Site(0x004373E1, Kind.Height, "004373DE: mov [ebp-4], 0x1e0"),
            new Site(0x0043823B, Kind.Width,  "00438237: mov [esp+8], 0x280"),   new Site(0x00438243, Kind.Height, "0043823F: mov [esp+0xc], 0x1e0"),
            new Site(0x004388D4, Kind.Width,  "004388D0: mov [esp+8], 0x280"),   new Site(0x004388DC, Kind.Height, "004388D8: mov [esp+0xc], 0x1e0"),
            new Site(0x0043A363, Kind.Width,  "0043A35F: mov [esp+8], 0x280"),   new Site(0x0043A36B, Kind.Height, "0043A367: mov [esp+0xc], 0x1e0"),
            new Site(0x0043A429, Kind.Width,  "0043A426: mov [ebp-8], 0x280"),   new Site(0x0043A430, Kind.Height, "0043A42D: mov [ebp-4], 0x1e0"),
            new Site(0x0043C9E0, Kind.Width,  "0043C9DC: mov [esp+8], 0x280"),   new Site(0x0043C9E8, Kind.Height, "0043C9E4: mov [esp+0xc], 0x1e0"),
            new Site(0x0043CADF, Kind.Width,  "0043CADB: mov [esp+8], 0x280"),   new Site(0x0043CAE7, Kind.Height, "0043CAE3: mov [esp+0xc], 0x1e0"),
            new Site(0x0043E5EB, Kind.Width,  "0043E5E8: mov [ebp-8], 0x280"),   new Site(0x0043E5F2, Kind.Height, "0043E5EF: mov [ebp-4], 0x1e0"),
            new Site(0x00468BA6, Kind.Width,  "00468BA2: mov [esp+8], 0x280"),   new Site(0x00468B9E, Kind.Height, "00468B9A: mov [esp+0xc], 0x1e0"),
        };
    }
}
