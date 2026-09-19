# Deadlock II online multiplayer without NetAccolade: work log

Session date: 2026-09-19. Machine: Windows 11, GOG build of Deadlock II: Shrine Wars v1.20 in
`C:\Users\nika\Documents\Deadlock2`. Goal set by the user: understand how NetAccolade was wired into
the game, replace it with a local Node.js service, and be able to test multiplayer alone.

This file is the overview. Details live in:

| document | what it holds |
|----------|---------------|
| [NetAccolade-Investigation.md](NetAccolade-Investigation.md) | reverse engineering of the game: launcher, `-ms`, `deadlock.ini` format, state machine, CGNet/DirectPlay wrapper |
| [netaccolade/server/README.md](netaccolade/server/README.md) | the Node.js lobby server: install, run, API, fake clients, fake games |
| [netaccolade/server/protocol-notes.md](netaccolade/server/protocol-notes.md) | DirectPlay 4 TCP/IP wire protocol as captured from the real game and read from `dplayx.dll` |
| [CLAUDE.md](CLAUDE.md) | repo conventions, now with pointers to all of the above |

## 1. What NetAccolade was, as far as the game is concerned

Read out of `DEADLOCK.EXE` (Borland C++, `debug/full.asm` capstone sweep) and `CGNET.DLL`:

- NetAccolade was a separate lobby program, `MClient.exe`, not shipped with the GOG version. The game
  only knows how to **start** it and how to be **started by** it.
- Start: main menu button → `LaunchNetAccolade` at `0x467884`: reads `HKLM\SOFTWARE\Accolade\NetAccolade\PATH`,
  asks "Quit Deadlock 2 to run NetAccolade?", `chdir` into that folder, `spawnv(P_NOWAIT, "<PATH>\MClient.exe",
  "-gi 1 -o deadlock.ini")`, quits.
- Return: the lobby writes **`deadlock.ini`** into the game directory and runs **`DEADLOCK.EXE -ms`**
  (`-ms` = matching service, one of nine switches: `-p -w -16 -8 -debug -dd -ns -ms -nosound`). With `-ms`
  the game reads the file after the intro (`ReadLaunchFile 0x470554`) and skips the menus: `Role=master`
  hosts a session and waits for `[Startup] Players` humans; anything else connects to
  `[Startup] Master Address` and shows the session list.
- The INI format was fully decoded (sections `[Meta] [Scenario Options] [World] [Startup]`, every key,
  every allowed value and range, and the rule that any missing or invalid key silently drops the launch).
- Networking: `CGNET.DLL` is a thin wrapper over DirectPlay 3/4 (`DPLAYX.dll`). TCP/IP provider,
  application GUID `{9F39C820-8CFE-11D1-904F-00A0C9363012}`, session flags `KEEPALIVE|MIGRATEHOST`,
  max 7 players, host player named "Deadlock 2 Host", joiners "Deadlock 2 Player", all game traffic as
  92-byte guaranteed capsules. `CGNetService_ConnectToIPAddress` builds a `DPAID_INet` address and calls
  `InitializeConnection`, which is the only place an IP address can enter the game. The DirectPlay TCP/IP
  provider's own dialog also lets the player type an address, which is what the user later used.
- Verified the same day by hand: a complete `deadlock.ini` plus `-ms` made the GOG build host a DirectPlay
  session (TCP 2300, UDP 2350).

Sample files: `netaccolade/host.ini`, `netaccolade/join.ini`, launcher `netaccolade/Start-OnlineWar.ps1`.

## 2. The Node.js NetAccolade stand-in (`netaccolade/server/`)

Built with no npm dependencies (Node 20 on this PC):

| file | role |
|------|------|
| `server.js` | lobby: players, chat, games with every scenario/world option, Ready, Launch; builds one `deadlock.ini` per participant; HTTP + Server-Sent Events on `127.0.0.1:7624`; persistent settings in `server.json` |
| `public/index.html` | the lobby page |
| `agent.js` | what the game actually starts: registers the game directory, opens the lobby page, long-polls for a launch command, writes `deadlock.ini`, runs `DEADLOCK.EXE -ms`, deletes the file when the game exits; starts `server.js` if nothing listens |
| `MClient.cs` → `MClient.exe` | 6 KB trampoline with the file name the game insists on; runs `node agent.js`; shows an error box if the agent fails |
| `dpenum.js` | fake DirectPlay host (section 4) |
| `run-server.vbs` | hidden launcher used by the Startup-folder shortcut |
| `Install-NetAccolade.ps1` | compiles the exe, writes the registry value (UAC once), creates the autostart shortcut, starts the server; `-Status`, `-Uninstall`, `-AutostartOnly` |

Features added over the session, in order:

1. Lobby, launch, agent, trampoline, installer (registry `HKLM\SOFTWARE\WOW6432Node\Accolade\NetAccolade`).
2. Agent finds `DEADLOCK.EXE` by walking up from its own folder, because the game `chdir`s into the
   NetAccolade folder before spawning `MClient.exe` (found from the first in-game click).
3. `/fakeclient N` chat command: bot players with simulated clients that join your game and press Ready,
   so Launch can be tested alone; bots report the `deadlock.ini` they receive; `/fakeclient clear`.
4. TCP/IP addressing: per-game connect address set by the host (or automatic: the host client's address,
   else its LAN IP, else `127.0.0.1`), shown in the game panel with this PC's LAN addresses;
   **Connect to IP** for a direct join without a lobby game; validation (IPv4 or host name).
5. Permanent server: `server.json` (`port`, `host`, `fakeGames`), hidden start via `run-server.vbs`,
   Startup-folder shortcut `NetAccolade Lobby.lnk`, idempotent (second start exits on the busy port).
6. `/fakegames N`: fake DirectPlay sessions (section 4), persisted in `server.json`, paused automatically
   while this PC hosts a real game through the lobby.

## 3. Verification runs (all on this PC)

| what | result |
|------|--------|
| `MClient.exe -gi 1 -o deadlock.ini` from the game dir, lobby in browser, host + API joiner, Launch | agent wrote `Role=master` file, started `DEADLOCK.EXE -ms`, game listened on TCP 2300/UDP 2350; joiner received `Role=slave`, `Master Address=127.0.0.1`; agent removed the file after the game exited |
| in-game NetAccolade button (clicked by the user) | game spawned the client, lobby opened in Edge; exposed the `chdir` bug, fixed |
| Launch with the game-spawned client as host and an API stand-in joiner | file written, game started with `-ms`, cleanup confirmed |
| `/fakeclient 2` through the API and by the user | bots joined, Ready, Launch produced master/slave plan; user confirmed "works good" |
| address logic | automatic address 192.168.8.49, explicit `my-host.example.net`, direct connect `203.0.113.7` all landed in the right files |
| `/fakegames 3` then game join list at `localhost` | first attempts empty; after switching replies to TCP the list showed "Tarth Uprising", "Gallius IV Revisited", "Shrine Rush" (screenshot) |
| joining a fake game | first "Could not connect"; after mirroring the real host's SUPERENUMPLAYERSREPLY and long-name convention the game reached "Waiting for Game: Deadlock 2 is waiting for the master computer to start the game" and sent its 92-byte hello capsule (type 9) |

## 4. The DirectPlay protocol work

Nothing in the game speaks to a lobby over the network, so single-PC testing of the join screen
needed a fake DirectPlay host. Because the real game can only run once per machine, its behaviour was
learned by three methods:

1. **Guess from the public spec**, which failed twice: replies must go out over a **new TCP connection
   to the requester's stream port**, not over UDP, and the host's reply after a join is
   **SUPERENUMPLAYERSREPLY (cmd 41)** with super-packed player records, not the old ENUMPLAYERSREPLY.
2. **Capture the real game as host**: run `DEADLOCK.EXE -ms` with `host.ini`, act as a DirectPlay client
   from Node (`scratchpad/capjoin*.js`), record every byte. This gave the exact ENUMSESSIONSREPLY,
   REQUESTPLAYERREPLY and SUPERENUMPLAYERSREPLY layouts.
3. **Disassemble `dplayx.dll`** (capstone sweep, base 0x5e080000) to explain the join rejection
   `0x8877014A` (DPERR_NONEWPLAYERS): the routine at `0x5e094492..0x5e0944e7` compares the **last DWORD of
   ADDFORWARDREQUEST with the session's `dwReserved1`** (the client copies it from the enumeration reply;
   a real host uses its system player ID), then checks `DPSESSION_JOINDISABLED` and the player count.
   The field I had taken for a tick count is a session stamp.

Two game-specific facts fell out as well: Deadlock II passes player names in `DPNAME.lpszLongName`
(so "Deadlock 2 Host" must be a long name), and the joiner's hello is a 92-byte player message whose
type byte (offset 8) is 9.

`dpenum.js` now: UDP 47624 enumeration → TCP reply; one TCP port per fake session (2300, 2301, 2302);
REQUESTPLAYERID/REPLY, ADDFORWARDREQUEST → SUPERENUMPLAYERSREPLY (host system player 0x0f, host app
player 0x0c with long name, joiner 0x05 with its IP filled in), CREATEPLAYER, PING/PINGREPLY,
DELETEPLAYER, player messages logged and reported to the lobby chat.

## 5. Current state on this PC

- Registry `HKLM\SOFTWARE\WOW6432Node\Accolade\NetAccolade\PATH` → `...\netaccolade\server`; in-game
  NetAccolade button works.
- Lobby server runs hidden, restarts at logon, `http://127.0.0.1:7624`, three fake sessions advertised
  (`server.json`: `fakeGames: 3`).
- Game paths that work: NetAccolade button → lobby → Launch (host or joiner); Connect to IP; the game's
  own TCP/IP join dialog with `localhost` shows and joins the fake sessions up to the waiting screen.
- Generated files (not source): `MClient.exe`, `mclient.log`, `server.log`.

## 6. Not done / open

- A real second machine joining a real host over TCP/IP has not been tested. DirectPlay 4 embeds
  addresses in its messages, so expect to need a VPN (ZeroTier, Tailscale, Hamachi) or forwarding of
  TCP/UDP 2300–2400 and UDP 47624.
- The fake host cannot play the master's part: after the hello the real master streams the saved game and
  drives turns with 92-byte capsules (`MasterDispatchNetMessage`, `SyncGame`, `NetGame.Sav`,
  `net_map.xfer`). Emulating that means reverse engineering that capsule protocol from `DEADLOCK.EXE`.
- Everything in the lobby is in memory; a restart empties players and games (settings persist).
- The `debug/`-style artefacts of this work (capture scripts, screenshots) live in the session scratchpad,
  not in the repo; the protocol notes carry the captured bytes that matter.

## 7. Pitfalls met along the way, for the next person

- Git Bash rewrites Windows-looking paths and `/switches` in arguments: `csc /nologo` became a file
  path, `C:\\Users` in JSON broke; use PowerShell for those or forward slashes.
- PowerShell: a script parameter named `$B` silently shadowed a local `$b`; `$Host` is a reserved
  automatic variable (renamed to `-HostGame`); screen captures need `SetProcessDPIAware` on a scaled display.
- JavaScript `0xFAB << 20` overflows to a negative number; use `>>> 0` when writing DWORDs.
- The game exits itself when a `-ms` launch fails, so an "exited early" run usually means a bad launch
  file, not a crash; `DEBUG.TXT` always ends at "GetGameOptions" either way.
- Only one `DEADLOCK.EXE` per PC (it exits if the `XenoMainWnd` window exists), and `dplaysvr.exe` keeps
  UDP/TCP 47624 after the game closes; kill it before starting the fake host.
