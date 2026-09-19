# NetAccolade in Deadlock II: how it is wired into the game

Goal: understand how the (long dead) NetAccolade matchmaking service talked to `DEADLOCK.EXE`, so that
Internet multiplayer ("online war") can be done without it. Everything below was read out of the
v1.20 GOG binaries (`DEADLOCK.EXE`, `CGNET.DLL`) and confirmed by running the game. Addresses are
virtual addresses in `DEADLOCK.EXE` (image base `0x400000`, `debug/full.asm`) unless marked `CGNET`.

## TL;DR

- NetAccolade was a separate lobby program (`MClient.exe`) that is **not** part of the GOG install.
  Deadlock II only knows two things about it: how to start it, and how to be started by it.
- The handoff from the lobby to the game is a plain INI file, `deadlock.ini`, in the game directory,
  plus the command-line switch `-ms` ("matching service"). Nothing else: no lobby DLL, no DirectPlay
  lobby connection, no network protocol between lobby and game.
- All actual networking is DirectPlay 3 (`DPLAYX.dll`) behind a thin Cyberlore wrapper, `CGNET.DLL`.
  For Internet play the wrapper uses the TCP/IP service provider and connects to an explicit IP address
  given in `deadlock.ini` (`[Startup] Master Address`). The in-game menus have **no** field to type an
  IP address; they only find sessions by LAN broadcast. The launch file is therefore the only way to
  play over the Internet, which is exactly the job NetAccolade did.
- Tested on this machine (Windows 11, legacy DirectPlay component installed): writing `deadlock.ini`
  by hand and starting `DEADLOCK.EXE -ms` makes the GOG build host a DirectPlay TCP/IP session
  (process loads `DPLAYX.dll` and `dpwsockx.dll`, listens on TCP 2300 and UDP 2350).
  Ready-made files and a launcher script are in `netaccolade/`.
- `netaccolade/server/` contains a working NetAccolade stand-in: a Node.js lobby server plus an `MClient.exe`
  that the game's own NetAccolade button starts. Verified 2026-09-19: game → `MClient.exe -gi 1 -o deadlock.ini`
  → browser lobby → Launch → agent writes `deadlock.ini`, starts `DEADLOCK.EXE -ms`, game hosts on TCP 2300.
  See `netaccolade/server/README.md`.

## 1. Starting NetAccolade from the game

Main-menu entry "NetAccolade" runs `LaunchNetAccolade` at `0x467884`:

1. `RegOpenKeyExA(HKEY_LOCAL_MACHINE, "SOFTWARE\Accolade\NetAccolade")`, then `RegQueryValueExA("PATH")`.
   Missing key or value shows "NetAccolade has not been installed properly. Please reinstall or call
   Accolade customer support."
2. Builds `<PATH>\MClient.exe` and asks "Quit Deadlock 2 to run NetAccolade?".
3. `spawnv(P_NOWAIT, "<PATH>\MClient.exe", ["<PATH>\MClient.exe", "-gi", "1", "-o", "deadlock.ini"])`
   (Borland RTL spawn at `0x4b1bfc`). `-gi 1` is presumably NetAccolade's game id for Deadlock II and
   `-o deadlock.ini` the launch file it should write back. `errno == ENOENT` gives "The NetAccolade
   program file couldn't be found.", any other error "Couldn't start NetAccolade. Try shutting down other
   applications...".
4. On success the menu returns state `0x33`, which makes the dispatcher return 0 and the game exits.

The strings at `0x4d51c3..0x4d5208` hold the key, value name, exe name and the argument vector.
Sibling launchers for the other 1998 services also exist and use the same idea (external program plus
data file): Mplayer via `MPCHECK.EXE`, TEN via `tengame.ini` / `ten.ini`. None of these files ship with
the GOG version.

## 2. Being started by NetAccolade: `-ms` and `deadlock.ini`

### Command-line switches (`0x46fae4`)

| Switch | Effect |
|--------|--------|
| `-p` | clears `0x4d1f58` (palette related) |
| `-w` | clears `0x4d5a80` (windowed / display related) |
| `-16`, `-8` | colour depth hint (`0x4d5a8c` = 16 or 8) |
| `-debug` | `0x51c3a0 = 1`, extra debug behaviour |
| `-dd` | `0x4d5a84 = 1` (DirectDraw related) |
| `-ns` | `0x4d599c = 0` |
| `-ms` | **matching-service launch**: `g_launchFlags (0x4d59a8) = 1`, game reads `.\deadlock.ini` at startup |
| `-nosound` | `0x4d5b40 = 1` |

Anything else prints "Unknown parameter %s" and aborts.

### When the file is read

`ReadLaunchFile` at `0x470554` runs once during start-up (after the intro, right before the
"GetGameOptions" line is written to `DEBUG.TXT`) and only if `-ms` was given. It reads
`.\deadlock.ini` (relative to the working directory, string at `0x4d5df4`) with
`GetPrivateProfileStringA`. A key is considered missing when the default string `<empty>` comes back
or the value fills the whole buffer. Every reader below aborts at the first missing or out-of-range
key and leaves the launch flag at 0, in which case the game just shows the normal main menu.

### File format

Section and key names are the pointer table at `0x4d5f5c`; the readers are `ReadMeta 0x471068`,
`ReadScenarioOptions 0x471170`, `ReadWorld 0x471634`, `ReadStartup 0x4719a0`.

```ini
[Meta]
File Type=new_game            ; saved_game | map_file | new_game   (index 0 | 1 | 2)
Version=288                   ; only read for saved_game and map_file; integer, must be 0..288 (0x120 = v1.20)
Name=Online War               ; session name shown to joiners; max 63 chars, truncated to 31 for DirectPlay

[Scenario Options]            ; required for new_game and map_file, ignored for saved_game
Players=2                     ; 2..7 (total colonies incl. AI)
Victory Condition=conquest    ; manifest_destiny | conquest | shrine_wars
Win Cities=5                  ; one of 2 3 5 7 10
Win Shrines=3                 ; one of 2 3 5
Win Turns=5                   ; one of 3 5 8
AI Skill Level=0              ; -2..3 (stored as value+2 = 0..5)
Random Events=true            ; true | false  (anything but "true" is false)
Allow Alliances=true
Fast Production=false
World Resources=true
Last Player Timer=false       ; if true, Last Player Clock applies
Last Player Clock=60          ; 3..960 (seconds)
Auto Timer=false              ; forced off when Last Player Timer is on
Auto Timer Clock=60           ; 3..960
Racial Abilities=standard_ability ; standard_ability | best_ability | no_ability

[World]                       ; required for new_game and map_file
Type=small                    ; small | medium | large | huge | custom
; the rest of the section is read only when Type=custom
Map File=maps\mymap.SAV       ; optional; non-empty makes this a map_file launch (loads that scenario)
Size=30                       ; 20..40, used for both width and height
Color=earthlike               ; earthlike | tropical | mars | icy | dry | volcanic | alien
Percent Oceans=30             ; 0..75
Percent Plains=30             ; the six percentages must add up to exactly 100
Percent Forests=20
Percent Swamps=10
Percent Mountains=5
Percent Wastelands=5

[Startup]
Role=master                   ; "master" hosts, anything else joins
Save File=Saves\MYGAME.SAV    ; read only for saved_game; path of the .SAV to continue, max 127 chars
Master Address=203.0.113.7    ; read only when Role is not master; IP or host name passed to DirectPlay, max 63 chars
User Name=Nika                ; your in-game player name, max 31 chars
Players=2                     ; humans the host waits for before starting (Role=master), 2..7
```

Validation details: `File Type` index is stored at `opts+0x5c`; `Version` is compared against the
running version `0x4d5ae8 = 0x120`; `Players` (Scenario) goes to `opts+0xc`; `Win *` are checked against
the tables at `0x4c425c/0x4c4274/0x4c4280`; timer clocks land in `opts+0x5a/+0x52`; the `[Startup]`
values go to `g_launchPlayers 0x4d5140`, the player-name buffer `0x509804` ("New Player" by default),
and a local address buffer that is handed straight to `CGNetService_ConnectToIPAddress`.

`ReadLaunchFile` then sets `g_launchFlags`:

| bit | meaning |
|-----|---------|
| `0x01` | `new_game` launch (random or custom world from `[World]`) |
| `0x02` | `saved_game` launch (continue `[Startup] Save File`) |
| `0x04` | `map_file` launch (`[World] Map File` non-empty) |
| `0x10` | `Role=master` |

Two extra checks live here: `[Startup] Players < 2` shows "Can't start network game with less than 2
participants." and drops the launch; a master without a "CD" shows the CD dialog and quits on Cancel.
The GOG build's CD check (`FindCD 0x457864`) looks for `.\DEADCINE.CAM` on a fixed drive, so it passes
as long as the working directory is the game directory.

### What the game does with it (state machine `0x469074`)

The main loop is a state dispatcher (state byte → handler, jump table at `0x4690d8`). Relevant states:

| state | handler | what happens |
|-------|---------|--------------|
| `0x35` | `0x467c8c` main menu | with launch flags set the menu is skipped: master+saved_game → `0x2e`, everything else → `0x37` |
| `0x2e` | `0x468d3c` | master + saved_game: reads the save's player table (`0x461e9c`, offset `0x15c`), sets `g_loadNetGame 0x4d513c`, → `0x38` |
| `0x37`/`0x38` | `0x468214` NetInit | DirectPlay 3 check, `CGNet_Cleanup`, re-reads `[Startup]`, **forces the TCP/IP provider** (`g_service 0x4d5a50 = 8`), errors "TCP/IP service is not available / could not be initialized"; master → `0x3b`, joiner → `0x3f` |
| `0x3b` | `0x468470` | reads `[Meta] Name` into the session-name buffer `0x5097c4` → `0x3d` |
| `0x3d` | `0x4684d0` | `map_file`: loads the map (`0x461c68`); `new_game`: generates the world from `[World]`; → `0x40` |
| `0x40` | `0x468a28` | host: `SelectService(TCP/IP)`, "Initializing Network" dialog, `HostSession(name)`, then `WaitForPlayers 0x468898` until `[Startup] Players` humans joined (dialog "Waiting for %d players"), `DisableJoin`, → `0x43` |
| `0x3f` | `0x468800` joiner | `ConnectToIP([Startup] Master Address)`, then the normal "Join a Network Game" session list (`0x426ba4`); OK → `0x41`, Cancel → `0x37` |
| `0x41` | `0x468c94` | `JoinSession(selected)` → `0x43` |
| `0x43` | `0x468ea4` | starts the game; master sends the game state to everyone ("Sending Game Data"), joiners receive it |
| `0x33` | | quit game (used after launching NetAccolade) |

Note for joiners: after connecting to the master's address the game still shows the session list; the
host's session (named after `[Meta] Name`) appears there and has to be selected and confirmed. That is
the DirectPlay "enumerate sessions at a given address" step and cannot be skipped.

## 3. The network layer: `CGNET.DLL` over DirectPlay 3

`CGNET.DLL` ("CGNet", Borland C++ 5, classes `CGNet`, `CGNetService`, `CGNetSession`, `CGNetPlayer`,
`CGNetMessage`) exports a C API; `DEADLOCK.EXE` imports 20 of the 28 functions. It links
`DirectPlayCreate`, `DirectPlayEnumerateA`, `DirectPlayLobbyCreateA` from `DPLAYX.dll`.

| CGNet call | DirectPlay behind it (`CGNET` addresses) |
|-----------|-------------------------------------------|
| `CGNet_Initialize(&obj, &appGuid)` `0x401190` | stores the application GUID and `IID_IDirectPlay2A/3A`; no DirectPlay call yet |
| `CGNet_FindServices(obj, &list)` `0x40128d` | `DirectPlayEnumerateA`, one `CGNetService` per provider |
| `CGNetService_GetType` `0x401e4c` | compares the provider GUID: **1 = IPX, 2 = TCP/IP, 3 = Serial, 4 = Modem** |
| creating the DP object `0x402593` | `DirectPlayCreate(spGuid)` + `QueryInterface(IID_IDirectPlay3A)`, fallback `IDirectPlay2A` |
| `CGNetService_ConnectToIPAddress(svc, "ip")` `0x401ed4` | TCP/IP only (else error -7); requires `IDirectPlay3` (else error `-0x85`); builds a compound address `{DPAID_ServiceProvider = DPSPGUID_TCPIP, DPAID_INet = "ip"}` with `IDirectPlayLobby2A::CreateCompoundAddress` and calls `IDirectPlay3::InitializeConnection(addr, 0)` (`0x40260d`) |
| `CGNetService_FindSessions` `0x401fb9` | `EnumSessions(DPSESSIONDESC2{guidApplication}, 0, cb, ctx, flags)`, flags `0x91` = AVAILABLE|ASYNC|RETURNSTATUS (modem: AVAILABLE only) |
| `CGNetService_CreateSession(svc, &sess, name, maxPlayers)` `0x402149` | `Open(DPSESSIONDESC2{flags = DPSESSION_KEEPALIVE|DPSESSION_MIGRATEHOST (0x44), guidApplication, dwMaxPlayers, lpszSessionNameA}, DPOPEN_CREATE)` |
| `CGNetSession_Join` `0x402b2b` | `Open(desc{guidInstance}, DPOPEN_JOIN|DPOPEN_RETURNSTATUS (0x81))` |
| `CGNetSession_FindPlayers` `0x402bdc` | `EnumPlayers(NULL, cb, ctx, DPENUMPLAYERS_SESSION if not yet joined else 0)` |
| `CGNetSession_CreatePlayer(sess, &plr, name)` `0x402d23` | `CreatePlayer(&id, DPNAME{lpszShortNameA = name}, NULL, NULL, 0, 0)` |
| `CGNetSession_DisableJoin` `0x402e9c` | `GetSessionDesc`, `dwFlags |= DPSESSION_JOINDISABLED (0x20)`, `SetSessionDesc` |
| `CGNetPlayer_SendMessageGuaranteed(from, to, buf, len)` `0x403a1f` | `Send(from, to, DPSEND_GUARANTEED, buf, len)` |
| `CGNetPlayer_BroadcastMessageGuaranteed` `0x4038e1` | `Send(from, DPID_ALLPLAYERS, DPSEND_GUARANTEED, ...)` |
| `CGNetPlayer_GetMessage` `0x403b4a` | `Receive` into an internal queue, returns a `CGNetMessage` |
| `CGNetMessage_GetInfo(msg, &from)` `0x404d59` | 1 = player data (from = sender), 2 = `DPSYS_CREATEPLAYERORGROUP`, 3 = `DPSYS_DESTROYPLAYERORGROUP`, 4 = `DPSYS_SESSIONLOST`, 5 = `DPSYS_HOST`, 6 = `DPSYS_SETPLAYERORGROUPNAME`, 7 = other system message |
| `CGNetMessage_GetBuffer(msg, &len)` `0x404e21` | payload pointer and size |

DirectPlay HRESULTs are mapped to small negative CGNet codes in `0x4016a7`; the game only tests two of
them: `-0x85` (no `IDirectPlay3`) → "Your version of DirectX is not supported by the Matching Service",
and generic failure.

GUIDs: application GUID **`{9F39C820-8CFE-11D1-904F-00A0C9363012}`** (`0x4d171c`); the four
`DPSPGUID_*` provider GUIDs, `DPAID_INet`, `DPAID_ServiceProvider`, `IID_IDirectPlay2A/3A` and
`IID_IDirectPlayLobby2A` live in `CGNET.DLL` `.data` at `0x40ff68..0x40ffd8`. The "DirectX version"
probe at `0x458bc8` does `DirectPlayCreate(GUID_NULL)` and queries `IDirectPlay3A`, then `IDirectPlay2A`.

### The game side of the wrapper (`0x457dbc..0x4586f0`)

- `NetInit 0x457dbc`: `CGNet_Initialize` + `CGNet_FindServices`; `ServiceMask 0x458138` turns the provider
  types into a bitmask **modem = 1, serial = 2, IPX = 4, TCP/IP = 8** (`g_service 0x4d5a50`);
  `SelectService 0x4581a8` picks the matching `CGNetService`.
- `HostSession(name) 0x4582e0`: session name truncated to 32 bytes, `CreateSession(..., maxPlayers = 7)`,
  host player named **"Deadlock 2 Host"** (`0x4d172c`; "Closed Deadlock 2 Host" `0x4d173c` is the name
  a joiner also accepts as host), `g_isHost 0x583858 = 1`.
- `EnumSessions 0x4583ac`: up to 20 session names in `0x58389c` (32 bytes each), handles in `0x583b1c`.
- `JoinSession(idx) 0x458434`: `Join`, local player **"Deadlock 2 Player"**, finds the host by player
  name (`FindHostPlayer 0x458640`), stores the master's id in `0x4d1714`, then sends a 92-byte hello
  capsule (type 9).
- `NetSend(buf, toId, broadcast) 0x458550`: every game message is a fixed **92-byte (0x5c) capsule**, sent
  guaranteed; `toId != 0` unicast, host or `broadcast` flag → all players, otherwise → master. `NetRecv
  0x457f7c` copies incoming capsules into `NetReceiveCapsule` blocks and turns
  `DPSYS_DESTROYPLAYERORGROUP` into a synthetic `NetPlayerDisconnect` capsule (type 0xd). Capsule
  header as far as it was needed here: byte 0 = local player index, bytes 1..2 = flags (0xff/0xfe/0xff),
  byte 8 = message type, dword at 0xe = size (0x5c), word at 0x16 = player slot.
- `NetShutdown 0x458298`: `CGNet_Cleanup`.

## 4. Doing "online war" without NetAccolade

Requirements (both sides): the 32-bit legacy DirectPlay component. On this PC it is present
(`C:\Windows\SysWOW64\dplayx.dll`, `dpwsockx.dll`; registry lists the TCP/IP, Modem and Serial
providers, no IPX). On a clean Windows 10/11 it is installed on demand the first time a DirectPlay
game runs, or via "Turn Windows features on or off → Legacy Components → DirectPlay".

Host:

1. Put a launch file in the game directory as `deadlock.ini` (`netaccolade/host.ini` is a complete
   example: `new_game`, small world, `Role=master`, `Players=2`).
2. Start `DEADLOCK.EXE -ms` with the game directory as working directory (the `-ms` flag is the whole
   trick; `netaccolade/Start-OnlineWar.ps1 -HostGame` does steps 1 and 2 and removes the file afterwards).
3. The game skips the menus, creates the DirectPlay session and waits in "Waiting for %d players" until
   the number of humans from `[Startup] Players` has joined, then starts and pushes the game state to
   everyone. Keep the "Players" values consistent: `[Scenario Options] Players` is the number of
   colonies, `[Startup] Players` the number of humans; the difference is filled with AI.

Joiner:

1. `deadlock.ini` with `Role=slave` and `Master Address=<host IP or hostname>` (`netaccolade/join.ini`).
2. `DEADLOCK.EXE -ms`. The game initialises TCP/IP, points DirectPlay at the address, and opens the
   "Join a Network Game" list. Select the host's session (its `[Meta] Name`) and confirm.

Network: DirectPlay 4's TCP/IP provider uses UDP 47624 for session enumeration and TCP+UDP 2300–2400
for data. In the host test here the process listened on TCP 2300 and UDP 2350 (plus ephemeral UDP
ports). DirectPlay of that era embeds the host's own IP address in its replies, so it does not survive
NAT well; the reliable setup is a VPN/virtual LAN (ZeroTier, Hamachi, Radmin, Tailscale with subnet
routing) and using the VPN address as `Master Address`. Direct port forwarding of the ranges above may
work if the host has a public IP.

Continuing a game: save normally (the host autosaves to `Saves\AUTOSAVE.SAV`, multiplayer saves go to
`MSaves\*.MSV`), then host with `File Type=saved_game`, `Version=288`, `[Startup] Save File=<path>`.

Things not verified yet: the joiner path end to end (only the host path was run here, and the joiner
attempt used an incomplete file), whether two GOG installs synchronise correctly over the Internet,
and the exact DirectPlay port usage of the joiner. The AI hang fixed by `Patch-DeadlockAIHang.ps1`
also affects multiplayer hosts, so keep the patch applied.

## 5. Test log (2026-09-19, this machine)

Three runs of `DEADLOCK.EXE -ms` with a hand-written `deadlock.ini`, all cleaned up afterwards:

1. `new_game` without `[Scenario Options]`/`[World]`: `ReadScenarioOptions` failed, launch flag stayed 0,
   the game fell back to the normal main menu (which is how the "any key missing → ignore" rule was found).
2. `Role=slave`, same incomplete file: launch failed the same way, the uninitialised `Players` value
   tripped "Can't start network game with less than 2 participants" and the game sat in that dialog.
3. Complete file (`netaccolade/host.ini`), `Role=master`, `Players=2`: `DPLAYX.dll`, `dpwsockx.dll`,
   `WS2_32.dll` loaded within 10 s; `netstat` showed `TCP 0.0.0.0:2300 LISTENING`, `UDP 0.0.0.0:2350`,
   two ephemeral UDP sockets; the process stayed in the wait-for-players state until killed after 45 s.
   `DEBUG.TXT` ended with the usual "GetGameOptions" line, i.e. no error path was taken.

## 6. Method and where to look next

- Strings: `python strings.py DEADLOCK.EXE` style scan; all network UI text is in `.DATA`
  (`0x50ab74..0x511300`), the launch-file vocabulary at `0x4d6068..0x4d6326`, NetAccolade strings at
  `0x4d51c3` and `0x511117..0x5112c0`.
- Imports: Borland thunks `jmp [IAT]` at `0x4b3e00..0x4b4430`; callers found with `call 0x4b43xx`.
  `RegOpenKeyExA` has a single caller (`0x4678a3`), which is how the launcher was located; the
  `GetPrivateProfileStringA` thunk has a single caller (`0x470fe1`), which led to the INI readers.
- `CGNET.DLL` was disassembled with capstone (`skipdata=True`); its exports are one-line wrappers
  (`0x404f40..0x405830`) around C++ methods, and every DirectPlay call is an indirect `call [vtbl+off]`
  on `IDirectPlay3A` (`+0x60 Open`, `+0x34 EnumSessions`, `+0x18 CreatePlayer`, `+0x68 Send`,
  `+0x58/+0x7c Get/SetSessionDesc`, `+0x38 InitializeConnection`).
- Open questions if someone wants to go further: the full 92-byte capsule protocol
  (`MasterDispatchNetMessage`, "SyncGame", `CHECKSUM.SAV`, `net_map.xfer`), and whether the join
  screen can be bypassed by pre-filling `g_selectedSession 0x4d5130`.
