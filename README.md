# Deadlock-Server

Tools, patches and research for playing **Deadlock II: Shrine Wars** (Accolade / Cyberlore, 1998,
GOG build v1.20) in 2026: a replacement for the long-dead NetAccolade online lobby, a fix for the
game's AI-turn hang, and the reverse-engineering notes behind both.

Nothing here is the game itself. Everything acts on an existing installation of Deadlock II
(`DEADLOCK.EXE` in what this README calls the *game directory*).

## What is in the repository

| path | what it is |
|------|------------|
| `netaccolade/server/` | **NetAccolade stand-in**: a Node.js lobby server the game starts and is started by, exactly the way the 1998 service worked. Chat, create/join games with every scenario option, Ready/Launch, fake players and fake DirectPlay sessions for testing alone. |
| `netaccolade/Start-OnlineWar.ps1`, `host.ini`, `join.ini` | **Lobby-less launcher**: writes the launch file and starts the game as host or joiner over TCP/IP without any lobby. |
| `Patch-DeadlockAIHang.ps1` | **AI hang patch**: 3-byte fix for the game freezing at 100% CPU during an AI turn. GUI and command line, with backup and restore. |
| `AI-Hang-Investigation.md` | How the hang was found and fixed: data structures, the circular job cycle, the patch, a reusable WinDbg recipe. |
| `NetAccolade-Investigation.md` | How the game talks to NetAccolade: the `-ms` switch, the complete `deadlock.ini` format, the CGNet/DirectPlay layer. |
| `Online-War-Worklog.md` | Overview of the whole online-multiplayer effort: findings, what was built, verification runs, open items, pitfalls. Start here for context. |
| `netaccolade/server/protocol-notes.md` | DirectPlay 4 TCP/IP wire protocol as captured from the real game and read from `dplayx.dll`. |
| `netaccolade/server/README.md` | Full server documentation: install, run, chat commands, addressing, HTTP API. |

## Requirements

- Windows (the game is a 32-bit Windows program; scripts are PowerShell 5+).
- Deadlock II: Shrine Wars v1.20 installed (GOG build; the patch checks the exact binary).
- For the lobby server: [Node.js](https://nodejs.org) 18 or newer on `PATH`, and the .NET Framework 4
  C# compiler that ships with Windows (used once to build the 6 KB `MClient.exe` trampoline).

## 1. NetAccolade lobby server

The game's main-menu **NetAccolade** button runs `<registry PATH>\MClient.exe`, quits, and expects that
program to later write `deadlock.ini` into the game directory and run `DEADLOCK.EXE -ms`. This repo
provides that program. `MClient.exe` hands off to `agent.js`, which registers with `server.js`, opens
the lobby page in your browser, and launches the game when the host presses Launch.

### Install

```powershell
cd netaccolade\server
.\Install-NetAccolade.ps1
```

This compiles `MClient.exe`, writes the registry value the game reads
(`HKLM\SOFTWARE\WOW6432Node\Accolade\NetAccolade\PATH`, one UAC prompt), puts a shortcut in your
Startup folder so the server runs hidden at every logon, and starts it now. Check with
`.\Install-NetAccolade.ps1 -Status`; remove everything with `-Uninstall`.

The agent finds the game directory from its working directory (the game launches it from there). If
you run `MClient.exe` by hand from elsewhere, create `netaccolade\server\mclient.json`:

```json
{ "gameDir": "C:\Games\Deadlock2" }
```

### Play

1. Start Deadlock II, choose **NetAccolade**, confirm the quit prompt. The lobby opens at
   `http://127.0.0.1:7624` with your game client linked.
2. Host: **Create Game**, set the scenario and world options, wait for players to press **Ready**, press
   **Launch**. Joiners: pick the game, press **Ready**.
3. Each player's game starts with `-ms`. The host's game opens a DirectPlay session; joiners' games
   show it in the "Join a Network Game" list, where they click it.

For play between machines run one server reachable by everyone (`NETACC_HOST=0.0.0.0 node server.js`,
ideally over a VPN such as ZeroTier or Tailscale) and point each player's agent at it with
`{ "server": "http://<host>:7624" }` in `mclient.json`. DirectPlay needs TCP/UDP 2300–2400 and UDP 47624
open between players. **Connect to IP** in the lobby joins a host that is not using the lobby at all.

### Testing alone

- `/fakeclient 2` in the lobby chat adds bot players that join and press Ready, so Launch can be tried.
- `/fakegames 3` makes the server advertise fake DirectPlay sessions on this PC, so the game's own
  TCP/IP join dialog (type `localhost`) lists and joins them up to the "waiting for the master" screen.
- `/help` lists the commands. Settings persist in `server.json`.

See `netaccolade/server/README.md` for the addressing rules, the HTTP API and behaviour notes.

## 2. Playing over TCP/IP without the lobby

`Start-OnlineWar.ps1` does the minimum: copies `host.ini` or `join.ini` to the game directory as
`deadlock.ini`, starts `DEADLOCK.EXE -ms`, and deletes the file when the game exits.

```powershell
# host a 2-player game
.\netaccolade\Start-OnlineWar.ps1 -HostGame -Players 2 -Name "Online War" -GameDir C:\Games\Deadlock2

# join it
.\netaccolade\Start-OnlineWar.ps1 -Join 10.147.17.5 -GameDir C:\Games\Deadlock2

# use your own launch file
.\netaccolade\Start-OnlineWar.ps1 -IniFile .\my-launch.ini -GameDir C:\Games\Deadlock2
```

`-GameDir` is required unless the script sits inside the game directory. Every key in the launch file is
range-checked by the game and a missing or invalid one silently aborts the launch; the format is in
`NetAccolade-Investigation.md`. Never leave a `deadlock.ini` in the game directory: with `-ms` it hijacks
start-up.

## 3. AI hang patch

Symptom: during an AI player's turn the game stops responding with one core at 100%, and reloading the
autosave reproduces it. Cause: the AI's "request resource" routine re-arms an already-run job inside the
same scheduling pass, so a circular prerequisite chain loops forever. The patch replaces that one store
with NOPs; the AI retries once per turn instead of hanging.

```powershell
# window with status, Patch, Restore and Browse
.\Patch-DeadlockAIHang.ps1 -ExePath C:\Games\Deadlock2\DEADLOCK.EXE

# command line
.\Patch-DeadlockAIHang.ps1 -ExePath C:\Games\Deadlock2\DEADLOCK.EXE -NoGui
.\Patch-DeadlockAIHang.ps1 -ExePath C:\Games\Deadlock2\DEADLOCK.EXE -Restore
```

The script refuses to touch a file whose size or surrounding bytes do not match the GOG v1.20 build,
writes `DEADLOCK.EXE.orig` before the first patch, and needs the game closed. Details in
`AI-Hang-Investigation.md`.

## Status and limits

- Verified on one PC: NetAccolade button, lobby, launch as host and joiner, fake clients, fake sessions,
  patched game. A real second machine joining over the Internet has not been tested yet.
- The fake DirectPlay host only gets a joiner to the waiting screen; it cannot play the master's part.
- The lobby keeps players and games in memory; restarting the server empties it.
- Generated files (`MClient.exe`, `*.log`, `mclient.json`) are not tracked.

## License

GPL-3.0, see `LICENSE`.
