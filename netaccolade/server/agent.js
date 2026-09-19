'use strict';
/*
 * The MClient.exe stand-in.  Deadlock II spawns  MClient.exe -gi 1 -o deadlock.ini  with the game
 * directory as working directory and then quits.  MClient.exe (a tiny trampoline) runs this script
 * with the same arguments.  It:
 *   1. makes sure the lobby server is running (starts server.js on localhost if it is not),
 *   2. registers as an agent (game dir, output file, game id, local IPs),
 *   3. opens the lobby page in the default browser (linked to this agent),
 *   4. long-polls for a "launch" command; when it arrives writes the deadlock.ini it was given
 *      into the game directory and starts  DEADLOCK.EXE -ms,
 *   5. waits for the game to exit and removes the launch file again.
 *
 * Env: NETACC_SERVER (default http://127.0.0.1:7624), NETACC_NO_BROWSER=1, NETACC_NO_AUTOSTART=1
 */
const http = require('http');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn } = require('child_process');

const LOG_FILE = path.join(__dirname, 'mclient.log');
function log(...a) {
  const line = `${new Date().toISOString()} ${a.join(' ')}\n`;
  try { fs.appendFileSync(LOG_FILE, line); } catch (_) { /* ignore */ }
  process.stdout.write(line);
}

function parseArgs(argv) {
  const out = { gameId: '1', outFile: 'deadlock.ini' };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '-gi' && argv[i + 1]) out.gameId = argv[++i];
    else if (argv[i] === '-o' && argv[i + 1]) out.outFile = argv[++i];
  }
  return out;
}

function readConfig() {
  const cfgPath = path.join(__dirname, 'mclient.json');
  let cfg = {};
  try { cfg = JSON.parse(fs.readFileSync(cfgPath, 'utf8')); } catch (_) { /* optional */ }
  const server = process.env.NETACC_SERVER || cfg.server || 'http://127.0.0.1:7624';
  return { server: new URL(server), cfg };
}

function request(base, method, p, body) {
  return new Promise((resolve, reject) => {
    const data = body ? JSON.stringify(body) : null;
    const req = http.request({ hostname: base.hostname, port: base.port || 80, path: p, method,
      headers: data ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(data) } : {}, timeout: 40000 }, (res) => {
      let buf = '';
      res.on('data', (c) => { buf += c; });
      res.on('end', () => {
        try { const j = JSON.parse(buf || '{}'); if (res.statusCode >= 400) reject(new Error(j.error || `HTTP ${res.statusCode}`)); else resolve(j); }
        catch (e) { reject(new Error(`bad response: ${buf.slice(0, 200)}`)); }
      });
    });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', reject);
    if (data) req.write(data);
    req.end();
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function ensureServer(base) {
  try { await request(base, 'GET', '/api/ping'); return true; } catch (_) { /* not running */ }
  const local = ['127.0.0.1', 'localhost', '::1'].includes(base.hostname);
  if (!local || process.env.NETACC_NO_AUTOSTART) return false;
  log('lobby server not running, starting server.js');
  const child = spawn(process.execPath, [path.join(__dirname, 'server.js')], { detached: true, stdio: 'ignore', windowsHide: true, env: Object.assign({}, process.env, { NETACC_PORT: base.port || 7624 }) });
  child.unref();
  for (let i = 0; i < 20; i++) {
    await sleep(500);
    try { await request(base, 'GET', '/api/ping'); return true; } catch (_) { /* retry */ }
  }
  return false;
}

function localIps() {
  const out = [];
  for (const list of Object.values(os.networkInterfaces())) for (const i of list) if (i.family === 'IPv4' && !i.internal) out.push(i.address);
  return out;
}

function openBrowser(u) {
  if (process.env.NETACC_NO_BROWSER) return;
  const cmd = process.platform === 'win32' ? spawn('cmd', ['/c', 'start', '', u], { detached: true, stdio: 'ignore', windowsHide: true })
    : spawn(process.platform === 'darwin' ? 'open' : 'xdg-open', [u], { detached: true, stdio: 'ignore' });
  cmd.unref();
}

function launchGame(cmd, gameDir) {
  const file = path.join(gameDir, path.basename(cmd.file || 'deadlock.ini'));
  fs.writeFileSync(file, cmd.content, { encoding: 'latin1' });
  log('wrote', file, `(${cmd.content.length} bytes)`);
  const exe = path.join(gameDir, path.basename(cmd.exe || 'DEADLOCK.EXE'));
  if (!fs.existsSync(exe)) throw new Error(`${exe} not found`);
  const args = Array.isArray(cmd.args) && cmd.args.length ? cmd.args : ['-ms'];
  log('starting', exe, args.join(' '));
  const game = spawn(exe, args, { cwd: gameDir, stdio: 'ignore', windowsHide: false });
  return new Promise((resolve) => {
    game.on('error', (e) => { log('game failed to start:', e.message); resolve(1); });
    game.on('exit', (code) => {
      log('game exited with', code);
      try { fs.unlinkSync(file); log('removed', file); } catch (_) { /* already gone */ }
      resolve(0);
    });
  });
}

// The game spawns MClient.exe with the game directory as cwd.  When someone double-clicks
// MClient.exe instead, cwd is this folder; walk upwards (and check mclient.json) to find DEADLOCK.EXE.
function resolveGameDir(cfg) {
  const candidates = [process.cwd(), cfg.gameDir, process.env.NETACC_GAMEDIR].filter(Boolean);
  let d = __dirname;
  for (let i = 0; i < 4; i++) { candidates.push(d); d = path.dirname(d); }
  for (const c of candidates) {
    try { if (fs.existsSync(path.join(c, 'DEADLOCK.EXE'))) return path.resolve(c); } catch (_) { /* skip */ }
  }
  return null;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const { server, cfg } = readConfig();
  const gameDir = resolveGameDir(cfg);
  if (!gameDir) { log(`DEADLOCK.EXE not found from cwd ${process.cwd()} or above ${__dirname}; set "gameDir" in mclient.json`); return 5; }
  log(`MClient stand-in: gameId=${args.gameId} out=${args.outFile} gameDir=${gameDir} server=${server.origin}`);
  if (!(await ensureServer(server))) { log('no lobby server reachable at', server.origin); return 2; }

  const reg = await request(server, 'POST', '/api/agent/register', { gameDir, outFile: args.outFile, gameId: args.gameId, ips: localIps() });
  log('registered as agent', reg.token);
  const lobbyUrl = reg.lobbyUrl.replace(/^http:\/\/[^/]+/, server.origin);
  openBrowser(lobbyUrl);
  log('lobby page:', lobbyUrl);

  const idleLimit = Date.now() + 3 * 60 * 60 * 1000; // give up after 3 hours in the lobby
  let failures = 0;
  while (Date.now() < idleLimit) {
    let r;
    try { r = await request(server, 'GET', reg.pollPath); failures = 0; }
    catch (e) {
      failures += 1; log('poll failed:', e.message);
      if (failures > 6) { log('lobby server gone, exiting'); return 3; }
      await sleep(2000); continue;
    }
    if (r.command === 'launch') {
      if (r.delayMs) await sleep(r.delayMs);
      let status = 'launched';
      try { launchGame(r, gameDir).then(() => process.exit(0)); }
      catch (e) { log('launch failed:', e.message); status = 'failed: ' + e.message; }
      try { await request(server, 'POST', `/api/agent/${reg.token}/done`, { status }); } catch (_) { /* best effort */ }
      if (status !== 'launched') return 4;
      return new Promise(() => { /* keep process alive until the game exits (handled above) */ });
    }
  }
  log('idle limit reached, exiting');
  return 0;
}

main().then((code) => { if (typeof code === 'number' && code !== 0) process.exit(code); }, (e) => { log('fatal:', e.stack || e.message); process.exit(1); });
