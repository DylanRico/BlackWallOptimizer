import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const catalog = JSON.parse(readFileSync(new URL('../catalogs/tweaks.json', import.meta.url), 'utf8'));
const tweak = catalog.tweaks.find(t => t.id === 'debloat_telemetry_diagtrack');
const name = 'POWERSHELL_TELEMETRY_OPTOUT';
const key = 'SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment';
const apply = tweak.applyOps.filter(op => op.name === name);
assert.deepEqual(apply, [{ type: 'reg_sz', hive: 'HKLM', key, name, value: '1' }]);
assert.ok(tweak.undoOps.some(op => op.type === 'reg_delete' && op.hive === 'HKLM' && op.key === key && op.name === name));
assert.ok(tweak.sources.some(source => source.url.startsWith('https://learn.microsoft.com/') && source.covers.some(cover => cover.includes(name))));
assert.equal(catalog.tweaks.filter(t => t.applyOps.some(op => op.hive === 'HKLM' && op.key?.toLowerCase() === key.toLowerCase() && op.name?.toLowerCase() === name.toLowerCase())).length, 1);
console.log('PASS: telemetry catalog contract.');
