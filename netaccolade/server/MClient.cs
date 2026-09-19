// MClient.exe stand-in for Deadlock II.  The game spawns "<PATH>\MClient.exe -gi 1 -o deadlock.ini"
// (PATH from HKLM\SOFTWARE\Accolade\NetAccolade).  This trampoline just runs agent.js with node.
// Build: csc /nologo /target:winexe /out:MClient.exe /r:System.Windows.Forms.dll MClient.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

static class MClient
{
    static int Main(string[] args)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string agent = Path.Combine(dir, "agent.js");
        if (!File.Exists(agent))
        {
            MessageBox.Show("agent.js was not found next to MClient.exe:\n" + agent, "NetAccolade stand-in", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        string node = FindNode();
        if (node == null)
        {
            MessageBox.Show("Node.js (node.exe) was not found on PATH.\nInstall Node.js or set the NETACC_NODE environment variable.", "NetAccolade stand-in", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }
        var sb = new StringBuilder();
        sb.Append('"').Append(agent).Append('"');
        foreach (string a in args) sb.Append(' ').Append(Quote(a));
        var psi = new ProcessStartInfo(node, sb.ToString());
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = Environment.CurrentDirectory; // the game directory, as spawned by DEADLOCK.EXE
        try
        {
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    string why;
                    switch (p.ExitCode)
                    {
                        case 2: why = "The lobby server could not be reached or started."; break;
                        case 3: why = "The lobby server went away while waiting."; break;
                        case 4: why = "Deadlock 2 could not be started for the launch."; break;
                        case 5: why = "DEADLOCK.EXE was not found (run MClient.exe from the game directory, or set gameDir in mclient.json)."; break;
                        default: why = "Unexpected error."; break;
                    }
                    MessageBox.Show(why + "\n\nDetails: " + Path.Combine(dir, "mclient.log"), "NetAccolade stand-in (exit " + p.ExitCode + ")", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start the lobby client:\n" + ex.Message, "NetAccolade stand-in", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 4;
        }
    }

    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        return "\"" + a.Replace("\"", "\\\"") + "\"";
    }

    static string FindNode()
    {
        string env = Environment.GetEnvironmentVariable("NETACC_NODE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string p in pathVar.Split(Path.PathSeparator))
        {
            if (p.Trim().Length == 0) continue;
            try
            {
                string c = Path.Combine(p.Trim(), "node.exe");
                if (File.Exists(c)) return c;
            }
            catch (ArgumentException) { }
        }
        string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs\\node.exe");
        return File.Exists(pf) ? pf : null;
    }
}
