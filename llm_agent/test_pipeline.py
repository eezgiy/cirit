"""
End-to-end smoke test for the direct HTTP boundary between the LLM agent and the backend
(no Docker, no broker - see server.py).

Requires: the .NET backend running (default http://localhost:5299) with reference data seeded,
this agent's server.py running (default http://localhost:8001), and a valid
OPENAI_API_KEY/ZAI_API_KEY in .env.

Steps:
1. Fetch a real track's enriched context from the backend.
2. POST it to this agent's own /assess endpoint (simulating what
   Ingestion/DetectionServiceClient.cs + AssessmentRequestPublisher do on the backend).
3. Poll the backend's GET /api/tracks/{id}/assessment until the agent posts its result back.
"""

import os
import sys
import time

import requests

if sys.platform == "win32":
    try:
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        if hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

BACKEND_BASE_URL = os.environ.get("BACKEND_BASE_URL", "http://localhost:5299").rstrip("/")
AGENT_BASE_URL = os.environ.get("AGENT_BASE_URL", "http://localhost:8001").rstrip("/")


def fetch_track_context(track_id: str) -> dict:
    resp = requests.get(f"{BACKEND_BASE_URL}/api/tracks/{track_id}/compare-reports", timeout=10)
    resp.raise_for_status()
    compare = resp.json()
    return {
        "trackId": track_id,
        "requestedAtUtc": None,
        "requestReason": "test_pipeline.py",
        "trackDetails": compare["trackDetails"],
        "movementProfile": compare["movementProfile"],
        "candidateFieldReports": compare["candidateFieldReports"],
    }


def main() -> None:
    track_id = sys.argv[1] if len(sys.argv) > 1 else "T0001"

    print("=" * 80)
    print("DOGRUDAN HTTP SINIRI TESTI (Kafka yok)")
    print(f"Backend : {BACKEND_BASE_URL}")
    print(f"Agent   : {AGENT_BASE_URL}")
    print(f"Track   : {track_id}")
    print("=" * 80)

    print(f"\n[1/3] '{track_id}' için bağlam backend'den çekiliyor...")
    context = fetch_track_context(track_id)
    print(f"      -> Status: {context['movementProfile']['status']}, "
          f"{len(context['candidateFieldReports'])} aday rapor.")

    print(f"\n[2/3] Agent servisine ({AGENT_BASE_URL}/assess) gönderiliyor...")
    resp = requests.post(f"{AGENT_BASE_URL}/assess", json=context, timeout=10)
    resp.raise_for_status()
    print(f"      -> {resp.status_code}: {resp.json()}")

    print(f"\n[3/3] Backend'den sonucu bekleniyor (GET /api/tracks/{track_id}/assessment)...")
    for attempt in range(1, 31):
        time.sleep(2)
        check = requests.get(f"{BACKEND_BASE_URL}/api/tracks/{track_id}/assessment", timeout=10)
        if check.status_code == 200:
            print(f"      -> Geldi ({attempt * 2}s sonra).")
            print("\n" + "#" * 80)
            print("[TEST SONUCU]")
            import json
            print(json.dumps(check.json(), indent=2, ensure_ascii=False))
            print("#" * 80)
            return
        print(f"      -> henüz yok (deneme {attempt}/30)...")

    print("[HATA] 60 saniyede sonuç gelmedi. agent servisinin (server.py) loglarını kontrol edin.")
    sys.exit(1)


if __name__ == "__main__":
    main()
