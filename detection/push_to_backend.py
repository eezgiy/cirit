"""
Stage-1 model (best-4.pt) -> Stage-2 backend entegrasyonu.

Her goruntu icin detect() calisir, ciktisi (class cx cy w h normalize, PDF formati) backend'in
POST /api/images/{image_id}/detections ucuna HTTP ile gonderilir. Backend bunu senkron olarak
geo-enrich eder, track_id ile eslestirir ve (kuyruk sinirindan) agent.assessment.requested'a
publish eder - bkz backend/README.md.

Kullanim:
    python3 push_to_backend.py [--backend-url http://localhost:5299] [--images-dir ../stage2/images]
"""

import argparse
import sys
from pathlib import Path

import requests

from detection import detect, format_yolo_txt


def push_one(image_path: Path, backend_url: str, confidence_threshold: float, device: str) -> None:
    result = detect(image_path, confidence_threshold=confidence_threshold, device=device, save_outputs=True)

    yolo_txt = format_yolo_txt(
        result["detections"], result["image_width"], result["image_height"]
    )

    image_id = result["image_id"]
    url = f"{backend_url.rstrip('/')}/api/images/{image_id}/detections"

    if not yolo_txt.strip():
        print(f"  -> {image_id}: 0 tespit, backend'e gonderilmiyor (bos govde reddedilir).")
        return

    response = requests.post(url, data=yolo_txt.encode("utf-8"),
                              headers={"Content-Type": "text/plain"}, timeout=30)

    if response.status_code == 200:
        matched = sum(1 for d in response.json() if d.get("trackId"))
        print(f"  -> {image_id}: {result['detection_count']} tespit, "
              f"{matched} track_id ile eslesti, backend kabul etti (200).")
    else:
        print(f"  -> {image_id}: backend HATA {response.status_code}: {response.text}", file=sys.stderr)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--backend-url", default="http://localhost:5299")
    parser.add_argument("--images-dir", default=str(Path(__file__).resolve().parent.parent / "stage2" / "images"))
    parser.add_argument("--confidence-threshold", type=float, default=0.25)
    parser.add_argument("--device", default="cpu")
    args = parser.parse_args()

    images_dir = Path(args.images_dir)
    image_extensions = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
    image_paths = sorted(p for p in images_dir.iterdir() if p.suffix.lower() in image_extensions)

    print(f"{len(image_paths)} goruntu bulundu. Backend: {args.backend_url}\n")

    for index, image_path in enumerate(image_paths, start=1):
        print(f"[{index}/{len(image_paths)}] {image_path.name}")
        try:
            push_one(image_path, args.backend_url, args.confidence_threshold, args.device)
        except requests.RequestException as ex:
            print(f"  -> HATA (backend'e ulasilamadi): {ex}", file=sys.stderr)
        except Exception as ex:  # noqa: BLE001 - toplu isleme, bir goruntudeki hata digerlerini durdurmasin
            print(f"  -> HATA: {ex}", file=sys.stderr)


if __name__ == "__main__":
    main()
