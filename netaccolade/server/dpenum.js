'use strict';
/*
 * Fake DirectPlay 4 (TCP/IP service provider) host for testing the Deadlock II join screen alone.
 *
 *  - Enumeration: Deadlock II sends a DirectPlay ENUMSESSIONS datagram to UDP 47624.  We answer, the
 *    way a real host does, with a TCP connection back to the requester carrying one ENUMSESSIONSREPLY
 *    per fake session, so they show up in the game's "Join a Network Game" list.
 *  - Join: every fake session has its own TCP port (2300, 2301, ...).  When the game joins one, the
 *    DirectPlay core talks to that port: REQUESTPLAYERID -> REQUESTPLAYERREPLY, ADDFORWARDREQUEST ->
 *    ENUMPLAYERSREPLY (session description + player list incl. a "Deadlock 2 Host" player),
 *    CREATEPLAYER, PING -> PINGREPLY, and finally the game's own 92-byte hello capsule.  That makes
 *    the join succeed in the game (it then waits for game data that never comes: nothing plays the
 *    master's part).
 *
 * Wire format (MC-DPL4CS, DirectPlay 4 Protocol; offsets are from the core header "play"):
 *   SP header (20): DWORD size|token<<20, SOCKADDR_IN return address (family 2, port BE, addr, 8 zero)
 *   core header (8): "play", WORD command, WORD version
 *   ENUMSESSIONS      (2):  GUID application, DWORD passwordOffset, DWORD flags, WCHAR password[]
 *   ENUMSESSIONSREPLY (1):  DPSESSIONDESC2 (80), DWORD nameOffset, WCHAR sessionName[]
 *   REQUESTPLAYERID   (5):  DWORD flags (1 = system player)
 *   REQUESTPLAYERREPLY(7):  DWORD id, DPSECURITYDESC (24), DWORD sspiOffset, DWORD capiOffset, HRESULT result
 *   ADDFORWARDREQUEST (19): DWORD idTo, playerId, groupId, createOffset, passwordOffset, DPLAYI_PACKEDPLAYER, password, tickCount
 *   ENUMPLAYERSREPLY  (3):  DWORD playerCount, groupCount, packedOffset, shortcutCount, descOffset, nameOffset, passwordOffset,
 *                           DPSESSIONDESC2, sessionName, password, DPLAYI_PACKEDPLAYER[]
 *   CREATEPLAYER      (8):  same shape as ADDFORWARDREQUEST
 *   PING (22) / PINGREPLY (23): DWORD idFrom, DWORD tickCount
 *   player message: SP header, then DWORD idFrom, DWORD idTo, payload (no "play" signature)
 *
 * Reference capture (real Deadlock II host, Windows 11 dplay v14), ENUMSESSIONSREPLY, 134 bytes over TCP:
 *   8600b0fa 0200 08fc 00000000 00..00 | 'play' 0100 0e00 | DPSESSIONDESC2(80) | 5c000000 | "Online War\0" UTF-16
 */
const dgram = require('dgram');
const net = require('net');
const crypto = require('crypto');

// {9F39C820-8CFE-11D1-904F-00A0C9363012}, Deadlock II's application GUID, in memory byte order.
const DEADLOCK2_APP_GUID = Buffer.from('20c8399ffe8cd111904f00a0c9363012', 'hex');
const GUID_NULL = Buffer.alloc(16);
const DPLAY_PORT = 47624;
const TOKEN_REQUEST = 0xFAB;
const CMD = { ENUMSESSIONSREPLY: 1, ENUMSESSIONS: 2, ENUMPLAYERSREPLY: 3, REQUESTPLAYERID: 5, REQUESTPLAYERREPLY: 7, CREATEPLAYER: 8,
  DELETEPLAYER: 11, ADDFORWARDREQUEST: 19, PING: 22, PINGREPLY: 23, SESSIONDESCCHANGED: 26, SUPERENUMPLAYERSREPLY: 41 };
const CMD_NAME = Object.fromEntries(Object.entries(CMD).map(([k, v]) => [v, k]));
const DPSESSION_KEEPALIVE = 0x40;
const DPSESSION_MIGRATEHOST = 0x04;
const DPLAYI_PLAYER_SYSPLAYER = 0x01;
const DPLAYI_PLAYER_NAMESRVR = 0x02;
const HOST_PLAYER_NAME = 'Deadlock 2 Host';

function ipBytes(ip) { const b = Buffer.alloc(4); String(ip || '0.0.0.0').replace(/^::ffff:/, '').split('.').forEach((x, i) => { b[i] = Number(x) & 255; }); return b; }
function sockaddr(port, ip) { const b = Buffer.alloc(16); b.writeUInt16LE(2, 0); b.writeUInt16BE(port & 0xffff, 2); ipBytes(ip).copy(b, 4); return b; }
function spHeader(size, port, ip) { const b = Buffer.alloc(20); b.writeUInt32LE(((size & 0xfffff) | (TOKEN_REQUEST << 20)) >>> 0, 0); sockaddr(port, ip).copy(b, 4); return b; }
function coreHeader(cmd, version) { const b = Buffer.alloc(8); b.write('play', 0, 'latin1'); b.writeUInt16LE(cmd, 4); b.writeUInt16LE(version, 6); return b; }
function wstr(s) { return Buffer.from(String(s) + '\0', 'utf16le'); }
function pad4(n) { return (n + 3) & ~3; }

// Parse the SP header + core header of one message (buf starts at the SP header).
function parseMessage(buf) {
  if (buf.length < 20) return null;
  const sizeToken = buf.readUInt32LE(0);
  const m = { size: sizeToken & 0xfffff, token: sizeToken >>> 20, port: buf.readUInt16BE(6), ip: [...buf.subarray(8, 12)].join('.'), system: false };
  if (buf.length >= 28 && buf.toString('latin1', 20, 24) === 'play') {
    m.system = true; m.cmd = buf.readUInt16LE(24); m.version = buf.readUInt16LE(26); m.body = buf.subarray(28, m.size || buf.length); m.core = buf.subarray(20, m.size || buf.length);
  } else if (buf.length >= 28) {
    m.idFrom = buf.readUInt32LE(20); m.idTo = buf.readUInt32LE(24); m.payload = buf.subarray(28, m.size || buf.length);
  }
  return m;
}

// DPLAYI_PACKEDPLAYER as used in ENUMPLAYERSREPLY / ADDFORWARDREQUEST / CREATEPLAYER.
// Deadlock II's CGNet wrapper fills DPNAME.lpszLongName (not the short name) when it creates a
// player and reads names back the same way, so the host player's name travels as LongName.
function packPlayer({ id, flags, shortName, longName, spData, sysPlayerId, version, playerData }) {
  const sn = shortName ? wstr(shortName) : Buffer.alloc(0);
  const ln = longName ? wstr(longName) : Buffer.alloc(0);
  const sp = spData || Buffer.alloc(0); const pd = playerData || Buffer.alloc(0);
  const fixed = 48; const size = fixed + sn.length + ln.length + sp.length + pd.length;
  const b = Buffer.alloc(size);
  [size, flags, id, sn.length, ln.length, sp.length, pd.length, 0, sysPlayerId, fixed, version, 0].forEach((v, i) => b.writeUInt32LE(v >>> 0, i * 4));
  let o = fixed; sn.copy(b, o); o += sn.length; ln.copy(b, o); o += ln.length; sp.copy(b, o); o += sp.length; pd.copy(b, o);
  return b;
}
function unpackPlayer(b, off) {
  if (b.length < off + 48) return null;
  const f = []; for (let i = 0; i < 12; i++) f.push(b.readUInt32LE(off + i * 4));
  const [size, flags, id, snLen, lnLen, spLen, pdLen, nPlayers, sysId, fixed, version] = f;
  let o = off + (fixed || 48);
  const shortName = snLen ? b.subarray(o, o + snLen).toString('utf16le').replace(/\0+$/, '') : ''; o += snLen;
  const longName = lnLen ? b.subarray(o, o + lnLen).toString('utf16le').replace(/\0+$/, '') : ''; o += lnLen;
  const spData = b.subarray(o, o + spLen); o += spLen;
  return { size, flags, id, shortName, longName, name: longName || shortName, spData, nPlayers, sysId, version, pdLen };
}
function spDataFor(ip, streamPort, dgramPort) { return Buffer.concat([sockaddr(streamPort, ip), sockaddr(dgramPort, ip)]); }
// A real host fills the joiner's IP into both sockaddrs of its SP data before republishing it.
function withIp(spData, ip) { const b = Buffer.from(spData && spData.length >= 32 ? spData : Buffer.alloc(32)); ipBytes(ip).copy(b, 4); ipBytes(ip).copy(b, 20); return b; }
// DPLAYI_SUPERPACKEDPLAYER (as seen on the wire from dplay v14): fixed 5 DWORDs, optional names, 1-byte SP data length.
function superPack({ id, flags, versionOrSys, shortName, longName, spData }) {
  let mask = 0x04;                                  // SP data length encoded in 1 byte
  if (shortName) mask |= 0x01;
  if (longName) mask |= 0x02;
  const fixed = Buffer.alloc(20);
  [16, flags, id, mask, versionOrSys].forEach((v, i) => fixed.writeUInt32LE(v >>> 0, i * 4));
  const sp = spData || Buffer.alloc(32);
  return Buffer.concat([fixed, shortName ? wstr(shortName) : Buffer.alloc(0), longName ? wstr(longName) : Buffer.alloc(0), Buffer.from([sp.length & 0xff]), sp]);
}
function describeSpData(sp) { if (!sp || sp.length < 32) return ''; return `stream ${[...sp.subarray(4, 8)].join('.')}:${sp.readUInt16BE(2)} dgram :${sp.readUInt16BE(18)}`; }

class FakeDirectPlayHost {
  constructor({ log = () => {}, onEvent = () => {}, tcpPort = 2300, appGuid = DEADLOCK2_APP_GUID } = {}) {
    this.log = log; this.onEvent = onEvent; this.tcpBase = tcpPort; this.appGuid = appGuid;
    this.sessions = []; this.sock = null; this.servers = []; this.requests = 0; this.replies = 0; this.joins = [];
    this.nextId = 0x00010010;
  }

  get running() { return !!this.sock; }

  async start() {
    if (this.sock) return false;
    await new Promise((resolve, reject) => {
      const sock = dgram.createSocket({ type: 'udp4', reuseAddr: true });
      sock.on('error', (e) => { this.log('dpenum socket error:', e.message); if (this.sock === sock) this.sock = null; reject(e); });
      sock.on('message', (msg, rinfo) => this.onDatagram(msg, rinfo));
      sock.bind(DPLAY_PORT, '0.0.0.0', () => { this.sock = sock; resolve(); });
    });
    await this.listenSessions();
    this.log(`fake DirectPlay host listening on UDP ${DPLAY_PORT}, TCP ${this.sessions.map((s) => s.port).join(',') || '(no sessions)'}`);
    return true;
  }

  stop() {
    if (this.sock) { try { this.sock.close(); } catch (_) { /* ignore */ } this.sock = null; }
    for (const srv of this.servers) { try { srv.close(); } catch (_) { /* ignore */ } }
    this.servers = [];
  }

  setSessions(list) {
    this.sessions = list.map((s, i) => ({
      name: String(s.name || 'Deadlock 2 Game').slice(0, 31),
      max: Math.min(7, Math.max(2, s.max || 7)),
      current: Math.max(1, Math.min((s.max || 7) - 1, s.current || 1)),
      instance: s.instance || crypto.randomBytes(16),
      flags: s.flags == null ? (DPSESSION_KEEPALIVE | DPSESSION_MIGRATEHOST) : s.flags,
      port: this.tcpBase + i,
      hostSysId: 0x00010001 + i * 0x100, hostAppId: 0x00010002 + i * 0x100,
      players: [],   // joined clients: {sysId, appId, name, ip, port, joinedAt, hello}
    }));
    if (this.sock) this.listenSessions().catch((e) => this.log('dpenum listen:', e.message));
  }

  async listenSessions() {
    for (const srv of this.servers) { try { srv.close(); } catch (_) { /* ignore */ } }
    this.servers = [];
    for (const s of this.sessions) {
      await new Promise((resolve) => {
        const srv = net.createServer((c) => this.onConnection(c, s));
        srv.on('error', (e) => { this.log(`dpenum: cannot listen on TCP ${s.port} (${e.code}); session "${s.name}" is not joinable`); resolve(); });
        srv.listen(s.port, '0.0.0.0', () => { this.servers.push(srv); resolve(); });
      });
    }
  }

  // ---- transport -------------------------------------------------------------------------
  sendTo(ip, port, payload, what) {
    const sock = net.connect({ host: ip, port }, () => { sock.write(payload, () => setTimeout(() => sock.destroy(), 1500)); });
    sock.setTimeout(4000, () => sock.destroy());
    sock.on('error', (e) => this.log(`dpenum: ${what} to ${ip}:${port} failed: ${e.code || e.message}`));
  }

  onConnection(conn, session) {
    let buf = Buffer.alloc(0);
    conn.on('data', (d) => {
      buf = Buffer.concat([buf, d]);
      while (buf.length >= 4) {
        const size = buf.readUInt32LE(0) & 0xfffff;
        if (size < 20 || size > 65536) { this.log(`dpenum: bad frame size ${size} on TCP ${session.port}, dropping connection`); conn.destroy(); return; }
        if (buf.length < size) break;
        const one = buf.subarray(0, size); buf = buf.subarray(size);
        try { this.handle(parseMessage(one), one, { address: conn.remoteAddress, port: conn.remotePort, local: conn.localAddress }, session, 'tcp'); }
        catch (e) { this.log('dpenum: handler error', e.message); }
      }
    });
    conn.on('error', () => {});
  }

  onDatagram(msg, rinfo) {
    const m = parseMessage(msg);
    if (!m || !m.system) return;
    if (m.cmd === CMD.ENUMSESSIONS) return this.handleEnum(m, msg, rinfo);
    this.handle(m, msg, { address: rinfo.address, port: rinfo.port, local: this.sock.address().address }, null, 'udp');
  }

  replyAddress(m, remote) { return { ip: m.ip !== '0.0.0.0' ? m.ip : remote.address.replace(/^::ffff:/, ''), port: m.port || remote.port }; }

  // ---- enumeration -----------------------------------------------------------------------
  handleEnum(m, msg, rinfo) {
    if (msg.length < 52) return;
    const guid = msg.subarray(28, 44);
    if (!guid.equals(this.appGuid) && !guid.equals(GUID_NULL)) return;
    this.requests++;
    const { ip, port } = this.replyAddress(m, rinfo);
    const payload = Buffer.concat(this.sessions.map((s) => this.buildEnumReply(m.version, s)));
    this.sendTo(ip, port, payload, 'ENUMSESSIONSREPLY');
    this.replies += this.sessions.length;
    this.log(`dpenum: ENUMSESSIONS from ${rinfo.address}:${rinfo.port} (v${m.version}) -> ${this.sessions.length} session(s) via TCP to ${ip}:${port}`);
  }

  sessionDesc(s) {
    const b = Buffer.alloc(80);
    b.writeUInt32LE(80, 0); b.writeUInt32LE(s.flags, 4); s.instance.copy(b, 8); this.appGuid.copy(b, 24);
    b.writeUInt32LE(s.max, 40); b.writeUInt32LE(s.current + s.players.length, 44);
    b.writeUInt32LE(s.hostSysId >>> 0, 56);   // dwReserved1 = host's system player ID; joiners echo it as the last DWORD of ADDFORWARDREQUEST
    return b;
  }

  buildEnumReply(version, s) {
    const name = wstr(s.name);
    const size = 20 + 8 + 80 + 4 + name.length;
    const off = Buffer.alloc(4); off.writeUInt32LE(8 + 80 + 4, 0);
    return Buffer.concat([spHeader(size, s.port, '0.0.0.0'), coreHeader(CMD.ENUMSESSIONSREPLY, version), this.sessionDesc(s), off, name]);
  }

  // ---- join handshake --------------------------------------------------------------------
  handle(m, raw, remote, session, via) {
    if (!m) return;
    const who = `${remote.address}:${remote.port}`;
    if (!m.system) {
      const p = session && session.players.find((x) => x.sysId === m.idFrom || x.appId === m.idFrom);
      this.log(`dpenum: player message ${m.idFrom.toString(16)} -> ${m.idTo.toString(16)}, ${m.payload.length} bytes, from ${who}: ${m.payload.subarray(0, 24).toString('hex')}`);
      if (session && p && !p.hello) { p.hello = m.payload.subarray(0, 92).toString('hex'); this.onEvent(`${p.name || 'a player'} joined "${session.name}" and sent its ${m.payload.length}-byte hello capsule (type 0x${m.payload[8] != null ? m.payload[8].toString(16) : '?'}). The fake host cannot send game data, so the game will wait there.`); }
      return;
    }
    const name = CMD_NAME[m.cmd] || `cmd ${m.cmd}`;
    const { ip, port } = this.replyAddress(m, remote);
    switch (m.cmd) {
      case CMD.ENUMSESSIONS: return this.handleEnum(m, raw, remote);
      case CMD.REQUESTPLAYERID: {
        const flags = m.body.length >= 4 ? m.body.readUInt32LE(0) : 0;
        const id = this.nextId++;
        const body = Buffer.alloc(4 + 24 + 12); body.writeUInt32LE(id, 0); body.writeUInt32LE(24, 4);   // id, secdesc.dwSize; rest 0, result DP_OK
        const reply = Buffer.concat([spHeader(20 + 8 + body.length, session ? session.port : this.tcpBase, '0.0.0.0'), coreHeader(CMD.REQUESTPLAYERREPLY, m.version), body]);
        this.sendTo(ip, port, reply, 'REQUESTPLAYERREPLY');
        if (session) session.pendingIds = (session.pendingIds || []).concat([{ id, flags, ip, port }]);
        this.log(`dpenum: ${name} (flags ${flags}) from ${who} via ${via} -> id 0x${id.toString(16)} to ${ip}:${port}`);
        return;
      }
      case CMD.ADDFORWARDREQUEST:
      case CMD.CREATEPLAYER: {
        if (!session) return this.log(`dpenum: ${name} without session from ${who}`);
        const idTo = m.body.readUInt32LE(0), playerId = m.body.readUInt32LE(4), createOff = m.body.readUInt32LE(12);
        const pp = unpackPlayer(m.core, createOff);
        this.log(`dpenum: ${name} from ${who}: idTo 0x${idTo.toString(16)} player 0x${playerId.toString(16)} flags ${pp ? pp.flags : '?'} short "${pp ? pp.shortName : '?'}" long "${pp ? pp.longName : '?'}" ${pp ? describeSpData(pp.spData) : ''} raw ${raw.subarray(0, 200).toString('hex')}`);
        if (m.cmd === CMD.ADDFORWARDREQUEST) {
          const joiner = { sysId: playerId, appId: null, name: null, ip, port, joinedAt: Date.now(), spData: pp ? pp.spData : spDataFor(ip, port, port), hello: null };
          session.players.push(joiner);
          this.joins.push({ session: session.name, ip, at: joiner.joinedAt });
          this.sendTo(ip, port, this.buildEnumPlayersReply(m.version, session, ip), 'ENUMPLAYERSREPLY');
          this.onEvent(`Deadlock 2 at ${ip} is joining fake game "${session.name}" (system player 0x${playerId.toString(16)}).`);
        } else {
          const joiner = session.players.find((x) => x.ip === ip && x.appId === null) || session.players[session.players.length - 1];
          if (joiner) { joiner.appId = playerId; joiner.name = pp ? pp.name : 'player'; }
          this.onEvent(`Player "${pp ? pp.name : '?'}" created in fake game "${session.name}".`);
        }
        return;
      }
      case CMD.PING: {
        const reply = Buffer.concat([spHeader(20 + 8 + m.body.length, session ? session.port : this.tcpBase, '0.0.0.0'), coreHeader(CMD.PINGREPLY, m.version), m.body]);
        this.sendTo(ip, port, reply, 'PINGREPLY');
        return;
      }
      case CMD.DELETEPLAYER: {
        if (session) { const pid = m.body.length >= 8 ? m.body.readUInt32LE(4) : 0; const i = session.players.findIndex((x) => x.sysId === pid || x.appId === pid); if (i >= 0) { const [p] = session.players.splice(i, 1); this.onEvent(`${p.name || 'a player'} left fake game "${session.name}".`); } }
        this.log(`dpenum: ${name} from ${who}`);
        return;
      }
      default:
        this.log(`dpenum: unhandled ${name} v${m.version} from ${who} via ${via}, ${raw.length} bytes: ${raw.subarray(0, 96).toString('hex')}`);
    }
  }

  // SUPERENUMPLAYERSREPLY, byte layout copied from a real Deadlock II host (dplay v14) answering a join:
  //   playerCount, groupCount, packedOffset, shortcutCount, descOffset(36), nameOffset(116), passwordOffset(0),
  //   DPSESSIONDESC2, sessionName (no padding), then DPLAYI_SUPERPACKEDPLAYER records:
  //   DWORD 16, flags, id, infoMask, versionOrSystemPlayerId, [longName if mask&2], BYTE spLen(32), spData.
  //   Host system player: flags 0x0f, versionOrSys = 14.  Host app player: flags 0x0c, versionOrSys = host sys id,
  //   long name "Deadlock 2 Host".  Joiner's system player: flags 0x05 with its IP filled in.
  buildEnumPlayersReply(version, s, clientIp) {
    const hostSp = spDataFor('0.0.0.0', s.port, 2350);
    const players = [
      superPack({ id: s.hostSysId, flags: 0x0f, versionOrSys: version, spData: hostSp }),
      superPack({ id: s.hostAppId, flags: 0x0c, versionOrSys: s.hostSysId, longName: HOST_PLAYER_NAME, spData: hostSp }),
    ];
    for (const p of s.players) {
      players.push(superPack({ id: p.sysId, flags: 0x05, versionOrSys: version, spData: withIp(p.spData, p.ip) }));
      if (p.appId) players.push(superPack({ id: p.appId, flags: 0x04, versionOrSys: p.sysId, longName: p.name || 'Deadlock 2 Player', spData: withIp(p.spData, p.ip) }));
    }
    const packed = Buffer.concat(players);
    const name = wstr(s.name);
    const descOffset = 8 + 7 * 4; const nameOffset = descOffset + 80; const packedOffset = nameOffset + name.length;
    const head = Buffer.alloc(7 * 4);
    [players.length, 0, packedOffset, 0, descOffset, nameOffset, 0].forEach((v, i) => head.writeUInt32LE(v >>> 0, i * 4));
    const core = Buffer.concat([coreHeader(CMD.SUPERENUMPLAYERSREPLY, version), head, this.sessionDesc(s), name, packed]);
    return Buffer.concat([spHeader(20 + core.length, s.port, '0.0.0.0'), core]);
  }

  hostIpFor(clientIp) {
    const ip = String(clientIp || '').replace(/^::ffff:/, '');
    if (ip.startsWith('127.')) return '127.0.0.1';
    try { for (const list of Object.values(require('os').networkInterfaces())) for (const i of list) if (i.family === 'IPv4' && !i.internal) return i.address; } catch (_) { /* ignore */ }
    return '127.0.0.1';
  }

  status() { return { running: this.running, requests: this.requests, replies: this.replies, sessions: this.sessions.map((s) => ({ name: s.name, port: s.port, current: s.current, max: s.max, joined: s.players.map((p) => ({ name: p.name, ip: p.ip, hello: !!p.hello })) })) }; }
}

// Build an ENUMSESSIONS request the way a client would (used by the self-test).
function buildEnumRequest({ version = 0x000e, returnPort = 0, appGuid = DEADLOCK2_APP_GUID, flags = 0x91 } = {}) {
  const size = 20 + 8 + 16 + 4 + 4 + 2;
  const b = Buffer.alloc(size);
  spHeader(size, returnPort, '0.0.0.0').copy(b, 0);
  b.write('play', 20, 'latin1'); b.writeUInt16LE(CMD.ENUMSESSIONS, 24); b.writeUInt16LE(version, 26);
  appGuid.copy(b, 28); b.writeUInt32LE(8 + 16 + 4 + 4, 44); b.writeUInt32LE(flags, 48);
  return b;
}

function parseReply(b) {
  if (b.length < 112 || b.toString('latin1', 20, 24) !== 'play' || b.readUInt16LE(24) !== CMD.ENUMSESSIONSREPLY) return null;
  const nameOff = b.readUInt32LE(20 + 8 + 80);
  const nameBuf = b.subarray(20 + nameOff);
  const end = nameBuf.indexOf('\0\0', 0, 'latin1');
  return { version: b.readUInt16LE(26), tcpPort: b.readUInt16BE(6), flags: b.readUInt32LE(32), max: b.readUInt32LE(68), current: b.readUInt32LE(72),
    instance: b.subarray(36, 52).toString('hex'), name: nameBuf.subarray(0, end < 0 ? nameBuf.length : end + (end % 2)).toString('utf16le') };
}

module.exports = { FakeDirectPlayHost, buildEnumRequest, parseReply, parseMessage, packPlayer, unpackPlayer, DEADLOCK2_APP_GUID, DPLAY_PORT, CMD };
