"""
Stage-1 detection HTTP service (no Docker, no broker - stdlib only).

Backend calls this directly (Ingestion/DetectionServiceClient.cs) the moment an image is
uploaded: POST /detect with the raw image bytes as the body, X-Image-Id and X-Extension
headers. Response body is the YOLO-normalized detection txt (class cx cy w h per line) -
exactly the format DetectionIngestionService.IngestAsync already parses, so the backend can
feed the response straight into its existing geo-enrichment + track-matching code path.

Run:
    python3 server.py            # listens on :8002 (DETECTION_SERVICE_PORT to override)
"""

import logging
import os
import tempfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from detection import detect, format_yolo_txt

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(name)s: %(message)s")
logger = logging.getLogger("DetectionService")

PORT = int(os.environ.get("DETECTION_SERVICE_PORT", "8002"))
# GPU otomatik denenmiyor: bu makinedeki GPU (GTX 1060, sm_61/Pascal) mevcut PyTorch derlemesiyle
# (sadece sm_75+) uyumsuz - "cudaErrorNoKernelImageForDevice" ile çöküyor. Uyumlu bir torch
# derlemesi kurulursa DETECTION_DEVICE=0 ile GPU'ya geçilebilir.
DEVICE = os.environ.get("DETECTION_DEVICE", "cpu")
CONFIDENCE_THRESHOLD = float(os.environ.get("DETECTION_CONFIDENCE_THRESHOLD", "0.25"))


class DetectionRequestHandler(BaseHTTPRequestHandler):
    def do_POST(self) -> None:
        if self.path != "/detect":
            self._send_json(404, {"error": f"Unknown path '{self.path}'. Use POST /detect."})
            return

        image_id = self.headers.get("X-Image-Id")
        extension = self.headers.get("X-Extension", ".jpg")
        content_length = int(self.headers.get("Content-Length", "0"))

        if not image_id or content_length == 0:
            self._send_json(400, {"error": "Missing X-Image-Id header or empty body."})
            return

        image_bytes = self.rfile.read(content_length)

        tmp_path = Path(tempfile.gettempdir()) / f"{image_id}{extension}"
        try:
            tmp_path.write_bytes(image_bytes)
            result = detect(tmp_path, confidence_threshold=CONFIDENCE_THRESHOLD, device=DEVICE, save_outputs=False)
            yolo_txt = format_yolo_txt(result["detections"], result["image_width"], result["image_height"])

            logger.info(f"{image_id}: {result['detection_count']} tespit.")
            body = yolo_txt.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        except Exception as ex:
            logger.error(f"Detection failed for {image_id}: {ex}", exc_info=True)
            self._send_json(500, {"error": str(ex)})
        finally:
            tmp_path.unlink(missing_ok=True)

    def do_GET(self) -> None:
        if self.path == "/health":
            self._send_json(200, {"status": "ok", "device": DEVICE})
        else:
            self._send_json(404, {"error": f"Unknown path '{self.path}'."})

    def _send_json(self, status: int, payload: dict) -> None:
        import json
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format: str, *args) -> None:  # noqa: A002 - matches BaseHTTPRequestHandler signature
        logger.info(f"{self.address_string()} - {format % args}")


def _warm_up() -> None:
    """The very first detect() call does extra one-time work (graph/JIT warmup) on top of the
    already-loaded model - took ~100s cold in manual testing. Without this, the first real
    request from the backend blows past its HTTP timeout and the response write fails with a
    broken pipe (the model finishes the work, but nobody is listening anymore). Run one dummy
    inference here, before accepting any real requests, so every real request is fast."""
    import numpy as np
    import cv2

    logger.info("Model isiniliyor (ilk cagri her zaman yavastir)...")
    dummy_path = Path(tempfile.gettempdir()) / "_warmup.jpg"
    cv2.imwrite(str(dummy_path), np.zeros((64, 64, 3), dtype=np.uint8))
    try:
        detect(dummy_path, confidence_threshold=CONFIDENCE_THRESHOLD, device=DEVICE, save_outputs=False)
        logger.info("Model hazir.")
    finally:
        dummy_path.unlink(missing_ok=True)


if __name__ == "__main__":
    logger.info(f"Detection service starting on :{PORT} (device={DEVICE}, confidence>={CONFIDENCE_THRESHOLD})")
    _warm_up()
    ThreadingHTTPServer(("0.0.0.0", PORT), DetectionRequestHandler).serve_forever()
