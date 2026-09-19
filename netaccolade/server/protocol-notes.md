# DirectPlay 4 TCP/IP wire protocol as Deadlock II uses it

Captured on 2026-09-19 against the real game (Windows 11, `dplayx.dll` v14 wire version) and checked
against the disassembly of `C:\Windows\SysWOW64\dplayx.dll` (image base 0x5e080000 in the sweep).
All offsets below are hex unless stated. Multi-byte integers are little-endian except sockaddr ports.

## Framing

Every message: **SP header (20 bytes)** then a **core message**.

```
SP header: DWORD sizeToken   size = low 20 bits (whole message incl. header), token = high 12 bits (0xFAB in every
                              captured message, requests and replies alike)
           SOCKADDR_IN      family 2, port big-endian, IPv4, 8 zero bytes  = the sender's *stream* (TCP) port;
                              IP usually 0.0.0.0 (receiver substitutes the connection's source address)
core:      "play" WORD command WORD version(14)
player msg (no "play"): DWORD idFrom, DWORD idTo, payload
```

Transport rules observed:
- Session enumeration request: UDP to port 47624 (`dplaysvr.exe` forwards to the local host process).
- **Every reply is sent on a new TCP connection from the host to the requester's IP and the port in the
  request's SP header.** No reply ever comes back on the requester's own connection or over UDP.
- A client keeps one TCP connection to the host's stream port for REQUESTPLAYERID, ADDFORWARDREQUEST,
  CREATEPLAYER and later game messages.
- Host process ports: TCP 2300 (first free in 2300..2400), UDP 2350; `dplaysvr.exe` holds UDP/TCP 47624.

## Enumeration

```
ENUMSESSIONS (cmd 2), 54 bytes:  GUID application, DWORD passwordOffset(32), DWORD flags(0x91), WCHAR password[]
ENUMSESSIONSREPLY (cmd 1), one per session:
  DPSESSIONDESC2 (80): dwSize 80, dwFlags 0x44, guidInstance, guidApplication, dwMaxPlayers 7, dwCurrentPlayers,
                       lpszSessionName 0, lpszPassword 0, dwReserved1 = HOST SYSTEM PLAYER ID, dwReserved2 0, dwUser1..4 0
  DWORD nameOffset (0x5c = from the "play" signature), WCHAR sessionName[]
Real capture: 8600b0fa 0200 08fc 00.. | 706c6179 0100 0e00 | desc | 5c000000 | "Online War\0"
```

## Join

```
REQUESTPLAYERID (cmd 5):       DWORD flags   9 = system player, 8 = application player   (both from the client)
REQUESTPLAYERREPLY (cmd 7):    DWORD id, 24 zero bytes (DPSECURITYDESC, dwSize 0), DWORD 0, DWORD 0, HRESULT 0
ADDFORWARDREQUEST (cmd 0x13):  DWORD idTo 0, playerId, groupId 0, createOffset 0x1c, passwordOffset 0x6c,
                               DPLAYI_PACKEDPLAYER (80 bytes, see below), WCHAR password "" , DWORD sessionStamp
SUPERENUMPLAYERSREPLY (cmd 0x29): DWORD playerCount, groupCount 0, packedOffset, shortcutCount 0,
                               descOffset 0x24, nameOffset 0x74, passwordOffset 0, DPSESSIONDESC2, WCHAR sessionName
                               (no padding), then DPLAYI_SUPERPACKEDPLAYER records
ADDFORWARDREPLY (cmd 0x24):    HRESULT error, only on rejection (e.g. 0x8877014A DPERR_NONEWPLAYERS)
CREATEPLAYER (cmd 8):          same shape as ADDFORWARDREQUEST, packed player carries the name; no reply
DELETEPLAYER (cmd 0x0b):       sent by the client when it leaves
PING (0x16) / PINGREPLY (0x17): DWORD idFrom, DWORD tickCount
```

`DPLAYI_PACKEDPLAYER` (client → host): 12 DWORDs `size, flags, id, shortNameLen, longNameLen, spDataLen(32),
playerDataLen, numPlayers, systemPlayerId, fixedSize(48), version(14), parentId`, then shortName, longName,
SP data (two SOCKADDR_IN: stream, datagram), player data, player IDs. **Deadlock II puts the player name in
`longName`** (its CGNet wrapper fills `DPNAME.lpszLongName`), e.g. `CREATEPLAYER ... long "Deadlock 2 Player"`.

`DPLAYI_SUPERPACKEDPLAYER` (host → client, real capture):
```
10000000 05000000 6d61f405 04000000 0e000000 20 [32 bytes sp data]        joiner's system player: flags 0x05, version 14
10000000 0f000000 6f61f405 04000000 0e000000 20 [sp: 2300 / 2350, ip 0]    host system player:     flags 0x0f (sys|nameserver|ingroup|local)
10000000 0c000000 6e61f405 06000000 6f61f405 "Deadlock 2 Host\0" 20 [sp]  host app player:        flags 0x0c, versionOrSys = host sys id
```
Fields: DWORD 16, flags, id, infoMask, versionOrSystemPlayerId; infoMask bit0 shortName present, bit1 longName
present, bits2-3 SP-data length size (1 = one byte), then the optional strings, then the length byte and SP data.
The host writes the joiner's real IP into the joiner's SP data; its own players keep 0.0.0.0.

### Why joins were rejected with DPERR_NONEWPLAYERS

`dplayx.dll` join validation (sweep addresses 0x5e094492..0x5e0944e7):
1. if the request has a password offset: the **last DWORD of ADDFORWARDREQUEST** must equal the session's
   `dwReserved1` (the client copies it from ENUMSESSIONSREPLY); mismatch → 0x8877014A;
2. `dwFlags & DPSESSION_JOINDISABLED (0x20)` → 0x8877014A;
3. `dwMaxPlayers != 0 && dwCurrentPlayers >= dwMaxPlayers` → 0x8877014A.
Password mismatch gives 0x88770154 (DPERR_INVALIDPASSWORD). The other 0x8877014A site (0x5e08ebe9) is the
client-side Open path (session flags 0x21 or full session).

## Deadlock II on top of DirectPlay

- Session created with flags `DPSESSION_KEEPALIVE | DPSESSION_MIGRATEHOST`, max 7 players, host player
  long-named "Deadlock 2 Host" (a closed game renames it "Closed Deadlock 2 Host"), joiners "Deadlock 2 Player".
- After joining, a client looks for the host player by that long name, then sends a 92-byte guaranteed
  player message (type byte 9 at offset 8) to it and waits for the master's game state; the master then
  streams the game and drives every turn with 92-byte capsules. Nothing outside the real game speaks that
  part, so a fake host can bring a client only as far as "connected, waiting for the game".
