import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import https from 'node:https';
import crypto from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const directory = path.join(root, 'artifacts', 'ota-network', crypto.randomUUID());
fs.mkdirSync(directory, { recursive: true });
const run = (command, args, options = {}) => new Promise((resolve, reject) => {
  const child = spawn(command, args, { cwd: root, windowsHide: true, ...options });
  let output = '';
  child.stdout?.on('data', bytes => { output += bytes; }); child.stderr?.on('data', bytes => { output += bytes; });
  child.on('error', reject);
  child.on('exit', code => code === 0 ? resolve(output) : reject(new Error(`${command} exited ${code}: ${output}`)));
});
const openssl = path.join(process.env.ProgramFiles, 'Git', 'usr', 'bin', 'openssl.exe');
const certificate = path.join(directory, 'tls-cert.pem'), key = path.join(directory, 'tls-key.pem');
await run(openssl, ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', certificate, '-days', '1', '-subj', '/CN=127.0.0.1', '-addext', 'subjectAltName=IP:127.0.0.1']);
const fingerprint = new crypto.X509Certificate(fs.readFileSync(certificate)).fingerprint256.replaceAll(':', '');
let upstream;
const sockets = new Set();
const relay = https.createServer({ key: fs.readFileSync(key), cert: fs.readFileSync(certificate), minVersion: 'TLSv1.2' }, (request, response) => {
  const remote = http.get(upstream + request.url, incoming => {
    response.writeHead(incoming.statusCode, incoming.headers);
    incoming.on('aborted', () => response.destroy()); incoming.pipe(response);
  });
  remote.on('error', () => response.destroy()); response.on('close', () => remote.destroy());
});
relay.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); });
await new Promise(resolve => relay.listen(0, '127.0.0.1', resolve));
const base = `https://127.0.0.1:${relay.address().port}/ota/`;
const originalChecks = path.join(root, 'artifacts', 'NativeOtaCheck.exe');
const fixture = path.join(directory, 'fixtures');
const framework = path.join(process.env.WINDIR, 'Microsoft.NET', 'Framework64', 'v4.0.30319');
let simulation;
let log = '', errors = '';
try {
  await run(originalChecks, ['--network-fixture', path.join(root, 'artifacts', 'releases', '1.4.1'), base, fixture]);
  const checks = path.join(directory, 'NetworkCheck.exe');
  await run(path.join(framework, 'csc.exe'), ['/nologo', '/target:exe', `/out:${checks}`, '/r:System.Web.Extensions.dll', `/resource:${path.join(fixture, 'test-public.xml')},update-public-key.xml`, path.join(root, 'native-wpf', 'OtaUpdate.cs'), path.join(root, 'tests', 'NativeOtaCheck.cs')]);
  simulation = spawn('postman.exe', ['simulation', 'run', 'postman/simulations/ota.sim.yaml', '--port', 'auto', '--no-history', '--output', 'ndjson'], { cwd: root, windowsHide: true, env: { ...process.env, BLACKWALL_OTA_FIXTURES: fixture } });
  const ready = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Postman simulation readiness timeout: ' + errors)), 25000);
    simulation.on('error', error => { clearTimeout(timer); reject(error); });
    simulation.on('exit', code => { clearTimeout(timer); reject(new Error(`Postman simulation exited ${code}: ${errors}`)); });
    simulation.stdout.on('data', bytes => {
      log += bytes;
      for (const line of log.split('\n')) {
        try {
          const event = JSON.parse(line);
          if (event.type === 'listening' || event.event === 'listening') {
            const match = line.match(/http:\/\/(?:localhost|127\.0\.0\.1):\d+/);
            if (match) { upstream = match[0]; clearTimeout(timer); resolve(); }
          }
        } catch {}
      }
    });
    simulation.stderr.on('data', bytes => { errors += bytes; });
  });
  await ready;
  const result = await run(checks, ['--network', base, fingerprint, path.join(directory, 'downloads')]);
  const evidence = await new Promise((resolve, reject) => http.get(upstream + '/ota/evidence', response => {
    let data = ''; response.on('data', bytes => { data += bytes; }); response.on('end', () => { try { resolve(JSON.parse(data)); } catch (error) { reject(error); } });
  }).on('error', reject));
  for (const route of ['/manifest', '/binary', '/tampered', '/oversize', '/truncated', '/failure', '/downgrade', '/slow']) if (!evidence.includes(route)) throw new Error('Missing Postman fixture request: ' + route);
  if (evidence.filter(route => route === '/loop').length !== 6) throw new Error('The native redirect limit was not exercised.');
  fs.writeFileSync(path.join(directory, 'request-evidence.json'), JSON.stringify(evidence, null, 2));
  const requests = log.split('\n').filter(line => { try { const event = JSON.parse(line); return event.type === 'request' || event.event === 'request'; } catch { return false; } });
  if (requests.length < 9) throw new Error('Insufficient Postman request evidence: ' + requests.length);
  fs.writeFileSync(path.join(directory, 'checks.log'), result);
  console.log(result.trim()); console.log(`Postman observed ${requests.length} requests; evidence in ${path.relative(root, directory)}.`);
} finally {
  fs.writeFileSync(path.join(directory, 'simulation.ndjson'), log);
  fs.writeFileSync(path.join(directory, 'simulation.err'), errors);
  if (simulation && simulation.exitCode === null) spawnSync('taskkill.exe', ['/PID', String(simulation.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
  for (const socket of sockets) socket.destroy(); relay.close();
}
