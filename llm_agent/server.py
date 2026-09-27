"""
LLM agent HTTP service (no Docker, no broker - stdlib http.server + a background thread per
request, matching llm/detection/server.py's pattern).

Backend calls POST /assess with the enriched track context the moment it wants an assessment
(Ingestion/DetectionIngestionService.cs, or the manual POST /api/tracks/{id}/request-assessment
endpoint). This responds 202 immediately (the actual GLM-5.3-flash tool-calling loop can take
tens of seconds) and processes the request on a background thread; once
agent_loop.run_agent_for_track finishes, the result is POSTed back to the backend's
POST /api/assessments - the exact counterpart of backend's AssessmentRequestClient.

Run:
    python3 server.py            # listens on :8001 (AGENT_PORT to override)
"""

import json
import logging
import os
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import requests

try:
    from dotenv import load_dotenv
    load_dotenv(override=False)
except ImportError:
    pass

from agent_loop import DEFAULT_BASE_URL, DEFAULT_MODEL, DroneIntelligenceAgent

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(name)s: %(message)s")
logger = logging.getLogger("AgentService")

PORT = int(os.environ.get("AGENT_PORT", "8001"))
BACKEND_BASE_URL = os.environ.get("BACKEND_BASE_URL", "http://localhost:5299").rstrip("/")

# Bounds how many tracks run their GLM tool-calling loop at the same time. DroneIntelligenceAgent
# itself holds no per-call mutable state (each run_agent_for_track call builds its own local
# message list), so concurrent tracks CAN safely call it at once - but firing every track fully
# unbounded floods the upstream GLM proxy's burst rate limit (observed: ~3 concurrent requests
# already trigger 429 Too Many Requests, forcing a 60s SDK-side retry wait on the rest). A small
# concurrency cap gets most of the parallel speedup without tripping that limit.
AGENT_MAX_CONCURRENCY = int(os.environ.get("AGENT_MAX_CONCURRENCY", "3"))
_agent_semaphore = threading.Semaphore(AGENT_MAX_CONCURRENCY)

_agent: DroneIntelligenceAgent | None = None
# Only guards the one-time lazy creation of the shared agent below.
_agent_init_lock = threading.Lock()


def _get_agent() -> DroneIntelligenceAgent:
    global _agent
    if _agent is None:
        with _agent_init_lock:
            if _agent is None:
                api_key = os.environ.get("OPENAI_API_KEY") or os.environ.get("ZAI_API_KEY")
                if not api_key:
                    raise RuntimeError("OPENAI_API_KEY / ZAI_API_KEY is not set (.env).")
                _agent = DroneIntelligenceAgent(api_key=api_key, base_url=DEFAULT_BASE_URL, model=DEFAULT_MODEL)
    return _agent


def _process_in_background(track_context: dict) -> None:
    track_id = track_context.get("trackId", "unknown")
    try:
        with _agent_semaphore:
            result = _get_agent().run_agent_for_track(track_context)
    except Exception as ex:
        logger.error(f"Agent loop failed for {track_id}: {ex}", exc_info=True)
        result = {
            "trackId": track_id,
            "verdict": "suspicious",
            "confidence": 0.0,
            "reasoning": f"[HATA] Agent döngüsü başarısız: {ex}",
            "evidenceRefs": [],
            "modelVersion": "error",
            "createdAtUtc": None,
        }
        import datetime
        result["createdAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()

    try:
        response = requests.post(f"{BACKEND_BASE_URL}/api/assessments", json=result, timeout=10)
        response.raise_for_status()
        logger.info(f"Posted assessment for {track_id} back to backend: verdict={result.get('verdict')}")
    except requests.RequestException as ex:
        logger.error(f"Failed to post assessment for {track_id} back to backend: {ex}")


class AgentRequestHandler(BaseHTTPRequestHandler):
    def do_POST(self) -> None:
        if self.path != "/assess":
            self._send_json(404, {"error": f"Unknown path '{self.path}'. Use POST /assess."})
            return

        content_length = int(self.headers.get("Content-Length", "0"))
        if content_length == 0:
            self._send_json(400, {"error": "Empty body."})
            return

        try:
            track_context = json.loads(self.rfile.read(content_length))
        except Exception as ex:
            self._send_json(400, {"error": f"Invalid JSON: {ex}"})
            return

        track_id = track_context.get("trackId", "unknown")
        logger.info(f"Accepted assessment request for track {track_id}, processing in background...")
        threading.Thread(target=_process_in_background, args=(track_context,), daemon=True).start()

        self._send_json(202, {"status": "accepted", "trackId": track_id})

    def do_GET(self) -> None:
        if self.path == "/health":
            self._send_json(200, {"status": "ok", "backend": BACKEND_BASE_URL})
        else:
            self._send_json(404, {"error": f"Unknown path '{self.path}'."})

    def _send_json(self, status: int, payload: dict) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format: str, *args) -> None:  # noqa: A002
        logger.info(f"{self.address_string()} - {format % args}")


if __name__ == "__main__":
    logger.info(f"LLM agent service starting on :{PORT} (backend={BACKEND_BASE_URL}, "
                f"max_concurrency={AGENT_MAX_CONCURRENCY})")
    _get_agent()  # fail fast if the API key is missing, before accepting any requests
    ThreadingHTTPServer(("0.0.0.0", PORT), AgentRequestHandler).serve_forever()
