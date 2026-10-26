const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const directory = process.env.BLACKWALL_OTA_FIXTURES;
if (!directory) throw new Error('BLACKWALL_OTA_FIXTURES must name disposable signed test fixtures.');
const manifest = fs.readFileSync(path.join(directory, 'update.json'));
const binary = fs.readFileSync(path.join(directory, 'Blackwall.exe'));
const requests = [];
http.createServer((req, res) => {
  const route = new URL(req.url, 'http://localhost').pathname;
  const send = (status, data, headers = {}) => { res.writeHead(status, { 'Content-Length': data.length, ...headers }); res.end(data); };
  requests.push(route);
  if (route === '/evidence') return send(200, JSON.stringify(requests));
  if (req.method !== 'GET') return send(501, 'Unsupported test operation');
  if (route === '/manifest') return send(200, manifest, { 'Content-Type': 'application/json' });
  if (route === '/binary') return send(200, binary, { 'Content-Type': 'application/octet-stream' });
  if (route === '/tampered') {
    const envelope = JSON.parse(manifest);
    const payload = Buffer.from(envelope.payload, 'base64'); payload[10] ^= 1;
    envelope.payload = payload.toString('base64');
    return send(200, JSON.stringify(envelope));
  }
  if (route === '/oversize') return send(200, 'X'.repeat(64));
  if (route === '/truncated') { res.writeHead(200, { 'Content-Length': binary.length }); res.flushHeaders(); res.write(binary.subarray(0, 1024)); return setTimeout(() => res.destroy(), 50); }
  if (route === '/failure') return send(500, '<html>test distribution failure</html>');
  if (route === '/downgrade') return send(302, '', { Location: 'http://127.0.0.1:9/not-allowed' });
  if (route === '/loop') return send(302, '', { Location: '/ota/loop' });
  if (route === '/slow') return setTimeout(() => send(200, manifest), 17000);
  return send(501, 'Endpoint outside the OTA test contract');
}).listen(Number(process.env.PORT || 4500), '127.0.0.1');
