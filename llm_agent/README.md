# LLM Agent (Aşama 2) — Backend'in doğrudan HTTP çağırdığı karşı taraf

Mimari kararların gerekçesi için `/home/gemini/.claude/plans/memoized-plotting-codd.md`
dosyasına bakın (not: Kafka/Redpanda önerisi Docker'ın hackathon temposunda fazla zaman alması
nedeniyle **doğrudan HTTP** ile değiştirildi — bkz `backend/README.md`'deki "Neden Kafka/Redpanda
değil" bölümü). Bu servis, backend'in `POST /assess` ile gönderdiği track bağlamını alır,
GLM-5.3-flash ile tool-calling döngüsü çalıştırır, kararı `POST {BackendBaseUrl}/api/assessments`
ile backend'e geri yazar. Ne broker var ne de Docker — düz bir Python HTTP sunucusu (`server.py`).

## Tasarım ilkesi

**Backend'in deterministik olarak hesapladığı her şey (piksel→WGS84, track eşleştirme,
hareket profili, aday saha raporları) agent'a `/assess` isteğinin gövdesinde INPUT olarak
gelir — agent bunlar için tool çağırmaz.** Tool'lar (`tools.py`) sadece agent'ın bu ilk paketin
**dışında** ihtiyaç duyabileceği şeyler içindir: başka bir track'i kontrol etmek (konvoy
şüphesi), daha geniş/farklı parametrelerle rapor araması, ya da keyfi bir koordinat için en
yakın bölge. Bu sayede piksel/koordinat/mesafe hesaplamasının tek bir doğru implementasyonu
olur (backend'de); agent hiçbir zaman kendi geo-matematiğini yapmaz.

## Akış

```
backend ──POST /assess (track bağlamı)──► server.py
                                              │  202 Accepted (hemen döner)
                                              ▼
                                    arka plan thread'i:
                                    agent_loop.run_agent_for_track()
                                    (GLM-5.3-flash tool-calling döngüsü)
                                              │
                                              ▼
                              backend ◄──POST /api/assessments (karar)── server.py
```

## Dosyalar

- `server.py` — **gerçek servis giriş noktası**. stdlib `http.server` ile `POST /assess`'i
  dinler, isteği hemen 202 ile kabul edip arka plan thread'inde işler, sonucu backend'e geri
  POST eder. `GET /health` ile ayakta olduğunu kontrol edebilirsiniz.
- `tools.py` — backend'in Tool API'sine (bkz `backend/README.md`) HTTP ile soran tool'lar +
  `submit_final_assessment_tool` (nihai kararı yapılandırılmış olarak zorunlu kılan tool).
- `agent_loop.py` — `DroneIntelligenceAgent.run_agent_for_track(track_context)`: track bağlamını
  Türkçe bir prompt'a çevirir, tool-calling döngüsünü çalıştırır, `submit_final_assessment_tool`
  çağrısını yakalayıp backend'in `AssessmentCompleted` şemasıyla birebir eşleşen bir dict döner.
  Döngü sonuna kadar karar gelmezse son turda `tool_choice` zorlanarak submit'e mecbur bırakılır
  (sonsuz döngü koruması). CLI olarak da çalışır: `python agent_loop.py T0047 sk-...`.
- `test_pipeline.py` — uçtan uca canlı test: backend'den gerçek bir track bağlamı çeker, bu
  servisin kendi `/assess`'ine gönderir, backend'den sonucu poll eder.
- `data_manager.py`, `spatial_matcher.py` — **artık kullanılmıyor** (dosyaların başında
  deprecation notu var). Backend'den önce bu iki dosya stage2 dosyalarını kendisi okuyup
  geo/track hesaplarını tekrar yapıyordu (50m eşleşme eşiğiyle, backend'in 100m'sinden farklı);
  artık backend tek gerçek kaynak, bu dosyalar sadece referans/offline deneme için duruyor.

## Çalıştırma

```bash
pip install -r requirements.txt
# .env: OPENAI_API_KEY, BACKEND_BASE_URL (varsayılan localhost:5299), AGENT_PORT (varsayılan 8001)

python server.py                   # gerçek servis: backend'den gelen /assess isteklerini dinler
python test_pipeline.py T0001      # tek seferlik uçtan uca doğrulama (backend + bu servis çalışıyor olmalı)
python agent_loop.py T0047 sk-...  # tek bir track için manuel CLI testi (backend'den bağlamı kendisi çeker, server.py'a ihtiyaç duymaz)
```

## Doğrulanan gerçek çalıştırma

Görsel yükleme → otomatik tespit → otomatik bu servise `/assess` çağrısı → gerçek GLM-5.3-flash
tool-calling → backend'e geri yazma zinciri uçtan uca doğrulandı. Gerçek örnekler:

- **T0047** (kamyon, 2 saattir üsse ~1.76km sabit mesafede orbit paterni + "yükü tespit
  edilemeyen kamyon" saha raporu) → `suspicious`, güven 0.82.
- **T0182** (üsse 5.7km'den 1.77km'ye yaklaşıp 70 dakika hareketsiz kalan araç, "planlı ikmal
  aracı" teyidiyle kısmen çelişen) → `suspicious`, güven 0.65.

Backend bu kararları `POST /api/assessments` üzerinden aldı, `TrackAssessments`'a yazdı,
`GET /api/tracks/{id}/assessment` ile doğrulandı.

## Karşılaşılan ve düzeltilen gerçek bug'lar

- `search_field_reports_tool`: backend'in bazı uçları (`/field-reports`) düz bir JSON dizisi
  döndürdüğünde `body["status"] = ...` atamasıyla çöküyordu. `tools.py`'daki `_get()` artık hem
  obje hem dizi yanıtları doğru sarmalıyor.
- Backend'in `AssessmentRequestClient`'ı (.NET tarafı) `PostAsJsonAsync` kullandığında büyük
  gövdeler için `Content-Length` yerine chunked transfer-encoding'e geçebiliyordu; bu servisin
  stdlib `http.server`'ı chunked gövdeleri parse etmiyor, boş gövde görüp 400 dönüyordu. .NET
  tarafında `StringContent` ile açık `Content-Length` gönderilerek düzeltildi.
