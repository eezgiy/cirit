/**
 * ROKETSAN // İHA HAVA VE KARA TAKTİK KOMUTA MERKEZİ
 * Aşama 2 - Otonom Araç Tespiti, Rota Takibi & GLM-5.3 LLM İstihbarat Füzyonu
 */

const API_BASE = window.location.origin.includes('5299') ? window.location.origin : 'http://localhost:5299';
const DETECTION_API = 'http://localhost:8002';
const AGENT_API = 'http://localhost:8001';

// Global State
const state = {
  baseLocation: { name: 'Askeri Üs Bölgesi', lat: 39.9304, lon: 32.8597 },
  zones: [],
  metaFrames: [],
  selectedMetaFrame: null,
  processingTimer: null,
  processingStartTime: 0,
  currentImageId: null,
  currentImageMeta: null,
  detections: [],
  selectedTrackId: null,
  selectedTrackData: null,
  trajectoryPoints: [],
  assessment: null,
  movementProfile: null,
  fieldReports: [],
  
  // Animation & Camera state
  simTimer: null,
  simIndex: 0,
  simSpeedMultiplier: 1,
  isSimPlaying: false,
  soundEnabled: true,
  mapFollowVehicle: true,
  lastIntelData: null
};

// Web Audio API Tactical Sounds
class TacticalAudio {
  constructor() {
    this.ctx = null;
  }
  
  _init() {
    if (!this.ctx) {
      const AudioCtx = window.AudioContext || window.webkitAudioContext;
      if (AudioCtx) this.ctx = new AudioCtx();
    }
  }

  playBlip(freq = 880, duration = 0.05) {
    if (!state.soundEnabled) return;
    try {
      this._init();
      if (!this.ctx) return;
      const osc = this.ctx.createOscillator();
      const gain = this.ctx.createGain();
      osc.type = 'sine';
      osc.frequency.setValueAtTime(freq, this.ctx.currentTime);
      gain.gain.setValueAtTime(0.08, this.ctx.currentTime);
      gain.gain.exponentialRampToValueAtTime(0.001, this.ctx.currentTime + duration);
      osc.connect(gain);
      gain.connect(this.ctx.destination);
      osc.start();
      osc.stop(this.ctx.currentTime + duration);
    } catch (e) {}
  }

  playScan() {
    if (!state.soundEnabled) return;
    this.playBlip(520, 0.06);
    setTimeout(() => this.playBlip(780, 0.08), 70);
  }

  playTargetAcquired() {
    if (!state.soundEnabled) return;
    this.playBlip(587, 0.08);
    setTimeout(() => this.playBlip(880, 0.12), 80);
  }

  playAlarm() {
    if (!state.soundEnabled) return;
    this.playBlip(1200, 0.1);
    setTimeout(() => this.playBlip(900, 0.15), 110);
  }
}

const audio = new TacticalAudio();

// Leaflet Map & Layer References
let map = null;
let baseMarker = null;
let rangeCircles = [];
let zoneMarkers = [];
let routePolyline = null;
let vehicleMarker = null;
let loiterMarkers = [];

// Watermark-Free Basemaps
let esriDarkLayer = null;
let esriSatelliteLayer = null;
let currentBasemap = 'dark';

// Multi-Vehicle Fleet Simulation State & Neon Palette
const FLEET_COLORS = [
  '#00e5ff', // Neon Cyan
  '#10b981', // Emerald Green
  '#f59e0b', // Amber Orange
  '#d946ef', // Fuchsia / Neon Magenta
  '#38bdf8', // Electric Sky
  '#e11d48', // Crimson Red
  '#a855f7', // Vivid Purple
  '#14b8a6'  // Deep Teal
];

let fleetPolylines = [];
let fleetMarkers = [];
let fleetVehicles = [];

// DOM References
const el = {};

document.addEventListener('DOMContentLoaded', () => {
  initDOM();
  initClock();
  initMap();
  loadInitialData();
  setupEventListeners();
  checkServicesHealth();
  setInterval(checkServicesHealth, 10000);
});

function initDOM() {
  el.imageCanvas = document.getElementById('imageCanvas');
  el.canvasCtx = el.imageCanvas ? el.imageCanvas.getContext('2d') : null;
  el.imageCanvasWrapper = document.getElementById('imageCanvasWrapper');
  el.imageLightbox = document.getElementById('imageLightbox');
  el.lightboxImg = document.getElementById('lightboxImg');
  el.btnCloseLightbox = document.getElementById('btnCloseLightbox');
  el.dropZone = document.getElementById('dropZone');
  el.fileInput = document.getElementById('fileInput');
  el.vehicleList = document.getElementById('vehicleList');
  el.vehicleCount = document.getElementById('vehicleCount');
  
  // Metadata Frame Tracker
  el.metaFrameSelect = document.getElementById('metaFrameSelect');
  el.btnInspectMetaFrame = document.getElementById('btnInspectMetaFrame');
  el.metaTotalFrames = document.getElementById('metaTotalFrames');
  el.metaHudId = document.getElementById('metaHudId');
  el.metaHudTime = document.getElementById('metaHudTime');
  el.metaHudRes = document.getElementById('metaHudRes');
  el.metaHudTracks = document.getElementById('metaHudTracks');
  
  // View Mode Tabs & Basemap Switchers
  el.tabSingleMode = document.getElementById('tabSingleMode');
  el.tabFleetMode = document.getElementById('tabFleetMode');
  el.fleetVehicleCount = document.getElementById('fleetVehicleCount');
  el.btnLayerDark = document.getElementById('btnLayerDark');
  el.btnLayerSatellite = document.getElementById('btnLayerSatellite');
  el.singleHudOverlay = document.getElementById('singleHudOverlay');
  el.fleetHudOverlay = document.getElementById('fleetHudOverlay');

  // Tactical Processing Modal
  el.processingModal = document.getElementById('processingModal');
  el.processingTitle = document.getElementById('processingTitle');
  el.processingSubText = document.getElementById('processingSubText');
  el.processingTimer = document.getElementById('processingTimer');
  el.processingStatusMsg = document.getElementById('processingStatusMsg');
  el.btnDismissModal = document.getElementById('btnDismissModal');

  // HUD
  el.hudTrackId = document.getElementById('hudTrackId');
  el.hudDistance = document.getElementById('hudDistance');
  el.hudSpeed = document.getElementById('hudSpeed');
  el.hudStatus = document.getElementById('hudStatus');
  el.hudEta = document.getElementById('hudEta');
  
  // Sim Controls
  el.btnPlayPause = document.getElementById('btnPlayPause');
  el.btnRestart = document.getElementById('btnRestart');
  el.simProgress = document.getElementById('simProgress');
  el.simSpeedSelect = document.getElementById('simSpeedSelect');
  el.simTimeLabel = document.getElementById('simTimeLabel');
  
  // Intel / Assessment
  el.verdictBanner = document.getElementById('verdictBanner');
  el.verdictTag = document.getElementById('verdictTag');
  el.confidencePct = document.getElementById('confidencePct');
  el.verdictMeta = document.getElementById('verdictMeta');
  el.reasoningText = document.getElementById('reasoningText');
  el.evidenceContainer = document.getElementById('evidenceContainer');
  el.conflictList = document.getElementById('conflictList');
  el.scenarioList = document.getElementById('scenarioList');
  el.fieldReportList = document.getElementById('fieldReportList');
  
  // Status Dots
  el.dotBackend = document.getElementById('dotBackend');
  el.dotDetection = document.getElementById('dotDetection');
  el.dotAgent = document.getElementById('dotAgent');
  el.btnSoundToggle = document.getElementById('btnSoundToggle');
}

function initClock() {
  const clockEl = document.getElementById('clockDisplay');
  function update() {
    const now = new Date();
    const utc = now.toISOString().substring(11, 19) + ' UTC';
    const loc = now.toLocaleTimeString('tr-TR');
    if (clockEl) clockEl.textContent = `${loc} [${utc}]`;
  }
  update();
  setInterval(update, 1000);
}

// Map Initialization with Leaflet (100% Watermark-Free Esri Dark Gray + Satellite)
function initMap() {
  map = L.map('tacticalMap', {
    zoomControl: false,
    attributionControl: false
  }).setView([state.baseLocation.lat, state.baseLocation.lon], 13);

  map.on('dragstart', () => {
    state.mapFollowVehicle = false;
  });

  L.control.zoom({ position: 'bottomright' }).addTo(map);

  // 1. Esri World Dark Gray Canvas: High-tech military dark style, zero watermarks!
  esriDarkLayer = L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Dark_Gray_Base/MapServer/tile/{z}/{y}/{x}', {
    maxNativeZoom: 16,
    maxZoom: 19,
    attribution: 'Tiles &copy; Esri'
  });

  // 2. Esri World Imagery (Satellite): Real aerial photographic satellite imagery, zero watermarks!
  esriSatelliteLayer = L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', {
    maxZoom: 19,
    attribution: 'Tiles &copy; Esri'
  });

  esriDarkLayer.addTo(map);

  renderBaseAndRings();

  // Leaflet measures its container's pixel size only once, at construction time. If web fonts
  // or the surrounding CSS grid/flex layout haven't settled yet (or the window is later
  // resized), that cached size goes stale - tiles/markers/paths outside the stale bounds render
  // blank or "disappear" on the next pan/zoom until invalidateSize() re-measures the real
  // container. Covering the paint cycle, slower reflows, full page load, font swap and window
  // resize catches every case that was causing the partial/blank map and vanishing paths.
  const refreshMapSize = () => map.invalidateSize();
  requestAnimationFrame(refreshMapSize);
  setTimeout(refreshMapSize, 300);
  window.addEventListener('load', refreshMapSize);
  window.addEventListener('resize', refreshMapSize);
  if (document.fonts && document.fonts.ready) {
    document.fonts.ready.then(refreshMapSize);
  }
}

function setMapBasemap(type) {
  if (!map) return;
  currentBasemap = type;

  if (type === 'satellite') {
    if (map.hasLayer(esriDarkLayer)) map.removeLayer(esriDarkLayer);
    if (!map.hasLayer(esriSatelliteLayer)) esriSatelliteLayer.addTo(map);
    if (el.btnLayerDark) el.btnLayerDark.classList.remove('active');
    if (el.btnLayerSatellite) el.btnLayerSatellite.classList.add('active');
    showToast('Harita Katmanı: Gerçek Uydu Görüntüsü (Satellite)', 'info');
  } else {
    if (map.hasLayer(esriSatelliteLayer)) map.removeLayer(esriSatelliteLayer);
    if (!map.hasLayer(esriDarkLayer)) esriDarkLayer.addTo(map);
    if (el.btnLayerSatellite) el.btnLayerSatellite.classList.remove('active');
    if (el.btnLayerDark) el.btnLayerDark.classList.add('active');
    showToast('Harita Katmanı: Taktik Koyu (Watermarksız)', 'info');
  }
}

function renderBaseAndRings() {
  if (!map) return;
  
  // Clear old circles if any
  rangeCircles.forEach(c => map.removeLayer(c));
  rangeCircles = [];
  if (baseMarker) map.removeLayer(baseMarker);

  const basePos = [state.baseLocation.lat, state.baseLocation.lon];

  // Base Range Defense Rings
  const rings = [
    { radius: 1000, color: '#ef4444', label: '1 KM - KRİTİK SAVUNMA HATTI' },
    { radius: 2000, color: '#f59e0b', label: '2 KM - İKAZ & MÜDAHALE' },
    { radius: 5000, color: '#38bdf8', label: '5 KM - DIŞ GÖZLEM HATTI' }
  ];

  rings.forEach(r => {
    const circle = L.circle(basePos, {
      radius: r.radius,
      color: r.color,
      weight: 1.5,
      opacity: 0.6,
      fillColor: r.color,
      fillOpacity: 0.04,
      dashArray: '4, 8'
    }).addTo(map);
    circle.bindTooltip(r.label, { permanent: false, direction: 'top', className: 'tactical-tooltip' });
    rangeCircles.push(circle);
  });

  // Base Center Icon
  const baseIcon = L.divIcon({
    className: 'military-base-marker',
    html: `
      <div class="base-radar-wave"></div>
      <div style="width:26px; height:26px; background:#ef4444; border:2px solid #fff; border-radius:50%; display:flex; align-items:center; justify-content:center; box-shadow:0 0 15px #ef4444;">
        <svg viewBox="0 0 24 24" width="14" height="14" fill="#fff"><path d="M12 2L1 21h22L12 2zm0 3.5l7.5 13.5h-15L12 5.5zM11 10v4h2v-4h-2zm0 6v2h2v-2h-2z"/></svg>
      </div>
    `,
    iconSize: [26, 26],
    iconAnchor: [13, 13]
  });

  baseMarker = L.marker(basePos, { icon: baseIcon }).addTo(map)
    .bindPopup(`<b>${state.baseLocation.name}</b><br>Konum: ${state.baseLocation.lat.toFixed(5)}, ${state.baseLocation.lon.toFixed(5)}`);
}

// ============================================================
// TACTICAL PROCESSING MODAL / PROGRESS HUD CONTROLLER
// ============================================================
function showProcessingModal(title, subText) {
  if (!el.processingModal) return;
  if (el.processingTitle) el.processingTitle.textContent = title;
  if (el.processingSubText) el.processingSubText.textContent = subText;
  if (el.processingStatusMsg) el.processingStatusMsg.textContent = 'Analiz devam ediyor, lütfen bekleyiniz...';
  if (el.btnDismissModal) el.btnDismissModal.classList.add('hidden');

  // Reset all 4 steps to initial state
  for (let i = 1; i <= 4; i++) {
    const stepEl = document.getElementById(`step${i}`);
    const stEl = document.getElementById(`step${i}Status`);
    if (stepEl) stepEl.className = 'pipe-step';
    if (stEl) stEl.textContent = 'Bekleniyor';
  }

  // Set step 1 active immediately
  updateProcessingStep(1, 'active', 'İşleniyor...', 'Görsel ve metadata parametreleri sunucuya hazırlanıyor...');

  // Start live elapsed timer
  state.processingStartTime = Date.now();
  if (state.processingTimer) clearInterval(state.processingTimer);
  state.processingTimer = setInterval(() => {
    const elapsedSec = ((Date.now() - state.processingStartTime) / 1000).toFixed(1);
    if (el.processingTimer) el.processingTimer.textContent = `GEÇEN SÜRE: ${elapsedSec}s`;
  }, 100);

  el.processingModal.classList.remove('hidden');
}

function updateProcessingStep(stepNum, status, statusText, descText) {
  const stepEl = document.getElementById(`step${stepNum}`);
  const stEl = document.getElementById(`step${stepNum}Status`);
  const descEl = document.getElementById(`step${stepNum}Desc`);

  if (stepEl) {
    stepEl.className = `pipe-step ${status}`;
  }
  if (stEl && statusText) {
    stEl.textContent = statusText;
  }
  if (descEl && descText) {
    descEl.textContent = descText;
  }
}

function hideProcessingModal(delayMs = 1200) {
  if (state.processingTimer) clearInterval(state.processingTimer);
  setTimeout(() => {
    if (el.processingModal) el.processingModal.classList.add('hidden');
  }, delayMs);
}

function failProcessingModal(errorMsg) {
  if (state.processingTimer) clearInterval(state.processingTimer);
  if (el.processingStatusMsg) el.processingStatusMsg.textContent = `Hata: ${errorMsg}`;
  
  // Mark current active step as failed
  for (let i = 1; i <= 4; i++) {
    const stepEl = document.getElementById(`step${i}`);
    if (stepEl && stepEl.classList.contains('active')) {
      updateProcessingStep(i, 'failed', 'Başarısız', errorMsg);
      break;
    }
  }

  if (el.btnDismissModal) {
    el.btnDismissModal.classList.remove('hidden');
    el.btnDismissModal.onclick = () => {
      if (el.processingModal) el.processingModal.classList.add('hidden');
    };
  }
}

// ============================================================
// METADATA FRAME TRACKER (image_meta.json)
// ============================================================
async function loadMetadataFrames() {
  try {
    const [metaResp, uploadedResp] = await Promise.all([
      fetch(`${API_BASE}/api/images/metadata`),
      fetch(`${API_BASE}/api/images/uploaded`),
    ]);
    if (!metaResp.ok) return;
    const list = await metaResp.json();
    state.metaFrames = list || [];

    const uploadedIds = uploadedResp.ok ? await uploadedResp.json() : [];
    state.uploadedImageIds = new Set(uploadedIds);

    // "Takip Edilen Kare" sadece gerçekten bir görsel dosyası yüklenmiş kareleri listeler -
    // image_meta.json'daki 40 kalibrasyon kaydının tamamını değil (bkz. ImageStorageService.
    // ListUploadedImageIds / GET /api/images/uploaded).
    const visibleFrames = state.metaFrames.filter(m => state.uploadedImageIds.has(m.imageId));

    if (el.metaTotalFrames) {
      el.metaTotalFrames.textContent = `${visibleFrames.length} Kare`;
    }

    if (el.metaFrameSelect) {
      const previousSelection = el.metaFrameSelect.value;
      el.metaFrameSelect.innerHTML = '';

      if (visibleFrames.length === 0) {
        const opt = document.createElement('option');
        opt.value = '';
        opt.textContent = 'Henüz görsel yüklenmedi';
        el.metaFrameSelect.appendChild(opt);
        state.selectedMetaFrame = null;
      } else {
        visibleFrames.forEach(m => {
          const opt = document.createElement('option');
          opt.value = m.imageId;
          opt.textContent = `${m.imageId} | Saat: ${m.captureTime} (${m.candidateTrackCount} Hedef İz) [${m.widthPx}x${m.heightPx}]`;
          el.metaFrameSelect.appendChild(opt);
        });

        const stillVisible = visibleFrames.find(m => m.imageId === previousSelection);
        state.selectedMetaFrame = stillVisible || visibleFrames[0];
        el.metaFrameSelect.value = state.selectedMetaFrame.imageId;
        updateMetaHud(state.selectedMetaFrame);
      }
    }
  } catch (e) {
    console.error('Metadata frames error:', e);
  }
}

function updateMetaHud(m) {
  if (!m) return;
  if (el.metaHudId) el.metaHudId.textContent = m.imageId;
  if (el.metaHudTime) el.metaHudTime.textContent = `${m.captureTime}:00`;
  if (el.metaHudRes) el.metaHudRes.textContent = `${m.widthPx} x ${m.heightPx} px`;
  if (el.metaHudTracks) el.metaHudTracks.textContent = `${m.candidateTrackCount || 0} Hedef İz`;
}

async function inspectMetadataFrame(metaId) {
  const meta = state.metaFrames.find(m => m.imageId === metaId);
  if (!meta) return;

  state.selectedMetaFrame = meta;
  state.currentImageId = meta.imageId;
  updateMetaHud(meta);

  showProcessingModal('Kare & Metadata İnceleniyor', `Kare: ${meta.imageId} | Zaman: ${meta.captureTime} | İz Sayısı: ${meta.candidateTrackCount}`);
  updateProcessingStep(1, 'completed', 'Tamamlandı', 'Metadata kaydı ve kamera kalibrasyonu yüklendi.');
  updateProcessingStep(2, 'active', 'İşleniyor...', 'Kareye ait YOLOv11 tespit verileri derleniyor...');

  try {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = async () => {
      renderImageOnCanvas(img);
      updateProcessingStep(2, 'completed', 'Tamamlandı', 'Görsel ve YOLO tespitleri yüklendi.');
      updateProcessingStep(3, 'active', 'Dönüştürülüyor...', 'WGS84 koordinat eşleşmeleri yapılıyor...');

      await fetchAndRenderDetections(meta.imageId);

      updateProcessingStep(3, 'completed', 'Tamamlandı', 'WGS84 dönüşümü ve iz eşleme sağlandı.');
      updateProcessingStep(4, 'completed', 'Tamamlandı', 'Rota takibi ve GLM-5.3 istihbarat analizi hazır.');
      
      if (el.processingStatusMsg) {
        el.processingStatusMsg.textContent = `✓ Başarılı: ${state.detections.length} araç tespit edildi.`;
      }
      audio.playTargetAcquired();
      hideProcessingModal(1000);
    };
    img.onerror = async () => {
      // If direct file load fails, still load detections and map
      updateProcessingStep(2, 'completed', 'Tamamlandı');
      await fetchAndRenderDetections(meta.imageId);
      hideProcessingModal(800);
    };
    img.src = `${API_BASE}/api/images/${meta.imageId}/file`;

  } catch (err) {
    failProcessingModal(err.message);
  }
}

// Initial Data Fetching
async function loadInitialData() {
  await loadMetadataFrames();

  try {
    const baseResp = await fetch(`${API_BASE}/api/base-location`).then(r => r.ok ? r.json() : null);
    if (baseResp && baseResp.lat) {
      state.baseLocation = baseResp;
      renderBaseAndRings();
    }
  } catch (e) {
    console.warn('Base location fetch fallback used');
  }

  try {
    const zonesResp = await fetch(`${API_BASE}/api/zones`).then(r => r.ok ? r.json() : []);
    state.zones = zonesResp || [];
    renderZones();
  } catch (e) {
    console.warn('Zones fetch fallback used');
  }

  // Auto-inspect the first UPLOADED frame (not just any metadata entry) so the command center
  // starts alive only once there's an actual image to show.
  if (state.selectedMetaFrame) {
    inspectMetadataFrame(state.selectedMetaFrame.imageId);
  }
}

function renderZones() {
  zoneMarkers.forEach(m => map.removeLayer(m));
  zoneMarkers = [];

  state.zones.forEach(z => {
    const zoneIcon = L.divIcon({
      className: 'zone-marker',
      html: `<div style="background:rgba(14,165,233,0.8); border:1px solid #00e5ff; color:#fff; font-size:10px; font-family:monospace; padding:2px 6px; border-radius:4px; white-space:nowrap; box-shadow:0 0 8px rgba(0,229,255,0.4);">WAYPOINT: ${z.name}</div>`,
      iconSize: [80, 20],
      iconAnchor: [40, 10]
    });
    const marker = L.marker([z.lat, z.lon], { icon: zoneIcon }).addTo(map);
    zoneMarkers.push(marker);
  });
}

// File Upload & Event Handlers
function setupEventListeners() {
  setupImageLightbox();

  if (el.metaFrameSelect) {
    el.metaFrameSelect.onchange = () => {
      const val = el.metaFrameSelect.value;
      const selected = state.metaFrames.find(m => m.imageId === val);
      if (selected) {
        state.selectedMetaFrame = selected;
        updateMetaHud(selected);
      }
      // Dropdown'dan bir kare seçilince "Kareyi İncele" butonuna basmaya gerek kalmadan
      // otomatik olarak görsel + tespitler + rota yüklensin.
      if (val) inspectMetadataFrame(val);
    };
  }

  if (el.btnInspectMetaFrame) {
    el.btnInspectMetaFrame.onclick = () => {
      const val = el.metaFrameSelect ? el.metaFrameSelect.value : null;
      if (val) inspectMetadataFrame(val);
    };
  }

  if (el.btnDismissModal) {
    el.btnDismissModal.onclick = () => {
      if (el.processingModal) el.processingModal.classList.add('hidden');
    };
  }

  if (el.dropZone && el.fileInput) {
    el.dropZone.onclick = () => el.fileInput.click();
    el.fileInput.onchange = (e) => {
      if (e.target.files.length > 0) {
        processUploadedFiles(e.target.files);
      }
      el.fileInput.value = ''; // aynı dosyaların tekrar seçilebilmesi için sıfırla
    };

    el.dropZone.ondragover = (e) => {
      e.preventDefault();
      el.dropZone.classList.add('dragover');
    };
    el.dropZone.ondragleave = () => el.dropZone.classList.remove('dragover');
    el.dropZone.ondrop = (e) => {
      e.preventDefault();
      el.dropZone.classList.remove('dragover');
      if (e.dataTransfer.files.length > 0) {
        processUploadedFiles(e.dataTransfer.files);
      }
    };
  }

  // View Mode Tabs (Single vs Fleet)
  if (el.tabSingleMode) {
    el.tabSingleMode.onclick = () => switchMapMode('single');
  }
  if (el.tabFleetMode) {
    el.tabFleetMode.onclick = () => switchMapMode('fleet');
  }

  // Watermark-Free Basemap Switchers
  if (el.btnLayerDark) {
    el.btnLayerDark.onclick = () => setMapBasemap('dark');
  }
  if (el.btnLayerSatellite) {
    el.btnLayerSatellite.onclick = () => setMapBasemap('satellite');
  }

  // Sim Controls
  if (el.btnPlayPause) {
    el.btnPlayPause.onclick = toggleSimPlay;
  }
  if (el.btnRestart) {
    el.btnRestart.onclick = restartSim;
  }
  if (el.simProgress) {
    el.simProgress.oninput = (e) => {
      seekSim(parseInt(e.target.value, 10));
    };
  }
  if (el.simSpeedSelect) {
    el.simSpeedSelect.onchange = (e) => {
      state.simSpeedMultiplier = parseFloat(e.target.value);
    };
  }

  // Sound toggle
  if (el.btnSoundToggle) {
    el.btnSoundToggle.onclick = () => {
      state.soundEnabled = !state.soundEnabled;
      el.btnSoundToggle.classList.toggle('active', state.soundEnabled);
      showToast(state.soundEnabled ? 'Taktiksel Sesler: AÇIK' : 'Taktiksel Sesler: KAPALI');
    };
  }
}

// Core Pipeline Execution

/** Resolves the metadata frame to link this file to, and POSTs it to /api/images/single. */
async function uploadOneImage(file) {
  let targetMetaId = el.metaFrameSelect ? el.metaFrameSelect.value : null;
  const fileNameStem = file.name.replace(/\.[^/.]+$/, "");

  const matchedMeta = state.metaFrames.find(m => m.imageId.toLowerCase() === fileNameStem.toLowerCase());
  if (matchedMeta) {
    targetMetaId = matchedMeta.imageId;
  } else if (!targetMetaId && state.metaFrames.length > 0) {
    targetMetaId = state.metaFrames[0].imageId;
  }

  const formData = new FormData();
  formData.append('file', file);
  if (targetMetaId) {
    formData.append('metadataId', targetMetaId);
  }

  const resp = await fetch(`${API_BASE}/api/images/single`, {
    method: 'POST',
    body: formData
  });

  if (!resp.ok) {
    const err = await resp.json().catch(() => ({ error: 'Upload başarısız' }));
    throw new Error(err.error || 'Upload hatası');
  }

  const data = await resp.json();
  return { data, targetMetaId };
}

/** Single-image pipeline: upload, then render canvas + detections + assessment for that one frame. */
async function processUploadedFile(file) {
  const fileNameStem = file.name.replace(/\.[^/.]+$/, "");
  const matchedMeta = state.metaFrames.find(m => m.imageId.toLowerCase() === fileNameStem.toLowerCase());
  if (matchedMeta && el.metaFrameSelect) {
    el.metaFrameSelect.value = matchedMeta.imageId;
    updateMetaHud(matchedMeta);
  }

  const preselectedMetaId = el.metaFrameSelect ? el.metaFrameSelect.value : null;
  const currentMetaObj = state.metaFrames.find(m => m.imageId === preselectedMetaId);
  const timeStr = currentMetaObj?.captureTime || '--:--';

  showProcessingModal('İHA Karesi İşleniyor & Hedefler Taranıyor',
    `Dosya: ${file.name} | Eşleşen Metadata: ${preselectedMetaId} (Saat: ${timeStr})`);

  audio.playScan();

  try {
    updateProcessingStep(1, 'completed', 'Tamamlandı', 'Görsel sunucuya iletildi, metadata eşlendi.');
    updateProcessingStep(2, 'active', 'İşleniyor...', 'YOLOv11 derin öğrenme modeli araçları tespit ediyor...');

    const { data, targetMetaId } = await uploadOneImage(file);
    const effectiveMetaId = data.metadataId || targetMetaId;
    state.currentImageId = effectiveMetaId;

    if (data.detection?.status === 'failed') {
      throw new Error(data.detection?.detail?.reason || 'YOLO tespit servisi hatası');
    }

    const count = data.detection?.detail?.detectionCount || 0;
    updateProcessingStep(2, 'completed', 'Tamamlandı', `${count} araç tespit edildi (araba, kamyonet, kamyon, otobüs).`);
    updateProcessingStep(3, 'active', 'Dönüştürülüyor...', `${effectiveMetaId} metadatasından WGS84 coğrafi projeksiyonu ve iz eşlemesi yapılıyor...`);

    // "Takip Edilen Kare" listesi yalnızca yüklenmiş görselleri gösterir - yeni yüklenen bu kare
    // dropdown'a girsin diye listeyi tazele.
    await loadMetadataFrames();
    if (el.metaFrameSelect) el.metaFrameSelect.value = effectiveMetaId;

    // Load preview image to canvas
    const imgUrl = URL.createObjectURL(file);
    const img = new Image();
    img.onload = async () => {
      renderImageOnCanvas(img);

      updateProcessingStep(3, 'completed', 'Tamamlandı', 'WGS84 projeksiyonu ve rota eşleştirmesi yapıldı.');
      updateProcessingStep(4, 'active', 'Sentezleniyor...', 'Üs tehdit mesafesi/ETA hesaplanıyor, GLM-5.3 LLM değerlendirmesi alınıyor...');

      await fetchAndRenderDetections(effectiveMetaId);

      updateProcessingStep(4, 'completed', 'Tamamlandı', 'İstihbarat sentezi ve rota simülatörü devrede.');
      if (el.processingStatusMsg) {
        el.processingStatusMsg.textContent = `✓ Başarılı: ${state.detections.length} araç tespit edildi, rotalar haritaya çizildi.`;
      }

      audio.playTargetAcquired();
      showToast(`Tespit tamamlandı! ${state.detections.length} araç bulundu.`);
      hideProcessingModal(1200);
    };
    img.src = imgUrl;

  } catch (err) {
    failProcessingModal(err.message);
    showToast(`Pipeline hatası: ${err.message}`, 'error');
  }
}

/** Entry point for both single- and multi-file selection/drop. Routes to the single-file
 * pipeline when there's exactly one file (unchanged UX), otherwise runs each file through
 * the backend pipeline sequentially and shows aggregate progress. */
async function processUploadedFiles(fileListLike) {
  const files = Array.from(fileListLike).filter(f =>
    f.type.startsWith('image/') || /\.(jpg|jpeg|png|bmp|webp)$/i.test(f.name));

  if (files.length === 0) {
    showToast('Desteklenen bir görsel dosyası bulunamadı (jpg/jpeg/png/bmp/webp).', 'error');
    return;
  }

  if (files.length === 1) {
    await processUploadedFile(files[0]);
    return;
  }

  showProcessingModal(`${files.length} İHA Karesi Toplu İşleniyor`,
    `Toplu yükleme başlıyor: 0 / ${files.length} tamamlandı`);
  audio.playScan();

  let successCount = 0;
  let failCount = 0;
  let totalVehicles = 0;
  let lastSuccess = null; // { file, metaId }

  for (let i = 0; i < files.length; i++) {
    const file = files[i];
    const progressLabel = `${i + 1}/${files.length}`;

    if (el.processingTitle) {
      el.processingTitle.textContent = `${files.length} İHA Karesi Toplu İşleniyor (${progressLabel})`;
    }
    if (el.processingSubText) {
      el.processingSubText.textContent = `Dosya ${progressLabel}: ${file.name}`;
    }
    updateProcessingStep(1, 'active', 'İşleniyor...', `[${progressLabel}] "${file.name}" sunucuya iletiliyor...`);
    updateProcessingStep(2, '', 'Bekleniyor', '');
    updateProcessingStep(3, '', 'Bekleniyor', '');
    updateProcessingStep(4, '', 'Bekleniyor', '');

    try {
      const { data, targetMetaId } = await uploadOneImage(file);
      updateProcessingStep(1, 'completed', 'Tamamlandı', `[${progressLabel}] "${file.name}" sunucuya iletildi.`);

      if (data.detection?.status === 'failed') {
        throw new Error(data.detection?.detail?.reason || 'YOLO tespit servisi hatası');
      }

      const effectiveMetaId = data.metadataId || targetMetaId;
      const count = data.detection?.detail?.detectionCount || 0;
      totalVehicles += count;
      successCount++;
      lastSuccess = { file, metaId: effectiveMetaId };

      updateProcessingStep(2, 'completed', 'Tamamlandı', `[${progressLabel}] ${count} araç tespit edildi.`);
      updateProcessingStep(3, 'completed', 'Tamamlandı', `[${progressLabel}] WGS84 projeksiyonu ve iz eşleştirmesi yapıldı.`);
      updateProcessingStep(4, 'completed', 'Tamamlandı', `[${progressLabel}] İstihbarat değerlendirmesi tetiklendi.`);
    } catch (err) {
      failCount++;
      console.error(`Toplu yükleme hatası (${file.name}):`, err);
      updateProcessingStep(2, 'failed', 'Başarısız', `[${progressLabel}] ${err.message}`);
    }
  }

  // "Takip Edilen Kare" listesini bu toplu yüklemede eklenen kareleri de içerecek şekilde tazele.
  await loadMetadataFrames();

  // Show the last successfully processed frame on the canvas/detection panel.
  if (lastSuccess) {
    state.currentImageId = lastSuccess.metaId;
    if (el.metaFrameSelect) el.metaFrameSelect.value = lastSuccess.metaId;
    const imgUrl = URL.createObjectURL(lastSuccess.file);
    const img = new Image();
    img.onload = () => {
      renderImageOnCanvas(img);
      fetchAndRenderDetections(lastSuccess.metaId);
    };
    img.src = imgUrl;
  }

  const summary = `Toplu yükleme tamamlandı: ${successCount} başarılı, ${failCount} başarısız, toplam ${totalVehicles} araç tespit edildi.`;
  if (el.processingStatusMsg) el.processingStatusMsg.textContent = summary;
  if (failCount > 0) {
    showToast(summary, 'error');
  } else {
    audio.playTargetAcquired();
    showToast(summary);
  }
  hideProcessingModal(1800);
}

// Draw Image and Bounding Boxes on Canvas
function renderImageOnCanvas(img) {
  if (!el.imageCanvas || !el.canvasCtx) return;
  const canvas = el.imageCanvas;
  canvas.width = img.naturalWidth || img.width;
  canvas.height = img.naturalHeight || img.height;
  el.canvasCtx.drawImage(img, 0, 0, canvas.width, canvas.height);
}

// ============================================================
// IMAGE LIGHTBOX - tespit önizleme canvas'ına tıklayınca büyük görünüm
// ============================================================
function setupImageLightbox() {
  if (el.imageCanvasWrapper) {
    el.imageCanvasWrapper.onclick = openImageLightbox;
  }
  if (el.btnCloseLightbox) {
    el.btnCloseLightbox.onclick = closeImageLightbox;
  }
  if (el.imageLightbox) {
    // Görselin kendisine değil, arka plana tıklanınca kapat.
    el.imageLightbox.onclick = (e) => {
      if (e.target === el.imageLightbox) closeImageLightbox();
    };
  }
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && el.imageLightbox && !el.imageLightbox.classList.contains('hidden')) {
      closeImageLightbox();
    }
  });
}

function openImageLightbox() {
  if (!el.imageCanvas || !el.imageLightbox || !el.lightboxImg) return;
  if (el.imageCanvas.width === 0 || el.imageCanvas.height === 0) return; // henüz görsel yok

  el.lightboxImg.src = el.imageCanvas.toDataURL('image/png');
  el.imageLightbox.classList.remove('hidden');
}

function closeImageLightbox() {
  if (el.imageLightbox) el.imageLightbox.classList.add('hidden');
}

async function fetchAndRenderDetections(imageId) {
  try {
    const resp = await fetch(`${API_BASE}/api/images/${imageId}/detections`);
    if (!resp.ok) return;
    const detections = await resp.json();
    state.detections = detections || [];

    // Redraw with boxes
    drawBoundingBoxes(state.detections);
    renderVehicleList(state.detections);

    // Auto select first matched track
    const firstMatched = state.detections.find(d => d.trackId);
    if (firstMatched) {
      selectVehicle(firstMatched.trackId);
    }
  } catch (e) {
    console.error('Detection fetch error:', e);
  }
}

function drawBoundingBoxes(detections) {
  if (!el.imageCanvas || !el.canvasCtx) return;
  const canvas = el.imageCanvas;
  const ctx = el.canvasCtx;

  const classColors = {
    car: '#00e5ff',
    van: '#38bdf8',
    truck: '#f59e0b',
    bus: '#a855f7'
  };

  detections.forEach((d, i) => {
    const isSelected = d.trackId && d.trackId === state.selectedTrackId;
    const color = classColors[d.className?.toLowerCase()] || '#00e5ff';
    
    // Normalized to pixels: cx, cy, w, h
    const w = d.wNorm * canvas.width;
    const h = d.hNorm * canvas.height;
    const x = (d.cxNorm * canvas.width) - (w / 2);
    const y = (d.cyNorm * canvas.height) - (h / 2);

    ctx.strokeStyle = isSelected ? '#ffffff' : color;
    ctx.lineWidth = isSelected ? 3.5 : 2;
    ctx.shadowColor = color;
    ctx.shadowBlur = isSelected ? 12 : 5;
    ctx.strokeRect(x, y, w, h);

    // Label background
    const label = `${(d.className || 'Araç').toUpperCase()} %${Math.round((d.confidence || 0) * 100)} ${d.trackId ? `[${d.trackId}]` : ''}`;
    ctx.font = 'bold 12px "JetBrains Mono", monospace';
    const textWidth = ctx.measureText(label).width;

    ctx.fillStyle = isSelected ? 'rgba(0, 229, 255, 0.95)' : 'rgba(15, 23, 42, 0.85)';
    ctx.fillRect(x, y - 20 > 0 ? y - 20 : y, textWidth + 10, 20);

    ctx.fillStyle = isSelected ? '#000' : color;
    ctx.fillText(label, x + 5, y - 20 > 0 ? y - 5 : y + 15);
  });
}

// Vehicle List in Left Sidebar
function renderVehicleList(detections) {
  if (!el.vehicleList) return;
  el.vehicleList.innerHTML = '';
  if (el.vehicleCount) el.vehicleCount.textContent = detections.length;

  const matchedCount = (detections || []).filter(d => d.trackId).length;
  if (el.fleetVehicleCount) el.fleetVehicleCount.textContent = matchedCount;

  if (detections.length === 0) {
    el.vehicleList.innerHTML = '<div style="font-size:0.75rem; color:var(--text-dim); text-align:center; padding:10px;">Görselde tespit edilmiş araç yok.</div>';
    return;
  }

  // If in fleet mode, refresh fleet simulation for newly loaded detections
  if (state.mapMode === 'fleet') {
    loadAndStartFleetSimulation();
  }

  detections.forEach(d => {
    const card = document.createElement('div');
    card.className = `vehicle-card ${d.trackId === state.selectedTrackId ? 'active' : ''}`;
    card.onclick = () => {
      if (d.trackId) selectVehicle(d.trackId);
    };

    const typeName = (d.className || 'Araç').toUpperCase();
    const conf = Math.round((d.confidence || 0) * 100);

    card.innerHTML = `
      <div class="v-info">
        <div class="v-icon">
          <svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor">
            <path d="M18.92 6.01C18.72 5.42 18.16 5 17.5 5h-11c-.66 0-1.21.42-1.42 1.01L3 12v8c0 .55.45 1 1 1h1c.55 0 1-.45 1-1v-1h12v1c0 .55.45 1 1 1h1c.55 0 1-.45 1-1v-8l-2.08-5.99zM6.5 16c-.83 0-1.5-.67-1.5-1.5S5.67 13 6.5 13s1.5.67 1.5 1.5S7.33 16 6.5 16zm11 0c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5zM5 11l1.5-4.5h11L19 11H5z"/>
          </svg>
        </div>
        <div class="v-text">
          <div class="v-title">${typeName} <span style="color:var(--cyan-glow); font-size:0.72rem;">%${conf}</span></div>
          <div class="v-sub">${d.trackId ? `Track: ${d.trackId}` : 'Eşleşen Track Yok'} ${d.distanceMeters ? `(${Math.round(d.distanceMeters)}m)` : ''}</div>
        </div>
      </div>
      <div class="v-badge">${d.trackId || 'No Track'}</div>
    `;

    el.vehicleList.appendChild(card);
  });
}

// Track Selection & Synchronization
async function selectVehicle(trackId) {
  if (!trackId) return;
  state.selectedTrackId = trackId;
  audio.playBlip(980, 0.04);

  // If currently in fleet mode, switch to single focus mode to inspect this vehicle
  if (state.mapMode === 'fleet') {
    switchMapMode('single');
  }

  // Update canvas highlights
  drawBoundingBoxes(state.detections);
  renderVehicleList(state.detections);

  // Update HUD
  if (el.hudTrackId) el.hudTrackId.textContent = trackId;

  // 1. Fetch Trajectory Points for Map Animation
  await loadTrackTrajectory(trackId);

  // 2. Fetch Movement Profile & Compare Reports
  await loadTrackIntel(trackId);

  // 3. Fetch or Poll LLM Assessment
  await loadTrackAssessment(trackId);
}

// ============================================================
// MAP MODE SWITCHER: SINGLE VEHICLE vs. ALL VEHICLES (FLEET)
// ============================================================
async function switchMapMode(mode) {
  state.mapMode = mode;
  audio.playBlip(720, 0.05);

  if (mode === 'fleet') {
    if (el.tabSingleMode) el.tabSingleMode.classList.remove('active');
    if (el.tabFleetMode) el.tabFleetMode.classList.add('active');
    if (el.singleHudOverlay) el.singleHudOverlay.style.display = 'none';
    if (el.fleetHudOverlay) el.fleetHudOverlay.classList.remove('hidden');

    // Remove single track layers
    if (routePolyline) map.removeLayer(routePolyline);
    if (vehicleMarker) map.removeLayer(vehicleMarker);
    loiterMarkers.forEach(m => map.removeLayer(m));

    pauseSim();
    await loadAndStartFleetSimulation();
  } else {
    if (el.tabFleetMode) el.tabFleetMode.classList.remove('active');
    if (el.tabSingleMode) el.tabSingleMode.classList.add('active');
    if (el.fleetHudOverlay) el.fleetHudOverlay.classList.add('hidden');
    if (el.singleHudOverlay) el.singleHudOverlay.style.display = 'flex';

    clearFleetSimulation();

    // Restore single track
    if (state.selectedTrackId) {
      await loadTrackTrajectory(state.selectedTrackId);
    }
  }
}

// 1. Load Trajectory and Map Path (Single Vehicle Mode)
async function loadTrackTrajectory(trackId) {
  try {
    const resp = await fetch(`${API_BASE}/api/tracks/${trackId}/points`);
    if (!resp.ok) return;
    const points = await resp.json();
    state.trajectoryPoints = points || [];

    if (state.trajectoryPoints.length === 0) return;

    // Remove existing polyline & vehicle marker
    if (routePolyline) map.removeLayer(routePolyline);
    if (vehicleMarker) map.removeLayer(vehicleMarker);
    loiterMarkers.forEach(m => map.removeLayer(m));
    loiterMarkers = [];

    const latlngs = state.trajectoryPoints.map(p => [p.lat, p.lon]);

    // Draw Route Polyline
    routePolyline = L.polyline(latlngs, {
      color: '#00e5ff',
      weight: 3.5,
      opacity: 0.85,
      dashArray: '8, 6',
      lineCap: 'round'
    }).addTo(map);

    // Smooth tactical camera zoom directly into the selected vehicle!
    state.mapFollowVehicle = true;
    map.flyTo(latlngs[0], 16, { animate: true, duration: 1.2 });

    // Identify Stationary/Loitering Points along route
    findAndMarkLoiteringPoints(state.trajectoryPoints);

    // Initialize vehicle marker at start
    const vehicleIcon = L.divIcon({
      className: 'vehicle-sim-marker',
      html: `<svg viewBox="0 0 24 24" width="16" height="16" fill="#000"><circle cx="12" cy="12" r="8"/></svg>`,
      iconSize: [28, 28],
      iconAnchor: [14, 14]
    });

    vehicleMarker = L.marker(latlngs[0], { icon: vehicleIcon }).addTo(map);
    vehicleMarker.on('click', () => {
      state.mapFollowVehicle = true;
      map.flyTo(vehicleMarker.getLatLng(), 16, { animate: true, duration: 0.8 });
    });

    // Setup Slider
    if (el.simProgress) {
      el.simProgress.max = state.trajectoryPoints.length - 1;
      el.simProgress.value = 0;
    }

    // Start Simulation automatically
    state.simIndex = 0;
    startSim();

  } catch (e) {
    console.error('Trajectory error:', e);
  }
}

// ============================================================
// ALL VEHICLES (FLEET LIVE RADAR) - MULTI-VEHICLE SIMULATION
// ============================================================
async function loadAndStartFleetSimulation() {
  if (!el.fleetHudOverlay) return;

  const matchedDetections = (state.detections || []).filter(d => d.trackId);
  const uniqueTrackIds = [...new Set(matchedDetections.map(d => d.trackId))];

  if (el.fleetVehicleCount) el.fleetVehicleCount.textContent = uniqueTrackIds.length;

  if (uniqueTrackIds.length === 0) {
    el.fleetHudOverlay.innerHTML = `
      <div style="font-family:var(--font-mono); font-size:0.75rem; color:var(--text-dim); padding:10px 14px; background:rgba(15,23,42,0.9); border:1px solid var(--border-color); border-radius:8px;">
        💡 Görselde henüz eşleşmiş track ID bulunmamaktadır. Sol taraftan araç tespit edilmiş bir kare seçin veya görsel yükleyin.
      </div>
    `;
    return;
  }

  el.fleetHudOverlay.innerHTML = `
    <div style="font-family:var(--font-mono); font-size:0.75rem; color:var(--cyan-glow); padding:8px 12px; background:rgba(11,17,32,0.9); border-radius:8px; border:1px solid var(--border-color);">
      ⚡ Görseldeki ${uniqueTrackIds.length} aracın rotası yükleniyor ve filo radar simülasyonu başlatılıyor...
    </div>
  `;

  clearFleetSimulation();

  try {
    const promises = uniqueTrackIds.map(async (tid) => {
      const resp = await fetch(`${API_BASE}/api/tracks/${tid}/compare-reports`);
      if (!resp.ok) {
        const ptsResp = await fetch(`${API_BASE}/api/tracks/${tid}/points`);
        const pts = ptsResp.ok ? await ptsResp.json() : [];
        return { trackId: tid, points: pts, details: {}, profile: {} };
      }
      const data = await resp.json();
      const ptsResp = await fetch(`${API_BASE}/api/tracks/${tid}/points`);
      const pts = ptsResp.ok ? await ptsResp.json() : [];
      return {
        trackId: tid,
        points: pts,
        details: data.trackDetails || {},
        profile: data.movementProfile || {}
      };
    });

    const fleetResults = await Promise.all(promises);

    let maxPts = 0;
    const combinedBounds = L.latLngBounds([
      [state.baseLocation.lat, state.baseLocation.lon]
    ]);

    fleetVehicles = fleetResults.map((item, idx) => {
      const color = FLEET_COLORS[idx % FLEET_COLORS.length];
      const matchedDet = matchedDetections.find(d => d.trackId === item.trackId);
      const className = (matchedDet?.className || item.details?.observedClassNames?.[0] || 'Araç').toUpperCase();
      const points = item.points || [];

      if (points.length > maxPts) maxPts = points.length;

      // 1. Draw distinct glowing neon polyline for this vehicle
      const latlngs = points.map(p => [p.lat, p.lon]);
      let polyline = null;
      if (latlngs.length > 0) {
        polyline = L.polyline(latlngs, {
          color: color,
          weight: 3.5,
          opacity: 0.85,
          dashArray: '6, 6',
          lineCap: 'round'
        }).addTo(map);

        polyline.bindTooltip(`<b>${item.trackId}</b> (${className})<br>Rota Noktaları: ${points.length}`, {
          permanent: false,
          direction: 'top',
          className: 'tactical-tooltip'
        });

        fleetPolylines.push(polyline);
        combinedBounds.extend(polyline.getBounds());
      }

      // 2. Create custom live animated radar marker
      let marker = null;
      if (latlngs.length > 0) {
        const markerHtml = `
          <div class="fleet-sim-marker" style="--track-color:${color}; --track-color-alpha:${color}44;">
            <div class="fleet-marker-halo"></div>
            <div class="fleet-marker-body">
              <svg viewBox="0 0 24 24"><path d="M12 2L4.5 20.29l.71.71L12 18l6.79 3 .71-.71z"/></svg>
            </div>
            <div class="fleet-marker-tag">
              <span>${item.trackId}</span>
              <span class="tag-speed" id="tagSpeed_${item.trackId}">-- km/h</span>
            </div>
          </div>
        `;

        const icon = L.divIcon({
          className: 'fleet-icon-wrapper',
          html: markerHtml,
          iconSize: [32, 32],
          iconAnchor: [16, 16]
        });

        marker = L.marker(latlngs[0], { icon }).addTo(map);
        marker.bindPopup(`
          <div style="font-family:var(--font-mono); font-size:0.75rem;">
            <b style="color:${color}; font-size:0.85rem;">${item.trackId} (${className})</b><br>
            <span>Durum: ${item.profile?.status || 'Bilinmiyor'}</span><br>
            <span>Ort. Hız: ${item.profile?.avgSpeedMps ? (item.profile.avgSpeedMps * 3.6).toFixed(0) : '--'} km/h</span><br>
            <span>Üsse Mesafe: ${item.profile?.minDistanceMeters ? (item.profile.minDistanceMeters / 1000).toFixed(2) : '--'} km</span>
          </div>
        `);
        fleetMarkers.push(marker);
      }

      return {
        trackId: item.trackId,
        className: className,
        color: color,
        points: points,
        profile: item.profile,
        polyline: polyline,
        marker: marker
      };
    });

    state.fleetMaxPoints = maxPts;

    // Fit map to show all fleet paths and the base
    if (fleetPolylines.length > 0) {
      map.fitBounds(combinedBounds, { padding: [60, 60] });
    }

    // Render Fleet HUD Cards
    const statusLabels = {
      Approaching: 'ÜSSE YAKLAŞIYOR',
      Stationary: 'DURAĞAN',
      Orbiting: 'DÖNÜYOR/KEŞİF',
      MovingAway: 'UZAKLAŞIYOR'
    };

    el.fleetHudOverlay.innerHTML = fleetVehicles.map(v => {
      const st = statusLabels[v.profile?.status] || v.profile?.status || 'Aktif İz';
      return `
        <div class="fleet-vehicle-card" style="--track-color:${v.color}; --track-glow:${v.color}55;" onclick="focusFleetVehicle('${v.trackId}')" title="Bu araca odaklan">
          <div class="fleet-card-header">
            <div class="fleet-card-title">
              <span class="fleet-color-indicator"></span>
              <span>${v.trackId}</span>
            </div>
            <span class="fleet-card-class">${v.className}</span>
          </div>
          <div class="fleet-card-metrics">
            <span class="fleet-card-speed" id="fSpeed_${v.trackId}">-- km/h</span>
            <span class="fleet-card-dist" id="fDist_${v.trackId}">-- km üsse</span>
          </div>
          <div class="fleet-card-status" id="fStat_${v.trackId}">${st}</div>
        </div>
      `;
    }).join('');

    // Setup Slider
    if (el.simProgress) {
      el.simProgress.max = Math.max(0, maxPts - 1);
      el.simProgress.value = 0;
    }

    state.simIndex = 0;
    startSim();

    showToast(`Filo Simülasyonu: ${fleetVehicles.length} araç haritada aynı anda canlı hareket ediyor!`, 'info');

  } catch (err) {
    console.error('Fleet simulation error:', err);
    el.fleetHudOverlay.innerHTML = `<div style="color:var(--danger); font-size:0.75rem; padding:8px;">Hata: ${err.message}</div>`;
  }
}

function clearFleetSimulation() {
  fleetPolylines.forEach(p => map.removeLayer(p));
  fleetMarkers.forEach(m => map.removeLayer(m));
  fleetPolylines = [];
  fleetMarkers = [];
  fleetVehicles = [];
  state.fleetMaxPoints = 0;
}

function focusFleetVehicle(trackId) {
  const v = fleetVehicles.find(x => x.trackId === trackId);
  if (!v || !v.marker) return;
  const latlng = v.marker.getLatLng();
  map.panTo(latlng, { animate: true, duration: 0.6 });
  v.marker.openPopup();
  audio.playBlip(920, 0.05);
}

// Detect and mark points where the vehicle was stationary
function findAndMarkLoiteringPoints(points) {
  let stationaryStart = null;
  let stationaryCount = 0;

  for (let i = 1; i < points.length; i++) {
    const prev = points[i - 1];
    const curr = points[i];
    const dist = calcHaversineMeters(prev.lat, prev.lon, curr.lat, curr.lon);

    if (dist < 15) {
      if (!stationaryStart) stationaryStart = prev;
      stationaryCount++;
    } else {
      if (stationaryCount >= 4 && stationaryStart) {
        // More than 4 intervals of no movement -> mark loitering area
        const loiterIcon = L.divIcon({
          className: 'loiter-marker',
          html: `<span>🛑 DURAĞAN NOKTA</span>`,
          iconSize: [95, 20],
          iconAnchor: [47, 10]
        });
        const marker = L.marker([stationaryStart.lat, stationaryStart.lon], { icon: loiterIcon })
          .addTo(map)
          .bindPopup(`<b>Durağanlık Tespit Edildi</b><br>Araç bu koordinatta uzun süre bekleme/orbit yapmıştır.`);
        loiterMarkers.push(marker);
      }
      stationaryStart = null;
      stationaryCount = 0;
    }
  }
}

function getMaxSimLength() {
  if (state.mapMode === 'fleet') {
    return state.fleetMaxPoints || 0;
  }
  return state.trajectoryPoints ? state.trajectoryPoints.length : 0;
}

// Movement Simulation Functions (Supports both Single and Fleet Modes)
function startSim() {
  if (state.simTimer) clearInterval(state.simTimer);
  state.isSimPlaying = true;
  state.mapFollowVehicle = true;
  updatePlayButtonUI();

  // Smooth tactical camera zoom into vehicle as movement starts!
  if (state.mapMode !== 'fleet' && state.trajectoryPoints && state.trajectoryPoints.length > 0) {
    const curPt = state.trajectoryPoints[state.simIndex] || state.trajectoryPoints[0];
    if (curPt && map) {
      const targetZoom = Math.max(map.getZoom(), 16);
      map.flyTo([curPt.lat, curPt.lon], targetZoom, { animate: true, duration: 0.8 });
    }
  }

  const maxLen = getMaxSimLength();
  state.simTimer = setInterval(() => {
    if (state.simIndex < maxLen - 1) {
      state.simIndex++;
      updateSimFrame();
    } else {
      pauseSim();
    }
  }, 1000 / state.simSpeedMultiplier);
}

function pauseSim() {
  state.isSimPlaying = false;
  if (state.simTimer) clearInterval(state.simTimer);
  updatePlayButtonUI();
}

function toggleSimPlay() {
  const maxLen = getMaxSimLength();
  if (state.isSimPlaying) {
    pauseSim();
  } else {
    state.mapFollowVehicle = true;
    if (state.simIndex >= maxLen - 1) state.simIndex = 0;
    startSim();
  }
}

function restartSim() {
  state.simIndex = 0;
  state.mapFollowVehicle = true;
  updateSimFrame();
  startSim();
}

function seekSim(index) {
  const maxLen = getMaxSimLength();
  state.simIndex = Math.max(0, Math.min(index, maxLen - 1));
  state.mapFollowVehicle = true;
  updateSimFrame();
  if (state.mapMode !== 'fleet' && state.trajectoryPoints && state.trajectoryPoints.length > 0) {
    const pt = state.trajectoryPoints[state.simIndex];
    if (pt && map) map.panTo([pt.lat, pt.lon], { animate: true, duration: 0.2 });
  }
}

function updatePlayButtonUI() {
  if (!el.btnPlayPause) return;
  el.btnPlayPause.innerHTML = state.isSimPlaying
    ? `<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor"><path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z"/></svg>`
    : `<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>`;
}

function updateSimFrame() {
  if (state.mapMode === 'fleet') {
    updateFleetSimFrame();
  } else {
    updateSingleSimFrame();
  }
}

function updateFleetSimFrame() {
  if (!fleetVehicles || fleetVehicles.length === 0) return;

  let commonTime = null;

  fleetVehicles.forEach(v => {
    const idx = Math.min(state.simIndex, v.points.length - 1);
    const pt = v.points[idx];
    if (!pt) return;
    if (!commonTime && pt.time) commonTime = pt.time;

    // Move marker
    if (v.marker) {
      v.marker.setLatLng([pt.lat, pt.lon]);
    }

    // Distance to base
    const distKm = calcHaversineMeters(pt.lat, pt.lon, state.baseLocation.lat, state.baseLocation.lon) / 1000;

    // Speed
    let speedMs = 0;
    if (idx > 0) {
      const prev = v.points[idx - 1];
      const m = calcHaversineMeters(prev.lat, prev.lon, pt.lat, pt.lon);
      speedMs = m / 30;
    }
    const speedKmh = speedMs * 3.6;

    // Floating tag
    const tagEl = document.getElementById(`tagSpeed_${v.trackId}`);
    if (tagEl) tagEl.textContent = `${speedKmh.toFixed(0)} km/h`;

    // Fleet HUD Card
    const fSpeed = document.getElementById(`fSpeed_${v.trackId}`);
    const fDist = document.getElementById(`fDist_${v.trackId}`);
    if (fSpeed) fSpeed.textContent = `${speedKmh.toFixed(0)} km/h (${speedMs.toFixed(1)} m/s)`;
    if (fDist) fDist.textContent = `${distKm.toFixed(2)} km üsse`;
  });

  if (el.simProgress) el.simProgress.value = state.simIndex;
  if (el.simTimeLabel) el.simTimeLabel.textContent = commonTime || '--:--:--';
}

function updateSingleSimFrame() {
  if (!state.trajectoryPoints || state.trajectoryPoints.length === 0) return;
  const pt = state.trajectoryPoints[state.simIndex];
  if (!pt) return;

  // Move marker
  if (vehicleMarker) {
    vehicleMarker.setLatLng([pt.lat, pt.lon]);
  }

  // Smooth camera auto-follow while vehicle is moving
  if (state.isSimPlaying && state.mapFollowVehicle && map) {
    map.panTo([pt.lat, pt.lon], { animate: true, duration: 0.25 });
  }

  // Update slider
  if (el.simProgress) el.simProgress.value = state.simIndex;
  if (el.simTimeLabel) el.simTimeLabel.textContent = pt.time || '--:--:--';

  // Calculate live distance to military base
  const distKm = calcHaversineMeters(pt.lat, pt.lon, state.baseLocation.lat, state.baseLocation.lon) / 1000;
  if (el.hudDistance) el.hudDistance.textContent = `${distKm.toFixed(2)} km`;

  // Calculate instantaneous speed and ETA
  let speedMs = 0;
  if (state.simIndex > 0) {
    const prev = state.trajectoryPoints[state.simIndex - 1];
    const m = calcHaversineMeters(prev.lat, prev.lon, pt.lat, pt.lon);
    speedMs = m / 30;
  }

  const speedKmh = speedMs * 3.6;
  if (el.hudSpeed) el.hudSpeed.textContent = `${speedMs.toFixed(1)} m/s (${speedKmh.toFixed(0)} km/h)`;

  // ETA Calculation to base
  if (el.hudEta) {
    if (speedMs > 0.4 && state.movementProfile?.status === 'Approaching') {
      const remainingSeconds = (distKm * 1000) / speedMs;
      const mins = Math.floor(remainingSeconds / 60);
      const secs = Math.floor(remainingSeconds % 60);
      el.hudEta.textContent = `${mins} dk ${secs} sn`;
      el.hudEta.className = 'hud-stat-val warning';
    } else if (state.movementProfile?.status === 'Stationary' || speedMs < 0.2) {
      el.hudEta.textContent = 'DURAĞAN (BEKLİYOR)';
      el.hudEta.className = 'hud-stat-val safe';
    } else if (state.movementProfile?.status === 'Orbiting') {
      el.hudEta.textContent = 'DÖNGÜSEL (ÇEVREDE DÖNÜYOR)';
      el.hudEta.className = 'hud-stat-val warning';
    } else {
      el.hudEta.textContent = 'UZAKLAŞIYOR / BELİRSİZ';
      el.hudEta.className = 'hud-stat-val';
    }
  }
}

// 2. Load Movement Profile, Field Reports & Conflict Detection
async function loadTrackIntel(trackId) {
  try {
    const resp = await fetch(`${API_BASE}/api/tracks/${trackId}/compare-reports`);
    if (!resp.ok) return;
    const data = await resp.json();
    state.lastIntelData = data;
    state.movementProfile = data.movementProfile;
    state.fieldReports = data.candidateFieldReports || [];

    // Contextual status before or after LLM assessment
    const currentVerdict = (state.assessment?.verdict || '').toLowerCase();
    const isSafe = currentVerdict === 'safe';
    const isUnsafe = currentVerdict === 'unsafe';

    if (el.hudStatus && data.movementProfile) {
      const st = data.movementProfile.status;
      if (st === 'Approaching') {
        if (isSafe) {
          el.hudStatus.textContent = 'ÜSSE YAKLAŞIYOR (ONAYLI DOST UNSUR)';
          el.hudStatus.className = 'hud-stat-val safe';
        } else if (isUnsafe) {
          el.hudStatus.textContent = 'YAKLAŞIYOR (YÜKSEK TEHDİT RİSKİ)';
          el.hudStatus.className = 'hud-stat-val danger';
        } else {
          el.hudStatus.textContent = 'YAKLAŞIYOR (İSTİHBARAT İNCELENİYOR)';
          el.hudStatus.className = 'hud-stat-val cyan';
        }
      } else if (st === 'Stationary') {
        el.hudStatus.textContent = isSafe ? 'DURAĞAN (GÜVENLİ - BEKLİYOR)' : 'DURAĞAN (HAREKETSİZ)';
        el.hudStatus.className = isSafe ? 'hud-stat-val safe' : 'hud-stat-val warning';
      } else if (st === 'Orbiting') {
        el.hudStatus.textContent = isSafe ? 'DÖNÜYOR (GÖREV UYGUN)' : 'DÖNÜYOR (KEŞİF PATERNI)';
        el.hudStatus.className = isSafe ? 'hud-stat-val safe' : 'hud-stat-val warning';
      } else {
        el.hudStatus.textContent = 'UZAKLAŞIYOR (GÜVENLİ)';
        el.hudStatus.className = 'hud-stat-val safe';
      }
    }

    // Run Intelligence Contradiction Engine
    analyzeContradictions(data, state.assessment);

    // Generate Tactical Threat Scenarios
    generateThreatScenarios(data, state.assessment);

    // Render Field Reports list
    renderFieldReports(state.fieldReports);

  } catch (e) {
    console.error('Track Intel error:', e);
  }
}

// Intelligence Contradiction Engine: compares ground truth vs field reports
function analyzeContradictions(data, asmt = state.assessment) {
  if (!el.conflictList) return;
  el.conflictList.innerHTML = '';

  const conflicts = [];
  const movement = data.movementProfile || {};
  const details = data.trackDetails || {};
  const reports = data.candidateFieldReports || [];
  const observedClasses = (details.observedClassNames || []).map(c => c.toLowerCase());

  const verdict = (asmt?.verdict || '').toLowerCase();
  const isSafe = verdict === 'safe';
  const isUnsafe = verdict === 'unsafe';

  // Check if official report confirmed friendly unit
  const hasOfficialFriendly = reports.some(r => {
    const txt = (r.rawText || '').toLowerCase();
    return (r.source === 'official') && (txt.includes('bağlı unsur') || txt.includes('bagli unsur') || txt.includes('önceden bildiril') || txt.includes('onceden bildiril'));
  });

  reports.forEach(r => {
    const text = (r.rawText || '').toLowerCase();

    // Check Class Mismatch
    if (text.includes('otomobil') || text.includes('binek')) {
      if (observedClasses.includes('truck') || observedClasses.includes('kamyon')) {
        conflicts.push({
          type: 'danger',
          title: `Sınıf Tanımlama Çelişkisi (Rapor: ${r.time})`,
          desc: `Saha raporu '${r.rawText}' otomobil bildirirken, sistemimizin YOLO tespiti aracın KAMYON olduğunu kesinleştirmiştir. Rapor kaynağı yanıltıcı veya hedefin kimliği gizlenmiş olabilir.`
        });
      }
    }

    // Check Loitering / Movement Mismatch
    if (text.includes('hareket') || text.includes('ilerliyor') || text.includes('yolda')) {
      if (movement.status === 'Stationary' && movement.statusDurationMinutes > 30) {
        conflicts.push({
          type: 'warning',
          title: `Durağanlık / Hareket Çelişkisi (Rapor: ${r.time})`,
          desc: `Raporda aracın seyir halinde olduğu iddia ediliyor; ancak telemetrimiz aracın ${Math.round(movement.statusDurationMinutes)} dakikadır hareketsiz beklediğini kanıtlamaktadır.`
        });
      }
    }

    // Check Radio Silence / Ambush Correlation (Harmonized with LLM & Official Intelligence)
    if (text.includes('telsiz') || text.includes('bağlantı kurulamıyor') || text.includes('irtibat')) {
      if (movement.status === 'Approaching') {
        if (isSafe || hasOfficialFriendly) {
          conflicts.push({
            type: 'safe',
            title: `İrtibat Kesintisi Notu (${r.time})`,
            desc: `Bölgedeki telsiz irtibat kesintisi ihbarı incelenmiştir. Resmi saha raporu ve GLM yapay zeka istihbarat değerlendirmesi bu aracın önceden bildirilmiş dost unsur olduğunu teyit ettiği için pusu/sabotaj şüphesi elenmiştir. Rutin kapı kimlik teyidi yeterlidir.`
          });
        } else if (isUnsafe) {
          conflicts.push({
            type: 'danger',
            title: `Kritik İrtibat Kesintisi Korelasyonu (${r.time})`,
            desc: `Bölgedeki askeri devriye ile telsiz bağlantısının kesildiği saat penceresi, bu aracın üsse doğru yaklaşma zamanıyla örtüşmektedir! Sızma veya taarruz şüphesi teyakkuz seviyesindedir.`
          });
        } else {
          conflicts.push({
            type: 'warning',
            title: `İrtibat Kesintisi & Yaklaşma Korelasyonu (${r.time})`,
            desc: `Telsiz bağlantısının kesildiği zaman aralığında aracın yaklaşması teyakkuz sebebi oluşturmaktadır. İstihbarat modeli nihai teyit yapana kadar ihtiyatla yaklaşılmalıdır.`
          });
        }
      }
    }
  });

  if (conflicts.length === 0) {
    el.conflictList.innerHTML = '<div style="font-size:0.75rem; color:var(--safe); padding:6px 0;">✓ Saha raporları ile tespit verileri arasında doğrudan çelişki bulunamadı.</div>';
  } else {
    conflicts.forEach(c => {
      const card = document.createElement('div');
      card.className = `conflict-card ${c.type}`;
      card.innerHTML = `
        <div class="conflict-header ${c.type}">
          <svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor"><path d="M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z"/></svg>
          ${c.title}
        </div>
        <div class="conflict-desc">${c.desc}</div>
      `;
      el.conflictList.appendChild(card);
    });
  }
}

// Tactical Threat Scenarios Evaluator (Harmonized with LLM Assessment)
function generateThreatScenarios(data, asmt = state.assessment) {
  if (!el.scenarioList) return;
  el.scenarioList.innerHTML = '';

  const movement = data.movementProfile || {};
  const reports = data.candidateFieldReports || [];

  const verdict = (asmt?.verdict || '').toLowerCase();
  const isSafe = verdict === 'safe';
  const isUnsafe = verdict === 'unsafe';

  const hasOfficialFriendly = reports.some(r => {
    const txt = (r.rawText || '').toLowerCase();
    return (r.source === 'official') && (txt.includes('bağlı unsur') || txt.includes('bagli unsur') || txt.includes('önceden bildiril') || txt.includes('onceden bildiril'));
  });

  const scenarios = [
    {
      id: 'kamikaze',
      name: 'Senaryo 1: Hızlı Üs Taarruzu / İntihar Yaklaşımı',
      calcRisk: () => {
        if (isSafe || hasOfficialFriendly) return 'low';
        if (isUnsafe) return 'high';
        if (movement.status === 'Approaching' && (movement.distanceToBaseKmNow < 3.5)) return 'high';
        if (movement.status === 'Approaching') return 'medium';
        return 'low';
      },
      action: (r) => {
        if (isSafe || hasOfficialFriendly) return 'Resmi teyitli dost unsur; taarruz riski bulunmamaktadır. Standart kapı intikal protokolü uygulanır.';
        return r === 'high' ? 'Savunma bataryaları hedefe kilitlenmeli, 1km hattında ikaz ateşi açılmalı.' : 'Üs giriş kapıları teyakkuzda tutulmalı.';
      }
    },
    {
      id: 'recon',
      name: 'Senaryo 2: Keşif & İstihbarat Toplama (Surveillance)',
      calcRisk: () => {
        if (isSafe || hasOfficialFriendly) return 'low';
        if (movement.status === 'Orbiting' || (movement.orbitScore < 0.25 && movement.statusDurationMinutes > 40)) return 'high';
        if (movement.orbitScore < 0.4) return 'medium';
        return 'low';
      },
      action: (r) => {
        if (isSafe || hasOfficialFriendly) return 'Hedefin hareket profili ve resmi intikal kaydı uyumlu; rutin gözlem.';
        return r === 'high' ? 'Hedefin optik/elektronik sinyal yayılımı taranmalı, jammer devreye alınmalı.' : 'Radyo frekans dinlemesi sürdürülmeli.';
      }
    },
    {
      id: 'ambush',
      name: 'Senaryo 3: Konvoy Pususu & İletişim Sabotajı',
      calcRisk: () => {
        if (isSafe || hasOfficialFriendly) return 'low';
        const hasRadio = reports.some(r => (r.rawText || '').includes('telsiz'));
        return hasRadio ? 'high' : 'low';
      },
      action: (r) => {
        if (isSafe || hasOfficialFriendly) return 'Bölgedeki telsiz kesintisi bu araçla ilintisiz değerlendirilmiştir; dost unsur için ek tehdit görülmüyor.';
        return r === 'high' ? 'Kuzey Yolu hattına İHA devriyesi yönlendirilmeli, alternatif frekansa geçilmeli.' : 'Rutin devriye raporları bekleniyor.';
      }
    },
    {
      id: 'deception',
      name: 'Senaryo 4: Dost İkmal Kılıfında Sahte İntikal',
      calcRisk: () => {
        if (isSafe) return 'low';
        const hasSupply = reports.some(r => (r.rawText || '').includes('ikmal') || (r.rawText || '').includes('teyidi') || (r.rawText || '').includes('bağlı'));
        if (hasSupply && (movement.status === 'Approaching' || movement.status === 'Orbiting')) return 'medium';
        return 'low';
      },
      action: (r) => {
        if (isSafe) return 'Giriş kontrol noktasında standart plaka ve kimlik teyidi yapılması yeterlidir.';
        return r === 'medium' ? 'Giriş kontrol noktasında fiziki evrak ve sürücü biyometrik teyidi zorunlu.' : 'Planlı teslimat listesiyle uyumlu.';
      }
    }
  ];

  scenarios.forEach(sc => {
    const risk = sc.calcRisk();
    const riskLabel = risk === 'high' ? 'YÜKSEK TEHDİT' : risk === 'medium' ? 'ORTA TEHDİT' : 'DÜŞÜK RİSK';
    const card = document.createElement('div');
    card.className = 'scenario-card';
    card.innerHTML = `
      <div class="sc-top">
        <div class="sc-title">
          <svg viewBox="0 0 24 24" width="13" height="13" fill="var(--cyan-glow)"><path d="M12 2L4 5v6.09c0 5.05 3.41 9.76 8 10.91 4.59-1.15 8-5.86 8-10.91V5l-8-3z"/></svg>
          ${sc.name}
        </div>
        <div class="sc-risk-badge ${risk}">${riskLabel}</div>
      </div>
      <div class="sc-action">Taktik Eylem: ${sc.action(risk)}</div>
    `;
    el.scenarioList.appendChild(card);
  });
}

function renderFieldReports(reports) {
  if (!el.fieldReportList) return;
  el.fieldReportList.innerHTML = '';

  if (reports.length === 0) {
    el.fieldReportList.innerHTML = '<div style="font-size:0.75rem; color:var(--text-dim);">Bu track için zaman penceresinde aday saha raporu yok.</div>';
    return;
  }

  reports.forEach(r => {
    const div = document.createElement('div');
    div.style.cssText = 'padding:6px 8px; background:rgba(255,255,255,0.03); border-radius:6px; font-size:0.72rem; line-height:1.4; border-left:2px solid var(--border-color);';
    div.innerHTML = `
      <div style="font-family:var(--font-mono); color:var(--cyan-glow); font-size:0.68rem; margin-bottom:2px;">[${r.time}] (${r.source.toUpperCase()})</div>
      <div style="color:#e2e8f0;">"${r.rawText}"</div>
    `;
    el.fieldReportList.appendChild(div);
  });
}

// 3. Load & Poll LLM Assessment
async function loadTrackAssessment(trackId) {
  if (el.verdictTag) el.verdictTag.textContent = 'DEĞERLENDİRİLİYOR...';
  if (el.confidencePct) el.confidencePct.textContent = '--%';
  if (el.verdictBanner) el.verdictBanner.className = 'verdict-banner';
  if (el.reasoningText) el.reasoningText.textContent = 'GLM-5.3 İstihbarat Ajanı analiz yapıyor ve tool-calling döngüsünü yürütüyor...';

  // First check if already assessed
  for (let attempt = 1; attempt <= 15; attempt++) {
    try {
      const resp = await fetch(`${API_BASE}/api/tracks/${trackId}/assessment`);
      if (resp.ok) {
        const assessment = await resp.json();
        renderAssessment(assessment);
        return;
      }
    } catch (e) {}

    // Wait 2s before retry
    await new Promise(r => setTimeout(r, 2000));
  }

  if (el.reasoningText) el.reasoningText.textContent = 'Değerlendirme zaman aşımına uğradı veya arka planda devam ediyor.';
}

function renderAssessment(asmt) {
  state.assessment = asmt;
  const verdict = (asmt.verdict || 'suspicious').toLowerCase();
  
  if (el.verdictBanner) el.verdictBanner.className = `verdict-banner ${verdict}`;
  
  const verdictTitles = {
    safe: 'GÜVENLİ (SAFE)',
    suspicious: 'ŞÜPHELİ (SUSPICIOUS)',
    unsafe: 'TEHLİKELİ (UNSAFE)'
  };

  if (el.verdictTag) el.verdictTag.textContent = verdictTitles[verdict] || verdict.toUpperCase();
  if (el.confidencePct) el.confidencePct.textContent = `%${Math.round((asmt.confidence || 0) * 100)}`;
  if (el.verdictMeta) el.verdictMeta.textContent = `Model: ${asmt.modelVersion || 'GLM-5.3-Flash'} | ${asmt.createdAtUtc ? new Date(asmt.createdAtUtc).toLocaleTimeString('tr-TR') : ''}`;

  if (el.reasoningText) el.reasoningText.textContent = asmt.reasoning || 'Gerekçe belirtilmedi.';

  // Evidence references
  if (el.evidenceContainer) {
    el.evidenceContainer.innerHTML = '';
    const refs = asmt.evidenceRefs || [];
    refs.forEach(ref => {
      const tag = document.createElement('span');
      tag.style.cssText = 'background:rgba(0,229,255,0.12); color:var(--cyan-glow); border:1px solid rgba(0,229,255,0.25); border-radius:4px; padding:2px 8px; font-size:0.68rem; font-family:var(--font-mono);';
      tag.textContent = ref;
      el.evidenceContainer.appendChild(tag);
    });
  }

  // Complete harmonization with HUD, contradictions, and scenario matrices
  updateAssessmentHarmonization(asmt);

  if (verdict === 'unsafe') audio.playAlarm();
  else if (verdict === 'suspicious') audio.playBlip(600, 0.15);
  else audio.playBlip(880, 0.08);
}

function updateAssessmentHarmonization(asmt) {
  if (!asmt) return;
  const verdict = (asmt.verdict || '').toLowerCase();
  const isSafe = verdict === 'safe';
  const isUnsafe = verdict === 'unsafe';

  // 1. Harmonize HUD Movement Profile Status
  if (el.hudStatus && state.movementProfile) {
    const st = state.movementProfile.status;
    if (st === 'Approaching') {
      if (isSafe) {
        el.hudStatus.textContent = 'ÜSSE YAKLAŞIYOR (ONAYLI DOST UNSUR)';
        el.hudStatus.className = 'hud-stat-val safe';
      } else if (isUnsafe) {
        el.hudStatus.textContent = 'YAKLAŞIYOR (YÜKSEK TEHDİT RİSKİ)';
        el.hudStatus.className = 'hud-stat-val danger';
      } else {
        el.hudStatus.textContent = 'YAKLAŞIYOR (ŞÜPHELİ / İNCELENİYOR)';
        el.hudStatus.className = 'hud-stat-val warning';
      }
    } else if (st === 'Stationary') {
      el.hudStatus.textContent = isSafe ? 'DURAĞAN (GÜVENLİ - BEKLİYOR)' : 'DURAĞAN (HAREKETSİZ)';
      el.hudStatus.className = isSafe ? 'hud-stat-val safe' : 'hud-stat-val warning';
    } else if (st === 'Orbiting') {
      el.hudStatus.textContent = isSafe ? 'DÖNÜYOR (GÖREV UYGUN)' : 'DÖNÜYOR (KEŞİF PATERNI)';
      el.hudStatus.className = isSafe ? 'hud-stat-val safe' : 'hud-stat-val warning';
    } else if (st === 'MovingAway') {
      el.hudStatus.textContent = 'UZAKLAŞIYOR (GÜVENLİ)';
      el.hudStatus.className = 'hud-stat-val safe';
    }
  }

  // 2. Harmonize ETA color
  if (el.hudEta && state.movementProfile?.status === 'Approaching') {
    el.hudEta.className = isSafe ? 'hud-stat-val safe' : isUnsafe ? 'hud-stat-val danger' : 'hud-stat-val warning';
  }

  // 3. Re-run Contradiction & Threat Scenario engines with updated assessment context
  if (state.lastIntelData) {
    analyzeContradictions(state.lastIntelData, asmt);
    generateThreatScenarios(state.lastIntelData, asmt);
  }
}

// Service Health Checker
async function checkServicesHealth() {
  // Backend
  try {
    const r = await fetch(`${API_BASE}/swagger/v1/swagger.json`, { method: 'HEAD' });
    if (el.dotBackend) el.dotBackend.className = r.ok ? 'status-dot active' : 'status-dot error';
  } catch (e) {
    if (el.dotBackend) el.dotBackend.className = 'status-dot error';
  }

  // Detection
  try {
    const r = await fetch(`${DETECTION_API}/health`);
    if (el.dotDetection) el.dotDetection.className = r.ok ? 'status-dot active' : 'status-dot error';
  } catch (e) {
    if (el.dotDetection) el.dotDetection.className = 'status-dot error';
  }

  // Agent
  try {
    const r = await fetch(`${AGENT_API}/health`);
    if (el.dotAgent) el.dotAgent.className = r.ok ? 'status-dot active' : 'status-dot error';
  } catch (e) {
    if (el.dotAgent) el.dotAgent.className = 'status-dot error';
  }
}

// Utility: Haversine distance in meters
function calcHaversineMeters(lat1, lon1, lat2, lon2) {
  const R = 6371000;
  const dLat = (lat2 - lat1) * Math.PI / 180;
  const dLon = (lon2 - lon1) * Math.PI / 180;
  const a = Math.sin(dLat / 2) * Math.sin(dLat / 2) +
            Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) *
            Math.sin(dLon / 2) * Math.sin(dLon / 2);
  const c = 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
  return R * c;
}

// Tactical Toast Notification
function showToast(msg, type = 'info') {
  let container = document.getElementById('toastContainer');
  if (!container) {
    container = document.createElement('div');
    container.id = 'toastContainer';
    document.body.appendChild(container);
  }

  const toast = document.createElement('div');
  toast.className = 'toast';
  toast.innerHTML = `
    <svg viewBox="0 0 24 24" width="16" height="16" fill="var(--cyan-glow)"><path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z"/></svg>
    <span>${msg}</span>
  `;
  container.appendChild(toast);

  setTimeout(() => {
    toast.style.opacity = '0';
    toast.style.transition = 'opacity 0.4s ease';
    setTimeout(() => toast.remove(), 400);
  }, 3500);
}
