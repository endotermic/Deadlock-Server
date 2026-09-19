# NetAccolade stand-in for Deadlock II (Node.js)

A localhost lobby that the game "connects to" exactly the way it connected to NetAccolade in 1998.
How that worked is in `../../NetAccolade-Investigation.md`; the short version:

```
game main menu "NetAccolade"
   └─ spawns  <HKLM\SOFTWARE\Accolade\NetAccolade\PATH>\MClient.exe -gi 1 -o deadlock.ini   (cwd = game dir), game quits
        └─ MClient.exe  →  node agent.js  →  registers with server.js (http://127.0.0.1:7624), opens the lobby page
             lobby: chat, Create Game (all scenario/world options), Join, Ready, host presses Launch
        └─ server builds one deadlock.ini per player (Role=master / Role=slave + Master Address), sends it to each agent
             agent writes <game dir>\deadlock.ini and runs  DEADLOCK.EXE -ms  (the game skips the menus and hosts/joins)
```

Only `[Startup] Master Address` in that file can carry an IP address, so this is also the only way to
play Deadlock II over the Internet.

## Files

| file | role |
|------|------|
| `server.js` | the lobby server (HTTP + Server-Sent Events, no npm dependencies, Node 18+) |
| `public/index.html` | the lobby page |
| `agent.js` | the client the game starts; talks to the server, launches the game |
| `MClient.cs` → `MClient.exe` | trampoline with the file name the game insists on; runs `node agent.js` |
| `Install-NetAccolade.ps1` | compiles `MClient.exe`, writes the registry value (asks for admin once) |
| `mclient.log` | agent log (created on first use) |

## Install

```powershell
cd <game dir>\netaccolade\server
.\Install-NetAccolade.ps1        # compiles MClient.exe, sets HKLM\SOFTWARE\WOW6432Node\Accolade\NetAccolade\PATH
.\Install-NetAccolade.ps1 -Status
```

Requirements: Node.js on `PATH` (tested with v20) and the .NET Framework 4 C# compiler that ships
with Windows (`%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe`).

## Run

The installer also makes the server permanent: it puts a shortcut to `run-server.vbs` into your
Startup folder (`shell:startup`, no admin needed) and starts it right away. `run-server.vbs` runs
`node server.js` hidden and appends its output to `server.log`; starting it twice is harmless (the
second instance finds port 7624 taken and exits). Settings live in `server.json`:

```json
{ "port": 7624, "host": "127.0.0.1", "fakeGames": 3 }
```

`fakeGames` is the number of fake DirectPlay sessions advertised on UDP 47624 from start-up, so the
game finds sessions at `127.0.0.1` / `localhost` (Connect to IP, or its own TCP/IP join dialog) at any
time, lobby or not, and can join them (see below). `/fakegames N` in the
chat updates the file. `Install-NetAccolade.ps1 -Uninstall` removes the shortcut, stops the server
and deletes the registry key.

You can still run it by hand (`node server.js`), and `agent.js` starts it on demand if nothing is
listening on port 7624 when the game launches `MClient.exe`. Then in Deadlock II pick **NetAccolade** from the main menu. The game asks
"Quit Deadlock 2 to run NetAccolade?", quits, and the lobby page opens with your game client linked
("client: connected · C:\...\Deadlock2" in the header).

Without the registry step you can also start the client by hand from the game directory:

```powershell
cd <game dir>
.\netaccolade\server\MClient.exe -gi 1 -o deadlock.ini
```

## Testing alone: fake clients

Type `/fakeclient 2` (1–6) in the lobby chat. Two bot players with simulated, always-connected
clients appear, join your game (or the next one you create or join) and press Ready, so the host's
Launch button becomes usable. On Launch the bots do not start a game; each posts the `deadlock.ini`
it was given (role, master address, size) to the launch chat. `/fakeclient clear` removes them,
`/help` lists the commands. Your own real client still launches Deadlock II as usual.

## Testing the join list alone: fake DirectPlay sessions

The game can only run once per PC, so with a single machine the "Join a Network Game" list is
always empty. `/fakegames 3` (1–10) in the lobby chat makes the server impersonate DirectPlay hosts
(`dpenum.js`): it answers session enumeration on UDP 47624 with three fake Deadlock II sessions, each
with its own TCP port (2300, 2301, 2302), and speaks enough of the DirectPlay 4 join protocol for the
game to **join** one: type `localhost` in the game's TCP/IP dialog (or use Connect to IP), pick a
session, press OK. The game gets its player IDs, the player list with a "Deadlock 2 Host" player,
creates "Deadlock 2 Player", and sends its hello capsule; the lobby chat reports each step. What does
not happen is the game itself: a fake host has no game state to send, so the joiner then waits for
the master forever. `/fakegames clear` stops it. UDP 47624 and TCP 2300+ must be free (a real
Deadlock II host or `dplaysvr.exe` on this PC also use them; hosting through the lobby pauses the fakes).
The message layouts were copied from captures of a real Deadlock II host and checked against the
`dplayx.dll` disassembly; see `protocol-notes.md`. Verified in the game on 2026-09-19. `NETACC_FAKEGAMES=3 node server.js` advertises from start-up.

## TCP/IP addressing

The joiner's game connects to whatever ends up in `[Startup] Master Address`. The server fills it in
per joiner:

1. the address the host typed for the game ("Connect address for joiners" when creating it, or
   "Set address" in the game panel, `auto` to reset), otherwise
2. the address the host's client connected to the server from, if that is not loopback, otherwise
3. the first LAN IPv4 the host's client reported, otherwise `127.0.0.1` (everything on one PC).

The game panel shows the address joiners will get and this PC's LAN addresses. For play over the
Internet set your public IP (with TCP/UDP 2300–2400 and UDP 47624 forwarded to the host) or,
more reliably, a VPN address (ZeroTier, Tailscale, Hamachi).

**Connect to IP** in the Play panel skips the lobby game entirely: it launches your Deadlock 2 as a
joiner pointed at the address you enter (the game then shows that host's session in its join list).
Use it when the host is not using this lobby at all, for example a host started with
`Start-OnlineWar.ps1 -HostGame`.

## Playing with someone else

The server is bound to `127.0.0.1` by default. To let other players in, run it on one machine with
`NETACC_HOST=0.0.0.0 node server.js` (or behind a VPN such as ZeroTier/Tailscale) and point the other
players' agents at it with `mclient.json` next to `agent.js`:

```json
{ "server": "http://10.147.17.5:7624" }
```

(or `set NETACC_SERVER=http://10.147.17.5:7624`). The server uses the address it sees each agent
connect from as that player's `Master Address`, so with a VPN everything lines up. DirectPlay itself
still needs TCP/UDP 2300–2400 and UDP 47624 open between the players; see the investigation document.

## HTTP API (for scripting/tests)

Agent side: `POST /api/agent/register {gameDir, outFile, gameId, ips}` → `{token, lobbyUrl, pollPath}`;
`GET /api/agent/:token/wait` long-poll (25 s) → `{command:'none'}` or
`{command:'launch', file, content, exe, args, delayMs}`; `POST /api/agent/:token/done {status}`.

Browser side: `POST /api/player/join {name, agent}` → `{player}`; `GET /api/events?player=` (SSE:
`state`, `chat`); `GET /api/state?player=`; `POST /api/chat {player, text, channel}`;
`POST /api/game/create {player, name, description, options}`; `POST /api/game/join {player, key}`;
`POST /api/game/leave`; `POST /api/game/ready {player, ready}`; `POST /api/game/launch {player}`;
`GET /api/ini/preview?player=`; `GET /api/options/schema`.

## Behaviour notes

- The launch file is written by the agent right before starting the game and deleted when the game
  exits. A stale `deadlock.ini` in the game directory would hijack the next `-ms` start.
- Joiners are launched 4 s after the host so the host's DirectPlay session exists when their game
  reaches the session list. Joiners still have to click the host's session in the game's
  "Join a Network Game" screen; that screen cannot be skipped.
- `[Scenario Options] Players` (colonies) is raised to the number of humans if needed; the
  difference is filled with AI colonies.
- Everything is in memory; restarting the server empties the lobby.
