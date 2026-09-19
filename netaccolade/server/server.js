'use strict';
/*
 * NetAccolade replacement lobby for Deadlock II: Shrine Wars.
 *
 * Node.js, no dependencies.  Plays the role the NetAccolade lobby (MClient.exe) played:
 *   - the game's "NetAccolade" button spawns MClient.exe -gi 1 -o deadlock.ini in the game dir;
 *     our MClient.exe runs agent.js, which registers here and opens the lobby page in a browser;
 *   - players chat, create/join a game, press Ready; the host presses Launch;
 *   - the server builds one deadlock.ini per participant (Role=master for the host, Role=slave +
 *     Master Address for everyone else) and hands it to each player's agent, which writes the file
 *     into the game directory and starts DEADLOCK.EXE -ms, exactly like the original lobby did.
 *
 * Start:  node server.js            (listens on http://127.0.0.1:7624)
 * Env:    NETACC_PORT, NETACC_HOST (use 0.0.0.0 to accept remote players)
 */
const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const { FakeDirectPlayHost } = require('./dpenum');

// Persistent settings (server.json next to this file): { "port", "host", "fakeGames" }.
const CONFIG_PATH = path.join(__dirname, 'server.json');
const CONFIG = (() => { try { return JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8')); } catch (_) { return {}; } })();
function saveConfig(patch) {
  Object.assign(CONFIG, patch);
  try { fs.writeFileSync(CONFIG_PATH, JSON.stringify(CONFIG, null, 2) + '\n'); } catch (e) { console.error('cannot write server.json:', e.message); }
}

const PORT = Number(process.env.NETACC_PORT) || Number(CONFIG.port) || 7624;
const HOST = process.env.NETACC_HOST || CONFIG.host || '127.0.0.1';
const VERSION = '0.1.0';
const PUBLIC_DIR = path.join(__dirname, 'public');
const GAME_EXE = 'DEADLOCK.EXE';
const GAME_ARGS = ['-ms'];

// ---------------------------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------------------------
const agents = new Map();   // token -> agent (one per running MClient.exe / agent.js)
const players = new Map();  // id -> player (one per browser session)
const games = new Map();    // key -> game
const chat = [];            // {ts, from, text, channel}
const sseClients = new Set();
const log = (...a) => console.log(new Date().toISOString(), ...a);

function id() { return crypto.randomBytes(8).toString('hex'); }
function now() { return Date.now(); }
function clip(s, n) { return String(s || '').replace(/[^\x20-\x7e]/g, '').replace(/[;=\r\n]/g, ' ').trim().slice(0, n); }
function isLoopback(a) { return !a || a === '::1' || a === '127.0.0.1' || a === '::ffff:127.0.0.1'; }
function plainIp(a) { return String(a || '').replace(/^::ffff:/, ''); }

// ---------------------------------------------------------------------------------------------
// deadlock.ini generation (format documented in ../../NetAccolade-Investigation.md)
// ---------------------------------------------------------------------------------------------
const VICTORY = ['manifest_destiny', 'conquest', 'shrine_wars'];
const WIN_CITIES = [2, 3, 5, 7, 10];
const WIN_SHRINES = [2, 3, 5];
const WIN_TURNS = [3, 5, 8];
const ABILITIES = ['standard_ability', 'best_ability', 'no_ability'];
const WORLD_TYPES = ['small', 'medium', 'large', 'huge', 'custom'];
const COLORS = ['earthlike', 'tropical', 'mars', 'icy', 'dry', 'volcanic', 'alien'];
const PERCENT_KEYS = ['oceans', 'plains', 'forests', 'swamps', 'mountains', 'wastelands'];

const DEFAULT_OPTIONS = {
  fileType: 'new_game', saveFile: '',
  players: 2, victory: 'conquest', winCities: 5, winShrines: 3, winTurns: 5, aiSkill: 0,
  randomEvents: true, allowAlliances: true, fastProduction: false, worldResources: true,
  lastPlayerTimer: false, lastPlayerClock: 60, autoTimer: false, autoTimerClock: 60,
  racialAbilities: 'standard_ability',
  worldType: 'small', mapFile: '', size: 30, color: 'earthlike',
  percents: { oceans: 30, plains: 30, forests: 20, swamps: 10, mountains: 5, wastelands: 5 },
};

function pick(list, v, name) {
  if (!list.includes(v)) throw new Error(`${name} must be one of ${list.join(', ')}`);
  return v;
}
function int(v, lo, hi, name) {
  const n = Number(v);
  if (!Number.isInteger(n) || n < lo || n > hi) throw new Error(`${name} must be an integer ${lo}..${hi}`);
  return n;
}
function bool(v) { return v === true || v === 'true' || v === 1 || v === '1' || v === 'on'; }

function normalizeOptions(raw) {
  const r = Object.assign({}, DEFAULT_OPTIONS, raw || {});
  const o = {};
  o.fileType = pick(['new_game', 'saved_game'], r.fileType, 'fileType');
  o.saveFile = clip(r.saveFile, 127);
  if (o.fileType === 'saved_game' && !o.saveFile) throw new Error('saveFile is required for saved_game');
  o.players = int(r.players, 2, 7, 'players');
  o.victory = pick(VICTORY, r.victory, 'victory');
  o.winCities = int(r.winCities, 2, 10, 'winCities'); pick(WIN_CITIES, o.winCities, 'winCities');
  o.winShrines = int(r.winShrines, 2, 5, 'winShrines'); pick(WIN_SHRINES, o.winShrines, 'winShrines');
  o.winTurns = int(r.winTurns, 3, 8, 'winTurns'); pick(WIN_TURNS, o.winTurns, 'winTurns');
  o.aiSkill = int(r.aiSkill, -2, 3, 'aiSkill');
  for (const k of ['randomEvents', 'allowAlliances', 'fastProduction', 'worldResources', 'lastPlayerTimer', 'autoTimer']) o[k] = bool(r[k]);
  o.lastPlayerClock = int(r.lastPlayerClock, 3, 960, 'lastPlayerClock');
  o.autoTimerClock = int(r.autoTimerClock, 3, 960, 'autoTimerClock');
  o.racialAbilities = pick(ABILITIES, r.racialAbilities, 'racialAbilities');
  o.worldType = pick(WORLD_TYPES, r.worldType, 'worldType');
  o.mapFile = clip(r.mapFile, 200);
  if (o.worldType === 'custom') {
    o.size = int(r.size, 20, 40, 'size');
    o.color = pick(COLORS, r.color, 'color');
    o.percents = {};
    let sum = 0;
    for (const k of PERCENT_KEYS) {
      const v = int((r.percents || {})[k], 0, k === 'oceans' ? 75 : 100, `percents.${k}`);
      o.percents[k] = v; sum += v;
    }
    if (sum !== 100) throw new Error('the six terrain percentages must add up to 100');
  } else {
    o.size = 30; o.color = 'earthlike'; o.percents = Object.assign({}, DEFAULT_OPTIONS.percents);
    o.mapFile = '';
  }
  return o;
}

function buildIni({ game, role, masterAddress, userName, humans }) {
  const o = game.options;
  const L = [];
  const fileType = o.mapFile ? 'map_file' : o.fileType;
  L.push('[Meta]', `File Type=${fileType}`);
  if (fileType !== 'new_game') L.push('Version=288');
  L.push(`Name=${clip(game.name, 31)}`, '');
  if (fileType !== 'saved_game') {
    L.push('[Scenario Options]',
      `Players=${Math.max(o.players, humans)}`,
      `Victory Condition=${o.victory}`,
      `Win Cities=${o.winCities}`, `Win Shrines=${o.winShrines}`, `Win Turns=${o.winTurns}`,
      `AI Skill Level=${o.aiSkill}`,
      `Random Events=${o.randomEvents}`, `Allow Alliances=${o.allowAlliances}`,
      `Fast Production=${o.fastProduction}`, `World Resources=${o.worldResources}`,
      `Last Player Timer=${o.lastPlayerTimer}`, `Last Player Clock=${o.lastPlayerClock}`,
      `Auto Timer=${o.autoTimer}`, `Auto Timer Clock=${o.autoTimerClock}`,
      `Racial Abilities=${o.racialAbilities}`, '');
    L.push('[World]', `Type=${o.worldType}`);
    if (o.worldType === 'custom') {
      L.push(`Map File=${o.mapFile}`, `Size=${o.size}`, `Color=${o.color}`);
      for (const k of PERCENT_KEYS) L.push(`Percent ${k[0].toUpperCase()}${k.slice(1)}=${o.percents[k]}`);
    }
    L.push('');
  }
  L.push('[Startup]', `Role=${role === 'master' ? 'master' : 'slave'}`);
  if (fileType === 'saved_game') L.push(`Save File=${o.saveFile}`);
  if (role !== 'master') L.push(`Master Address=${masterAddress}`);
  L.push(`User Name=${clip(userName, 31) || 'Player'}`, `Players=${humans}`, '');
  return L.join('\r\n');
}

// ---------------------------------------------------------------------------------------------
// Agents (MClient.exe / agent.js instances)
// ---------------------------------------------------------------------------------------------
function agentOnline(a) { return !!a && (a.fake || a.waiters.length > 0 || now() - a.lastSeen < 40000); }

// ---------------------------------------------------------------------------------------------
// Fake clients for testing alone:  type  /fakeclient 3  in the lobby chat.
// Each bot is a player with a simulated (always online) agent.  Bots join the game of the person
// who summoned them (or the next game that person creates/joins) and press Ready.  On Launch they
// do not start a game; they report the deadlock.ini they were given in the launch chat.
// ---------------------------------------------------------------------------------------------
const BOT_RACES = ['ChCh-t', 'Cyth', 'Human', 'Maug', 'Re\'Lu', 'Tarth', 'Uva Mosk', 'Skirineen'];
const BOT_ADJ = ['Rusty', 'Sneaky', 'Grumpy', 'Lucky', 'Hasty', 'Quiet', 'Brave', 'Odd'];

function createFakeClients(n, owner) {
  const made = [];
  for (let i = 0; i < n; i++) {
    const name = clip(`${BOT_ADJ[Math.floor(Math.random() * BOT_ADJ.length)]} ${BOT_RACES[Math.floor(Math.random() * BOT_RACES.length)]} ${Math.floor(Math.random() * 90) + 10}`, 31);
    const a = { token: id(), fake: true, gameDir: `C:\\FakeClients\\${name.replace(/[^A-Za-z0-9]/g, '')}`, outFile: 'deadlock.ini', gameId: '1',
      ips: [`10.99.${Math.floor(Math.random() * 250) + 1}.${Math.floor(Math.random() * 250) + 1}`], remoteAddress: `10.99.${i + 1}.${Math.floor(Math.random() * 250) + 1}`,
      createdAt: now(), lastSeen: now(), pending: null, waiters: [], playerId: null };
    a.remoteAddress = a.ips[0];
    agents.set(a.token, a);
    const p = { id: id(), name, gameKey: null, ready: false, online: true, sse: 0, agentToken: a.token, lastSeen: now(), fake: true, ownerId: owner.id };
    a.playerId = p.id;
    players.set(p.id, p);
    made.push(p);
  }
  addChat('system', `${made.length} fake client(s) connected: ${made.map((p) => p.name).join(', ')}`, 'lobby');
  const g = playerGame(owner);
  if (g) attachFakes(g, owner);
  return made;
}

function idleFakes(owner) { return [...players.values()].filter((p) => p.fake && !p.gameKey && (!owner || p.ownerId === owner.id)); }

function attachFakes(g, owner) {
  if (!g || g.launched) return;
  for (const p of idleFakes(owner)) {
    if (participants(g).length >= 7) break;
    p.gameKey = g.key; p.ready = true;
    addChat(p.name, ['ready when you are', 'let\'s go', 'my shrines are waiting', 'ready'][Math.floor(Math.random() * 4)], g.key);
  }
}

function clearFakeClients() {
  let n = 0;
  for (const p of [...players.values()]) {
    if (!p.fake) continue;
    n++; p.gameKey = null; players.delete(p.id);
    if (p.agentToken) agents.delete(p.agentToken);
  }
  addChat('system', `${n} fake client(s) disconnected`, 'lobby');
  return n;
}

// ---------------------------------------------------------------------------------------------
// Fake DirectPlay sessions:  /fakegames 3  makes three Deadlock II sessions appear in the game's
// "Join a Network Game" list when it enumerates 127.0.0.1 (or this PC's LAN address).  They cannot
// be joined; they exist so the TCP/IP connect path can be tested on one machine.
// ---------------------------------------------------------------------------------------------
const fakeHost = new FakeDirectPlayHost({ log, onEvent: (text) => { addChat('system', `[fake host] ${text}`, 'lobby'); broadcastState(); } });
const fakeGames = [];
const FAKE_GAME_NAMES = ['Shrine Rush', 'Gallius IV Revisited', 'Tarth Uprising', 'Cyth Expansion', 'Maug Foundry War', 'Humans vs Everyone', 'ChCh-t Swarm', 'Re\'Lu Mind Games', 'Uva Mosk Groves', 'Skirineen Bazaar'];

async function setFakeGames(n, channel, persist) {
  if (persist) saveConfig({ fakeGames: n });
  fakeGames.length = 0;
  for (let i = 0; i < n; i++) {
    const max = [4, 5, 6, 7][Math.floor(Math.random() * 4)];
    fakeGames.push({ name: `${FAKE_GAME_NAMES[i % FAKE_GAME_NAMES.length]}${i >= FAKE_GAME_NAMES.length ? ' ' + (i + 1) : ''}`, max, current: 1 + Math.floor(Math.random() * (max - 1)) });
  }
  fakeHost.setSessions(fakeGames);
  if (n > 0 && !fakeHost.running) {
    try { await fakeHost.start(); }
    catch (e) {
      addChat('system', `Cannot open UDP ${require('./dpenum').DPLAY_PORT} for fake sessions (${e.code || e.message}). A real DirectPlay host (or dplaysvr.exe) is using it; close it and try again.`, channel);
      return;
    }
  }
  if (n === 0) { fakeHost.stop(); addChat('system', 'Fake DirectPlay sessions removed.', channel); return; }
  addChat('system', `${n} fake Deadlock 2 session(s) are now advertised on this PC: ${fakeGames.map((g) => `"${g.name}" (${g.current}/${g.max})`).join(', ')}. In the game, Connect to IP 127.0.0.1 (or ${serverIps()[0] || 'this PC'}) and they appear in the join list. They cannot be joined.`, channel);
}

function handleChatCommand(pl, text) {
  const m = text.match(/^\/(\w+)\s*(.*)$/);
  if (!m) return false;
  const cmd = m[1].toLowerCase(); const arg = m[2].trim();
  if (cmd === 'fakeclient' || cmd === 'fakeclients' || cmd === 'bots') {
    if (/^(clear|0|none|off)$/i.test(arg)) { clearFakeClients(); }
    else {
      const n = Math.min(6, Math.max(1, parseInt(arg || '1', 10) || 1));
      createFakeClients(n, pl);
    }
  } else if (cmd === 'fakegames' || cmd === 'fakegame' || cmd === 'fakesessions') {
    const n = /^(clear|0|none|off)$/i.test(arg) ? 0 : Math.min(10, Math.max(1, parseInt(arg || '3', 10) || 3));
    setFakeGames(n, 'lobby', true).then(broadcastState);
  } else if (cmd === 'help') {
    addChat('system', 'Commands: /fakeclient N (1-6) connects N test players that join your game and press Ready, /fakeclient clear removes them; /fakegames N (1-10) advertises N fake Deadlock 2 sessions on this PC for the game\'s join list (Connect to IP 127.0.0.1), /fakegames clear stops that.', 'lobby');
  } else {
    addChat('system', `Unknown command /${cmd}. Try /help.`, 'lobby');
  }
  broadcastState();
  return true;
}

function registerAgent(body, req) {
  const a = {
    token: id(),
    gameDir: String(body.gameDir || ''),
    outFile: clip(body.outFile || 'deadlock.ini', 64) || 'deadlock.ini',
    gameId: String(body.gameId || '1'),
    ips: Array.isArray(body.ips) ? body.ips.map(String).slice(0, 8) : [],
    remoteAddress: plainIp(req.socket.remoteAddress),
    createdAt: now(), lastSeen: now(),
    pending: null, waiters: [], playerId: null,
  };
  agents.set(a.token, a);
  log('agent registered', a.token, a.gameDir, 'from', a.remoteAddress);
  return a;
}

function agentWait(a, res) {
  a.lastSeen = now();
  if (a.pending) { const cmd = a.pending; a.pending = null; return json(res, 200, cmd); }
  a.waiters.push(res);
  const timer = setTimeout(() => { drop(); if (!res.writableEnded) json(res, 200, { command: 'none' }); }, 25000);
  const drop = () => { clearTimeout(timer); a.waiters = a.waiters.filter((r) => r !== res); a.lastSeen = now(); };
  res.on('close', drop);
  broadcastState();
}

function deliver(a, cmd) {
  a.lastSeen = now();
  if (a.fake) {
    const pl = players.get(a.playerId);
    const role = /Role=master/.test(cmd.content) ? 'master' : 'slave';
    const addr = (cmd.content.match(/Master Address=(.*)/) || [])[1];
    const g = pl && playerGame(pl);
    addChat(pl ? pl.name : 'bot', `[fake client] got deadlock.ini: Role=${role}${addr ? `, Master Address=${addr.trim()}` : ''}, ${cmd.content.length} bytes; would run ${cmd.exe} ${(cmd.args || []).join(' ')} in ${a.gameDir}${cmd.delayMs ? ` after ${cmd.delayMs} ms` : ''}`, g ? g.key : 'lobby');
    log('fake agent', a.token, 'received launch as', role);
    return;
  }
  if (a.waiters.length) {
    const ws = a.waiters; a.waiters = [];
    for (const r of ws) if (!r.writableEnded) json(r, 200, cmd);
  } else {
    a.pending = cmd;
  }
}

// ---------------------------------------------------------------------------------------------
// Players and games
// ---------------------------------------------------------------------------------------------
function getPlayer(pid) {
  const p = players.get(String(pid || ''));
  if (!p) throw httpError(401, 'unknown player, re-enter the lobby');
  p.lastSeen = now();
  return p;
}
function playerGame(p) { return p.gameKey ? games.get(p.gameKey) : null; }
function participants(g) { return [...players.values()].filter((p) => p.gameKey === g.key); }

function leaveGame(p) {
  const g = playerGame(p);
  p.gameKey = null; p.ready = false;
  if (!g) return;
  if (g.hostId === p.id) {
    for (const q of participants(g)) { q.gameKey = null; q.ready = false; }
    games.delete(g.key);
    addChat('system', `${p.name} closed the game "${g.name}"`, 'lobby');
  } else {
    addChat('system', `${p.name} left the game`, g.key);
  }
}

function addChat(from, text, channel) {
  const m = { ts: now(), from, text: String(text).slice(0, 500), channel: channel || 'lobby' };
  chat.push(m);
  if (chat.length > 500) chat.splice(0, chat.length - 500);
  broadcast('chat', m);
}

// ---------------------------------------------------------------------------------------------
// TCP/IP addressing.  DirectPlay on the joiner's side needs the host's IP (DPAID_INet); the game
// only takes it from [Startup] Master Address.  The host can set it explicitly per game (public
// IP, VPN address, hostname); otherwise the server guesses from what it sees.
// ---------------------------------------------------------------------------------------------
function validAddress(s) {
  s = String(s || '').trim();
  if (!s || s.length > 63) return null;
  if (!/^[A-Za-z0-9][A-Za-z0-9.\-]*$/.test(s)) return null;  // IPv4 or hostname; DirectPlay 4 has no IPv6
  return s;
}

function autoHostAddress(hostAgent) {
  if (!hostAgent) return null;
  if (!isLoopback(hostAgent.remoteAddress)) return hostAgent.remoteAddress;
  const ip = hostAgent.ips.find((x) => /^\d+\.\d+\.\d+\.\d+$/.test(x) && !x.startsWith('127.'));
  return ip || '127.0.0.1';
}

function pickHostAddress(game, hostAgent, joinerAgent) {
  if (game && game.hostAddress) return game.hostAddress;
  if (isLoopback(joinerAgent.remoteAddress) && isLoopback(hostAgent.remoteAddress)) return '127.0.0.1';
  return autoHostAddress(hostAgent) || '127.0.0.1';
}

function serverIps() {
  const out = [];
  try { for (const list of Object.values(require('os').networkInterfaces())) for (const i of list) if (i.family === 'IPv4' && !i.internal) out.push(i.address); } catch (_) { /* ignore */ }
  return out;
}

// Direct connect: launch this player's game as a joiner against an arbitrary address, no lobby game.
function directConnect(pl, address) {
  const a = agents.get(pl.agentToken);
  if (!agentOnline(a)) throw httpError(400, 'your Deadlock II client (MClient.exe) is not connected');
  const addr = validAddress(address);
  if (!addr) throw httpError(400, 'enter an IPv4 address or host name (letters, digits, dots, dashes)');
  const game = { name: `Direct ${addr}`, options: normalizeOptions({}) };
  const content = buildIni({ game, role: 'slave', masterAddress: addr, userName: pl.name, humans: 2 });
  deliver(a, { command: 'launch', file: a.outFile, gameDir: a.gameDir, content, exe: GAME_EXE, args: GAME_ARGS, delayMs: 0 });
  addChat('system', `${pl.name} is connecting directly to ${addr}`, 'lobby');
  log('direct connect', pl.name, addr);
  return addr;
}

function launchGame(host) {
  const g = playerGame(host);
  if (!g) throw httpError(400, 'you are not in a game');
  if (g.hostId !== host.id) throw httpError(403, 'only the host can launch');
  if (g.launched) throw httpError(400, 'already launched');
  const ps = participants(g);
  if (ps.length < 2) throw httpError(400, 'need at least 2 players in the game');
  const hostAgent = agents.get(host.agentToken);
  if (!agentOnline(hostAgent)) throw httpError(400, 'your Deadlock II client (MClient.exe) is not connected');
  for (const p of ps) {
    if (p.id === host.id) continue;
    if (!p.ready) throw httpError(400, `${p.name} is not ready`);
    if (!agentOnline(agents.get(p.agentToken))) throw httpError(400, `${p.name} has no Deadlock II client connected`);
  }
  g.launched = true;
  const humans = ps.length;
  const plan = [];
  for (const p of ps) {
    const a = agents.get(p.agentToken);
    const role = p.id === host.id ? 'master' : 'slave';
    const content = buildIni({ game: g, role, masterAddress: pickHostAddress(g, hostAgent, a), userName: p.name, humans });
    const cmd = { command: 'launch', file: a.outFile, gameDir: a.gameDir, content, exe: GAME_EXE, args: GAME_ARGS, delayMs: role === 'master' ? 0 : 4000 };
    plan.push({ player: p.name, role, agent: a.token });
    deliver(a, cmd);
  }
  addChat('system', `Launching "${g.name}" with ${humans} players. NetAccolade shuts down, Deadlock 2 starts up!`, g.key);
  if (fakeHost.running && isLoopback(hostAgent.remoteAddress)) {
    fakeHost.stop();
    addChat('system', 'Fake DirectPlay sessions paused while this PC hosts a real game (UDP 47624 released). They come back on the next server start, or with /fakegames N.', 'lobby');
  }
  log('launch', g.name, JSON.stringify(plan));
  setTimeout(() => { for (const p of participants(g)) { p.gameKey = null; p.ready = false; } games.delete(g.key); broadcastState(); }, 15000);
  return plan;
}

// ---------------------------------------------------------------------------------------------
// SSE
// ---------------------------------------------------------------------------------------------
function snapshotFor(p) {
  const a = p && agents.get(p.agentToken);
  return {
    me: p ? { id: p.id, name: p.name, gameKey: p.gameKey, ready: p.ready,
      agent: a ? { gameDir: a.gameDir, online: agentOnline(a), address: a.remoteAddress } : null } : null,
    players: [...players.values()].map((q) => ({ id: q.id, name: q.name, gameKey: q.gameKey, ready: q.ready, online: q.online,
      fake: !!q.fake, agentOnline: agentOnline(agents.get(q.agentToken)) })),
    games: [...games.values()].map((g) => ({ key: g.key, name: g.name, description: g.description, hostId: g.hostId,
      hostName: (players.get(g.hostId) || {}).name, options: g.options, launched: g.launched,
      hostAddress: g.hostAddress || null, autoAddress: autoHostAddress(agents.get((players.get(g.hostId) || {}).agentToken)),
      participants: participants(g).map((q) => q.id) })),
    fakeGames: fakeHost.running ? fakeHost.status().sessions : [],
    server: { version: VERSION, host: HOST, port: PORT, agents: agents.size, ips: serverIps(), dpenum: { running: fakeHost.running, requests: fakeHost.requests, replies: fakeHost.replies } },
  };
}
function sseSend(res, event, data) { res.write(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`); }
function broadcast(event, data) { for (const c of sseClients) sseSend(c.res, event, data); }
function broadcastState() { for (const c of sseClients) sseSend(c.res, 'state', snapshotFor(players.get(c.playerId))); }

function openSse(req, res, p) {
  res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache', Connection: 'keep-alive' });
  res.write(':ok\n\n');
  const c = { res, playerId: p.id };
  sseClients.add(c);
  p.online = true; p.sse = (p.sse || 0) + 1;
  if (p.removeTimer) { clearTimeout(p.removeTimer); p.removeTimer = null; }
  sseSend(res, 'state', snapshotFor(p));
  for (const m of chat.slice(-100)) sseSend(res, 'chat', m);
  const hb = setInterval(() => res.write(':hb\n\n'), 15000);
  req.on('close', () => {
    clearInterval(hb); sseClients.delete(c);
    p.sse -= 1;
    if (p.sse <= 0) {
      p.online = false;
      p.removeTimer = setTimeout(() => { if (!p.online) { leaveGame(p); players.delete(p.id); broadcastState(); } }, 60000);
    }
    broadcastState();
  });
  broadcastState();
}

// ---------------------------------------------------------------------------------------------
// HTTP plumbing
// ---------------------------------------------------------------------------------------------
function httpError(code, msg) { const e = new Error(msg); e.status = code; return e; }
function json(res, code, obj) {
  if (res.writableEnded) return;
  const body = JSON.stringify(obj);
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': Buffer.byteLength(body), 'Cache-Control': 'no-store' });
  res.end(body);
}
function readJson(req) {
  return new Promise((resolve, reject) => {
    let data = '';
    req.on('data', (c) => { data += c; if (data.length > 65536) { reject(httpError(413, 'body too large')); req.destroy(); } });
    req.on('end', () => { try { resolve(data ? JSON.parse(data) : {}); } catch (e) { reject(httpError(400, 'invalid JSON')); } });
    req.on('error', reject);
  });
}
const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.ico': 'image/x-icon' };
function serveStatic(res, rel) {
  const file = path.normalize(path.join(PUBLIC_DIR, rel === '/' ? 'index.html' : rel));
  if (!file.startsWith(PUBLIC_DIR) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) return json(res, 404, { error: 'not found' });
  res.writeHead(200, { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-cache' });
  fs.createReadStream(file).pipe(res);
}

async function route(req, res) {
  const u = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
  const p = u.pathname; const m = req.method;
  if (!p.startsWith('/api/')) return serveStatic(res, p);
  const body = m === 'POST' ? await readJson(req) : {};

  if (p === '/api/ping') return json(res, 200, { ok: true, name: 'netaccolade-lobby', version: VERSION, time: now() });

  // --- agent (MClient.exe) side --------------------------------------------------------------
  if (p === '/api/agent/register' && m === 'POST') {
    const a = registerAgent(body, req);
    return json(res, 200, { token: a.token, lobbyUrl: `http://${HOST === '0.0.0.0' ? '127.0.0.1' : HOST}:${PORT}/?agent=${a.token}`, pollPath: `/api/agent/${a.token}/wait` });
  }
  let mm = p.match(/^\/api\/agent\/([0-9a-f]{16})\/(wait|done)$/);
  if (mm) {
    const a = agents.get(mm[1]);
    if (!a) return json(res, 404, { error: 'unknown agent' });
    if (mm[2] === 'wait') return agentWait(a, res);
    log('agent done', a.token, body.status || ''); agents.delete(a.token);
    if (a.playerId && players.has(a.playerId)) players.get(a.playerId).agentToken = null;
    broadcastState(); return json(res, 200, { ok: true });
  }

  // --- browser side -------------------------------------------------------------------------
  if (p === '/api/player/join' && m === 'POST') {
    const name = clip(body.name, 31);
    if (!name) throw httpError(400, 'name required');
    let pl = body.player && players.get(String(body.player));
    if (!pl) { pl = { id: id(), name, gameKey: null, ready: false, online: false, sse: 0, agentToken: null, lastSeen: now() }; players.set(pl.id, pl); }
    pl.name = name;
    if (body.agent && agents.has(String(body.agent))) {
      const a = agents.get(String(body.agent));
      if (pl.agentToken && pl.agentToken !== a.token && agents.has(pl.agentToken)) agents.get(pl.agentToken).playerId = null;
      pl.agentToken = a.token; a.playerId = pl.id;
    }
    addChat('system', `${name} entered the lobby`, 'lobby');
    broadcastState();
    return json(res, 200, { player: pl.id });
  }
  if (p === '/api/events') { const pl = getPlayer(u.searchParams.get('player')); return openSse(req, res, pl); }
  if (p === '/api/state') return json(res, 200, snapshotFor(players.get(u.searchParams.get('player'))));

  if (p === '/api/chat' && m === 'POST') {
    const pl = getPlayer(body.player); const text = String(body.text || '').trim();
    if (!text) throw httpError(400, 'empty message');
    if (text.startsWith('/') && handleChatCommand(pl, text)) return json(res, 200, { ok: true, command: true });
    const g = playerGame(pl);
    addChat(pl.name, text, body.channel === 'game' && g ? g.key : 'lobby');
    return json(res, 200, { ok: true });
  }
  if (p === '/api/game/create' && m === 'POST') {
    const pl = getPlayer(body.player);
    if (pl.gameKey) throw httpError(400, 'leave your current game first');
    const name = clip(body.name, 31) || `${pl.name}'s game`;
    let options; try { options = normalizeOptions(body.options); } catch (e) { throw httpError(400, e.message); }
    let hostAddress = null;
    if (body.hostAddress && String(body.hostAddress).trim()) { hostAddress = validAddress(body.hostAddress); if (!hostAddress) throw httpError(400, 'host address must be an IPv4 address or host name'); }
    const g = { key: id(), name, description: clip(body.description, 200), hostId: pl.id, options, hostAddress, createdAt: now(), launched: false };
    games.set(g.key, g); pl.gameKey = g.key; pl.ready = true;
    addChat('system', `${pl.name} created the game "${name}"`, 'lobby');
    attachFakes(g, pl);
    broadcastState(); return json(res, 200, { key: g.key });
  }
  if (p === '/api/game/join' && m === 'POST') {
    const pl = getPlayer(body.player); const g = games.get(String(body.key || ''));
    if (!g) throw httpError(404, 'game not found');
    if (g.launched) throw httpError(400, 'game already launched');
    if (participants(g).length >= 7) throw httpError(400, 'game is full');
    if (pl.gameKey && pl.gameKey !== g.key) leaveGame(pl);
    pl.gameKey = g.key; pl.ready = false;
    addChat('system', `${pl.name} is considering joining`, g.key);
    attachFakes(g, pl);
    broadcastState(); return json(res, 200, { ok: true });
  }
  if (p === '/api/game/address' && m === 'POST') {
    const pl = getPlayer(body.player); const g = playerGame(pl);
    if (!g || g.hostId !== pl.id) throw httpError(403, 'only the host can set the connect address');
    const s = String(body.address || '').trim();
    if (!s || /^auto$/i.test(s)) g.hostAddress = null;
    else { const v = validAddress(s); if (!v) throw httpError(400, 'enter an IPv4 address or host name'); g.hostAddress = v; }
    addChat('system', g.hostAddress ? `${pl.name} set the connect address to ${g.hostAddress}` : `${pl.name} switched the connect address back to automatic`, g.key);
    broadcastState(); return json(res, 200, { hostAddress: g.hostAddress });
  }
  if (p === '/api/connect' && m === 'POST') {
    const pl = getPlayer(body.player);
    if (pl.gameKey) throw httpError(400, 'leave your current game first');
    const addr = directConnect(pl, body.address); broadcastState(); return json(res, 200, { ok: true, address: addr });
  }
  if (p === '/api/game/leave' && m === 'POST') { const pl = getPlayer(body.player); leaveGame(pl); broadcastState(); return json(res, 200, { ok: true }); }
  if (p === '/api/game/ready' && m === 'POST') {
    const pl = getPlayer(body.player); if (!pl.gameKey) throw httpError(400, 'not in a game');
    pl.ready = bool(body.ready); broadcastState(); return json(res, 200, { ready: pl.ready });
  }
  if (p === '/api/game/launch' && m === 'POST') { const pl = getPlayer(body.player); const plan = launchGame(pl); broadcastState(); return json(res, 200, { ok: true, plan }); }
  if (p === '/api/ini/preview') {
    const pl = getPlayer(u.searchParams.get('player')); const g = playerGame(pl);
    if (!g) throw httpError(400, 'not in a game');
    const role = g.hostId === pl.id ? 'master' : 'slave';
    const hostAgent = agents.get((players.get(g.hostId) || {}).agentToken); const myAgent = agents.get(pl.agentToken);
    const addr = hostAgent && myAgent ? pickHostAddress(g, hostAgent, myAgent) : (g.hostAddress || autoHostAddress(hostAgent) || '<host address>');
    res.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8' });
    return res.end(buildIni({ game: g, role, masterAddress: addr, userName: pl.name, humans: Math.max(2, participants(g).length) }));
  }
  if (p === '/api/options/schema') return json(res, 200, { defaults: DEFAULT_OPTIONS, victory: VICTORY, winCities: WIN_CITIES, winShrines: WIN_SHRINES, winTurns: WIN_TURNS, abilities: ABILITIES, worldTypes: WORLD_TYPES, colors: COLORS });
  return json(res, 404, { error: 'no such endpoint' });
}

const server = http.createServer((req, res) => {
  route(req, res).catch((e) => { if (!e.status) log('error', e); json(res, e.status || 500, { error: e.message || 'internal error' }); });
});
server.listen(PORT, HOST, () => {
  log(`NetAccolade lobby ${VERSION} listening on http://${HOST}:${PORT}/`);
  const n = parseInt(process.env.NETACC_FAKEGAMES != null ? process.env.NETACC_FAKEGAMES : (CONFIG.fakeGames != null ? CONFIG.fakeGames : 0), 10);
  if (n > 0) setFakeGames(Math.min(10, n), 'lobby').catch((e) => log('fake games:', e.message));
});
server.on('error', (e) => { console.error('cannot listen:', e.message); process.exit(1); });

module.exports = { buildIni, normalizeOptions, DEFAULT_OPTIONS };
