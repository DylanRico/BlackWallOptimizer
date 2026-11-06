// Checks every official source cited in catalogs/tweaks.json against the live page: the page must load, each
// covered value must appear in it, and each cover must match a real operation of that tweak (no invented
// coverage). Prints, per tweak, how many of its operations are backed by Microsoft documentation.
// Usage: node scripts/check-official-sources.mjs
import { readFileSync } from 'node:fs';

const catalog = JSON.parse(readFileSync(new URL('../catalogs/tweaks.json', import.meta.url), 'utf8'));
const pages = new Map();
const page = url => {
  if (!pages.has(url)) pages.set(url, fetch(url, { redirect: 'follow' }).then(async r => ({
    status: r.status,
    text: (await r.text()).replace(/<[^>]+>/g, ' ').replace(/&#92;|&#x5C;/gi, '\\').replace(/&gt;/g, '>').replace(/\s+/g, ' ').toLowerCase()
  })).catch(e => ({ status: 0, text: '', error: e.message })));
  return pages.get(url);
};

// Same text the app logs for an operation (FullOperation in BlackwallApp.cs).
const describe = op => op.type === 'powercfg' ? 'powercfg ' + op.args
  : op.type === 'appx_remove' ? 'Remove-AppxPackage -AllUsers + Remove-AppxProvisionedPackage -Online · ' + op.name
  : op.type === 'service_stop' || op.type === 'service_start' ? op.type + ' ' + op.name
  : op.hive + '\\' + op.key + ' › ' + (op.name || '(predeterminado)') + (op.type === 'reg_delete' ? '' : ' = ' + op.value);

const failures = [];
let documented = 0, total = 0;
for (const t of catalog.tweaks) {
  const ops = (t.applyOps || []).map(describe);
  const covered = new Set();
  for (const s of t.sources || []) {
    const p = await page(s.url);
    if (p.status !== 200) { failures.push(`${t.id}: ${s.url} respondió ${p.status || p.error}`); continue; }
    for (const cover of s.covers) {
      const [match, proof = match] = cover.split('|');
      const term = proof.includes('›') ? proof.split('›').pop().trim() : proof;
      if (!p.text.includes(term.toLowerCase())) failures.push(`${t.id}: «${term}» no aparece en ${s.url}`);
      const hits = ops.filter(o => o.toLowerCase().includes(match.toLowerCase()));
      if (!hits.length) failures.push(`${t.id}: «${match}» no corresponde a ninguna operación del ajuste`);
      if (!s.deprecated) hits.forEach(h => covered.add(h));
    }
  }
  documented += covered.size; total += ops.length;
  const status = !ops.length ? '-' : covered.size === ops.length ? 'oficial' : covered.size ? 'parcial' : 'sin documentación';
  console.log(`${status.padEnd(18)} ${String(covered.size).padStart(2)}/${String(ops.length).padEnd(2)} ${t.id}`);
}
console.log(`\n${documented} de ${total} operaciones respaldadas por documentación oficial · ${pages.size} páginas comprobadas`);
if (failures.length) { console.error('\nFALLOS:\n' + failures.join('\n')); process.exit(1); }
console.log('PASS: todas las fuentes citadas existen y contienen lo que se cita');
