import test from "node:test";
import assert from "node:assert/strict";
import vm from "node:vm";
import { readFile } from "node:fs/promises";
import { hardwareFixture } from "./ui-fixtures.mjs";
import { createPreviewServer } from "../scripts/preview-ui.mjs";

const source = await readFile(new URL("../src/app.js", import.meta.url), "utf8");

test("executable icon provides classic small frames and a high-resolution transparent PNG", async () => {
  const ico = await readFile(new URL("../src-tauri/icons/icon.ico", import.meta.url));
  assert.equal(ico.readUInt16LE(2), 1);
  const frames = Array.from({length: ico.readUInt16LE(4)}, (_, index) => {
    const entry = 6 + index * 16;
    const size = ico[entry] || 256;
    const length = ico.readUInt32LE(entry + 8), offset = ico.readUInt32LE(entry + 12);
    assert.ok(offset + length <= ico.length);
    return {size, bytes: ico.subarray(offset, offset + length)};
  });
  assert.deepEqual(frames.map(frame => frame.size), [16,24,32,48,64,128,256]);
  for (const frame of frames.filter(frame => frame.size <= 64)) {
    assert.equal(frame.bytes.readUInt32LE(0), 40);
    assert.equal(frame.bytes.readInt32LE(4), frame.size);
    assert.equal(frame.bytes.readInt32LE(8), frame.size * 2);
    assert.equal(frame.bytes.readUInt16LE(14), 32);
    assert.equal(frame.bytes.length, 40 + frame.size ** 2 * 4 + Math.ceil(frame.size / 32) * 4 * frame.size);
  }
  const large = frames.find(frame => frame.size === 256).bytes;
  assert.equal(large.toString("ascii", 1, 4), "PNG");
  assert.equal(large[25], 6);
});

function harness(fetch, stored = null) {
  const elements = new Map();
  const listeners = {};
  function element(id) {
    if (!elements.has(id)) {
      const classes = new Set();
      const el = { textContent: "", innerHTML: "", disabled: false, scrollTop: 0,
        classList: { toggle(name, on) { if (on) classes.add(name); else classes.delete(name); }, add(name) { classes.add(name); }, remove(name) { classes.delete(name); }, contains(name) { return classes.has(name); } },
        handlers: {}, addEventListener(name, handler) { this.handlers[name] = handler; }, setAttribute(name, value) { this[name] = value; }, removeAttribute(name) { delete this[name]; },
        insertAdjacentHTML(_, html) { this.innerHTML = html + this.innerHTML; }, querySelector() { return element(`${id}-progress`); }
      };
      elements.set(id, el);
    }
    return elements.get(id);
  }
  const context = vm.createContext({ fetch, console, Set, Map, Date, Promise,
    window: { addEventListener(name, callback) { listeners[name] = callback; } },
    document: { documentElement: element("html"), getElementById: element, querySelectorAll: () => [], querySelector: () => element("sidebar"), addEventListener() {}, hidden: false, hasFocus: () => true },
    localStorage: { getItem() { return stored; }, setItem(_, value) { stored = value; } },
    setInterval() {}
  });
  vm.runInContext(source, context);
  return { run: code => vm.runInContext(code, context), element, listeners, stored: () => stored };
}
const response = data => ({ ok: true, json: async () => data });
const settle = () => new Promise(resolve => setImmediate(resolve));

test("visual preferences persist while collapse preserves view, filters and selection", () => {
  const h = harness(async () => response({}));
  h.run('setupVisualPreferences(); state.activeTab="apps"; state.selectedApps.add("fixture"); state.appCategoryFilter="Tools";');
  h.element("main-view").innerHTML = "existing content";
  h.element("main-view").scrollTop = 123;
  h.element("btn-sidebar-toggle").handlers.click();
  assert.equal(h.element("html")["data-sidebar"], "compact");
  assert.equal(h.element("btn-sidebar-toggle")["aria-expanded"], "false");
  assert.equal(h.element("main-view").innerHTML, "existing content");
  assert.equal(h.element("main-view").scrollTop, 123);
  assert.equal(h.run('state.activeTab === "apps" && state.appCategoryFilter === "Tools" && state.selectedApps.has("fixture")'), true);
  h.element("btn-motion-toggle").handlers.click();
  assert.equal(h.run("motionAllowed()"), false);
  const restored = harness(async () => response({}), h.stored());
  restored.run("setupVisualPreferences()");
  assert.equal(restored.element("html")["data-sidebar"], "compact");
  assert.equal(restored.element("html")["data-motion"], "reduced");
});

test("corrupt preferences fall back and OS reduced motion wins", () => {
  const h = harness(async () => response({}), "{broken");
  h.run("setupVisualPreferences()");
  assert.equal(h.element("html")["data-sidebar"], "expanded");
  h.run('window.matchMedia = () => ({matches:true});');
  assert.equal(h.run("motionAllowed()"), false);
});

test("smooth wheel traverses intermediate positions, reverses and cancels", () => {
  const h = harness(async () => response({}));
  h.run(`
    var frames = new Map(), frameId = 0, now = 0;
    globalThis.performance = {now: () => now};
    globalThis.requestAnimationFrame = callback => { frames.set(++frameId, callback); return frameId; };
    globalThis.cancelAnimationFrame = id => frames.delete(id);
    var pane = document.getElementById("scroll-test");
    pane.scrollHeight = 2000; pane.clientHeight = 400;
    setupSmoothScroll(pane);
    function wheel(delta) { var e = {deltaY: delta, deltaMode: 0, preventDefault() { this.prevented = true; }}; pane.handlers.wheel(e); return !!e.prevented; }
    function advance(time) { now = time; var pending = [...frames.values()]; frames.clear(); pending.forEach(callback => callback(time)); }
  `);
  assert.equal(h.run("wheel(120)"), true);
  h.run("advance(130)");
  assert.ok(h.element("scroll-test").scrollTop > 0 && h.element("scroll-test").scrollTop < 120);
  h.run("wheel(-120); advance(390)");
  assert.equal(h.element("scroll-test").scrollTop, 0);
  h.run("wheel(99999); advance(650)");
  assert.equal(h.element("scroll-test").scrollTop, 1600);
  h.run("wheel(-120); pane.handlers.pointerdown()");
  assert.equal(h.run("frames.size"), 0);
  h.run("visualPreferences.reduceMotion = true");
  assert.equal(h.run("wheel(-120)"), false);
});

test("cards keep consequences outside disclosure and use catalog short copy", () => {
  const h = harness(async () => response({}));
  const html = h.run('renderTweakCard({id:"test", title:"Technical title", shortTitle:"Short title", summary:"Short summary", description:"Full description", riskLevel:"high", riskWarning:"Visible consequence", undoOps:[{}]})');
  assert.match(html, /Short title/);
  assert.match(html, /Short summary/);
  assert.ok(html.indexOf("Visible consequence") > html.indexOf("</details>"));
  assert.match(html, /<summary>Ver efecto y reversión/);
});

test("card cascade staggers entries and reduced motion cancels every pending animation", () => {
  const h = harness(async () => response({}));
  h.run(`
    var started = [], cancelled = 0;
    document.querySelectorAll = () => [0,1,2].map(() => ({
      animate(keyframes, options) { started.push(options); return {cancel() { cancelled++; }}; }
    }));
    revealCards();
  `);
  assert.equal(h.run("started[0].delay"), 0);
  assert.equal(h.run("started[1].delay"), 45);
  assert.equal(h.run("started[2].duration"), 540);
  h.run("visualPreferences.reduceMotion = true; applyVisualPreferences()");
  assert.equal(h.run("cancelled"), 3);
  assert.equal(h.run("cardAnimations.size"), 0);
});

test("startup renders immediately and defers four unrelated scans", async () => {
  const calls = [];
  const h = harness(async url => { calls.push(url); return response(url.endsWith("scan_hardware") ? hardwareFixture : { tweaks: [], apps: [], categories: [] }); });
  h.run("setupVisualPreferences = setupNavigation = setupThemeToggle = setupOmnibox = setupModalsAndQuickButtons = () => {}; renderActiveTab = () => {};");
  await h.listeners.DOMContentLoaded();
  await settle();
  assert.deepEqual(calls.sort(), ["/api/get_catalogs", "/api/scan_hardware"]);
});

test("concurrent reads share one request and progress lasts until completion", async () => {
  let resolve;
  let calls = 0;
  const h = harness(() => { calls++; return new Promise(done => { resolve = done; }); });
  const first = h.run('invokeBackend("check_installed_apps")');
  const second = h.run('invokeBackend("check_installed_apps")');
  assert.equal(first, second);
  await settle();
  assert.equal(calls, 1);
  assert.equal(h.element("operation-rail").classList.contains("hidden"), false);
  resolve(response({}));
  await first;
  assert.equal(h.element("operation-rail").classList.contains("hidden"), true);
});

test("failed reads show a contextual retry card and successful retry clears it", async () => {
  let calls = 0;
  const h = harness(async () => ++calls === 1 ? { ok: false, text: async () => "WMI denied" } : response({}));
  h.run('state.activeTab = "apps"; renderActiveTab = () => {};');
  await h.run("refreshInstalledApps()");
  assert.equal(h.element("error-panel").classList.contains("hidden"), false);
  assert.match(h.element("error-detail").textContent, /WMI denied/);
  await h.element("btn-error-retry").onclick();
  assert.equal(calls, 2);
  assert.equal(h.element("error-panel").classList.contains("hidden"), true);
});

test("system failure is not treated as success and never offers automatic mutation retry", async () => {
  const h = harness(async () => response({ success: false, stderr: "Access denied" }));
  await assert.rejects(h.run('invokeBackend("execute_tweak_commands", {commands: []})'), /Access denied/);
  assert.equal(h.element("btn-error-retry").classList.contains("hidden"), true);
});

test("dismissing a read failure does not silently retry when revisiting its module", async () => {
  let calls = 0;
  const h = harness(async () => { calls++; throw new Error("WMI unavailable"); });
  await h.run("refreshHardwareAudit()");
  h.element("btn-error-dismiss").onclick();
  h.run("ensureTabData()");
  await settle();
  assert.equal(calls, 1);
  assert.equal(h.element("error-panel").classList.contains("hidden"), true);
  assert.match(h.run("renderHardwareView()"), /Reintentar auditoría/);
});

test("an incomplete hardware response stays unverified and produces a retry error", async () => {
  const h = harness(async () => response({ chassis: {} }));
  await h.run("refreshHardwareAudit()");
  assert.equal(h.run('loadedModules.has("scan_hardware")'), false);
  assert.equal(h.run("state.hardware"), null);
  assert.match(h.element("error-detail").textContent, /incompleto/);
});

test("all modules render local catalog fixtures and auto profile waits for hardware", async () => {
  const h = harness(async () => response({}));
  const tweaks = JSON.parse(await readFile(new URL("../catalogs/tweaks.json", import.meta.url), "utf8"));
  const apps = JSON.parse(await readFile(new URL("../catalogs/apps.json", import.meta.url), "utf8"));
  h.run(`state.tweaks = ${JSON.stringify(tweaks.tweaks)}; state.apps = ${JSON.stringify(apps.apps)}; state.categories = ["Todas"]; loadedModules.add("get_catalogs"); loadedModules.add("check_all_tweaks");`);
  const waiting = h.run('renderTweaksView("optimization")');
  assert.match(waiting, /Esperando hardware/);
  assert.match(waiting, /id="btn-apply-recommended-profile"[^>]+disabled/);
  h.run(`state.hardware = ${JSON.stringify(hardwareFixture)};`);
  for (const module of ["hardware", "optimization", "debloat", "apps", "monitor", "safety"]) {
    h.run(`state.activeTab = "${module}"; renderActiveTab();`);
    assert.match(h.element("main-view").innerHTML, /module-heading/);
    assert.ok(h.element("main-view").innerHTML.length > 500);
  }
});

test("batch progress uses completed item counts and unknown tweak states stay unknown", () => {
  const h = harness(async () => response({}));
  h.run('setBatchProgress("Test", 1, 3)');
  assert.equal(h.element("operation-rail-progress").value, 1);
  assert.equal(h.element("operation-rail-progress").max, 3);
  const html = h.run('renderTweakCard({id:"test", title:"Test", subcategory:"Test", description:"Test", riskLevel:"low"})');
  assert.match(html, /Pendiente de verificar/);
  assert.match(html, /data-id="test" disabled/);
});

test("tweak writes require catalogs, state verification, and hardware together", () => {
  const h = harness(async () => response({}));
  const card = 'renderTweakCard({id:"test", title:"Test", subcategory:"Test", description:"Test", riskLevel:"low"})';
  for (const [catalog, states, hardware] of [[false, true, true], [true, false, true], [true, true, false]]) {
    h.run(`loadedModules.clear(); ${catalog ? 'loadedModules.add("get_catalogs");' : ""} ${states ? 'loadedModules.add("check_all_tweaks");' : ""} state.hardware = ${hardware ? JSON.stringify(hardwareFixture) : "null"};`);
    assert.match(h.run(card), /data-id="test" disabled/);
  }
  h.run(`loadedModules.add("get_catalogs"); loadedModules.add("check_all_tweaks"); state.hardware = ${JSON.stringify(hardwareFixture)};`);
  assert.doesNotMatch(h.run(card), /data-id="test" disabled/);
});

test("preview server serves every read fixture and rejects all system changes", async () => {
  const server = createPreviewServer({ delay: 0, errorOnce: true });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    assert.equal((await fetch(base)).status, 200);
    for (const cmd of ["execute_tweak_commands", "install_app", "create_restore_point", "perform_ota_update", "kill_script_process"]) {
      assert.equal((await fetch(`${base}/api/${cmd}`, { method: "POST" })).status, 403);
    }
    assert.equal((await fetch(`${base}/api/scan_hardware`, { method: "POST" })).status, 503);
    for (const cmd of ["get_catalogs", "scan_hardware", "check_all_tweaks", "check_installed_apps", "get_live_scripts", "get_restore_points"]) {
      const res = await fetch(`${base}/api/${cmd}`, { method: "POST" });
      assert.equal(res.status, 200);
      assert.equal(typeof await res.json(), "object");
    }
  } finally { await new Promise(resolve => server.close(resolve)); }
});
