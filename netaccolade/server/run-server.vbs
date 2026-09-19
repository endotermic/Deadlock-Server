' Starts the NetAccolade lobby server hidden (no console window) and appends its output to server.log.
' Used by the Startup-folder shortcut that Install-NetAccolade.ps1 creates; safe to run twice
' (a second instance exits at once because port 7624 is already taken).
Set fso = CreateObject("Scripting.FileSystemObject")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
Set sh = CreateObject("WScript.Shell")
node = "node"
On Error Resume Next
env = sh.ExpandEnvironmentStrings("%NETACC_NODE%")
If env <> "%NETACC_NODE%" And env <> "" Then node = env
On Error GoTo 0
cmd = "cmd.exe /c cd /d """ & dir & """ && """ & node & """ server.js >> server.log 2>&1"
sh.Run cmd, 0, False
