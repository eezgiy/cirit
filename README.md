# Roketsan Hackathon - Aşama 2 (Nesne Tespiti, Takip & LLM Agent Değerlendirme Sistemi)

Bu proje, İHA/gözetleme görüntülerinden araç tespiti (YOLO), coğrafi zenginleştirme (WGS84 koordinat dönüşümü), araç hareket profili analizi ve GLM-5.3-Flash LLM tabanlı taktiksel tehdit değerlendirmesi yapan entegre bir karar destek sistemidir.

---

## 🏗 Mimari ve Servisler

Proje üç ana bağımsız servisten oluşur ve doğrudan HTTP haberleşmesi kullanır (Docker veya mesaj kuyruğu gerektirmez):

| Servis | Teknoloji | Varsayılan Port | Açıklama |
|---|---|---|---|
| **Backend** | .NET 10 / ASP.NET Core / SQLite | `5299` | Geo-dönüşüm, track eşleştirme, hareket profili, referans veriler, DB ve Tool API |
| **Detection** | Python / YOLOv11 (`best-4.pt`) | `8002` | Aşama 1 nesne tespit modeli (car, van, truck, bus) |
| **LLM Agent** | Python / GLM-5.3-Flash / OpenAI Tool-Calling | `8001` | Askeri istihbarat ve taktiksel tehdit analizi yapan otonom karar ajanı |

---

## 🚀 Hızlı Başlangıç (Tek Tıkla Çalıştırma)

### 1. Servisleri Başlatma
Proje ana dizinindeki `run_all.bat` dosyasını çift tıklayarak veya terminalden çalıştırın:
```cmd
run_all.bat
```
Bu dosya 3 ayrı komut satırı penceresinde Backend, Detection ve LLM Agent servislerini otomatik olarak ayağa kaldırır.

### 2. Sağlık ve Uçtan Uca Test
Servisler başladıktan sonra projenin sorunsuz çalıştığını doğrulamak için:
```cmd
test_system.bat
```
Bu betik:
- 3 servisin ayakta olduğunu (`5299`, `8002`, `8001`) doğrular.
- `T0001` track'i için arka plandaki LLM döngüsünü tetikler ve nihai kararı ekrana basar.

---

## 🌐 Web Arayüzü (Swagger UI)

Backend ayağa kalktığında tarayıcınızdan şu adrese gidin:
👉 **[http://localhost:5299/swagger](http://localhost:5299/swagger)**

### Swagger Üzerinden Örnek Senaryo Testi:
1. **Görüntü Yükleme (`POST /api/images/single`):**
   - `file` alanından `stage2/images/img_000267.jpg` dosyasını seçip **Execute** butonuna basın.
   - **Otomatik Zincir:** Backend görüntüyü kaydeder -> Detection servisine (:8002) iletir -> YOLO tespiti yapar -> Pikselleri WGS84 koordinatlarına çevirir -> İlgili `track_id`'lerle eşleştirir -> LLM Agent'a (:8001) değerlendirme isteği gönderir.
2. **Değerlendirme Sonucunu Görme (`GET /api/tracks/{trackId}/assessment`):**
   - Eşleşen track ID'lerden birini (örn: `T0045`, `T0226` veya `T0001`) girerek kararı sorgulayın (`verdict`: `safe` / `suspicious` / `unsafe`, `confidence`, `reasoning`, `evidenceRefs`).

---

## 🛠 Manuel / Ayrı Ayrı Çalıştırma Adımları

Eğer servisleri elle tek tek çalıştırmak isterseniz:

### Terminal 1: Backend (.NET)
```cmd
cd backend\src\Backend.Api
dotnet run --urls http://localhost:5299
```

### Terminal 2: Detection Servisi (YOLO)
```cmd
cd detection
python server.py
```

### Terminal 3: LLM Agent Servisi
```cmd
cd llm_agent
python server.py
```

---

## 📦 Kurulum ve Gereksinimler

- **.NET SDK:** .NET 10 (veya .NET 8)
- **Python:** Python 3.10+ (Kütüphaneler: `ultralytics`, `opencv-python`, `requests`, `python-dotenv`, `openai`, `pandas`, `numpy`)
- İlgili kütüphaneleri yüklemek için:
  ```cmd
  pip install -r requirements.txt
  ```
- **API Anahtarı:** `llm_agent/.env` dosyasında GLM / OpenAI anahtarınız tanımlıdır.
