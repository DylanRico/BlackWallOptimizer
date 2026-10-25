// ============================================================================
// Blackwall — Frontend Controller (Compatible con Tauri v2 Native IPC + Bridge)
// ============================================================================

const state = {
  activeTab: "hardware",
  theme: "dark",
  hardware: null,
  tweaks: [],
  tweakStates: {},
  apps: [],
  installedApps: {},
  categories: [],
  selectedApps: new Set(),
  version: null,
  liveScripts: [],
  restoreInfo: { protectionEnabled: null, points: [] },
  consoleLogs: [
    "[SISTEMA] Interfaz Blackwall iniciada.",
    "[SEGURIDAD] Respaldo previo solicitado antes de aplicar optimizaciones."
  ],
  optDeviceFilter: "auto", // auto | laptop | desktop | all
  optRiskFilter: "all",    // all | low | medium | high
  appCategoryFilter: "Todas",
  autoRestoreBeforeTweak: true
};

const pendingRequests = new Map();
const loadedModules = new Set();
const operationErrors = new Map();
const dismissedErrors = new Set();
const activeOperations = new Map();
const readOperations = {
  get_catalogs: { label: "Cargando catálogos", tab: null, retry: loadCatalogs },
  scan_hardware: { label: "Leyendo hardware · WMI / SMBIOS", tab: "hardware", retry: refreshHardwareAudit },
  check_all_tweaks: { label: "Verificando Registro y servicios", tab: "optimization", retry: refreshTweaksState },
  check_installed_apps: { label: "Detectando aplicaciones instaladas", tab: "apps", retry: refreshInstalledApps },
  get_live_scripts: { label: "Consultando procesos activos", tab: "monitor", retry: refreshLiveScripts },
  get_restore_points: { label: "Consultando protección del sistema", tab: "safety", retry: refreshRestorePoints }
};
let operationSequence = 0;
let batchProgress = null;

// Puente híbrido: usa Tauri v2 si corre dentro del .exe compilado en Rust, o el bridge nativo local
function invokeBackend(cmd, payload = {}) {
  const operation = readOperations[cmd];
  if (operation && pendingRequests.has(cmd)) return pendingRequests.get(cmd);
  dismissedErrors.delete(cmd);
  const token = ++operationSequence;
  activeOperations.set(token, operation?.label || "Ejecutando operación solicitada");
  updateOperationFeedback();
  const request = (async () => {
    await Promise.resolve();
    try {
      let data;
      if (window.__TAURI__?.core) {
        data = await window.__TAURI__.core.invoke(cmd, payload);
      } else {
        const res = await fetch(`/api/${cmd}`, {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload)
        });
        if (!res.ok) throw new Error((await res.text()) || `Error HTTP ${res.status}`);
        data = await res.json();
      }
      if (data?.success === false) throw new Error(data.stderr || data.stdout || "El sistema informó que la operación falló.");
      if (cmd === "scan_hardware" && (!data?.chassis || !data?.cpu)) throw new Error("El informe de hardware está incompleto.");
      if (operation) loadedModules.add(cmd);
      operationErrors.delete(cmd);
      return data;
    } catch (error) {
      operationErrors.set(cmd, { message: error.message || String(error), operation });
      throw error;
    } finally {
      activeOperations.delete(token);
      pendingRequests.delete(cmd);
      updateOperationFeedback();
    }
  })();
  if (operation) pendingRequests.set(cmd, request);
  return request;
}

function updateOperationFeedback() {
  const rail = document.getElementById("operation-rail");
  if (rail) {
    rail.classList.toggle("hidden", !activeOperations.size && !batchProgress);
    document.getElementById("operation-label").textContent = batchProgress?.label || [...activeOperations.values()].join(" · ");
    document.getElementById("operation-count").textContent = batchProgress ? `${batchProgress.completed}/${batchProgress.total} completadas` : `${activeOperations.size} en curso`;
    const progress = rail.querySelector("progress");
    if (batchProgress) {
      progress.max = batchProgress.total;
      progress.value = batchProgress.completed;
    } else {
      progress.removeAttribute("value");
    }
  }
  const panel = document.getElementById("error-panel");
  if (!panel) return;
  const entry = [...operationErrors.entries()].find(([command, error]) =>
    !dismissedErrors.has(command) && (!error.operation?.tab || error.operation.tab === state.activeTab ||
    (error.operation.tab === "optimization" && state.activeTab === "debloat")));
  panel.classList.toggle("hidden", !entry);
  if (!entry) return;
  const [command, error] = entry;
  document.getElementById("error-title").textContent = error.operation?.label || "La operación no se completó";
  document.getElementById("error-detail").textContent = `${command} · ${error.message}`;
  const retry = document.getElementById("btn-error-retry");
  retry.classList.toggle("hidden", !error.operation);
  retry.disabled = pendingRequests.has(command);
  retry.onclick = () => error.operation.retry();
  document.getElementById("btn-error-dismiss").onclick = () => {
    dismissedErrors.add(command);
    updateOperationFeedback();
  };
}

function ensureTabData() {
  const commands = {
    hardware: ["scan_hardware"], optimization: ["check_all_tweaks"],
    debloat: ["check_all_tweaks"], apps: ["check_installed_apps"],
    monitor: ["get_live_scripts"], safety: ["get_restore_points"]
  };
  for (const command of commands[state.activeTab] || []) {
    if (!loadedModules.has(command) && !pendingRequests.has(command) && !operationErrors.has(command)) {
      readOperations[command].retry();
    }
  }
}

function setBatchProgress(label, completed = 0, total = 0) {
  batchProgress = label && total ? { label, completed, total } : null;
  updateOperationFeedback();
}

function appendLog(msg, type = "INFO") {
  const ts = new Date().toLocaleTimeString();
  state.consoleLogs.push(`[${ts}] [${type}] ${msg}`);
  const box = document.getElementById("internal-console-box");
  if (box) {
    box.textContent = state.consoleLogs.join("\n");
    box.scrollTop = box.scrollHeight;
  }
}

function setStatus(text, taskText = "Sin tareas en cola") {
  const ind = document.getElementById("status-indicator");
  const tsk = document.getElementById("active-task-label");
  if (ind) ind.textContent = text;
  if (tsk) tsk.textContent = taskText;
}

// Inicialización general
window.addEventListener("DOMContentLoaded", async () => {
  setupVisualPreferences();
  setupNavigation();
  setupThemeToggle();
  setupOmnibox();
  setupModalsAndQuickButtons();

  renderActiveTab();
  loadCatalogs();
  ensureTabData();

  setInterval(() => {
    if (state.activeTab === "monitor" && !document.hidden && !operationErrors.has("get_live_scripts")) {
      refreshLiveScripts();
    }
  }, 4000);
});

async function loadCatalogs() {
  try {
    const data = await invokeBackend("get_catalogs");
    state.tweaks = data.tweaks || [];
    state.apps = data.apps || [];
    state.categories = ["Todas", ...(data.categories || [])];
    state.version = data.version || null;

    document.getElementById("badge-opt-count").textContent =
      state.tweaks.filter(t => t.category === "optimization").length;
    document.getElementById("badge-debloat-count").textContent =
      state.tweaks.filter(t => t.category === "debloat").length;
    document.getElementById("badge-apps-count").textContent = state.apps.length;
    if (state.version?.appVersion) {
      document.getElementById("app-version").textContent = `v${state.version.appVersion} / PORTABLE`;
      document.getElementById("btn-ota-check").textContent = "Actualizaciones";
    }
    renderActiveTab();
  } catch (err) {
    appendLog(`Error cargando catálogos: ${err.message}`, "ERROR");
  }
}

async function refreshTweaksState() {
  try {
    const states = await invokeBackend("check_all_tweaks");
    if (states && typeof states === "object") {
      state.tweakStates = states;
      const activeCount = Object.values(states).filter(Boolean).length;
      appendLog(`Estado de tweaks verificado en el sistema: ${activeCount}/${state.tweaks.length} ya activos.`);
      if (state.activeTab === "optimization" || state.activeTab === "debloat") {
        renderActiveTab();
      }
    }
  } catch (err) {
    appendLog(`Error evaluando estado inicial de tweaks: ${err.message}`, "WARN");
  }
}

async function refreshInstalledApps() {
  try {
    const map = await invokeBackend("check_installed_apps");
    if (map && typeof map === "object") {
      state.installedApps = map;
      const instCount = Object.values(map).filter(x => x && x.installed).length;
      appendLog(`Catálogo de aplicaciones auditado: ${instCount}/${state.apps.length} ya instaladas en este PC.`);
      if (state.activeTab === "apps") {
        renderActiveTab();
      }
    }
  } catch (err) {
    appendLog(`Error detectando aplicaciones instaladas: ${err.message}`, "WARN");
  }
}

async function refreshRestorePoints() {
  try {
    const rp = await invokeBackend("get_restore_points");
    if (rp && typeof rp === "object") {
      state.restoreInfo = {
        protectionEnabled: !!rp.protectionEnabled,
        points: Array.isArray(rp.points) ? rp.points : (rp.points ? [rp.points] : [])
      };
      if (state.activeTab === "safety") {
        renderActiveTab();
      }
    }
  } catch (err) {
    appendLog(`Aviso al leer puntos de restauración: ${err.message}`, "WARN");
  }
}

async function refreshHardwareAudit() {
  setStatus(" Escaneando hardware (WMI / SMBIOS / S.M.A.R.T. / Batería)...", "Auditoría en curso...");
  appendLog("Iniciando escaneo profundo de hardware mediante CIM/WMI/SMBIOS...");
  try {
    const hw = await invokeBackend("scan_hardware");
    state.hardware = hw;
    const pill = document.getElementById("device-badge-pill");
    if (pill && hw && hw.chassis) {
      pill.textContent = hw.chassis.isLaptop
        ? ` Laptop: ${hw.chassis.manufacturer} ${hw.chassis.model}`
        : ` Desktop: ${hw.chassis.manufacturer} ${hw.chassis.model}`;
    }
    appendLog(`Auditoría completada: ${hw.chassis.deviceType} (${hw.cpu.name})`);
    setStatus(" Hardware auditado en tiempo real", "Listo");
    if (["hardware", "optimization", "debloat"].includes(state.activeTab)) {
      renderActiveTab();
    }
  } catch (err) {
    appendLog(`Error en auditoría de hardware: ${err.message}`, "ERROR");
    setStatus(" Error parcial en consulta WMI", "Listo");
  }
}

async function refreshLiveScripts() {
  try {
    const list = await invokeBackend("get_live_scripts");
    state.liveScripts = Array.isArray(list) ? list : (list ? [list] : []);
    const badge = document.getElementById("badge-scripts-count");
    if (badge) badge.textContent = `${state.liveScripts.length} Activos`;
    if (state.activeTab === "monitor") {
      const container = document.getElementById("live-scripts-table-body");
      if (container) container.innerHTML = renderLiveScriptsRows();
    }
  } catch (err) {
    appendLog(`Error al consultar procesos de scripts: ${err.message}`, "WARN");
  }
}

// ============================================================================
// NAVEGACIÓN Y TEMA CLARO / OSCURO
// ============================================================================

function setupNavigation() {
  document.querySelectorAll(".nav-btn").forEach(btn => {
    btn.addEventListener("click", () => {
      switchTab(btn.dataset.tab);
    });
  });

  document.getElementById("btn-jump-console").addEventListener("click", () => {
    switchTab("monitor");
  });
}

function setupThemeToggle() {
  const btn = document.getElementById("btn-theme-toggle");
  btn.addEventListener("click", () => {
    state.theme = state.theme === "dark" ? "light" : "dark";
    document.documentElement.setAttribute("data-theme", state.theme);
    btn.textContent = state.theme === "dark" ? "Modo claro" : "Modo oscuro";
    saveVisualPreferences();
  });
}

const visualPreferences = { sidebarCollapsed: false, reduceMotion: false };
let pageAnimation, sidebarAnimation;
let cardObserver;
const cardAnimations = new Set();

function revealCards() {
  cardObserver?.disconnect();
  for (const animation of cardAnimations) animation.cancel();
  cardAnimations.clear();
  if (!motionAllowed()) return;
  const cards = document.querySelectorAll("#main-view .card, #main-view .device-banner");
  const reveal = (card, index) => {
    const animation = card.animate?.([
      { opacity: 0, transform: "perspective(1000px) translateY(32px) rotateX(5deg) scale(.985)" },
      { opacity: 1, transform: "perspective(1000px) translateY(-2px) rotateX(0) scale(1)", offset: .8 },
      { opacity: 1, transform: "none" }
    ], { duration: 540, delay: Math.min(index * 45, 360), easing: "cubic-bezier(.16,1,.3,1)", fill: "backwards" });
    if (animation) {
      cardAnimations.add(animation);
      animation.finished?.then(() => cardAnimations.delete(animation), () => cardAnimations.delete(animation));
    }
  };
  if (typeof IntersectionObserver === "undefined") { cards.forEach(reveal); return; }
  cardObserver = new IntersectionObserver(entries => {
    let index = 0;
    for (const entry of entries) if (entry.isIntersecting) {
      reveal(entry.target, index++);
      cardObserver.unobserve(entry.target);
    }
  }, { root: document.getElementById("main-view"), threshold: .08 });
  cards.forEach(card => cardObserver.observe(card));
}
function motionAllowed() {
  return !visualPreferences.reduceMotion && !window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
}

function saveVisualPreferences() {
  try { localStorage.setItem("blackwall-ui", JSON.stringify({ ...visualPreferences, theme: state.theme })); }
  catch { /* The current session remains usable when storage is unavailable. */ }
}

function applyVisualPreferences() {
  const root = document.documentElement;
  root.setAttribute("data-sidebar", visualPreferences.sidebarCollapsed ? "compact" : "expanded");
  root.setAttribute("data-motion", visualPreferences.reduceMotion ? "reduced" : "full");
  root.setAttribute("data-theme", state.theme);
  const menu = document.getElementById("btn-sidebar-toggle");
  menu.setAttribute("aria-expanded", String(!visualPreferences.sidebarCollapsed));
  menu.setAttribute("aria-label", visualPreferences.sidebarCollapsed ? "Expandir menú" : "Contraer menú");
  menu.title = visualPreferences.sidebarCollapsed ? "Expandir menú" : "Contraer menú";
  const motion = document.getElementById("btn-motion-toggle");
  motion.textContent = visualPreferences.sidebarCollapsed ? "≈" : (visualPreferences.reduceMotion ? "Movimiento reducido" : "Animaciones activas");
  motion.setAttribute("aria-pressed", String(visualPreferences.reduceMotion));
  motion.title = visualPreferences.reduceMotion ? "Activar animaciones" : "Reducir movimiento";
  motion.setAttribute("aria-label", motion.title);
  document.getElementById("btn-theme-toggle").textContent = state.theme === "dark" ? "Modo claro" : "Modo oscuro";
  if (!motionAllowed()) {
    pageAnimation?.cancel(); sidebarAnimation?.cancel();
    cardObserver?.disconnect();
    for (const animation of cardAnimations) animation.cancel();
    cardAnimations.clear();
  }
}

function setupVisualPreferences() {
  try {
    const saved = JSON.parse(localStorage.getItem("blackwall-ui"));
    if (saved && typeof saved === "object") {
      visualPreferences.sidebarCollapsed = saved.sidebarCollapsed === true;
      visualPreferences.reduceMotion = saved.reduceMotion === true;
      state.theme = saved.theme === "light" ? "light" : "dark";
    }
  } catch { /* Ignore corrupt or unavailable preferences. */ }
  applyVisualPreferences();
  document.getElementById("btn-sidebar-toggle").addEventListener("click", () => {
    visualPreferences.sidebarCollapsed = !visualPreferences.sidebarCollapsed;
    applyVisualPreferences();
    saveVisualPreferences();
    const sidebar = document.querySelector(".sidebar");
    sidebarAnimation?.cancel();
    if (motionAllowed()) sidebarAnimation = sidebar.animate?.([{ opacity: 0.65, transform: "translateY(6px)" }, { opacity: 1, transform: "none" }], { duration: 220, easing: "ease-out" });
  });
  document.getElementById("btn-motion-toggle").addEventListener("click", () => {
    visualPreferences.reduceMotion = !visualPreferences.reduceMotion;
    applyVisualPreferences();
    saveVisualPreferences();
  });
  const updateActivity = () => document.documentElement.setAttribute("data-active", String(!document.hidden && document.hasFocus()));
  document.addEventListener("visibilitychange", updateActivity);
  window.addEventListener("focus", updateActivity);
  window.addEventListener("blur", updateActivity);
  window.matchMedia?.("(prefers-reduced-motion: reduce)").addEventListener("change", applyVisualPreferences);
  updateActivity();
  document.addEventListener("pointermove", event => {
    if (!motionAllowed()) return;
    const button = event.target.closest?.(".btn, .nav-btn");
    if (!button || button.disabled) return;
    const bounds = button.getBoundingClientRect();
    button.style.setProperty("--pointer-x", `${((event.clientX - bounds.left) / bounds.width - .5) * 4}px`);
    button.style.setProperty("--pointer-y", `${((event.clientY - bounds.top) / bounds.height - .5) * 4}px`);
  });
  document.querySelectorAll(".main-content, .sidebar, .omnibox-dropdown").forEach(setupSmoothScroll);
  document.querySelectorAll(".nav-btn").forEach(button => {
    button.title = button.querySelector(".nav-label").textContent;
    button.setAttribute("aria-label", button.title);
  });
}

function setupSmoothScroll(element) {
  let frame = 0, target = 0;
  const cancel = () => { cancelAnimationFrame(frame); frame = 0; };
  element.addEventListener("wheel", event => {
    if (!motionAllowed() || event.ctrlKey || event.shiftKey || event.deltaX || element.scrollHeight <= element.clientHeight) { cancel(); return; }
    const from = element.scrollTop;
    const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? element.clientHeight : 1);
    const origin = frame && delta * (target - from) >= 0 ? target : from;
    target = Math.max(0, Math.min(element.scrollHeight - element.clientHeight, origin + delta));
    cancel();
    if (target === from) return;
    event.preventDefault();
    const start = performance.now();
    const tick = now => {
      if (!motionAllowed()) { element.scrollTop = target; frame = 0; return; }
      const t = Math.min(1, (now - start) / 260);
      element.scrollTop = from + (target - from) * (1 - Math.pow(1 - t, 3));
      frame = t < 1 ? requestAnimationFrame(tick) : 0;
    };
    frame = requestAnimationFrame(tick);
  }, { passive: false });
  element.addEventListener("pointerdown", cancel);
  element.addEventListener("keydown", cancel);
  element.addEventListener("scrollcancel", cancel);
}

// ============================================================================
// BUSCADOR GLOBAL CENTRADO (OMNIBOX CTRL + K)
// ============================================================================

function setupOmnibox() {
  const input = document.getElementById("omnibox-input");
  const dropdown = document.getElementById("omnibox-results");

  window.addEventListener("keydown", e => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
      e.preventDefault();
      input.focus();
      input.select();
    } else if (e.key === "Escape") {
      dropdown.classList.add("hidden");
    }
  });

  input.addEventListener("input", () => {
    const q = input.value.trim().toLowerCase();
    if (!q) {
      dropdown.classList.add("hidden");
      return;
    }
    const matches = searchGlobalIndex(q);
    if (matches.length === 0) {
      dropdown.innerHTML = `<div style="padding:12px; color:var(--text-secondary); font-size:13px;">No se encontraron resultados para "<b>${escapeHtml(q)}</b>"</div>`;
    } else {
      dropdown.innerHTML = matches.map((m, idx) => `
        <div class="omnibox-item" data-idx="${idx}">
          <div>
            <div style="font-size:13px; font-weight:600;">${escapeHtml(m.title)}</div>
            <div style="font-size:11.5px; color:var(--text-secondary);">${escapeHtml(m.subtitle)}</div>
          </div>
          <span class="risk-badge ${m.badgeClass || 'risk-low'}">${escapeHtml(m.badge)}</span>
        </div>
      `).join("");

      dropdown.querySelectorAll(".omnibox-item").forEach(el => {
        el.addEventListener("click", () => {
          const item = matches[Number(el.dataset.idx)];
          dropdown.classList.add("hidden");
          input.value = "";
          item.onSelect();
        });
      });
    }
    dropdown.classList.remove("hidden");
  });

  document.addEventListener("click", e => {
    if (!e.target.closest(".omnibox-wrapper")) {
      dropdown.classList.add("hidden");
    }
  });
}

function searchGlobalIndex(q) {
  const results = [];

  if (state.hardware) {
    const hw = state.hardware;
    const hwEntries = [
      { title: `Serial BIOS / Service Tag: ${hw.chassis.serialNumber}`, subtitle: `${hw.chassis.manufacturer} ${hw.chassis.model} (${hw.chassis.deviceType})`, keywords: "serial numero serie bios service tag sku uuid chasis" },
      { title: `Placa Base: ${hw.motherboard.manufacturer} ${hw.motherboard.product}`, subtitle: `BIOS ${hw.motherboard.biosVersion} • ${hw.motherboard.bootMode} • ${hw.motherboard.tpmVersion}`, keywords: "placa base motherboard bios uefi tpm secure boot" },
      { title: `Procesador: ${hw.cpu.name}`, subtitle: `${hw.cpu.physicalCores} Núcleos / ${hw.cpu.logicalThreads} Hilos • Socket ${hw.cpu.socket}`, keywords: "cpu procesador ryzen intel nucleos hilos cache" },
      { title: `Memoria RAM: ${hw.memory.totalGb} GB ${hw.memory.memoryType} @ ${hw.memory.speedMhz} MHz`, subtitle: `${hw.memory.channelMode} • ${hw.memory.occupiedSlots}/${hw.memory.totalSlots} Slots ocupados`, keywords: "ram memoria ddr4 ddr5 slot dimm hynix samsung crucial kingston" },
      { title: `Batería: Salud ${hw.battery.healthPercent}% (Desgaste ${hw.battery.wearLevelPercent}%)`, subtitle: `Diseño: ${hw.battery.designedCapacityMwh} mWh vs Actual: ${hw.battery.fullChargedCapacityMwh} mWh`, keywords: "bateria wear desgaste salud carga mwh laptop" }
    ];
    for (const h of hwEntries) {
      if (h.title.toLowerCase().includes(q) || h.subtitle.toLowerCase().includes(q) || h.keywords.includes(q)) {
        results.push({
          title: h.title,
          subtitle: h.subtitle,
          badge: "Hardware",
          badgeClass: "risk-low",
          onSelect: () => switchTab("hardware")
        });
      }
    }
  }

  for (const t of state.tweaks) {
    const hay = `${t.title} ${t.description} ${t.subcategory} ${(t.tags || []).join(" ")}`.toLowerCase();
    if (hay.includes(q)) {
      results.push({
        title: t.title,
        subtitle: `${t.subcategory} — ${t.description.slice(0, 85)}...`,
        badge: t.riskLevel === "low" ? " Bajo" : t.riskLevel === "medium" ? " Medio" : " Alto",
        badgeClass: `risk-${t.riskLevel}`,
        onSelect: () => {
          switchTab(t.category === "optimization" ? "optimization" : "debloat");
          setTimeout(() => {
            const card = document.getElementById(`tweak-card-${t.id}`);
            if (card) card.scrollIntoView({ behavior: "smooth", block: "center" });
          }, 80);
        }
      });
    }
  }

  for (const a of state.apps) {
    const hay = `${a.name} ${a.description} ${a.category} ${a.wingetId} ${(a.tags || []).join(" ")}`.toLowerCase();
    if (hay.includes(q)) {
      const inst = state.installedApps[a.id]?.installed;
      results.push({
        title: ` ${a.name}`,
        subtitle: `${a.category} • WinGet ID: ${a.wingetId}`,
        badge: inst ? "✓ Instalado" : "Instalar",
        badgeClass: inst ? "risk-low" : "risk-medium",
        onSelect: () => {
          state.selectedApps.add(a.id);
          switchTab("apps");
        }
      });
    }
  }

  return results.slice(0, 10);
}

function switchTab(tabName) {
  const changed = state.activeTab !== tabName;
  state.activeTab = tabName;
  document.querySelectorAll(".nav-btn").forEach(b => {
    b.classList.toggle("active", b.dataset.tab === tabName);
    b.setAttribute("aria-current", b.dataset.tab === tabName ? "page" : "false");
  });
  renderActiveTab();
  ensureTabData();
  document.getElementById("main-view").scrollTop = 0;
  const main = document.getElementById("main-view");
  main.dispatchEvent?.(new Event("scrollcancel"));
  pageAnimation?.cancel();
  if (changed && motionAllowed()) pageAnimation = main.animate?.([{ opacity: 0.55, transform: "translateY(18px)" }, { opacity: 1, transform: "none" }], { duration: 360, easing: "cubic-bezier(.16,1,.3,1)" });
}

// ============================================================================
// RENDERIZADO CENTRAL POR PESTAÑA
// ============================================================================

function renderActiveTab() {
  const main = document.getElementById("main-view");
  switch (state.activeTab) {
    case "hardware":
      main.innerHTML = renderHardwareView();
      bindHardwareEvents();
      break;
    case "optimization":
      main.innerHTML = renderTweaksView("optimization");
      bindTweaksEvents();
      break;
    case "debloat":
      main.innerHTML = renderTweaksView("debloat");
      bindTweaksEvents();
      break;
    case "apps":
      main.innerHTML = renderAppsView();
      bindAppsEvents();
      break;
    case "monitor":
      main.innerHTML = renderMonitorView();
      bindMonitorEvents();
      break;
    case "safety":
      main.innerHTML = renderSafetyView();
      bindSafetyEvents();
      break;
  }
  const modules = {
    hardware: ["01", "Auditoría de hardware", "Conoce cada componente de tu sistema."],
    optimization: ["02", "Rendimiento adaptativo", "Control preciso sobre energía, latencia y servicios."],
    debloat: ["03", "Privacidad y debloat", "Elige qué se ejecuta y qué permanece en tu equipo."],
    apps: ["04", "Centro de software", "Tu catálogo de herramientas, en un solo lugar."],
    monitor: ["05", "Monitor de procesos", "Observa las tareas activas y consulta el registro de actividad."],
    safety: ["06", "Protección del sistema", "Verifica tus opciones de recuperación antes de modificar Windows."]
  };
  const [number, title, description] = modules[state.activeTab];
  main.insertAdjacentHTML("afterbegin", `<div class="module-heading"><div><h1>${title}</h1><p>${description}</p></div><span class="module-number" aria-hidden="true">${number}</span></div>`);
  updateOperationFeedback();
  revealCards();
}

// ----------------------------------------------------------------------------
// MÓDULO A: AUDITORÍA PROFUNDA DE HARDWARE Y EXPORTACIÓN
// ----------------------------------------------------------------------------
function renderHardwareView() {
  if (!state.hardware) {
    return `
      <div class="device-banner">
        <div>
          <div class="eyebrow">HARDWARE / DIAGNÓSTICO</div>
          <div class="device-banner-title">${operationErrors.has("scan_hardware") ? "No se pudo completar la auditoría" : "Conectando con tu equipo"}</div>
          <div class="device-banner-meta">${operationErrors.has("scan_hardware") ? "Consulta el detalle del error y vuelve a intentarlo." : "Leyendo información de componentes. Puedes explorar los demás módulos mientras termina."}</div>
        </div>
        ${operationErrors.has("scan_hardware") ? '<button id="btn-rescan-hw" class="btn btn-primary">Reintentar auditoría ↻</button>' : ""}
      </div>
      <div class="grid-3" aria-hidden="true">${["PROCESADOR", "MEMORIA", "ALMACENAMIENTO"].map(label => `<div class="card skeleton-card"><span class="eyebrow">${label}</span><div class="skeleton-line"></div><div class="skeleton-line short"></div></div>`).join("")}</div>
    `;
  }

  const hw = state.hardware;
  const isLap = hw.chassis.isLaptop;

  return `
    <div class="device-banner">
      <div>
        <div class="device-banner-title">
          ${escapeHtml(hw.chassis.manufacturer)} ${escapeHtml(hw.chassis.model)}
          <span class="risk-badge risk-low">${escapeHtml(hw.chassis.deviceType)}</span>
        </div>
        <div class="device-banner-meta">
          S/N BIOS: <b>${escapeHtml(hw.chassis.serialNumber)}</b> • SKU: <b>${escapeHtml(hw.chassis.sku)}</b> • Escaneado: ${escapeHtml(hw.scannedAt)}
        </div>
      </div>
      <div style="display:flex; gap:8px; flex-wrap:wrap;">
        <button id="btn-rescan-hw" class="btn btn-sm"> Re-escanear</button>
        <button id="btn-export-json" class="btn btn-sm"> Exportar JSON</button>
        <button id="btn-export-txt" class="btn btn-sm"> Exportar TXT</button>
        <button id="btn-export-html" class="btn btn-primary btn-sm"> Exportar Reporte HTML</button>
      </div>
    </div>

    <div class="grid-3">
      <div class="card">
        <div class="card-header">
          <div class="card-title"> 1. Identificación OEM y Chasis</div>
          <span class="risk-badge risk-low">SMBIOS</span>
        </div>
        <div class="kv-list">
          <div class="kv-row"><span class="kv-label">Tipo de Equipo</span><span class="kv-val">${escapeHtml(hw.chassis.deviceType)}</span></div>
          <div class="kv-row"><span class="kv-label">Marca / Fabricante</span><span class="kv-val">${escapeHtml(hw.chassis.manufacturer)}</span></div>
          <div class="kv-row"><span class="kv-label">Modelo / Producto</span><span class="kv-val">${escapeHtml(hw.chassis.model)}</span></div>
          <div class="kv-row"><span class="kv-label">Serial BIOS / Service Tag</span><span class="kv-val" style="color:var(--accent);">${escapeHtml(hw.chassis.serialNumber)}</span></div>
          <div class="kv-row"><span class="kv-label">SKU del Sistema</span><span class="kv-val">${escapeHtml(hw.chassis.sku)}</span></div>
          <div class="kv-row"><span class="kv-label">UUID Hardware</span><span class="kv-val" style="font-size:10.5px;">${escapeHtml(hw.chassis.uuid)}</span></div>
        </div>
      </div>

      <div class="card">
        <div class="card-header">
          <div class="card-title"> 2. Placa Base, BIOS y TPM</div>
          <span class="risk-badge risk-low">${escapeHtml(hw.motherboard.bootMode)}</span>
        </div>
        <div class="kv-list">
          <div class="kv-row"><span class="kv-label">Placa Base</span><span class="kv-val">${escapeHtml(hw.motherboard.manufacturer)} ${escapeHtml(hw.motherboard.product)}</span></div>
          <div class="kv-row"><span class="kv-label">Serial de Placa Base</span><span class="kv-val">${escapeHtml(hw.motherboard.serialNumber)}</span></div>
          <div class="kv-row"><span class="kv-label">Versión / Fecha BIOS</span><span class="kv-val">${escapeHtml(hw.motherboard.biosVersion)} (${escapeHtml(hw.motherboard.biosDate)})</span></div>
          <div class="kv-row"><span class="kv-label">Modo de Arranque</span><span class="kv-val">${escapeHtml(hw.motherboard.bootMode)}</span></div>
          <div class="kv-row"><span class="kv-label">Estado Secure Boot</span><span class="kv-val">${escapeHtml(hw.motherboard.secureBoot)}</span></div>
          <div class="kv-row"><span class="kv-label">Módulo de Seguridad TPM</span><span class="kv-val">${escapeHtml(hw.motherboard.tpmVersion)}</span></div>
        </div>
      </div>

      <div class="card">
        <div class="card-header">
          <div class="card-title"> 3. Procesador (CPU)</div>
          <span class="risk-badge risk-low">Uso: ${hw.cpu.loadPercent}%</span>
        </div>
        <div class="kv-list">
          <div class="kv-row"><span class="kv-label">Modelo Exacto</span><span class="kv-val">${escapeHtml(hw.cpu.name)}</span></div>
          <div class="kv-row"><span class="kv-label">Arquitectura / Socket</span><span class="kv-val">${escapeHtml(hw.cpu.architecture)} • ${escapeHtml(hw.cpu.socket)}</span></div>
          <div class="kv-row"><span class="kv-label">Núcleos / Hilos Lógicos</span><span class="kv-val">${hw.cpu.physicalCores} Físicos / ${hw.cpu.logicalThreads} Hilos</span></div>
          <div class="kv-row"><span class="kv-label">Frecuencia Base / Actual</span><span class="kv-val">${hw.cpu.baseClockMhz} MHz / ${hw.cpu.currentClockMhz} MHz</span></div>
          <div class="kv-row"><span class="kv-label">Caché L2 / Caché L3</span><span class="kv-val">${hw.cpu.l2CacheKb} KB / ${(hw.cpu.l3CacheKb / 1024).toFixed(1)} MB</span></div>
          <div class="kv-row"><span class="kv-label">Virtualización / Temp</span><span class="kv-val">${hw.cpu.virtualizationEnabled ? "Activa (SVM/VT-x)" : "Inactiva"} • ${hw.cpu.temperatureC ? hw.cpu.temperatureC + " °C" : "Sensor OEM"}</span></div>
        </div>
      </div>
    </div>

    <div class="card">
      <div class="card-header">
        <div>
          <div class="card-title"> 4. Memoria RAM — Resumen General y Desglose por Slot Físico (${hw.memory.occupiedSlots} de ${hw.memory.totalSlots} Slots Ocupados)</div>
          <div class="card-desc">Total Instalado: <b>${hw.memory.totalGb} GB ${escapeHtml(hw.memory.memoryType)} @ ${hw.memory.speedMhz} MHz</b> • Modo: <b>${escapeHtml(hw.memory.channelMode)}</b> • En uso: <b>${hw.memory.usedGb} GB (${hw.memory.usagePercent}%)</b></div>
        </div>
        <span class="risk-badge risk-low">${escapeHtml(hw.memory.channelMode)}</span>
      </div>
      <div class="progress-track"><div class="progress-fill" style="width:${hw.memory.usagePercent}%"></div></div>
      <div class="grid-2" style="margin-top:6px;">
        ${hw.memory.slots.map(s => `
          <div class="slot-card ${s.occupied ? '' : 'empty'}">
            <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:8px;">
              <span style="font-weight:700; font-size:13px;"> ${escapeHtml(s.slotName)} (${escapeHtml(s.bankLabel)})</span>
              <span class="risk-badge ${s.occupied ? 'risk-low' : 'risk-medium'}">${s.occupied ? `${s.capacityGb} GB ${s.memType} @ ${s.speedMhz} MHz` : 'SLOT VACÍO'}</span>
            </div>
            ${s.occupied ? `
              <div class="kv-list">
                <div class="kv-row"><span class="kv-label">Fabricante del Módulo</span><span class="kv-val">${escapeHtml(s.manufacturer)}</span></div>
                <div class="kv-row"><span class="kv-label">Número de Parte (Part #)</span><span class="kv-val">${escapeHtml(s.partNumber)}</span></div>
                <div class="kv-row"><span class="kv-label">Número de Serie Stick</span><span class="kv-val">${escapeHtml(s.serialNumber)}</span></div>
                <div class="kv-row"><span class="kv-label">Voltaje Configurado</span><span class="kv-val">${s.voltageMv} mV (${(s.voltageMv / 1000).toFixed(2)}V)</span></div>
              </div>
            ` : `<div style="font-size:12px; color:var(--text-secondary);">Ranura física libre en la placa base para futura ampliación de RAM.</div>`}
          </div>
        `).join("")}
      </div>
    </div>

    <div class="grid-3">
      <div class="card">
        <div class="card-header">
          <div class="card-title"> 5. Almacenamiento &amp; S.M.A.R.T.</div>
          <span class="risk-badge risk-low">${hw.storage.length} Unidad(es)</span>
        </div>
        ${hw.storage.map(d => `
          <div class="kv-list" style="margin-bottom:8px;">
            <div class="kv-row"><span class="kv-label">Modelo</span><span class="kv-val">${escapeHtml(d.friendlyName)}</span></div>
            <div class="kv-row"><span class="kv-label">Interfaz / Partición</span><span class="kv-val">${escapeHtml(d.busType)} (${escapeHtml(d.mediaType)}) • ${escapeHtml(d.partitionStyle)}</span></div>
            <div class="kv-row"><span class="kv-label">Capacidad Total / Libre</span><span class="kv-val">${d.sizeGb} GB / ${d.freeGb} GB Libres</span></div>
            <div class="kv-row"><span class="kv-label">Número de Serie</span><span class="kv-val">${escapeHtml(d.serialNumber)}</span></div>
            <div class="kv-row"><span class="kv-label">Salud S.M.A.R.T.</span><span class="kv-val" style="color:var(--risk-low-text);">${escapeHtml(d.healthStatus)} (${d.healthPercent}%)</span></div>
          </div>
        `).join("")}
      </div>

      <div class="card">
        <div class="card-header">
          <div class="card-title"> 6. Tarjeta Gráfica y Pantalla</div>
          <span class="risk-badge risk-low">DXGI / WMI</span>
        </div>
        ${hw.gpus.map(g => `
          <div class="kv-list">
            <div class="kv-row"><span class="kv-label">GPU Detectada</span><span class="kv-val">${escapeHtml(g.name)}</span></div>
            <div class="kv-row"><span class="kv-label">VRAM Dedicada</span><span class="kv-val">${g.vramGb} GB</span></div>
            <div class="kv-row"><span class="kv-label">Versión de Driver</span><span class="kv-val">${escapeHtml(g.driverVersion)}</span></div>
            <div class="kv-row"><span class="kv-label">Fecha de Driver</span><span class="kv-val">${escapeHtml(g.driverDate)}</span></div>
            <div class="kv-row"><span class="kv-label">Resolución y Refresco</span><span class="kv-val">${escapeHtml(g.resolution)} @ ${g.refreshRateHz} Hz</span></div>
          </div>
        `).join("")}
      </div>

      <div class="card">
        <div class="card-header">
          <div class="card-title"> 7. Batería y Nivel de Desgaste</div>
          <span class="risk-badge ${hw.battery.wearLevelPercent < 20 ? 'risk-low' : 'risk-medium'}">
            ${hw.battery.present ? `Salud: ${hw.battery.healthPercent}%` : 'PC Escritorio'}
          </span>
        </div>
        <div class="kv-list">
          <div class="kv-row"><span class="kv-label">Estado Actual</span><span class="kv-val">${escapeHtml(hw.battery.status)} (${hw.battery.chargePercent}%)</span></div>
          <div class="kv-row"><span class="kv-label">Capacidad de Diseño</span><span class="kv-val">${hw.battery.designedCapacityMwh} mWh</span></div>
          <div class="kv-row"><span class="kv-label">Carga Completa Actual</span><span class="kv-val">${hw.battery.fullChargedCapacityMwh} mWh</span></div>
          <div class="kv-row"><span class="kv-label">Desgaste (Wear Level)</span><span class="kv-val" style="color:var(--accent);">${hw.battery.wearLevelPercent}% desgastado</span></div>
          <div class="kv-row"><span class="kv-label">Vida Útil Restante</span><span class="kv-val" style="color:var(--risk-low-text);">${hw.battery.healthPercent}%</span></div>
          <div class="kv-row"><span class="kv-label">Tasa Actual / Química</span><span class="kv-val">${hw.battery.dischargeRateMw} mW • ${escapeHtml(hw.battery.chemistry)}</span></div>
        </div>
      </div>
    </div>

    <div class="card">
      <div class="card-header">
        <div class="card-title"> 8. Sistema Operativo Windows y Adaptadores de Red</div>
        <span class="risk-badge risk-low">${escapeHtml(hw.osNetwork.caption)} (${escapeHtml(hw.osNetwork.displayVersion)} • Build ${escapeHtml(hw.osNetwork.buildNumber)})</span>
      </div>
      <div class="grid-2">
        <div class="kv-list">
          <div class="kv-row"><span class="kv-label">Edición de Windows</span><span class="kv-val">${escapeHtml(hw.osNetwork.caption)}</span></div>
          <div class="kv-row"><span class="kv-label">Versión / Compilación</span><span class="kv-val">${escapeHtml(hw.osNetwork.displayVersion)} (OS Build ${escapeHtml(hw.osNetwork.buildNumber)})</span></div>
          <div class="kv-row"><span class="kv-label">Fecha de Instalación</span><span class="kv-val">${escapeHtml(hw.osNetwork.installDate)}</span></div>
          <div class="kv-row"><span class="kv-label">Tiempo Encendido (Uptime)</span><span class="kv-val">${escapeHtml(hw.osNetwork.uptime)}</span></div>
          <div class="kv-row"><span class="kv-label">Licencia OEM / Clave Parcial</span><span class="kv-val">${escapeHtml(hw.osNetwork.partialProductKey)}</span></div>
        </div>
        <div class="kv-list">
          ${hw.osNetwork.adapters.map(na => `
            <div class="kv-row">
              <span class="kv-label">${escapeHtml(na.name)} (${escapeHtml(na.linkSpeed)})</span>
              <span class="kv-val">${escapeHtml(na.ipv4)} • MAC: ${escapeHtml(na.macAddress)}</span>
            </div>
          `).join("")}
        </div>
      </div>
    </div>
  `;
}

function bindHardwareEvents() {
  const btnRescan = document.getElementById("btn-rescan-hw");
  if (btnRescan) btnRescan.addEventListener("click", refreshHardwareAudit);

  const btnJson = document.getElementById("btn-export-json");
  if (btnJson) {
    btnJson.addEventListener("click", () => {
      downloadFile(
        `Blackwall-Hardware-${state.hardware.chassis.serialNumber}.json`,
        JSON.stringify(state.hardware, null, 2),
        "application/json"
      );
    });
  }

  const btnTxt = document.getElementById("btn-export-txt");
  if (btnTxt) {
    btnTxt.addEventListener("click", () => {
      const h = state.hardware;
      const txt = [
        "====================================================================",
        ` REPORTE TÉCNICO DE AUDITORÍA BLACKWALL — ${h.scannedAt}`,
        "====================================================================",
        `Equipo:          ${h.chassis.manufacturer} ${h.chassis.model} (${h.chassis.deviceType})`,
        `Serial BIOS:     ${h.chassis.serialNumber} | SKU: ${h.chassis.sku}`,
        `UUID:            ${h.chassis.uuid}`,
        `Placa Base:      ${h.motherboard.manufacturer} ${h.motherboard.product} (S/N: ${h.motherboard.serialNumber})`,
        `BIOS / UEFI:     ${h.motherboard.biosVersion} (${h.motherboard.biosDate}) | ${h.motherboard.bootMode} | ${h.motherboard.tpmVersion}`,
        `Procesador:      ${h.cpu.name} (${h.cpu.physicalCores}C/${h.cpu.logicalThreads}T)`,
        `Memoria RAM:     ${h.memory.totalGb} GB ${h.memory.memoryType} @ ${h.memory.speedMhz} MHz (${h.memory.channelMode})`,
        ...h.memory.slots.map(s => `  - ${s.slotName} (${s.bankLabel}): ${s.occupied ? `${s.capacityGb}GB ${s.manufacturer} P/N:${s.partNumber} S/N:${s.serialNumber}` : "VACÍO"}`),
        `Almacenamiento:  ${h.storage.map(d => `${d.friendlyName} (${d.sizeGb}GB ${d.busType} - Salud ${d.healthPercent}%)`).join(", ")}`,
        `Batería:         Salud ${h.battery.healthPercent}% (Desgaste ${h.battery.wearLevelPercent}%) — Diseño: ${h.battery.designedCapacityMwh} mWh / Actual: ${h.battery.fullChargedCapacityMwh} mWh`,
        `Sistema Oper.:   ${h.osNetwork.caption} ${h.osNetwork.displayVersion} (Build ${h.osNetwork.buildNumber})`
      ].join("\r\n");
      downloadFile(`Blackwall-Reporte-${h.chassis.serialNumber}.txt`, txt, "text/plain");
    });
  }

  const btnHtml = document.getElementById("btn-export-html");
  if (btnHtml) {
    btnHtml.addEventListener("click", () => {
      const h = state.hardware;
      const html = `<!DOCTYPE html><html lang="es"><head><meta charset="utf-8"><title>Reporte Blackwall - ${h.chassis.serialNumber}</title>
      <style>body{font-family:'Segoe UI',sans-serif;background:#0f172a;color:#f8fafc;padding:32px;max-width:980px;margin:auto}
      .box{background:#1e293b;border:1px solid #334155;border-radius:10px;padding:18px;margin-bottom:16px}
      h1,h2{color:#38bdf8;margin-top:0}table{width:100%;border-collapse:collapse}td,th{padding:8px;border-bottom:1px solid #334155;text-align:left}</style></head>
      <body><h1>Reporte de Auditoría Profunda Blackwall</h1>
      <div class="box"><h2>Identificación del Equipo (${h.chassis.deviceType})</h2>
      <p><b>Marca / Modelo:</b> ${h.chassis.manufacturer} ${h.chassis.model}<br>
      <b>Número de Serie BIOS:</b> ${h.chassis.serialNumber} | <b>UUID:</b> ${h.chassis.uuid}<br>
      <b>Placa Base:</b> ${h.motherboard.manufacturer} ${h.motherboard.product} (S/N: ${h.motherboard.serialNumber}) | <b>BIOS:</b> ${h.motherboard.biosVersion}</p></div>
      <div class="box"><h2>Procesador y Memoria RAM (${h.memory.totalGb} GB ${h.memory.memoryType} - ${h.memory.channelMode})</h2>
      <p><b>CPU:</b> ${h.cpu.name} (${h.cpu.physicalCores} núcleos / ${h.cpu.logicalThreads} hilos)</p>
      <table><tr><th>Slot</th><th>Banco</th><th>Capacidad</th><th>Fabricante</th><th>Part Number</th><th>Serial</th></tr>
      ${h.memory.slots.map(s => `<tr><td>${s.slotName}</td><td>${s.bankLabel}</td><td>${s.occupied ? s.capacityGb + ' GB' : 'Vacío'}</td><td>${s.manufacturer}</td><td>${s.partNumber}</td><td>${s.serialNumber}</td></tr>`).join("")}
      </table></div>
      <div class="box"><h2>Batería y Almacenamiento</h2>
      <p><b>Batería:</b> Salud ${h.battery.healthPercent}% (Desgaste: ${h.battery.wearLevelPercent}%) — Diseño: ${h.battery.designedCapacityMwh} mWh / Carga Completa: ${h.battery.fullChargedCapacityMwh} mWh</p>
      <p><b>Unidades:</b> ${h.storage.map(d => `${d.friendlyName} (${d.sizeGb} GB ${d.busType}, Salud ${d.healthPercent}%)`).join(" | ")}</p></div>
      </body></html>`;
      downloadFile(`Blackwall-Reporte-${h.chassis.serialNumber}.html`, html, "text/html");
    });
  }
}

// ----------------------------------------------------------------------------
// MÓDULO B: OPTIMIZACIÓN ADAPTATIVA Y DEBLOAT CON SEMÁFORO DE RIESGO
// ----------------------------------------------------------------------------
function renderTweaksView(categoryType) {
  const isLap = state.hardware ? state.hardware.chassis.isLaptop : true;
  const effectiveDeviceScope =
    state.optDeviceFilter === "auto"
      ? (state.hardware ? (isLap ? "laptop" : "desktop") : "all")
      : state.optDeviceFilter;

  const list = state.tweaks.filter(t => {
    if (t.category !== categoryType) return false;
    if (effectiveDeviceScope !== "all" && !t.deviceScope.includes(effectiveDeviceScope)) return false;
    if (state.optRiskFilter !== "all" && t.riskLevel !== state.optRiskFilter) return false;
    return true;
  });

  const title = categoryType === "optimization"
    ? " Optimización de Rendimiento y Energía (Adaptada a Laptop / Desktop)"
    : " Debloat Multi-Versión (Windows 10 y Windows 11) & Privacidad";

  return `
    <div class="device-banner">
      <div>
        <div class="device-banner-title">${title}</div>
        <div class="device-banner-meta">
          El estado actual de cada opción se verifica en tiempo real contra el Registro y Servicios de tu PC.
        </div>
      </div>
      <div style="display:flex; gap:8px;">
        <button id="btn-recheck-tweaks" class="btn btn-sm"> Re-evaluar Estados</button>
        <button id="btn-apply-recommended-profile" class="btn btn-primary btn-sm" ${!state.hardware || !loadedModules.has("check_all_tweaks") || !loadedModules.has("get_catalogs") ? 'disabled title="Espera la verificación del hardware, los catálogos y los estados"' : ""}>
           Aplicar Perfil Seguro (${state.hardware ? (isLap ? "Laptop" : "Desktop") : "Esperando hardware"})
        </button>
      </div>
    </div>

    <div class="filter-bar">
      <span style="font-size:12px; color:var(--text-secondary); font-weight:600;">Perfil de Equipo:</span>
      <button class="btn btn-sm dev-scope-btn ${effectiveDeviceScope === 'laptop' ? 'btn-primary' : ''}" data-scope="laptop"> Enfoque Laptop (Batería/Térmico)</button>
      <button class="btn btn-sm dev-scope-btn ${effectiveDeviceScope === 'desktop' ? 'btn-primary' : ''}" data-scope="desktop"> Enfoque Desktop (Máxima Potencia)</button>
      <button class="btn btn-sm dev-scope-btn ${effectiveDeviceScope === 'all' ? 'btn-primary' : ''}" data-scope="all"> Mostrar Todos</button>

      <span style="font-size:12px; color:var(--text-secondary); font-weight:600; margin-left:12px;">Semáforo de Riesgo:</span>
      <button class="btn btn-sm risk-filter-btn ${state.optRiskFilter === 'all' ? 'btn-primary' : ''}" data-risk="all">Todos</button>
      <button class="btn btn-sm risk-filter-btn ${state.optRiskFilter === 'low' ? 'btn-primary' : ''}" data-risk="low"> Riesgo Bajo</button>
      <button class="btn btn-sm risk-filter-btn ${state.optRiskFilter === 'medium' ? 'btn-primary' : ''}" data-risk="medium"> Riesgo Medio</button>
      <button class="btn btn-sm risk-filter-btn ${state.optRiskFilter === 'high' ? 'btn-primary' : ''}" data-risk="high"> Riesgo Alto</button>
    </div>

    <div class="grid-2">
      ${list.map(t => renderTweakCard(t)).join("")}
    </div>
  `;
}

function renderTweakCard(t) {
  const riskLabel =
    t.riskLevel === "low"
      ? "Riesgo bajo"
      : t.riskLevel === "medium"
      ? "Precaución"
      : "Riesgo alto";

  const verified = loadedModules.has("get_catalogs") && loadedModules.has("check_all_tweaks") && !!state.hardware;
  const isApplied = !!state.tweakStates[t.id];

  return `
    <div id="tweak-card-${t.id}" class="card" style="${isApplied ? 'border-color: var(--risk-low-border);' : ''}">
      <div class="card-header">
        <div>
          <span style="font-size:11px; color:var(--accent); font-weight:700; text-transform:uppercase;">${escapeHtml(t.subcategory)}</span>
          <div class="card-title" style="margin-top:2px;">${escapeHtml(t.shortTitle || t.title)}</div>
        </div>
        <span class="risk-badge risk-${t.riskLevel}">${riskLabel}</span>
      </div>

      <div class="card-desc">${escapeHtml(t.summary || t.description)}</div>
      ${t.affects ? `<p class="card-impact">Afecta: ${escapeHtml(t.affects)}</p>` : ""}
      <details class="tweak-details"><summary>Ver efecto y reversión</summary><div class="detail-content">
        <p>${escapeHtml(t.description)}</p>
        <p>Reversión: ${t.undoOps?.length ? "Revertir ejecuta los valores definidos por el catálogo; puede no recuperar tu configuración anterior exacta." : "Este ajuste no declara una reversión automática."}</p>

      ${t.benefits && t.benefits.length ? `
        <div style="font-size:12px; color:var(--risk-low-text); display:flex; flex-direction:column; gap:3px;">
          ${t.benefits.map(b => `<div>✓ ${escapeHtml(b)}</div>`).join("")}
        </div>
      ` : ""}

      </div></details>
      ${t.riskWarning ? `
        <div class="warning-box"> <b>Impacto:</b> ${escapeHtml(t.riskWarning)}</div>
      ` : ""}

      <div style="display:flex; justify-content:space-between; align-items:center; margin-top:auto; padding-top:8px; border-top:1px solid var(--border-subtle);">
        <span style="font-size:11.5px; color:var(--text-secondary);">
          Estado en tu PC: <b style="color:${isApplied ? 'var(--risk-low-text)' : 'var(--text-primary)'}">${!verified ? "Pendiente de verificar" : isApplied ? "Activo en el Sistema ✓" : "Sin aplicar (verificado)"}</b>
        </span>
        <div style="display:flex; gap:8px;">
          <button class="btn btn-sm btn-undo-tweak" data-id="${t.id}" ${!verified ? "disabled" : ""}> Revertir</button>
          <button class="btn btn-primary btn-sm btn-apply-tweak" data-id="${t.id}" ${!verified ? "disabled" : ""}> Aplicar</button>
        </div>
      </div>
    </div>
  `;
}

function bindTweaksEvents() {
  const btnRecheck = document.getElementById("btn-recheck-tweaks");
  if (btnRecheck) {
    btnRecheck.addEventListener("click", async () => {
      setStatus(" Comprobando estado actual de tweaks en el Registro y Servicios...", "check_all_tweaks");
      await refreshTweaksState();
      setStatus(" Estados de tweaks sincronizados con el sistema", "Listo");
    });
  }

  document.querySelectorAll(".dev-scope-btn").forEach(btn => {
    btn.addEventListener("click", () => {
      state.optDeviceFilter = btn.dataset.scope;
      renderActiveTab();
    });
  });

  document.querySelectorAll(".risk-filter-btn").forEach(btn => {
    btn.addEventListener("click", () => {
      state.optRiskFilter = btn.dataset.risk;
      renderActiveTab();
    });
  });

  document.querySelectorAll(".btn-apply-tweak").forEach(btn => {
    btn.addEventListener("click", async () => {
      const tweak = state.tweaks.find(x => x.id === btn.dataset.id);
      if (!tweak) return;
      await runTweakOperation(tweak, "apply");
    });
  });

  document.querySelectorAll(".btn-undo-tweak").forEach(btn => {
    btn.addEventListener("click", async () => {
      const tweak = state.tweaks.find(x => x.id === btn.dataset.id);
      if (!tweak) return;
      await runTweakOperation(tweak, "undo");
    });
  });

  const btnProfile = document.getElementById("btn-apply-recommended-profile");
  if (btnProfile) {
    btnProfile.addEventListener("click", async () => {
      if (!state.hardware || !loadedModules.has("get_catalogs") || !loadedModules.has("check_all_tweaks")) return;
      const isLap = state.hardware ? state.hardware.chassis.isLaptop : true;
      const scope = isLap ? "laptop" : "desktop";
      const safeTweaks = state.tweaks.filter(t => t.riskLevel === "low" && t.deviceScope.includes(scope));
      if (batchProgress) return;
      setBatchProgress("Aplicando perfil de rendimiento", 0, safeTweaks.length);
      try {
        setStatus(` Creando Punto de Restauración antes de aplicar ${safeTweaks.length} tweaks...`, "Ejecutando lote...");
        await invokeBackend("create_restore_point", { description: `Blackwall Auto-Backup (${scope.toUpperCase()})` });
        for (let i = 0; i < safeTweaks.length; i++) {
          if (!await runTweakOperation(safeTweaks[i], "apply", true)) return;
          setBatchProgress("Aplicando perfil de rendimiento", i + 1, safeTweaks.length);
        }
        await refreshTweaksState();
        setStatus(` Perfil ${scope.toUpperCase()} aplicado con éxito`, "Completado");
      } catch (err) {
        appendLog(`Perfil interrumpido: ${err.message}`, "ERROR");
        setStatus("Perfil interrumpido; revisa el aviso", "Error");
      } finally {
        setBatchProgress(null);
      }
      renderActiveTab();
    });
  }
}

async function runTweakOperation(tweak, mode, skipRestorePoint = false) {
  const isApply = mode === "apply";
  const cmds = isApply ? tweak.apply : tweak.undo;
  try {
    if (isApply && state.autoRestoreBeforeTweak && !skipRestorePoint) {
      setStatus(" Verificando Punto de Restauración previo...", tweak.title);
      appendLog(`Creando punto de restauración preventivo antes de: ${tweak.title}`);
      await invokeBackend("create_restore_point", { description: `Blackwall Pre-${tweak.id}` });
    }
    setStatus(` ${isApply ? "Aplicando" : "Revirtiendo"}: ${tweak.title}...`, "Ejecutando PowerShell/Reg...");
    appendLog(`${isApply ? "[APPLY]" : "[UNDO]"} ${tweak.title} -> ${cmds.join(" ; ")}`);

    const res = await invokeBackend("execute_tweak_commands", { commands: cmds });
    state.tweakStates[tweak.id] = isApply;
    if (res.stdout) appendLog(res.stdout, "STDOUT");
    if (res.stderr) appendLog(res.stderr, "STDERR");

    setStatus(` ${isApply ? "Tweak aplicado" : "Tweak revertido"}: ${tweak.title}`, "Listo");
    renderActiveTab();
    return true;
  } catch (err) {
    appendLog(`Error en tweak ${tweak.id}: ${err.message}`, "ERROR");
    setStatus(` Error ejecutando ${tweak.title}`, "Error");
    return false;
  }
}

// ----------------------------------------------------------------------------
// MÓDULO E: INSTALADOR DESATENDIDO DE APLICACIONES (WINGET + FALLBACK)
// ----------------------------------------------------------------------------
function renderAppsView() {
  const filteredApps = state.apps.filter(a =>
    state.appCategoryFilter === "Todas" ? true : a.category === state.appCategoryFilter
  );

  return `
    <div class="device-banner">
      <div>
        <div class="device-banner-title"> Descarga e Instalación 100% Silenciosa (WinGet + Fallback HTTP Directo)</div>
        <div class="device-banner-meta">
          Detecta automáticamente qué programas ya tienes instalados y descarga siempre la versión más reciente sin asistentes.
        </div>
      </div>
      <div style="display:flex; gap:10px;">
        <button id="btn-rescan-apps" class="btn btn-sm"> Re-escanear Instaladas</button>
        <button id="btn-upgrade-all-winget" class="btn btn-sm"> Actualizar Todas las Apps del PC</button>
        <button id="btn-install-selected" class="btn btn-primary btn-sm" ${!loadedModules.has("check_installed_apps") ? "disabled" : ""}>
           Instalar Seleccionadas (${state.selectedApps.size})
        </button>
      </div>
    </div>

    <div class="filter-bar">
      ${state.categories.map(cat => `
        <button class="btn btn-sm app-cat-btn ${state.appCategoryFilter === cat ? 'btn-primary' : ''}" data-cat="${escapeHtml(cat)}">
          ${escapeHtml(cat)}
        </button>
      `).join("")}
    </div>

    <div class="grid-3">
      ${filteredApps.map(a => {
        const checked = state.selectedApps.has(a.id);
        const instInfo = state.installedApps[a.id];
        const isInstalled = !!(instInfo && instInfo.installed);
        return `
          <div class="card" style="cursor:pointer; border-color:${checked ? 'var(--accent)' : isInstalled ? 'var(--risk-low-border)' : 'var(--border-subtle)'}" data-appid="${a.id}">
            <div class="card-header">
              <div>
                <div style="display:flex; align-items:center; gap:6px;">
                  <span style="font-size:11px; color:var(--accent); font-weight:700;">${escapeHtml(a.category)}</span>
                  ${isInstalled ? `<span class="risk-badge risk-low">✓ Instalado ${instInfo.version ? `(${escapeHtml(instInfo.version)})` : ''}</span>` : ""}
                </div>
                <div class="card-title" style="margin-top:3px;">${escapeHtml(a.name)}</div>
              </div>
              <input type="checkbox" class="app-check" aria-label="Seleccionar ${escapeHtml(a.name)}" data-appid="${a.id}" ${checked ? "checked" : ""} style="width:18px; height:18px; accent-color:var(--accent);" />
            </div>
            <div class="card-desc">${escapeHtml(a.description)}</div>
            <div style="display:flex; justify-content:space-between; align-items:center; margin-top:auto; padding-top:8px; border-top:1px solid var(--border-subtle); font-size:11.5px;">
              <span style="font-family:monospace; color:var(--text-secondary);">${escapeHtml(a.wingetId)}</span>
              <button class="btn btn-sm btn-install-single" data-appid="${a.id}" ${!loadedModules.has("check_installed_apps") ? "disabled" : ""}>${isInstalled ? "Reinstalar / Actualizar" : "Instalar Silencioso"}</button>
            </div>
          </div>
        `;
      }).join("")}
    </div>
  `;
}

function bindAppsEvents() {
  const btnRescanApps = document.getElementById("btn-rescan-apps");
  if (btnRescanApps) {
    btnRescanApps.addEventListener("click", async () => {
      setStatus(" Escaneando programas instalados en el Registro...", "check_installed_apps");
      await refreshInstalledApps();
      setStatus(" Estado de aplicaciones instaladas actualizado", "Listo");
    });
  }

  document.querySelectorAll(".app-cat-btn").forEach(btn => {
    btn.addEventListener("click", () => {
      state.appCategoryFilter = btn.dataset.cat;
      renderActiveTab();
    });
  });

  document.querySelectorAll(".app-check").forEach(chk => {
    chk.addEventListener("change", e => {
      e.stopPropagation();
      const id = chk.dataset.appid;
      if (chk.checked) state.selectedApps.add(id);
      else state.selectedApps.delete(id);
      renderActiveTab();
    });
  });

  document.querySelectorAll(".btn-install-single").forEach(btn => {
    btn.addEventListener("click", async e => {
      e.stopPropagation();
      const app = state.apps.find(x => x.id === btn.dataset.appid);
      if (app) {
        await installSingleApp(app);
        await refreshInstalledApps();
      }
    });
  });

  const btnInstallBatch = document.getElementById("btn-install-selected");
  if (btnInstallBatch) {
    btnInstallBatch.addEventListener("click", async () => {
      const chosen = state.apps.filter(a => state.selectedApps.has(a.id));
      if (chosen.length === 0) return;
      if (batchProgress) return;
      setBatchProgress("Instalando software seleccionado", 0, chosen.length);
      try {
        for (let i = 0; i < chosen.length; i++) {
          setStatus(` [${i + 1}/${chosen.length}] Instalando ${chosen[i].name} en modo silencioso...`, `${chosen[i].wingetId}`);
          if (!await installSingleApp(chosen[i])) return;
          setBatchProgress("Instalando software seleccionado", i + 1, chosen.length);
        }
        await refreshInstalledApps();
        setStatus(` ${chosen.length} aplicación(es) instaladas silenciosamente`, "Completado");
      } finally {
        setBatchProgress(null);
      }
    });
  }

  const btnUpgradeAll = document.getElementById("btn-upgrade-all-winget");
  if (btnUpgradeAll) {
    btnUpgradeAll.addEventListener("click", async () => {
      setStatus(" Ejecutando winget upgrade --all --silent...", "Actualizando todo el software del PC");
      appendLog("Iniciando actualización masiva desatendida: winget upgrade --all --include-unknown --silent");
      try {
        const res = await invokeBackend("execute_tweak_commands", {
          commands: ["winget upgrade --all --include-unknown --silent --accept-package-agreements --accept-source-agreements"]
        });
        if (res.stdout) appendLog(res.stdout, "WINGET");
        await refreshInstalledApps();
        setStatus(" Todas las aplicaciones del sistema han sido actualizadas", "Listo");
      } catch (err) {
        appendLog(`Error en winget upgrade: ${err.message}`, "ERROR");
      }
    });
  }
}

async function installSingleApp(app) {
  setStatus(` Instalando silenciosamente ${app.name} (${app.wingetId})...`, "WinGet / Fallback");
  appendLog(`Instalando ${app.name} -> winget install --id ${app.wingetId} --silent`);
  try {
    const res = await invokeBackend("install_app", {
      wingetId: app.wingetId,
      fallbackUrl: app.fallbackUrl,
      silentArgs: app.silentArgs
    });
    if (res.stdout) appendLog(res.stdout, "INSTALL");
    if (res.stderr) appendLog(res.stderr, "WARN");
    setStatus(` ${app.name} procesado correctamente`, "Listo");
    return true;
  } catch (err) {
    appendLog(`Error instalando ${app.name}: ${err.message}`, "ERROR");
    setStatus(`Instalación interrumpida: ${app.name}`, "Error");
    return false;
  }
}

// ----------------------------------------------------------------------------
// MÓDULO D: MONITOR DE SCRIPTS EN VIVO ("SCRIPTS EN EJECUCIÓN")
// ----------------------------------------------------------------------------
function renderMonitorView() {
  return `
    <div class="device-banner">
      <div>
        <div class="device-banner-title"> Monitor de Scripts en Vivo (Consola Interna + Procesos del Sistema)</div>
        <div class="device-banner-meta">
          Inspecciona en tiempo real todos los motores <code>powershell.exe</code>, <code>pwsh.exe</code>, <code>cmd.exe</code>, <code>wscript.exe</code>, <code>cscript.exe</code> y <code>mshta.exe</code> con su línea de comandos exacta.
        </div>
      </div>
      <div style="display:flex; gap:8px;">
        <button id="btn-refresh-scripts" class="btn btn-primary btn-sm"> Refrescar Procesos Ahora</button>
      </div>
    </div>

    <div class="card">
      <div class="card-header">
        <div class="card-title"> 1. Cola y Consola Interna de Ejecución de Blackwall</div>
        <button id="btn-clear-console" class="btn btn-sm"> Limpiar Consola</button>
      </div>
      <pre id="internal-console-box" class="console-box">${escapeHtml(state.consoleLogs.join("\n"))}</pre>
    </div>

    <div class="card">
      <div class="card-header">
        <div>
          <div class="card-title"> 2. Motores de Scripts Activos en Windows (${state.liveScripts.length} Detectados)</div>
          <div class="card-desc">Incluye decodificador automático si algún script en ejecución utiliza parámetros ofuscados <code>-EncodedCommand</code> (Base64).</div>
        </div>
        <span class="risk-badge risk-low">Tiempo Real (WMI Win32_Process)</span>
      </div>
      <div id="live-scripts-table-body" style="display:flex; flex-direction:column; gap:10px;">
        ${renderLiveScriptsRows()}
      </div>
    </div>
  `;
}

function renderLiveScriptsRows() {
  if (!loadedModules.has("get_live_scripts")) return `<div class="card-desc">${operationErrors.has("get_live_scripts") ? "No se pudo consultar la lista de procesos." : "Consultando procesos del sistema…"}</div>`;
  if (!state.liveScripts || state.liveScripts.length === 0) {
    return `<div style="padding:14px; color:var(--text-secondary); font-size:13px;">No hay otros scripts externos en ejecución en este instante.</div>`;
  }
  return state.liveScripts.map(p => `
    <div class="slot-card" style="display:flex; justify-content:space-between; align-items:flex-start; gap:14px;">
      <div style="flex:1; min-width:0;">
        <div style="display:flex; align-items:center; gap:8px; margin-bottom:4px;">
          <span class="risk-badge risk-low">PID ${p.pid}</span>
          <b style="font-size:13.5px;">${escapeHtml(p.name)}</b>
          <span style="font-size:11.5px; color:var(--text-secondary);">Parent PID: ${p.parentPid} • RAM: ${p.memoryMb} MB • Inicio: ${escapeHtml(p.startedAt)}</span>
        </div>
        <div style="font-family:monospace; font-size:12px; color:var(--accent); word-break:break-all; user-select:text;">
          ${escapeHtml(p.commandLine)}
        </div>
        ${p.decodedCmd ? `
          <div class="warning-box" style="margin-top:6px; font-family:monospace;">
             <b>Base64 Decodificado:</b> ${escapeHtml(p.decodedCmd)}
          </div>
        ` : ""}
      </div>
      <button class="btn btn-danger btn-sm btn-kill-script" data-pid="${p.pid}"> Terminar (Kill)</button>
    </div>
  `).join("");
}

function bindMonitorEvents() {
  const btnRefresh = document.getElementById("btn-refresh-scripts");
  if (btnRefresh) btnRefresh.addEventListener("click", refreshLiveScripts);

  const btnClear = document.getElementById("btn-clear-console");
  if (btnClear) {
    btnClear.addEventListener("click", () => {
      state.consoleLogs = ["[SISTEMA] Consola limpiada por el usuario."];
      renderActiveTab();
    });
  }

  document.querySelectorAll(".btn-kill-script").forEach(btn => {
    btn.addEventListener("click", async () => {
      const pid = Number(btn.dataset.pid);
      appendLog(`Terminando árbol de proceso PID ${pid}...`, "WARN");
      await invokeBackend("kill_script_process", { pid });
      await refreshLiveScripts();
    });
  });
}

// ----------------------------------------------------------------------------
// MÓDULO C: SEGURIDAD, PUNTOS DE RESTAURACIÓN Y REPARACIÓN DEL SISTEMA
// ----------------------------------------------------------------------------
function renderSafetyView() {
  const pts = state.restoreInfo.points || [];
  return `
    <div class="device-banner">
      <div>
        <div class="device-banner-title"> Módulo de Seguridad, Puntos de Restauración y Reparación</div>
        <div class="device-banner-meta">
          Blackwall verifica que la Protección del Sistema esté habilitada y crea un Punto de Restauración automáticamente antes de aplicar tweaks.
        </div>
      </div>
      <button id="btn-refresh-rp" class="btn btn-sm"> Actualizar Lista de Puntos</button>
    </div>

    <div class="grid-2">
      <div class="card">
        <div class="card-header">
          <div class="card-title"> Protección Automática y Puntos de Restauración</div>
          <span class="risk-badge ${state.restoreInfo.protectionEnabled ? "risk-low" : "risk-medium"}">${!loadedModules.has("get_restore_points") ? "Pendiente de verificar" : state.restoreInfo.protectionEnabled ? "Protección habilitada" : "Protección no confirmada"}</span>
        </div>
        <div class="card-desc">
          Crea un punto de control instantáneo mediante <code>Checkpoint-Computer</code> / <code>SRSetRestorePointW</code> o abre el restaurador nativo de Windows (<code>rstrui.exe</code>).
        </div>
        <label style="display:flex; align-items:center; gap:10px; font-size:13px; cursor:pointer; margin:6px 0;">
          <input id="chk-auto-rp" type="checkbox" ${state.autoRestoreBeforeTweak ? "checked" : ""} style="width:17px; height:17px; accent-color:var(--accent);" />
          <span>Crear Punto de Restauración automáticamente antes de aplicar optimizaciones</span>
        </label>
        <div style="display:flex; gap:10px; margin-top:auto;">
          <button id="btn-manual-rp" class="btn btn-primary btn-sm"> Crear Punto de Restauración Ahora</button>
          <button id="btn-open-rstrui" class="btn btn-sm"> Abrir Restaurar Sistema (rstrui.exe)</button>
        </div>
      </div>

      <div class="card">
        <div class="card-header">
          <div class="card-title"> Herramientas de Reparación de Imagen (DISM, SFC &amp; Red)</div>
          <span class="risk-badge risk-low">Mantenimiento</span>
        </div>
        <div class="card-desc">
          Verifica y repara archivos corruptos del sistema operativo Windows, exporta respaldos del Registro y restablece la pila TCP/IP + caché DNS.
        </div>
        <div style="display:flex; gap:8px; flex-wrap:wrap; margin-top:auto;">
          <button id="btn-run-sfc" class="btn btn-sm"> Ejecutar SFC /scannow</button>
          <button id="btn-run-dism" class="btn btn-sm"> Ejecutar DISM /RestoreHealth</button>
          <button id="btn-reset-net" class="btn btn-sm"> Flush DNS &amp; Reset Winsock</button>
          <button id="btn-reg-backup" class="btn btn-primary btn-sm"> Backup Registro (.reg)</button>
        </div>
      </div>
    </div>

    <div class="card">
      <div class="card-header">
        <div class="card-title"> Puntos de Restauración Detectados en el Sistema (${pts.length})</div>
        <span class="risk-badge risk-low">Unidad C:\\</span>
      </div>
      ${pts.length === 0 ? `
        <div style="font-size:13px; color:var(--text-secondary);">
          ${!loadedModules.has("get_restore_points") ? "La consulta de puntos de restauración todavía no se ha completado." : "No se listaron puntos previos. La consulta puede requerir permisos de Administrador."}
        </div>
      ` : `
        <div class="kv-list">
          ${pts.map(p => `
            <div class="kv-row">
              <span class="kv-label"><b>#${p.sequenceNumber}</b> — ${escapeHtml(p.description)}</span>
              <span class="kv-val">${escapeHtml(p.creationTime)}</span>
            </div>
          `).join("")}
        </div>
      `}
    </div>
  `;
}

function bindSafetyEvents() {
  const chk = document.getElementById("chk-auto-rp");
  if (chk) {
    chk.addEventListener("change", () => {
      state.autoRestoreBeforeTweak = chk.checked;
    });
  }

  const btnRefreshRp = document.getElementById("btn-refresh-rp");
  if (btnRefreshRp) {
    btnRefreshRp.addEventListener("click", refreshRestorePoints);
  }

  const btnManualRp = document.getElementById("btn-manual-rp");
  if (btnManualRp) {
    btnManualRp.addEventListener("click", createManualRestorePoint);
  }

  const btnRstrui = document.getElementById("btn-open-rstrui");
  if (btnRstrui) {
    btnRstrui.addEventListener("click", async () => {
      await invokeBackend("execute_tweak_commands", { commands: ["Start-Process rstrui.exe"] });
    });
  }

  const btnSfc = document.getElementById("btn-run-sfc");
  if (btnSfc) {
    btnSfc.addEventListener("click", async () => {
      setStatus(" Ejecutando verificación de integridad SFC /scannow...", "sfc.exe /scannow");
      appendLog("Iniciando System File Checker (sfc /scannow)...");
      const res = await invokeBackend("execute_tweak_commands", {
        commands: ["sfc /verifyonly"]
      });
      if (res.stdout) appendLog(res.stdout, "SFC");
      if (res.stderr) appendLog(res.stderr, "SFC-WARN");
      setStatus(" Verificación SFC completada (revisa Consola en Vivo)", "Listo");
    });
  }

  const btnDism = document.getElementById("btn-run-dism");
  if (btnDism) {
    btnDism.addEventListener("click", async () => {
      setStatus(" Ejecutando análisis de imagen DISM /CheckHealth...", "DISM.exe");
      appendLog("Iniciando diagnóstico de imagen de Windows (DISM /Online /Cleanup-Image /CheckHealth)...");
      const res = await invokeBackend("execute_tweak_commands", {
        commands: ["DISM.exe /Online /Cleanup-Image /CheckHealth"]
      });
      if (res.stdout) appendLog(res.stdout, "DISM");
      if (res.stderr) appendLog(res.stderr, "DISM-WARN");
      setStatus(" Diagnóstico DISM completado (revisa Consola en Vivo)", "Listo");
    });
  }

  const btnRegBackup = document.getElementById("btn-reg-backup");
  if (btnRegBackup) {
    btnRegBackup.addEventListener("click", async () => {
      setStatus(" Exportando respaldo del Registro...", "reg export");
      const res = await invokeBackend("execute_tweak_commands", {
        commands: [
          "$out = Join-Path $env:USERPROFILE 'Desktop\\Blackwall-Registry-Backup.reg'",
          "reg export 'HKCU\\Control Panel\\Desktop' $out /y",
          "Write-Output \"Backup guardado en: $out\""
        ]
      });
      if (res.stdout) appendLog(res.stdout, "BACKUP");
      setStatus(" Respaldo del Registro exportado en el Escritorio", "Listo");
    });
  }

  const btnResetNet = document.getElementById("btn-reset-net");
  if (btnResetNet) {
    btnResetNet.addEventListener("click", async () => {
      setStatus(" Limpiando caché DNS y restableciendo sockets...", "ipconfig /flushdns");
      const res = await invokeBackend("execute_tweak_commands", {
        commands: ["ipconfig /flushdns", "netsh winsock reset"]
      });
      if (res.stdout) appendLog(res.stdout, "NET");
      setStatus(" Caché DNS y Winsock restablecidos", "Listo");
    });
  }
}

async function createManualRestorePoint() {
  setStatus(" Creando Punto de Restauración de Windows...", "Checkpoint-Computer");
  appendLog("Solicitando creación de Punto de Restauración en unidad C:\\...");
  try {
    const res = await invokeBackend("create_restore_point", {
      description: `Blackwall Manual Checkpoint (${new Date().toLocaleDateString()})`
    });
    if (res.stdout) appendLog(res.stdout, "RESTORE");
    await refreshRestorePoints();
    setStatus(" Punto de Restauración creado exitosamente", "Listo");
  } catch (err) {
    appendLog(`Error creando punto de restauración: ${err.message}`, "ERROR");
  }
}

// ============================================================================
// MODAL OTA Y BOTONES RÁPIDOS DE CABECERA
// ============================================================================

function setupModalsAndQuickButtons() {
  document.getElementById("btn-create-rp-top").addEventListener("click", createManualRestorePoint);

  const modal = document.getElementById("ota-modal");
  const body = document.getElementById("ota-modal-body");
  const closeModal = () => {
    modal.classList.add("hidden");
    document.getElementById("btn-ota-check").focus();
  };
  modal.addEventListener("keydown", event => {
    if (event.key === "Escape") closeModal();
    if (event.key !== "Tab") return;
    const first = document.getElementById("btn-ota-postpone");
    const last = document.getElementById("btn-ota-confirm");
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  });

  document.getElementById("btn-ota-check").addEventListener("click", () => {
    const v = state.version || {
      appVersion: "Sin consultar",
      changelog: []
    };
    body.innerHTML = `
      <div style="margin-bottom:10px;">
        Versión del catálogo: <b>v${escapeHtml(v.appVersion)}</b> · Versión publicada: <b style="color:var(--accent);">v${escapeHtml(v.latestRemoteVersion || v.appVersion)}</b>
      </div>
      <div style="font-weight:600; color:var(--text-primary); margin-bottom:6px;">Notas de la versión (Changelog):</div>
      <ul style="padding-left:18px; display:flex; flex-direction:column; gap:5px;">
        ${(v.changelog || []).map(item => `<li>${escapeHtml(item)}</li>`).join("")}
      </ul>
      <div style="margin-top:12px; padding:10px; border-radius:8px; background:var(--bg-app); font-size:11.5px;">
         <b>Verificación de integridad:</b> Si aceptas, Blackwall comprobará el SHA-256 del binario antes de actualizar (<code>${escapeHtml(v.binary?.sha256?.slice(0, 20) || "No disponible")}</code>).
      </div>
    `;
    document.getElementById("btn-ota-confirm").disabled = !v.binary?.downloadUrl || !v.binary?.sha256;
    modal.classList.remove("hidden");
    document.getElementById("btn-ota-postpone").focus();
  });

  document.getElementById("btn-ota-postpone").addEventListener("click", () => {
    closeModal();
  });

  document.getElementById("btn-ota-confirm").addEventListener("click", async () => {
    closeModal();
    const v = state.version || {};
    setStatus(" Ejecutando actualización remota OTA (SHA-256 + Self-Replace)...", "perform_ota_update");
    appendLog("[OTA] Iniciando verificación SHA-256 y actualización de catálogos/binario...");
    try {
      const res = await invokeBackend("perform_ota_update", {
        downloadUrl: v.binary?.downloadUrl || "",
        expectedSha256: v.binary?.sha256 || ""
      });
      if (res.stdout) appendLog(res.stdout, "OTA");
      await loadCatalogs();
      document.getElementById("btn-ota-check").textContent = `✓ OTA v${state.version?.appVersion || "1.1.0"}`;
      setStatus(` Blackwall actualizado a la versión v${state.version?.appVersion || "1.1.0"}`, "Completado");
      renderActiveTab();
    } catch (err) {
      appendLog(`Error en OTA: ${err.message}`, "ERROR");
    }
  });
}

function escapeHtml(str) {
  return String(str ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function downloadFile(filename, content, mimeType) {
  const blob = new Blob([content], { type: `${mimeType};charset=utf-8` });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}
