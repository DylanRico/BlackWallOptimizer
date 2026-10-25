import http from "node:http";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { hardwareFixture } from "../tests/ui-fixtures.mjs";

const root = new URL("../", import.meta.url);

export function createPreviewServer({ delay = 350, errorOnce = false } = {}) {
  let failHardware = errorOnce;
  return http.createServer(async (req, res) => {
    try {
      const pathname = new URL(req.url, "http://localhost").pathname;
      if (req.method === "POST" && pathname.startsWith("/api/")) {
        const command = pathname.slice(5);
        const readers = {
          get_catalogs: async () => {
            const [tweaks, apps, version] = await Promise.all(["tweaks", "apps", "version"].map(async name => JSON.parse(await readFile(new URL(`catalogs/${name}.json`, root), "utf8"))));
            return { tweaks: tweaks.tweaks, apps: apps.apps, categories: apps.categories, version };
          },
          scan_hardware: () => hardwareFixture,
          check_all_tweaks: () => ({}),
          check_installed_apps: () => ({}),
          get_live_scripts: () => [],
          get_restore_points: () => ({ protectionEnabled: false, points: [] })
        };
        if (!Object.hasOwn(readers, command)) {
          res.writeHead(403).end("Vista previa: las operaciones que modifican el sistema están bloqueadas.");
          return;
        }
        await new Promise(resolve => setTimeout(resolve, delay));
        if (command === "scan_hardware" && failHardware) {
          failHardware = false;
          res.writeHead(503).end("WMI_TEST: fallo simulado para verificar Intentar de nuevo.");
          return;
        }
        res.writeHead(200, { "Content-Type": "application/json" }).end(JSON.stringify(await readers[command]()));
        return;
      }
      const files = { "/": ["src/index.html", "text/html"], "/index.html": ["src/index.html", "text/html"], "/app.js": ["src/app.js", "text/javascript"], "/styles.css": ["src/styles.css", "text/css"] };
      const file = files[pathname];
      if (req.method !== "GET" || !file) { res.writeHead(404).end(); return; }
      res.writeHead(200, { "Content-Type": `${file[1]}; charset=utf-8` }).end(await readFile(new URL(file[0], root)));
    } catch (error) {
      res.writeHead(500).end(error.message);
    }
  });
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const server = createPreviewServer({ errorOnce: process.argv.includes("--error-once") });
  server.listen(3848, "127.0.0.1", () => console.log("Blackwall UI preview: http://127.0.0.1:3848 · synthetic fixtures, all system changes blocked"));
}
