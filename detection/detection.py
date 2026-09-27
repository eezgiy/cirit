from pathlib import Path
from typing import Union
import json
import cv2

from ultralytics import YOLO


# =========================================================
# AYARLAR
# =========================================================

BASE_DIR = Path(__file__).resolve().parent
MODEL_PATH = BASE_DIR / "best-4.pt"

OUTPUT_DIR = BASE_DIR / "outputs"
JSON_DIR = OUTPUT_DIR / "json"
YOLO_DIR = OUTPUT_DIR / "yolo"
VISUAL_DIR = OUTPUT_DIR / "visual"

JSON_DIR.mkdir(parents=True, exist_ok=True)
YOLO_DIR.mkdir(parents=True, exist_ok=True)
VISUAL_DIR.mkdir(parents=True, exist_ok=True)


# Model sadece bir kez yüklenir
model = YOLO(str(MODEL_PATH))


# =========================================================
# DETECTION
# =========================================================

def detect(
    image_path: Union[str, Path],
    confidence_threshold: float = 0.25,
    device: str = "cpu",
    save_outputs: bool = True,
) -> dict:

    image_path = Path(image_path)

    if not image_path.exists():
        raise FileNotFoundError(
            f"Görüntü bulunamadı: {image_path}"
        )

    # -----------------------------------------------------
    # YOLO INFERENCE
    # -----------------------------------------------------

    results = model.predict(
        source=str(image_path),
        conf=confidence_threshold,
        device=device,
        verbose=False,
    )

    result = results[0]

    image_height = int(result.orig_shape[0])
    image_width = int(result.orig_shape[1])

    detections = []

    # -----------------------------------------------------
    # DETECTION SONUÇLARI
    # -----------------------------------------------------

    for box in result.boxes:

        x1, y1, x2, y2 = box.xyxy[0].tolist()

        confidence = float(box.conf[0])
        class_id = int(box.cls[0])

        class_name = model.names[class_id]

        # Backend daha sonra kullanabilir.
        # Şimdilik merkez pixel bilgisini de veriyoruz.
        center_x = (x1 + x2) / 2
        center_y = (y1 + y2) / 2

        detection = {
            "class_id": class_id,
            "class_name": class_name,
            "confidence": round(confidence, 4),

            "bbox": {
                "x1": round(x1, 2),
                "y1": round(y1, 2),
                "x2": round(x2, 2),
                "y2": round(y2, 2),
            },

            "center_pixel": {
                "x": round(center_x, 2),
                "y": round(center_y, 2),
            }
        }

        detections.append(detection)

    # Confidence yüksekten düşüğe sırala
    detections.sort(
        key=lambda x: x["confidence"],
        reverse=True
    )

    # -----------------------------------------------------
    # BACKEND JSON
    # -----------------------------------------------------

    output = {
        "image_id": image_path.stem,
        "image_width": image_width,
        "image_height": image_height,
        "confidence_threshold": confidence_threshold,
        "detection_count": len(detections),
        "detections": detections,
    }

    # -----------------------------------------------------
    # DOSYALARA KAYDET
    # -----------------------------------------------------

    if save_outputs:

        save_json(output, image_path.stem)

        save_yolo_txt(
            detections,
            image_path.stem,
            image_width,
            image_height,
        )

        save_visual(
            image_path,
            detections,
        )

    return output


# =========================================================
# JSON KAYDET
# =========================================================

def save_json(result: dict, image_id: str):

    output_path = JSON_DIR / f"{image_id}.json"

    with open(
        output_path,
        "w",
        encoding="utf-8"
    ) as f:

        json.dump(
            result,
            f,
            indent=2,
            ensure_ascii=False,
        )


# =========================================================
# YOLO TXT FORMATINDA KAYDET
# =========================================================

def format_yolo_txt(
    detections,
    image_width,
    image_height,
) -> str:
    """Backend'in /api/images/{id}/detections ucuna gonderdigimiz gövdeyle
    birebir ayni format: class_id x_center y_center width height (normalize)."""

    lines = []

    for detection in detections:

        bbox = detection["bbox"]

        x1 = bbox["x1"]
        y1 = bbox["y1"]
        x2 = bbox["x2"]
        y2 = bbox["y2"]

        center_x = ((x1 + x2) / 2) / image_width
        center_y = ((y1 + y2) / 2) / image_height

        bbox_width = (x2 - x1) / image_width
        bbox_height = (y2 - y1) / image_height

        class_id = detection["class_id"]
        confidence = float(detection.get("confidence", 0.0))

        lines.append(
            f"{class_id} "
            f"{center_x:.6f} "
            f"{center_y:.6f} "
            f"{bbox_width:.6f} "
            f"{bbox_height:.6f} "
            f"{confidence:.4f}"
        )

    return "\n".join(lines) + ("\n" if lines else "")


def save_yolo_txt(
    detections,
    image_id,
    image_width,
    image_height,
):

    output_path = YOLO_DIR / f"{image_id}.txt"

    with open(output_path, "w") as f:
        f.write(format_yolo_txt(detections, image_width, image_height))


# =========================================================
# BBOX ÇİZİLMİŞ GÖRSEL
# =========================================================

def save_visual(
    image_path,
    detections,
):

    image = cv2.imread(str(image_path))

    if image is None:
        raise ValueError(
            f"Görüntü OpenCV tarafından okunamadı: {image_path}"
        )

    for detection in detections:

        bbox = detection["bbox"]

        x1 = int(bbox["x1"])
        y1 = int(bbox["y1"])
        x2 = int(bbox["x2"])
        y2 = int(bbox["y2"])

        class_name = detection["class_name"]
        confidence = detection["confidence"]

        # Bounding box
        cv2.rectangle(
            image,
            (x1, y1),
            (x2, y2),
            (0, 255, 0),
            2,
        )

        # Confidence yüzde olarak
        confidence_percent = confidence * 100

        label = (
            f"{class_name} "
            f"{confidence_percent:.1f}%"
        )

        cv2.putText(
            image,
            label,
            (x1, max(y1 - 10, 20)),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.55,
            (0, 255, 0),
            2,
        )

    output_path = (
        VISUAL_DIR /
        f"{Path(image_path).stem}_detected.jpg"
    )

    cv2.imwrite(
        str(output_path),
        image,
    )


# =========================================================
# TEST
# =========================================================

if __name__ == "__main__":

    IMAGES_DIR = BASE_DIR.parent / "stage2" / "images"

    # Desteklenen görsel formatları
    image_extensions = {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".webp"
    }

    # Klasördeki bütün görselleri bul
    image_paths = sorted([
        path
        for path in IMAGES_DIR.iterdir()
        if path.suffix.lower() in image_extensions
    ])

    print("\n===================================")
    print("TOPLU DETECTION BAŞLIYOR")
    print("===================================")

    print(f"Bulunan görüntü sayısı: {len(image_paths)}\n")

    all_results = []

    # -----------------------------------------------------
    # TÜM GÖRSELLERİ İŞLE
    # -----------------------------------------------------

    for index, image_path in enumerate(image_paths, start=1):

        print(
            f"[{index}/{len(image_paths)}] "
            f"{image_path.name} işleniyor..."
        )

        try:

            result = detect(
                image_path,
                confidence_threshold=0.25,
                device="cpu"
            )

            all_results.append(result)

            print(
                f"    → {result['detection_count']} "
                f"nesne bulundu"
            )

            # Bulunan nesneleri terminale yaz
            for detection in result["detections"]:

                print(
                    f"       "
                    f"{detection['class_name']:<6} "
                    f"{detection['confidence'] * 100:.1f}%"
                )

        except Exception as e:

            print(
                f"    HATA: {e}"
            )

    # -----------------------------------------------------
    # TÜM SONUÇLARI TEK JSON'A KAYDET
    # -----------------------------------------------------

    all_json_path = OUTPUT_DIR / "all_detections.json"

    with open(
        all_json_path,
        "w",
        encoding="utf-8"
    ) as f:

        json.dump(
            {
                "total_images": len(image_paths),
                "processed_images": len(all_results),
                "results": all_results
            },
            f,
            indent=2,
            ensure_ascii=False
        )

    # -----------------------------------------------------
    # GENEL ÖZET
    # -----------------------------------------------------

    total_detections = sum(
        result["detection_count"]
        for result in all_results
    )

    print("\n===================================")
    print("DETECTION TAMAMLANDI")
    print("===================================")

    print(f"Toplam görüntü: {len(image_paths)}")
    print(f"Başarıyla işlenen: {len(all_results)}")
    print(f"Toplam detection: {total_detections}")

    print(
        f"\nBackend JSON:\n"
        f"{all_json_path}"
    )

    print("\nÇıktılar outputs/ klasöründe.")