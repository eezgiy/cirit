"""
Spatial Mapping and Matcher Module for Drone Imagery (Hackathon Stage 2)

DEPRECATED / NOT USED BY THE LIVE PIPELINE: this duplicated the .NET backend's geo-transform
and track-matching logic (with a different match threshold - 50m here vs 100m in
Backend.Domain.TrackMatcher). The backend is now the single source of truth for this math;
tools.py/agent_loop.py call its Tool API instead of this module. Kept only for offline/manual
experimentation with the raw stage2 files.

Performs:
1. Pixel coordinate to WGS84 (Lat, Lon) linear projection using image metadata corners.
2. Temporal & spatial matching between target coordinates and vehicle tracks from tracks.csv.
3. Intelligence fusion: Linking detections with field reports (field_reports.json) and zone context (zones.json).
"""

import logging
import math
import re
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple, Union

import pandas as pd

from data_manager import DataManager, get_data_manager

logger = logging.getLogger("SpatialMappingAndMatcher")


def haversine_distance(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    """
    Calculates the great-circle distance between two WGS84 points in meters.

    Args:
        lat1, lon1: First point in degrees.
        lat2, lon2: Second point in degrees.

    Returns:
        Distance in meters.
    """
    r_earth = 6371000.0  # Earth's radius in meters
    phi1 = math.radians(lat1)
    phi2 = math.radians(lat2)
    delta_phi = math.radians(lat2 - lat1)
    delta_lambda = math.radians(lon2 - lon1)

    a = (
        math.sin(delta_phi / 2.0) ** 2
        + math.cos(phi1) * math.cos(phi2) * math.sin(delta_lambda / 2.0) ** 2
    )
    c = 2.0 * math.atan2(math.sqrt(a), math.sqrt(1.0 - a))
    return r_earth * c


def parse_time_to_minutes(time_str: str) -> int:
    """
    Parses 'HH:MM' string to total minutes from midnight for quick delta comparisons.
    """
    try:
        parts = time_str.strip().split(":")
        return int(parts[0]) * 60 + int(parts[1])
    except Exception:
        return 0


class SpatialMappingAndMatcher:
    """
    Spatial Mapping and Tracking Matcher for Aerial Drone Surveillance.
    """

    def __init__(
        self,
        data_manager: Optional[DataManager] = None,
        data_dir: Optional[Union[str, Path]] = None,
    ) -> None:
        """
        Initializes the matcher with a DataManager instance.

        Args:
            data_manager: Optional existing DataManager instance.
            data_dir: Directory containing stage2 dataset files if data_manager is None.
        """
        if data_manager is not None:
            self.dm = data_manager
        else:
            self.dm = get_data_manager()
            if not self.dm._is_loaded:
                self.dm._resolve_data_dir(data_dir)
                self.dm.load_all_data()

        # Compile coordinate regex for field report mining: e.g. "39.9374N 32.8483E" or "39.9374 32.8483"
        self._coord_pattern = re.compile(
            r"(\d{2}\.\d+)\s*[Nn]?\s*[,/ ]\s*(\d{2}\.\d+)\s*[Ee]?"
        )

    def pixel_to_coordinate(self, img_id: str, x: float, y: float) -> Tuple[float, float]:
        """
        Converts pixel coordinates (x, y) on the drone image to real-world WGS84 (Lat, Lon).

        Formula:
            lon = top_left.lon + (x / width_px) * (top_right.lon - top_left.lon)
            lat = top_left.lat + (y / height_px) * (bottom_left.lat - top_left.lat)

        Args:
            img_id: Image ID (e.g. 'img_003839')
            x: Horizontal pixel position (0 <= x <= width_px)
            y: Vertical pixel position (0 <= y <= height_px)

        Returns:
            Tuple[float, float]: (latitude, longitude)

        Raises:
            KeyError: If img_id is not found in image_meta.json.
            ValueError: If metadata format is incomplete.
        """
        meta = self.dm.get_image_metadata(img_id)
        if not meta:
            raise KeyError(f"Image ID '{img_id}' not found in image_meta.json")

        width_px = float(meta["width_px"])
        height_px = float(meta["height_px"])

        corners = meta.get("corner_coordinates", {})
        top_left = corners.get("top_left")        # [lat, lon]
        top_right = corners.get("top_right")      # [lat, lon]
        bottom_left = corners.get("bottom_left")  # [lat, lon]

        if not (top_left and top_right and bottom_left):
            raise ValueError(f"Missing corner coordinates for image '{img_id}'")

        # Note: Index 0 is Latitude, Index 1 is Longitude
        top_left_lat, top_left_lon = float(top_left[0]), float(top_left[1])
        top_right_lat, top_right_lon = float(top_right[0]), float(top_right[1])
        bottom_left_lat, bottom_left_lon = float(bottom_left[0]), float(bottom_left[1])

        # Linear projection
        lon = top_left_lon + (x / width_px) * (top_right_lon - top_left_lon)
        lat = top_left_lat + (y / height_px) * (bottom_left_lat - top_left_lat)

        return lat, lon

    def bbox_to_coordinate(
        self, img_id: str, bbox: Union[List[float], Tuple[float, ...]]
    ) -> Tuple[float, float]:
        """
        Calculates center point of bounding box [x, y, w, h] and projects it to WGS84.

        Args:
            img_id: Image ID.
            bbox: [x, y, width, height] format.

        Returns:
            Tuple[float, float]: (center_latitude, center_longitude)
        """
        if len(bbox) < 4:
            raise ValueError(f"Invalid bbox format, expected [x, y, w, h], got {bbox}")

        x, y, w, h = float(bbox[0]), float(bbox[1]), float(bbox[2]), float(bbox[3])
        center_x = x + (w / 2.0)
        center_y = y + (h / 2.0)

        return self.pixel_to_coordinate(img_id, center_x, center_y)

    def find_matching_track(
        self,
        img_id: str,
        target_lat: float,
        target_lon: float,
        max_distance_meters: float = 50.0,
    ) -> Dict[str, Any]:
        """
        Matches a detected vehicle coordinate with recorded GPS tracks at capture_time.

        Args:
            img_id: Image ID to fetch capture_time.
            target_lat: Detected vehicle latitude.
            target_lon: Detected vehicle longitude.
            max_distance_meters: Distance threshold in meters (default: 50.0m).

        Returns:
            Dict containing match status, track ID, speed, route history, and base approach trend.
        """
        meta = self.dm.get_image_metadata(img_id)
        if not meta:
            return {
                "matched": False,
                "reason": f"Görsel meta verisi bulunamadı: {img_id}",
            }

        capture_time = meta.get("capture_time")
        if not capture_time:
            return {
                "matched": False,
                "reason": f"Görselin çekim saati (capture_time) tanımlı değil: {img_id}",
            }

        tracks_df = self.dm.get_tracks_df()
        if tracks_df is None or tracks_df.empty:
            return {
                "matched": False,
                "reason": "tracks.csv verisi yüklenmemiş veya boş.",
            }

        # Filter tracks at the exact capture time
        active_tracks = tracks_df[tracks_df["time"] == capture_time]

        # Fallback: If no exact time match, search for the nearest recorded time tick within +-5 minutes
        if active_tracks.empty:
            target_mins = parse_time_to_minutes(capture_time)
            all_times = tracks_df["time"].unique()
            time_diffs = [(t, abs(parse_time_to_minutes(t) - target_mins)) for t in all_times]
            time_diffs.sort(key=lambda x: x[1])
            
            if time_diffs and time_diffs[0][1] <= 5:
                nearest_time = time_diffs[0][0]
                logger.info(f"Capture time '{capture_time}' not in tracks, using nearest tick '{nearest_time}' (delta: {time_diffs[0][1]}m)")
                active_tracks = tracks_df[tracks_df["time"] == nearest_time]
            else:
                return {
                    "matched": False,
                    "reason": f"Saat {capture_time} civarında aktif araç izi (track) bulunamadı.",
                    "capture_time": capture_time,
                }

        # Find closest track to (target_lat, target_lon)
        min_distance = float("inf")
        closest_row = None

        for _, row in active_tracks.iterrows():
            dist = haversine_distance(target_lat, target_lon, float(row["lat"]), float(row["lon"]))
            if dist < min_distance:
                min_distance = dist
                closest_row = row

        if closest_row is None:
            return {
                "matched": False,
                "reason": f"Saat {capture_time} için hiçbir araç konumu bulunamadı.",
                "capture_time": capture_time,
            }

        closest_track_id = str(closest_row["track_id"])

        # Check threshold
        if min_distance > max_distance_meters:
            return {
                "matched": False,
                "reason": (
                    f"En yakın araç ({closest_track_id}) {min_distance:.1f}m mesafede. "
                    f"Eşik değer ({max_distance_meters:.1f}m) aşıldı."
                ),
                "closest_track_id": closest_track_id,
                "closest_distance_meters": round(min_distance, 2),
                "capture_time": capture_time,
            }

        # MATCH FOUND: Analyze vehicle history, speed, and base approach trend
        track_full_history = tracks_df[tracks_df["track_id"] == closest_track_id].copy()
        
        # Sort by time up to current capture time
        track_full_history["mins"] = track_full_history["time"].apply(parse_time_to_minutes)
        current_mins = parse_time_to_minutes(capture_time)
        past_points = track_full_history[track_full_history["mins"] <= current_mins].sort_values("mins")

        # 1. Base coordinates (Merkez Us)
        base_coords = self.dm.get_base_coordinates() or (39.92184, 32.85306)
        base_lat, base_lon = base_coords

        # 2. Route history with distance to base
        history_list = []
        for _, p in past_points.iterrows():
            p_lat, p_lon = float(p["lat"]), float(p["lon"])
            d_base = haversine_distance(p_lat, p_lon, base_lat, base_lon)
            history_list.append({
                "time": str(p["time"]),
                "lat": round(p_lat, 6),
                "lon": round(p_lon, 6),
                "distance_to_base_meters": round(d_base, 1),
            })

        # 3. Calculate Speed (km/h) between the last two waypoints
        speed_kmh = 0.0
        if len(history_list) >= 2:
            last_p = history_list[-1]
            prev_p = history_list[-2]
            delta_dist = haversine_distance(prev_p["lat"], prev_p["lon"], last_p["lat"], last_p["lon"])
            delta_time_sec = (parse_time_to_minutes(last_p["time"]) - parse_time_to_minutes(prev_p["time"])) * 60
            if delta_time_sec > 0:
                speed_kmh = round((delta_dist / delta_time_sec) * 3.6, 2)

        # 4. Analyze Base Approach Trend
        base_trend = self._analyze_base_approach_trend(history_list)

        return {
            "matched": True,
            "track_id": closest_track_id,
            "match_distance_meters": round(min_distance, 2),
            "capture_time": capture_time,
            "target_coordinate": (round(target_lat, 6), round(target_lon, 6)),
            "track_coordinate": (round(float(closest_row["lat"]), 6), round(float(closest_row["lon"]), 6)),
            "speed_kmh": speed_kmh,
            "base_approach_trend": base_trend,
            "total_recorded_points": len(history_list),
            "route_history": history_list[-10:],  # Last 10 waypoints for brevity
        }

    def _analyze_base_approach_trend(self, history_list: List[Dict[str, Any]]) -> Dict[str, Any]:
        """
        Analyzes whether the vehicle is approaching or moving away from the base headquarters.
        """
        if not history_list:
            return {"status": "unknown", "description": "Yetersiz rota verisi."}

        current_dist = history_list[-1]["distance_to_base_meters"]

        if len(history_list) < 2:
            return {
                "status": "stationary",
                "distance_to_base_meters": current_dist,
                "description": f"Tek nokta mevcut, üsse mesafe {current_dist:.1f}m.",
            }

        # Look at distance delta over the last 3-4 points if available
        lookback = min(4, len(history_list))
        initial_dist = history_list[-lookback]["distance_to_base_meters"]
        net_dist_change = current_dist - initial_dist  # negative = getting closer

        if net_dist_change < -50.0:
            trend = "approaching"
            desc = (
                f"Üsse yaklaşıyor (Son {lookback} noktada mesafe "
                f"{initial_dist:.0f}m -> {current_dist:.0f}m, fark: {net_dist_change:.0f}m)."
            )
        elif net_dist_change > 50.0:
            trend = "moving_away"
            desc = (
                f"Üsten uzaklaşıyor (Son {lookback} noktada mesafe "
                f"{initial_dist:.0f}m -> {current_dist:.0f}m, fark: +{net_dist_change:.0f}m)."
            )
        else:
            trend = "stable_or_tangential"
            desc = f"Üsse mesafesini koruyor / teğet seyrediyor ({current_dist:.0f}m)."

        return {
            "status": trend,
            "current_distance_to_base_meters": current_dist,
            "delta_distance_meters": round(net_dist_change, 1),
            "description": desc,
        }

    def get_relevant_reports(
        self,
        img_id: str,
        target_lat: float,
        target_lon: float,
        max_time_diff_minutes: int = 60,
        max_distance_km: float = 3.0,
    ) -> List[Dict[str, Any]]:
        """
        Finds field reports relevant to the detection by temporal proximity and spatial context.

        Args:
            img_id: Image ID to get capture_time.
            target_lat: Target latitude.
            target_lon: Target longitude.
            max_time_diff_minutes: Maximum time delta in minutes (default: 60 min).
            max_distance_km: Maximum distance in kilometers for spatial match (default: 3.0 km).

        Returns:
            List of matching field report dictionaries with match details.
        """
        meta = self.dm.get_image_metadata(img_id)
        capture_time = meta.get("capture_time", "12:00") if meta else "12:00"
        target_time_mins = parse_time_to_minutes(capture_time)

        all_reports = self.dm.get_field_reports()
        relevant_reports: List[Dict[str, Any]] = []

        # Find closest zone name for target coordinate
        nearest_zone_name = self._get_nearest_zone_name(target_lat, target_lon)

        for rep in all_reports:
            rep_time = rep.get("time", "")
            rep_text = rep.get("text", "")
            rep_source = rep.get("source", "unknown")

            # 1. Temporal filter
            rep_time_mins = parse_time_to_minutes(rep_time)
            time_diff = abs(rep_time_mins - target_time_mins)
            if time_diff > max_time_diff_minutes:
                continue

            # 2. Spatial extraction from report text
            extracted_coords = self._extract_coordinates_from_text(rep_text)
            matched_reason = []
            dist_km = None

            if extracted_coords:
                r_lat, r_lon = extracted_coords
                dist_m = haversine_distance(target_lat, target_lon, r_lat, r_lon)
                dist_km = round(dist_m / 1000.0, 2)
                if dist_km <= max_distance_km:
                    matched_reason.append(f"Doğrudan koordinat eşleşmesi (~{int(dist_m)}m)")
                else:
                    # Report has coordinates but too far
                    continue
            else:
                # 3. Zone name match in report text
                if nearest_zone_name and nearest_zone_name.lower() in rep_text.lower():
                    matched_reason.append(f"Bölge ismi eşleşmesi ('{nearest_zone_name}')")

            # If report is general intelligence (e.g. tatbikat/hava durumu) and very close in time (< 30 min)
            if not matched_reason and time_diff <= 30:
                text_lower = rep_text.lower()
                if any(w in text_lower for w in ["tatbikat", "dost unsur", "hava", "ihbar"]):
                    matched_reason.append("Genel istihbarat / operasyonel bilgi")

            if matched_reason:
                relevant_reports.append({
                    "time": rep_time,
                    "time_diff_minutes": time_diff,
                    "source": rep_source,
                    "text": rep_text,
                    "distance_km": dist_km,
                    "match_reasons": matched_reason,
                })

        # Sort by relevance: shortest distance first, then lowest time diff
        relevant_reports.sort(
            key=lambda r: (
                r["distance_km"] if r["distance_km"] is not None else 999.0,
                r["time_diff_minutes"],
            )
        )
        return relevant_reports

    def _extract_coordinates_from_text(self, text: str) -> Optional[Tuple[float, float]]:
        """
        Extracts (lat, lon) coordinates from unstructured field report text.
        Example: '39.9374N 32.8483E civarinda 1 kamyon goruldu' -> (39.9374, 32.8483)
        """
        match = self._coord_pattern.search(text)
        if match:
            try:
                lat = float(match.group(1))
                lon = float(match.group(2))
                return lat, lon
            except Exception:
                return None
        return None

    def _get_nearest_zone_name(self, lat: float, lon: float) -> Optional[str]:
        """
        Determines the nearest zone name defined in zones.json for a given coordinate.
        """
        zones_data = self.dm.get_zones()
        if not zones_data:
            return None

        zones_list = zones_data.get("zones", [])
        closest_name = None
        min_dist = float("inf")

        for z in zones_list:
            center = z.get("center")
            if center and len(center) >= 2:
                z_lat, z_lon = float(center[0]), float(center[1])
                d = haversine_distance(lat, lon, z_lat, z_lon)
                if d < min_dist:
                    min_dist = d
                    closest_name = z.get("name")

        return closest_name

    def process_detection(
        self,
        img_id: str,
        detection: Dict[str, Any],
        max_track_distance_meters: float = 50.0,
    ) -> Dict[str, Any]:
        """
        High-level pipeline: Fuses YOLO detection with WGS84 coordinate projection,
        GPS track matching, and intelligence field reports.

        Args:
            img_id: Image identifier.
            detection: Detection dict with 'class', 'bbox', 'confidence'.
            max_track_distance_meters: Maximum distance threshold for track matching.

        Returns:
            Dict containing full intelligence package for the detection.
        """
        bbox = detection["bbox"]
        det_class = detection.get("class", "unknown")
        confidence = detection.get("confidence", 0.0)

        # 1. Project pixel bbox center to WGS84
        target_lat, target_lon = self.bbox_to_coordinate(img_id, bbox)

        # 2. Match with vehicle GPS tracks
        track_match = self.find_matching_track(
            img_id, target_lat, target_lon, max_distance_meters=max_track_distance_meters
        )

        # 3. Retrieve relevant field reports
        reports = self.get_relevant_reports(img_id, target_lat, target_lon)

        return {
            "img_id": img_id,
            "detection": {
                "class": det_class,
                "confidence": confidence,
                "bbox": bbox,
            },
            "wgs84_coordinate": {
                "lat": round(target_lat, 6),
                "lon": round(target_lon, 6),
            },
            "track_match": track_match,
            "relevant_field_reports": reports,
        }


# Global singleton instance
_global_matcher: Optional[SpatialMappingAndMatcher] = None


def get_spatial_matcher() -> SpatialMappingAndMatcher:
    """Returns or creates the global SpatialMappingAndMatcher instance."""
    global _global_matcher
    if _global_matcher is None:
        _global_matcher = SpatialMappingAndMatcher()
    return _global_matcher


def pixel_to_coordinate(img_id: str, x: float, y: float) -> Tuple[float, float]:
    """Module-level shortcut for pixel_to_coordinate."""
    return get_spatial_matcher().pixel_to_coordinate(img_id, x, y)


def find_matching_track(
    img_id: str, target_lat: float, target_lon: float, max_distance_meters: float = 50.0
) -> Dict[str, Any]:
    """Module-level shortcut for find_matching_track."""
    return get_spatial_matcher().find_matching_track(
        img_id, target_lat, target_lon, max_distance_meters=max_distance_meters
    )


def get_relevant_reports(
    img_id: str, target_lat: float, target_lon: float
) -> List[Dict[str, Any]]:
    """Module-level shortcut for get_relevant_reports."""
    return get_spatial_matcher().get_relevant_reports(img_id, target_lat, target_lon)


# ==============================================================================
# Standalone Test Execution Block
# ==============================================================================
if __name__ == "__main__":
    import json

    print("=" * 70)
    print("SPATIAL MAPPING & MATCHER TEST WITH MOCK YOLO DETECTION")
    print("=" * 70)

    # Initialize matcher
    matcher = SpatialMappingAndMatcher()

    # 1. Mock YOLO detection input
    mock_img_id = "img_003839"
    mock_detection = {
        "class": "truck",
        "bbox": [610, 380, 60, 28],
        "confidence": 0.92,
    }

    print(f"\n[Girdi] Görsel ID: {mock_img_id}")
    print(f"[Girdi] YOLO Tespiti: Sınıf={mock_detection['class']}, Güven={mock_detection['confidence']}, BBox={mock_detection['bbox']}")

    # 2. Test 1: pixel_to_coordinate
    x_c = mock_detection["bbox"][0] + mock_detection["bbox"][2] / 2.0
    y_c = mock_detection["bbox"][1] + mock_detection["bbox"][3] / 2.0
    lat, lon = matcher.pixel_to_coordinate(mock_img_id, x_c, y_c)
    print(f"\n--- 1. Piksel -> WGS84 Dönüşümü ---")
    print(f"Merkez Piksel : ({x_c:.1f}, {y_c:.1f})")
    print(f"Hesaplanan WGS84: Enlem={lat:.6f}, Boylam={lon:.6f}")

    # 3. Test 2: find_matching_track
    print(f"\n--- 2. Araç İzi (tracks.csv) Eşleştirme (Maks Mesafe: 50m) ---")
    match_result = matcher.find_matching_track(mock_img_id, lat, lon, max_distance_meters=50.0)
    if match_result.get("matched"):
        print(f"[EŞLEŞTİ] Track ID: {match_result['track_id']}")
        print(f"Mesafe      : {match_result['match_distance_meters']} metre")
        print(f"Tahmini Hız : {match_result['speed_kmh']} km/h")
        print(f"Üs Trendi   : {match_result['base_approach_trend']['description']}")
        print(f"Geçmiş Nokta: {match_result['total_recorded_points']} adet rota kaydı mevcut.")
    else:
        print(f"[EŞLEŞMEDİ] Sebep: {match_result.get('reason')}")
        if "closest_track_id" in match_result:
            print(f"En yakın araç: {match_result['closest_track_id']} ({match_result['closest_distance_meters']}m)")

    # 4. Test 3: get_relevant_reports
    print(f"\n--- 3. İlgili Saha Raporları (field_reports.json) ---")
    reports = matcher.get_relevant_reports(mock_img_id, lat, lon)
    print(f"Bulunan ilgili rapor sayısı: {len(reports)}")
    for i, rep in enumerate(reports, start=1):
        dist_str = f"{rep['distance_km']} km" if rep['distance_km'] is not None else "Bölgesel/Genel"
        print(f"  [{i}] Saat: {rep['time']} (Fark: {rep['time_diff_minutes']} dk) | Kaynak: {rep['source']:<11} | Mesafe: {dist_str}")
        print(f"      Eşleşme Nedeni: {', '.join(rep['match_reasons'])}")
        print(f"      Rapor Metni   : \"{rep['text']}\"")

    # 5. Full End-to-End Pipeline Test
    print(f"\n--- 4. Uçtan Uca Entegre İstihbarat Paketi (process_detection) ---")
    full_package = matcher.process_detection(mock_img_id, mock_detection)
    print(json.dumps(full_package, indent=2, ensure_ascii=False))

    print("\n" + "=" * 70)
    print("TEST TAMAMLANDI")
    print("=" * 70)
