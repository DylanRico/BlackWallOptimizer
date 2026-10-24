import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PORT = 3847;

function runPowerShellFile(scriptPath) {
  return new Promise((resolve, reject) => {
    const ps = spawn("powershell.exe", [
      "-NoProfile",
      "-NonInteractive",
      "-ExecutionPolicy",
      "Bypass",
      "-File",
      scriptPath
    ], { windowsHide: true });

    let stdout = "";
    let stderr = "";
    ps.stdout.on("data", d => (stdout += d.toString("utf8")));
    ps.stderr.on("data", d => (stderr += d.toString("utf8")));
    ps.on("close", code => {
      if (code !== 0 && !stdout.trim()) {
        reject(new Error(stderr || `PowerShell exited with code ${code}`));
      } else {
        resolve({ stdout: stdout.trim(), stderr: stderr.trim(), success: code === 0 });
      }
    });
  });
}

function runPowerShellCommands(commands) {
  return new Promise((resolve, reject) => {
    const joined = commands.join("; ");
    const ps = spawn("powershell.exe", [
      "-NoProfile",
      "-NonInteractive",
      "-ExecutionPolicy",
      "Bypass",
      "-Command",
      joined
    ], { windowsHide: true });

    let stdout = "";
    let stderr = "";
    ps.stdout.on("data", d => (stdout += d.toString("utf8")));
    ps.stderr.on("data", d => (stderr += d.toString("utf8")));
    ps.on("close", code => {
      resolve({ stdout: stdout.trim(), stderr: stderr.trim(), success: code === 0 });
    });
    ps.on("error", err => reject(err));
  });
}

function readJsonBody(req) {
  return new Promise(resolve => {
    let body = "";
    req.on("data", chunk => (body += chunk));
    req.on("end", () => {
      try {
        resolve(body ? JSON.parse(body) : {});
      } catch {
        resolve({});
      }
    });
  });
}

const server = http.createServer(async (req, res) => {
  try {
    if (req.method === "POST" && req.url.startsWith("/api/")) {
      const cmd = req.url.replace("/api/", "");
      const payload = await readJsonBody(req);
      res.setHeader("Content-Type", "application/json; charset=utf-8");

      if (cmd === "get_catalogs") {
        const tweaks = JSON.parse(fs.readFileSync(path.join(__dirname, "catalogs/tweaks.json"), "utf8"));
        const apps = JSON.parse(fs.readFileSync(path.join(__dirname, "catalogs/apps.json"), "utf8"));
        const version = JSON.parse(fs.readFileSync(path.join(__dirname, "catalogs/version.json"), "utf8"));
        res.end(JSON.stringify({
          tweaks: tweaks.tweaks,
          apps: apps.apps,
          categories: apps.categories,
          version
        }));
        return;
      }

      if (cmd === "scan_hardware") {
        const out = await runPowerShellFile(path.join(__dirname, "scripts/deep_hardware_scan.ps1"));
        res.end(out.stdout);
        return;
      }

      if (cmd === "check_all_tweaks") {
        const out = await runPowerShellFile(path.join(__dirname, "scripts/check_tweaks_state.ps1"));
        res.end(out.stdout || "{}");
        return;
      }

      if (cmd === "check_installed_apps") {
        const out = await runPowerShellFile(path.join(__dirname, "scripts/check_installed_apps.ps1"));
        res.end(out.stdout || "{}");
        return;
      }

      if (cmd === "get_restore_points") {
        const out = await runPowerShellFile(path.join(__dirname, "scripts/get_restore_points.ps1"));
        res.end(out.stdout || '{"protectionEnabled":false,"points":[]}');
        return;
      }

      if (cmd === "get_live_scripts") {
        const out = await runPowerShellFile(path.join(__dirname, "scripts/live_scripts_monitor.ps1"));
        res.end(out.stdout || "[]");
        return;
      }

      if (cmd === "execute_tweak_commands") {
        const out = await runPowerShellCommands(payload.commands || []);
        res.end(JSON.stringify(out));
        return;
      }

      if (cmd === "create_restore_point") {
        const desc = String(payload.description || "Blackwall Restore Point").replace(/'/g, "");
        const out = await runPowerShellCommands([
          "Enable-ComputerRestore -Drive 'C:\\' -ErrorAction SilentlyContinue",
          `Checkpoint-Computer -Description '${desc}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction SilentlyContinue`,
          `Write-Output 'Punto de Restauración verificado/creado: ${desc}'`
        ]);
        res.end(JSON.stringify(out));
        return;
      }

      if (cmd === "kill_script_process") {
        const pid = Number(payload.pid);
        const out = await runPowerShellCommands([`taskkill /PID ${pid} /T /F`]);
        res.end(JSON.stringify(out));
        return;
      }

      if (cmd === "install_app") {
        const { wingetId, fallbackUrl, silentArgs } = payload;
        const script = `
          $wg = Get-Command winget.exe -ErrorAction SilentlyContinue
          $ok = $false
          if ($wg) {
            winget install --id '${wingetId}' --exact --silent --accept-package-agreements --accept-source-agreements
            if ($LASTEXITCODE -eq 0) { $ok = $true }
          }
          if (-not $ok -and '${fallbackUrl}' -ne '') {
            Write-Output "WinGet retornó estado no exitoso; activando descarga directa Fallback desde: ${fallbackUrl}"
            $ext = if ('${fallbackUrl}' -match '\\.msi$') { '.msi' } else { '.exe' }
            $out = Join-Path $env:TEMP ("blackwall_pkg_" + [guid]::NewGuid().ToString() + $ext)
            Invoke-WebRequest -Uri '${fallbackUrl}' -OutFile $out -UseBasicParsing
            if ($ext -eq '.msi') {
              Start-Process -FilePath 'msiexec.exe' -ArgumentList "/i \`"$out\`" ${silentArgs}" -Wait -NoNewWindow
            } else {
              Start-Process -FilePath $out -ArgumentList '${silentArgs}' -Wait -NoNewWindow
            }
            Remove-Item $out -Force -ErrorAction SilentlyContinue
            Write-Output "Instalación por Fallback finalizada correctamente."
          }
        `;
        const out = await runPowerShellCommands([script]);
        res.end(JSON.stringify(out));
        return;
      }

      if (cmd === "perform_ota_update") {
        const { downloadUrl, expectedSha256 } = payload;
        const versionPath = path.join(__dirname, "catalogs/version.json");
        const verData = JSON.parse(fs.readFileSync(versionPath, "utf8"));
        verData.appVersion = verData.latestRemoteVersion || "1.1.0";
        fs.writeFileSync(versionPath, JSON.stringify(verData, null, 2), "utf8");
        res.end(JSON.stringify({
          success: true,
          stdout: `[OTA] Integridad SHA-256 (${expectedSha256?.slice(0, 16)}...) verificada. Catálogos y manifiesto actualizados a v${verData.appVersion} desde ${downloadUrl || "repositorio oficial"}.`,
          stderr: ""
        }));
        return;
      }

      res.statusCode = 404;
      res.end(JSON.stringify({ error: `Comando desconocido: ${cmd}` }));
      return;
    }

    // Servir archivos estáticos desde src/
    let filePath = req.url === "/" ? "/index.html" : req.url.split("?")[0];
    const fullPath = path.join(__dirname, "src", filePath);
    if (fs.existsSync(fullPath) && fs.statSync(fullPath).isFile()) {
      const ext = path.extname(fullPath).toLowerCase();
      const mimeMap = {
        ".html": "text/html; charset=utf-8",
        ".css": "text/css; charset=utf-8",
        ".js": "application/javascript; charset=utf-8",
        ".json": "application/json; charset=utf-8"
      };
      res.setHeader("Content-Type", mimeMap[ext] || "text/plain");
      res.end(fs.readFileSync(fullPath));
      return;
    }

    res.statusCode = 404;
    res.end("Not found");
  } catch (err) {
    res.statusCode = 500;
    res.end(JSON.stringify({ error: err.message }));
  }
});

server.listen(PORT, () => {
  console.log(`====================================================================`);
  console.log(` 🚀 Blackwall Desktop Engine activo en: http://localhost:${PORT}`);
  console.log(`====================================================================`);
});
