# Backend (Aşama 2)

Mimari kararların gerekçesi için `/home/gemini/.claude/plans/memoized-plotting-codd.md` dosyasına bakın
(not: o dosya Kafka/Redpanda'yı önerir — pratikte Docker/broker kurulumu fazla zaman aldığı için
**doğrudan HTTP** ile değiştirildi, aşağıya bakın; mimarideki tek fark bu, geri kalan her şey plandaki gibi).

Üç bağımsız süreç birlikte çalışır, hiçbiri Docker/broker gerektirmez:

| Süreç | Ne yapar | Varsayılan port |
|---|---|---|
| **bu klasör** (.NET) | geo-enrichment, track-matching, referans veri, Tool API, DB | 5299 |
| `../detection/` (Python) | stage-1 YOLO modeli (best-4.pt) | 8002 |
| `../llm_agent/` (Python) | GLM-5.3-flash tool-calling agent | 8001 |

```
Görsel yükle (POST /api/images/single)
        │
        ▼
   backend dosyayı kaydeder
        │  HTTP: ham bytes
        ▼
   detection servisi (:8002) — YOLO çalışır, YOLO-normalize txt döner
        │
        ▼
   backend: geo-enrich + track-match (in-process, aynı kod POST /api/images/{id}/detections'ın kullandığı)
        │  HTTP: track bağlamı (trackDetails + movementProfile + candidateFieldReports)
        ▼
   agent servisi (:8001) — 202 döner, arka planda GLM-5.3-flash tool-calling çalışır
        │  HTTP: {trackId, verdict, confidence, reasoning, evidenceRefs}
        ▼
   backend: POST /api/assessments — TrackAssessments'a yazılır
        │
        ▼
   GET /api/tracks/{id}/assessment
```

Tek bir görsel yükleme çağrısı **tüm zinciri otomatik tetikler** — ayrı bir "şimdi tespit çalıştır"
adımına gerek yok.

## Projeler

- `../detection/` — Stage-1 tespit modeli (`best-4.pt`, `ikinci_gorev/`'den entegre edildi) +
  `detection.py` (Ultralytics YOLO inference) + `server.py` (stdlib `http.server`, POST /detect) +
  `push_to_backend.py` (toplu/manuel test scripti).
- `../llm_agent/` — `agent_loop.py` (tool-calling döngüsü) + `tools.py` (backend Tool API'sine HTTP
  soran araçlar) + `server.py` (stdlib `http.server`, POST /assess).
- `src/Backend.Domain` — bağımsız, saf/deterministik hesaplamalar (piksel→WGS84, haversine,
  track eşleştirme, hareket profili, field report konum ayrıştırma). Dış bağımlılığı yok, EF/ASP.NET'ten habersiz.
- `src/Backend.Api` — ASP.NET Core minimal API + EF Core (SQLite). Referans veri yükleme
  (`Ingestion/ReferenceDataLoader`), tespit ingestion'ı (`Ingestion/DetectionIngestionService`,
  `Ingestion/DetectionServiceClient`), görsel upload (`Ingestion/ImageStorageService`,
  `Endpoints/ImageEndpoints`), Tool API (`Tools/TrackContextService`, `Endpoints/ToolEndpoints`)
  ve agent hand-off (`Messaging/`: `AssessmentRequestClient`, `AssessmentIngestionService`) burada.
- `tests/Backend.Tests` — xUnit. `GeoTransformTests`, gorev_tanimi.pdf'teki sayfa 2 örneğini
  (img_000123, kutu 610,380,60,28 → merkez piksel 640,394 → 39.94439/32.86350) birebir doğrulayan
  golden test'i içeriyor.

## Çalıştırma

Üç ayrı terminal (sırasız başlatılabilir, ama detection/agent servislerinin hazır olması ilk
görsel yüklemesinden önce iyi olur — detection servisi ilk açılışta ~5-10sn model warm-up yapar):

```bash
# 1) Backend
cd backend/src/Backend.Api && dotnet run --urls http://localhost:5299

# 2) Stage-1 detection servisi
cd llm/detection && python3 server.py            # :8002, ilk açılışta model warm-up yapar

# 3) LLM agent servisi (.env'de OPENAI_API_KEY olmalı)
cd llm/llm_agent && python3 server.py            # :8001
```

`dotnet test` ile 21 domain testini çalıştırabilirsiniz (broker/servis gerekmez, saf hesaplama testleri).

Her backend açılışında `appsettings.json`'daki `Stage2DataDirectory` (varsayılan: `../../../stage2`,
yani `llm/stage2`) altındaki `zones.json`, `tracks.csv`, `image_meta.json`, `field_reports.json`
otomatik olarak SQLite'a (`backend.db`) upsert edilir (`ReferenceDataLoader.SeedFromDiskAsync`).

**GPU notu**: bu makinede bir GPU var ama kurulu PyTorch derlemesi (sm_75+) o GPU'nun mimarisini
(sm_61/Pascal) desteklemiyor — `detection/server.py` CPU'da çalışır (`DETECTION_DEVICE=cpu`).
Uyumlu bir PyTorch build'i kurulursa `DETECTION_DEVICE=0` ile GPU'ya geçilebilir.

## Detection entegrasyonu (`../detection/`)

Stage-1 modeli (`best-4.pt`, YOLO/Ultralytics, 4 sınıf: car/van/truck/bus) ve onu çalıştıran
`detection.py`, `ikinci_gorev/` projesinden buraya entegre edildi. İki kullanım şekli var:

1. **Otomatik** (önerilen): `POST /api/images` veya `/single` ile görsel yükleyin — backend
   `detection/server.py`'yi (:8002) doğrudan HTTP ile çağırır, sonucu otomatik geo-enrich edip
   track'lerle eşleştirir.
2. **Manuel/toplu**: `python3 push_to_backend.py --backend-url http://localhost:5299` — 40
   görüntünün hepsini işler, backend'in eski `/detections` ucuna gönderir (detection servisi
   ayakta olmasa da çalışır, kendi içinde modeli çalıştırır).

**`appsettings.json`'daki `DetectionClassNames` artık tahmin değil, doğrulanmış:** modelin
`model.names` sırası `data.yaml`'daki alfabetik listeden (bus/car/truck/van) **farklı** —
gerçek çıktı `0=car, 1=van, 2=truck, 3=bus`. Bunu hem `ikinci_gorev/json/*.json`'daki tüm 40
dosyanın `class_id`↔`class_name` eşlemesini çapraz kontrol ederek (çelişki yok) hem de modeli
gerçekten yükleyip `img_003839` için orijinal çıktıyla birebir aynı sonucu üreterek doğruladım.

## Uçlar

**Swagger**: `http://localhost:5299/swagger` — tüm uçları "Try it out" ile tarayıcıdan test edebilirsiniz.

**Görsel yükleme + otomatik pipeline** (`Endpoints/ImageEndpoints.cs`) —
`image_meta.json`'daki bilgi sadece geo-kalibrasyon (boyut/köşe/çekim saati); asıl piksel
verisi (.jpg) ayrı yüklenir ve yüklenince otomatik olarak tespit+geo-enrich+track-match+agent
isteği zinciri tetiklenir:

- `POST /api/images/single` (form alanı `file`, tek dosya) — **Swagger'dan test için bunu kullanın**
  (Swagger UI dizi/binary şemalar için gerçek çoklu dosya seçimini desteklemiyor).
- `POST /api/images` (form alanı `files`, tekrarlanabilir — **çoklu dosya**) — curl/script/frontend
  için; tek istekte birden fazla görsel kabul eder.
- `GET /api/images/{imageId}/file` — yüklenen dosyayı sırasıyla `uploaded-images/`'de, yoksa
  orijinal `stage2/images/`'de arayıp döner.

Her ikisinin yanıtında her görsel için `detection: { status: "completed"|"skipped"|"failed", detail }`
alanı var: `skipped` = o `image_id` için `image_meta.json` henüz seed edilmemiş (geo-transform
yapılamaz, tespit atlanır); `failed` = detection servisine ulaşılamadı/hata verdi. Her iki durumda
da **görsel yine kaydedilir** — sadece otomatik tespit atlanır, sonradan manuel olarak
`POST /api/images/{id}/detections` ile tetiklenebilir.

**Referans veri upload'ı** (`Endpoints/ReferenceDataEndpoints.cs`, `Ingestion/ReferenceDataStorageService.cs`)
— bu 4 dosya gerçek dosya olarak (`multipart/form-data`, form alanı `file` — Swagger'da düzgün bir
dosya seçici çıkar) yüklenir; hem `uploaded-reference-data/`'e diske kaydedilir hem de
**natural key'e göre upsert** edilir (asla tabloyu silip yeniden doldurmaz):

- `POST /api/reference-data/zones` (form alanı `file`, `zones.json`) — bölge adına göre upsert.
- `POST /api/reference-data/tracks` (form alanı `file`, `tracks.csv`) — `(track_id, time)` çiftine göre upsert.
- `POST /api/reference-data/image-meta` (form alanı `file`, `image_meta.json`) — `image_id`'ye göre upsert; var olan tespitleri **silmez**.
- `POST /api/reference-data/field-reports` (form alanı `file`, `field_reports.json`) — `(time, source, text)` üçlüsüne göre dedup edilerek **eklenir**.

Hepsi `{"upserted": N, "savedPath": "..."}` döner; bozuk JSON/CSV → 400.

**Manuel detection ingestion** (backend-içi, senkron):

- `POST /api/images/{imageId}/detections` — gövde: YOLO-normalized txt (`class cx cy w h [conf]`).
  Görüntü `image_meta.json`'da zaten seed edilmiş olmalı. Otomatik akışın da altta kullandığı uç bu.

**Tool API** (agent servisinin senkron REST ile çağırdığı uçlar — plan.md §5):

- `GET /api/tracks/{trackId}` — get_track_details
- `GET /api/tracks/{trackId}/movement-profile` — get_movement_profile (status/hız/durağanlık/orbit skoru)
- `GET /api/tracks/{trackId}/field-reports?radiusMeters=&includeUnresolved=` — search_field_reports (track bazlı)
- `GET /api/tracks/{trackId}/compare-reports` — compare_with_reports (yukarı üçünün birleşimi; agent'a giden payload'ın aynısı)
- `GET /api/zones/nearest?lat=&lon=` — get_nearest_zone
- `GET /api/field-reports/search?lat=&lon=&radiusMeters=&includeUnresolved=` — search_field_reports (koordinat bazlı)

**Agent hand-off** (`Messaging/`, doğrudan HTTP — broker yok):

- `AssessmentRequestClient` → `POST {AgentBaseUrl}/assess` (varsayılan `http://localhost:8001`):
  `DetectionIngestionService`, bir ingestion sonrası eşleşen her track_id için otomatik çağırır.
  Servis hatası ingestion'ı başarısız kılmaz (best-effort + loglanır).
- `POST /api/tracks/{trackId}/request-assessment` — aynı çağrıyı elle/yeniden tetikler.
- `POST /api/assessments` — agent servisi, kararını burada POST eder (`AssessmentIngestionService`
  ile `TrackAssessments`'a track_id başına **son karar kazanır** mantığıyla upsert edilir).
- `GET /api/tracks/{trackId}/assessment` — o track için kaydedilmiş son verdict'i döner (yoksa 404).

## Neden Kafka/Redpanda değil

İlk implementasyon Redpanda (Kafka API uyumlu) kullanıyordu; Docker kurulum/yeniden başlatma
döngüsü hackathon temposunda fazla zaman aldığı için **doğrudan HTTP**'ye geçildi:
backend `AssessmentRequestClient` ile agent'ın `/assess`'ini çağırır, agent arka planda işleyip
sonucu backend'in `POST /api/assessments`'ına geri yazar. Aynı asenkron, best-effort, "servis
kapalıysa ingestion'ı bozma" davranışı korundu — sadece taşıma katmanı broker'dan HTTP'ye indi.
İleride gerçek ölçek/replay ihtiyacı çıkarsa `IAssessmentRequestClient`/`AssessmentIngestionService`
arayüzlerinin arkasına bir broker tekrar eklenebilir; çağıran kodun değişmesi gerekmez.

## Uçtan uca durum

Backend + `../detection/` + `../llm_agent/` gerçek verilerle uçtan uca doğrulandı: gerçek görsel
yükleme → gerçek YOLO tespiti (stage-1 model) → geo-enrichment/track-matching → agent servisine
otomatik HTTP çağrısı → gerçek GLM-5.3-flash tool-calling → `POST /api/assessments` →
`TrackAssessments` → `GET /api/tracks/{id}/assessment`. Örnek gerçek sonuç: T0047 (2 saattir üsse
sabit mesafede orbit paterni) ve T0182 (üsse yaklaşıp 70dk hareketsiz) için gerekçeli "suspicious"
kararları üretildi, backend'e doğru şekilde geri yazıldı.

## Bilinen eksik / yapılacaklar

- Üç servis de elle başlatılıyor (systemd/process manager yok); biri çökerse otomatik yeniden
  başlamaz.
- `detection/server.py` ve `llm_agent/server.py` stdlib `http.server` kullanıyor (tek thread pool,
  prod-grade değil) — hackathon ölçeği için yeterli, ciddi trafik için gerçek bir ASGI/WSGI
  sunucusuna (uvicorn/gunicorn) geçilmeli.
