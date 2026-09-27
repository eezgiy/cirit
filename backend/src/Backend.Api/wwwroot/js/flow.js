/**
 * ROKETSAN // ANALİZ AKIŞI
 * Adım adım: servis sağlığı -> referans veri yükleme -> görsel yükleme -> LLM sonuçları.
 * Sistemin hiçbir veriyi kendiliğinden okumadığı (bkz. backend), her adımın gerçek bir
 * yükleme eylemine karşılık geldiği bir "wizard" akışıdır.
 */

const API_BASE = window.location.origin.includes('5299') ? window.location.origin : 'http://localhost:5299';
const DETECTION_API = 'http://localhost:8002';
const AGENT_API = 'http://localhost:8001';

const REFDATA_ENDPOINTS = {
  zones: '/api/reference-data/zones',
  tracks: '/api/reference-data/tracks',
  imageMeta: '/api/reference-data/image-meta',
  fieldReports: '/api/reference-data/field-reports',
};

// Aynı verinin zaten backend'de olup olmadığını kontrol etmek için kullanılan salt-okunur
// karşılıkları (yükleme endpoint'leri değil).
const REFDATA_CHECK_ENDPOINTS = {
  zones: '/api/zones',
  tracks: '/api/tracks',
  imageMeta: '/api/images/metadata',
  fieldReports: '/api/field-reports',
};

// Backend (ASP.NET Core, Scoped DbContext) ve detection servisi (ThreadingHTTPServer + tekil
// paylaşılan YOLO modeli) eşzamanlı isteklerde doğrulanmış şekilde çalışıyor - bu yüzden görsel
// yükleme artık tek tek değil, sınırlı sayıda paralel "worker" ile yapılır. Sınırsız paralellik
// yerine küçük bir tavan seçildi ki CPU'da tek YOLO modelini aynı anda çok fazla isteğe
// boğmayalım (bkz. LLM agent'taki AGENT_MAX_CONCURRENCY ile aynı mantık).
const IMAGE_UPLOAD_CONCURRENCY = 3;

const state = {
  currentStep: 1,
  refDataDone: { zones: false, tracks: false, imageMeta: false, fieldReports: false },
  uploadedImages: [],       // { fileName, status, detectionCount, matchedTrackIds }
  matchedTrackIds: new Set(),
};

const el = {};

document.addEventListener('DOMContentLoaded', () => {
  initDOM();
  setupEventListeners();
  checkServicesHealth();
  checkExistingReferenceData();
  setInterval(checkServicesHealth, 10000);
  goToStep(1);
});

function initDOM() {
  el.flowStepper = document.getElementById('flowStepper');
  el.panels = {
    1: document.getElementById('panelStep1'),
    2: document.getElementById('panelStep2'),
    3: document.getElementById('panelStep3'),
    4: document.getElementById('panelStep4'),
  };

  el.healthBackend = document.getElementById('healthBackend');
  el.healthDetection = document.getElementById('healthDetection');
  el.healthAgent = document.getElementById('healthAgent');
  el.dotBackend = document.getElementById('dotBackend');
  el.dotDetection = document.getElementById('dotDetection');
  el.dotAgent = document.getElementById('dotAgent');

  el.btnStartFlow = document.getElementById('btnStartFlow');
  el.btnStep2Next = document.getElementById('btnStep2Next');
  el.btnStep3Next = document.getElementById('btnStep3Next');
  el.btnRefreshResults = document.getElementById('btnRefreshResults');
  el.btnResetFlow = document.getElementById('btnResetFlow');

  el.refdataGrid = document.getElementById('refdataGrid');

  el.flowDropZone = document.getElementById('flowDropZone');
  el.flowFileInput = document.getElementById('flowFileInput');
  el.flowImageList = document.getElementById('flowImageList');
  el.flowImageCount = document.getElementById('flowImageCount');

  el.resultsSummary = document.getElementById('resultsSummary');
  el.resultsGrid = document.getElementById('resultsGrid');
}

// ============================================================
// STEP NAVIGATION
// ============================================================
function goToStep(n) {
  state.currentStep = n;

  Object.entries(el.panels).forEach(([num, panel]) => {
    panel.classList.toggle('hidden', Number(num) !== n);
  });

  if (el.flowStepper) {
    el.flowStepper.querySelectorAll('.flow-step-indicator').forEach(ind => {
      const step = Number(ind.dataset.step);
      ind.classList.toggle('active', step === n);
      ind.classList.toggle('done', step < n);
    });
  }

  if (n === 4) {
    loadResults();
  }

  window.scrollTo({ top: 0, behavior: 'smooth' });
}

function setupEventListeners() {
  if (el.btnStartFlow) el.btnStartFlow.onclick = () => goToStep(2);

  document.querySelectorAll('.flow-back-btn').forEach(btn => {
    btn.onclick = () => goToStep(Number(btn.dataset.goto));
  });

  if (el.btnStep2Next) el.btnStep2Next.onclick = () => goToStep(3);
  if (el.btnStep3Next) el.btnStep3Next.onclick = () => goToStep(4);
  if (el.btnRefreshResults) el.btnRefreshResults.onclick = () => loadResults();
  if (el.btnResetFlow) el.btnResetFlow.onclick = resetFlow;

  setupReferenceDataCards();
  setupImageUpload();
}

// ============================================================
// SERVICE HEALTH (step 1)
// ============================================================
async function checkServicesHealth() {
  setHealthState('backend', false);
  setHealthState('detection', false);
  setHealthState('agent', false);

  try {
    const r = await fetch(`${API_BASE}/swagger/v1/swagger.json`, { method: 'HEAD' });
    setHealthState('backend', r.ok);
  } catch (e) { /* offline */ }

  try {
    const r = await fetch(`${DETECTION_API}/health`);
    setHealthState('detection', r.ok);
  } catch (e) { /* offline */ }

  try {
    const r = await fetch(`${AGENT_API}/health`);
    setHealthState('agent', r.ok);
  } catch (e) { /* offline */ }
}

function setHealthState(service, online) {
  const pillMap = { backend: el.healthBackend, detection: el.healthDetection, agent: el.healthAgent };
  const dotMap = { backend: el.dotBackend, detection: el.dotDetection, agent: el.dotAgent };

  const pill = pillMap[service];
  if (pill) {
    pill.classList.toggle('online', online);
    pill.classList.toggle('offline', !online);
  }
  const dot = dotMap[service];
  if (dot) {
    dot.classList.toggle('active', online);
    dot.classList.toggle('error', !online);
  }
}

// ============================================================
// STEP 2: REFERENCE DATA UPLOAD
// ============================================================
function setupReferenceDataCards() {
  if (!el.refdataGrid) return;

  el.refdataGrid.querySelectorAll('.refdata-card').forEach(card => {
    const key = card.dataset.key;
    const input = card.querySelector('.refdata-input');
    const btn = card.querySelector('.refdata-upload-btn');
    const status = card.querySelector('.refdata-status');

    btn.onclick = async () => {
      const file = input.files[0];
      if (!file) {
        status.textContent = 'Önce bir dosya seçin.';
        card.classList.add('error');
        card.classList.remove('done');
        return;
      }

      card.classList.remove('error', 'done');
      status.textContent = 'Yükleniyor...';
      btn.disabled = true;

      try {
        const formData = new FormData();
        formData.append('file', file);

        const resp = await fetch(`${API_BASE}${REFDATA_ENDPOINTS[key]}`, {
          method: 'POST',
          body: formData,
        });

        if (!resp.ok) {
          const err = await resp.json().catch(() => ({ error: 'Yükleme başarısız.' }));
          throw new Error(err.error || 'Yükleme başarısız.');
        }

        const data = await resp.json();
        status.textContent = `✓ Yüklendi (${data.upserted} kayıt)`;
        card.classList.add('done');
        state.refDataDone[key] = true;
        showToast(`${file.name} yüklendi (${data.upserted} kayıt).`);
      } catch (err) {
        status.textContent = `✗ Hata: ${err.message}`;
        card.classList.add('error');
        state.refDataDone[key] = false;
        showToast(`Hata: ${err.message}`, 'error');
      } finally {
        btn.disabled = false;
        updateStep2NextButton();
      }
    };
  });
}

function updateStep2NextButton() {
  const allDone = Object.values(state.refDataDone).every(Boolean);
  if (el.btnStep2Next) el.btnStep2Next.disabled = !allDone;
}

// Sayfa her açıldığında backend'de bu dört veri setinden herhangi biri zaten varsa (önceki bir
// oturumdan kalmış olabilir), ilgili kartı otomatik "yüklü" işaretler - aynı dosyaları tekrar
// seçip yüklemeye zorlamaz. Kullanıcı isterse yine de yeni bir dosya seçip üzerine yükleyebilir
// (upsert olduğu için güvenli).
async function checkExistingReferenceData() {
  await Promise.all(Object.entries(REFDATA_CHECK_ENDPOINTS).map(async ([key, path]) => {
    try {
      const resp = await fetch(`${API_BASE}${path}`);
      if (!resp.ok) return;
      const data = await resp.json();
      const count = Array.isArray(data) ? data.length : 0;
      if (count > 0) {
        markReferenceDataAlreadyLoaded(key, count);
      }
    } catch (e) {
      // Backend henüz ayakta değil veya erişilemiyor - Adım 2'ye ulaşınca zaten manuel yükleme yapılabilir.
    }
  }));
}

function markReferenceDataAlreadyLoaded(key, count) {
  state.refDataDone[key] = true;

  const card = el.refdataGrid ? el.refdataGrid.querySelector(`.refdata-card[data-key="${key}"]`) : null;
  if (card) {
    card.classList.add('done');
    card.classList.remove('error');
    const status = card.querySelector('.refdata-status');
    if (status) status.textContent = `✓ Zaten yüklü (${count} kayıt) - isterseniz üzerine güncelleyebilirsiniz`;
  }

  updateStep2NextButton();
}

// ============================================================
// STEP 3: IMAGE UPLOAD
// ============================================================
function setupImageUpload() {
  if (!el.flowDropZone || !el.flowFileInput) return;

  el.flowDropZone.onclick = () => el.flowFileInput.click();
  el.flowFileInput.onchange = (e) => {
    if (e.target.files.length > 0) processImageFiles(e.target.files);
    el.flowFileInput.value = '';
  };

  el.flowDropZone.ondragover = (e) => {
    e.preventDefault();
    el.flowDropZone.classList.add('dragover');
  };
  el.flowDropZone.ondragleave = () => el.flowDropZone.classList.remove('dragover');
  el.flowDropZone.ondrop = (e) => {
    e.preventDefault();
    el.flowDropZone.classList.remove('dragover');
    if (e.dataTransfer.files.length > 0) processImageFiles(e.dataTransfer.files);
  };
}

async function processImageFiles(fileListLike) {
  const files = Array.from(fileListLike).filter(f =>
    f.type.startsWith('image/') || /\.(jpg|jpeg|png|bmp|webp)$/i.test(f.name));

  if (files.length === 0) {
    showToast('Desteklenen bir görsel dosyası bulunamadı.', 'error');
    return;
  }

  showToast(`${files.length} görsel, en fazla ${Math.min(IMAGE_UPLOAD_CONCURRENCY, files.length)} paralel işlenecek...`);

  // Sınırlı sayıda "worker" aynı dosya kuyruğundan sırayla çeker - her worker kendi dosyasını
  // bitirince kuyruktan bir sonrakini alır, yani N dosya her zaman aynı anda uçuşta olur.
  let nextIndex = 0;
  async function worker() {
    while (nextIndex < files.length) {
      const file = files[nextIndex++];
      await uploadOneImageFlow(file);
    }
  }

  const workerCount = Math.min(IMAGE_UPLOAD_CONCURRENCY, files.length);
  await Promise.all(Array.from({ length: workerCount }, worker));
}

async function uploadOneImageFlow(file) {
  const row = addImageRow(file.name);

  try {
    const formData = new FormData();
    formData.append('file', file);

    const resp = await fetch(`${API_BASE}/api/images/single`, {
      method: 'POST',
      body: formData,
    });

    if (!resp.ok) {
      const err = await resp.json().catch(() => ({ error: 'Upload başarısız' }));
      throw new Error(err.error || 'Upload hatası');
    }

    const data = await resp.json();
    if (data.detection?.status === 'failed') {
      throw new Error(data.detection?.detail?.reason || 'YOLO tespit servisi hatası');
    }

    const count = data.detection?.detail?.detectionCount || 0;
    const trackIds = data.detection?.detail?.matchedTrackIds || [];
    trackIds.forEach(id => state.matchedTrackIds.add(id));

    updateImageRow(row, 'done', `${count} araç, ${trackIds.length} track eşleşti`);
    state.uploadedImages.push({ fileName: file.name, status: 'done', detectionCount: count, matchedTrackIds: trackIds });
  } catch (err) {
    updateImageRow(row, 'failed', err.message);
    state.uploadedImages.push({ fileName: file.name, status: 'failed' });
    showToast(`${file.name}: ${err.message}`, 'error');
  }

  updateStep3NextButton();
}

function addImageRow(fileName) {
  if (!el.flowImageList) return null;

  const emptyNote = el.flowImageList.querySelector('.flow-empty-note');
  if (emptyNote) emptyNote.remove();

  const row = document.createElement('div');
  row.className = 'flow-image-row pending';
  row.innerHTML = `
    <span class="fir-name">${fileName}</span>
    <span class="fir-detail"></span>
    <span class="fir-status">İşleniyor...</span>
  `;
  el.flowImageList.appendChild(row);

  if (el.flowImageCount) {
    el.flowImageCount.textContent = String(el.flowImageList.querySelectorAll('.flow-image-row').length);
  }

  return row;
}

function updateImageRow(row, status, detailText) {
  if (!row) return;
  row.className = `flow-image-row ${status}`;
  row.querySelector('.fir-detail').textContent = detailText || '';
  row.querySelector('.fir-status').textContent =
    status === 'done' ? '✓ Tamamlandı' : status === 'failed' ? '✗ Başarısız' : 'İşleniyor...';
}

function updateStep3NextButton() {
  const hasSuccess = state.uploadedImages.some(i => i.status === 'done');
  if (el.btnStep3Next) el.btnStep3Next.disabled = !hasSuccess;
}

// ============================================================
// STEP 4: LLM RESULTS
// ============================================================
async function loadResults() {
  const trackIds = Array.from(state.matchedTrackIds);

  if (trackIds.length === 0) {
    if (el.resultsSummary) {
      el.resultsSummary.textContent = 'Bu oturumda yüklenen görsellerden eşleşen bir track bulunamadı.';
    }
    if (el.resultsGrid) el.resultsGrid.innerHTML = '';
    return;
  }

  if (el.resultsSummary) {
    el.resultsSummary.textContent =
      `${trackIds.length} track için GLM-5.3 İstihbarat Ajanı değerlendirmesi bekleniyor/gösteriliyor... ` +
      `(Ajan track'leri sırayla işler; çok track'li gruplarda birkaç dakika sürebilir.)`;
  }

  if (el.resultsGrid) el.resultsGrid.innerHTML = '';

  const cards = {};
  trackIds.forEach(trackId => {
    cards[trackId] = createResultCard(trackId);
    el.resultsGrid.appendChild(cards[trackId]);
  });

  await Promise.all(trackIds.map(trackId => pollAssessment(trackId, cards[trackId])));
}

function createResultCard(trackId) {
  const card = document.createElement('div');
  card.className = 'result-card';
  card.innerHTML = `
    <div class="result-card-head">
      <span class="result-track-id">${trackId}</span>
      <span class="result-verdict-badge pending">BEKLENİYOR</span>
    </div>
    <div class="result-meta"></div>
    <div class="result-reasoning">GLM-5.3 İstihbarat Ajanı analiz yapıyor...</div>
    <div class="result-evidence-row"></div>
  `;
  return card;
}

// Ajan servisi tüm track'leri tek bir kilit üzerinden SIRAYLA işler (bkz. llm_agent/server.py
// _agent_lock) - bu yüzden çok track'li bir grupta son track'in sonucu birkaç dakika alabilir.
// Süre aşımı bilinçli olarak yüksek tutuldu (90 x 3s = ~4.5dk); "Sonuçları Yenile" butonu her
// zaman elle tekrar denemeye izin verir.
async function pollAssessment(trackId, card, maxAttempts = 90, delayMs = 3000) {
  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    try {
      const resp = await fetch(`${API_BASE}/api/tracks/${trackId}/assessment`);
      if (resp.ok) {
        const asmt = await resp.json();
        renderResultCard(card, asmt);
        return;
      }
    } catch (e) { /* retry */ }

    const reasoningEl = card.querySelector('.result-reasoning');
    if (reasoningEl && attempt > 5) {
      reasoningEl.textContent =
        `GLM-5.3 İstihbarat Ajanı analiz yapıyor... (${Math.round(attempt * delayMs / 1000)}s, sırada beklenebilir)`;
    }

    await new Promise(r => setTimeout(r, delayMs));
  }

  card.querySelector('.result-reasoning').textContent =
    'Değerlendirme uzun sürüyor, arka planda hâlâ devam ediyor olabilir. "Sonuçları Yenile" ile tekrar deneyin.';
}

function renderResultCard(card, asmt) {
  const verdict = (asmt.verdict || 'suspicious').toLowerCase();
  const verdictTitles = { safe: 'GÜVENLİ', suspicious: 'ŞÜPHELİ', unsafe: 'TEHLİKELİ' };

  const badge = card.querySelector('.result-verdict-badge');
  badge.className = `result-verdict-badge ${verdict}`;
  badge.textContent = verdictTitles[verdict] || verdict.toUpperCase();

  card.querySelector('.result-meta').textContent =
    `Güven: %${Math.round((asmt.confidence || 0) * 100)} | Model: ${asmt.modelVersion || 'GLM-5.3-Flash'} | ` +
    (asmt.createdAtUtc ? new Date(asmt.createdAtUtc).toLocaleString('tr-TR') : '');

  card.querySelector('.result-reasoning').textContent = asmt.reasoning || 'Gerekçe belirtilmedi.';

  const evidenceRow = card.querySelector('.result-evidence-row');
  evidenceRow.innerHTML = '';
  (asmt.evidenceRefs || []).forEach(ref => {
    const tag = document.createElement('span');
    tag.className = 'result-evidence-tag';
    tag.textContent = ref;
    evidenceRow.appendChild(tag);
  });

  if (verdict === 'unsafe') showToast(`${asmt.trackId}: TEHLİKELİ değerlendirildi.`, 'error');
}

// ============================================================
// RESET
// ============================================================
function resetFlow() {
  state.uploadedImages = [];
  state.matchedTrackIds = new Set();

  if (el.flowImageList) {
    el.flowImageList.innerHTML = '<div class="flow-empty-note">Henüz görsel yüklenmedi.</div>';
  }
  if (el.flowImageCount) el.flowImageCount.textContent = '0';
  updateStep3NextButton();

  if (el.resultsGrid) el.resultsGrid.innerHTML = '';
  if (el.resultsSummary) el.resultsSummary.textContent = 'Sonuçlar yükleniyor...';

  showToast('Yeni analiz için görsel adımına dönüldü.');
  goToStep(3);
}

// ============================================================
// TOAST (aynı görsel dil - tactical.css .toast/#toastContainer)
// ============================================================
function showToast(msg, type = 'info') {
  let container = document.getElementById('toastContainer');
  if (!container) {
    container = document.createElement('div');
    container.id = 'toastContainer';
    document.body.appendChild(container);
  }

  const toast = document.createElement('div');
  toast.className = 'toast';
  toast.style.borderLeftColor = type === 'error' ? 'var(--danger)' : 'var(--cyan-glow)';
  toast.innerHTML = `
    <svg viewBox="0 0 24 24" width="16" height="16" fill="${type === 'error' ? 'var(--danger)' : 'var(--cyan-glow)'}"><path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z"/></svg>
    <span>${msg}</span>
  `;
  container.appendChild(toast);

  setTimeout(() => {
    toast.style.opacity = '0';
    toast.style.transform = 'translateX(30px)';
    setTimeout(() => toast.remove(), 300);
  }, 4000);
}
