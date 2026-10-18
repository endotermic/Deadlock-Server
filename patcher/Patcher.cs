// Patcher.cs - byte patcher for Deadlock II: Shrine Wars v1.20 (GOG build of DEADLOCK.EXE).
//
// Compiled and run by installer.cmd with the C# compiler that ships with Windows
// (%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe). Written against C# 5 / .NET 4.0 on
// purpose: no string interpolation, no newer syntax, no NuGet.
//
// Patches carried (details in ..\AI-Hang-Investigation.md and ..\Minimize-Crash-Investigation.md):
//
//   1. AI turn hang. The "request resource" routine at VA 0x00408B58 re-arms an already-processed
//      job by clearing its done flag at VA 0x00408BAB (mov [esi+0Ch], eax). With a circular
//      building/resource prerequisite chain the scheduler pass never ends and the game sits at
//      100% CPU. Fix: NOP the 3-byte store (file offset 0x83AB).
//
//   2. Crash on restore after minimise. On WM_ACTIVATE(inactive) the cursor shutdown routine at
//      VA 0x00494FB6 frees the software cursor's save-under bitmap but leaves the "cursor is drawn"
//      flag [0x51DC8C] set; on WM_ACTIVATE(active) the redraw blits from the NULL pointer and the
//      game dies with 0xC000041D (a black screen that looks like a hang). Fix: rewrite the tail of
//      the routine in place so it also clears the flag (file offsets 0x947C1 and 0x94803).
//
// File offset = VA - 0x400800 (CODE section: VA 0x401000 at raw offset 0x800).
//
// Usage:  Patcher.exe [path\to\DEADLOCK.EXE] [/status] [/restore] [/scriptdir:<dir>]
//   no switch   apply every patch the file is still missing (backup to DEADLOCK.EXE.orig first)
//   /status     only report which patches are applied
//   /restore    copy DEADLOCK.EXE.orig back over DEADLOCK.EXE
//   /scriptdir  extra folder to look for DEADLOCK.EXE in (installer.cmd passes its own folder)
// Exit code 0 = done or nothing to do, 1 = refused or failed, 2 = bad arguments.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DeadlockPatcher
{
    sealed class Site
    {
        public uint VirtualAddr;
        public int Offset;
        public byte[] Original;
        public byte[] Patched;
    }

    sealed class Patch
    {
        public string Id;
        public string Name;
        public int ContextOffset;      // bytes that must match before this patch is considered at all
        public byte[] Context;
        public Site[] Sites;
    }

    enum PatchState { WrongCode, Patched, Original, Unknown }

    static class Program
    {
        const int ExpectedSize = 1738752;   // GOG DEADLOCK.EXE v1.20

        static readonly Patch[] Patches = new[]
        {
            new Patch
            {
                Id = "AIHang",
                Name = "AI turn hang (job scheduler infinite loop)",
                ContextOffset = 0x83A3,
                // 8B F0 mov esi,eax / 85 F6 test esi,esi / 74 32 je / 33 C0 xor eax,eax
                Context = new byte[] { 0x8B, 0xF0, 0x85, 0xF6, 0x74, 0x32, 0x33, 0xC0 },
                Sites = new[]
                {
                    new Site
                    {
                        VirtualAddr = 0x00408BAB, Offset = 0x83AB,
                        Original = new byte[] { 0x89, 0x46, 0x0C },     // mov [esi+0Ch], eax  (done = 0)
                        Patched  = new byte[] { 0x90, 0x90, 0x90 }      // nop nop nop
                    }
                }
            },
            new Patch
            {
                Id = "MinimizeCrash",
                Name = "Crash on restore after minimise (cursor save-under)",
                ContextOffset = 0x947B6,
                // 55 push ebp / 8B EC mov ebp,esp / 83 3D 9CDC5100 00 cmp [51DC9C],0 / 74 je
                Context = new byte[] { 0x55, 0x8B, 0xEC, 0x83, 0x3D, 0x9C, 0xDC, 0x51, 0x00, 0x00, 0x74 },
                Sites = new[]
                {
                    new Site
                    {
                        VirtualAddr = 0x00494FC1, Offset = 0x947C1,
                        Original = new byte[] { 0x50 },                 // je rel8 -> 0x495012
                        Patched  = new byte[] { 0x52 }                  // je rel8 -> 0x495014
                    },
                    new Site
                    {
                        VirtualAddr = 0x00495003, Offset = 0x94803,
                        Original = new byte[] {
                            0x33, 0xC9,                                 // xor ecx, ecx
                            0x89, 0x0D, 0x9C, 0xDC, 0x51, 0x00,         // mov [51DC9C], ecx   ; initialised = 0
                            0x33, 0xC0,                                 // xor eax, eax
                            0xA3, 0x7C, 0xEC, 0x65, 0x00,               // mov [65EC7C], eax   ; show count = 0
                            0xB8, 0x01, 0x00, 0x00, 0x00,               // mov eax, 1
                            0x5D, 0xC3,                                 // pop ebp / ret
                            0x00, 0x00 },                               // padding
                        Patched = new byte[] {
                            0x33, 0xC0,                                 // xor eax, eax
                            0xA3, 0x9C, 0xDC, 0x51, 0x00,               // mov [51DC9C], eax   ; initialised = 0
                            0xA3, 0x7C, 0xEC, 0x65, 0x00,               // mov [65EC7C], eax   ; show count = 0
                            0xA3, 0x8C, 0xDC, 0x51, 0x00,               // mov [51DC8C], eax   ; drawn = 0 (new)
                            0xB8, 0x01, 0x00, 0x00, 0x00,               // mov eax, 1
                            0x5D, 0xC3 }                                // pop ebp / ret
                    }
                }
            }
        };

        static int Main(string[] args)
        {
            string exePath = null, scriptDir = null;
            bool statusOnly = false, restore = false;

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

        // Candidate order: explicit argument, installer.cmd's folder, current folder, GOG registry path.
        static string LocateExe(string explicitPath, string scriptDir)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
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
            catch { return null; }
        }

        // ------------------------------------------------------------------ checks

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
            if (patched == p.Sites.Length) { detail = "applied"; return PatchState.Patched; }
            if (original == p.Sites.Length) { detail = "not applied"; return PatchState.Original; }
            detail = "bytes at the patch site(s) are neither original nor patched";
            return PatchState.Unknown;
        }

        static byte[] ReadChecked(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            if (file.Length != ExpectedSize)
                throw new InvalidDataException(string.Format(
                    "File is {0} bytes, expected {1} (GOG v1.20 build). Refusing to touch it.", file.Length, ExpectedSize));
            return file;
        }

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

        // ------------------------------------------------------------------ actions

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

            string backup = path + ".orig";
            if (!File.Exists(backup))
            {
                File.Copy(path, backup);
                Console.WriteLine("Backup written to " + backup);
            }
            else Console.WriteLine("Backup already exists at " + backup + " (kept).");

            foreach (Patch p in todo)
                foreach (Site s in p.Sites)
                    Array.Copy(s.Patched, 0, file, s.Offset, s.Patched.Length);
            File.WriteAllBytes(path, file);

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
