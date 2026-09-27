"""
Data Management Module for Hackathon Stage 2

DEPRECATED / NOT USED BY THE LIVE PIPELINE: the .NET backend is now the single source of
truth for these datasets (it upserts them via /api/reference-data/* and does the geo-transform
+ track-matching once, consistently - see backend/README.md). tools.py, agent_loop.py and
kafka_service.py no longer import this module; they call the backend's Tool API instead.
Kept only for offline/manual experimentation with the raw stage2 files.

Handles loading, in-memory caching, indexing, and querying of static project datasets:
1. image_meta.json (Image dimensions, capture times, corner WGS84 coordinates)
2. zones.json (Base and 8 zone coordinates)
3. tracks.csv (Past 2 hours vehicle GPS tracks: track_id, time, lat, lon)
4. field_reports.json (Field reports: time, source, text)
"""

import json
import logging
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple, Union

import pandas as pd

# Lazy/convenience import of KafkaConsumerService and SpatialMappingAndMatcher
try:
    from kafka_service import KafkaConsumerService
except ImportError:
    pass

try:
    from spatial_matcher import SpatialMappingAndMatcher
except ImportError:
    pass

# Setup module logger
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s"
)
logger = logging.getLogger("DataManager")


class DataManager:
    """
    Centralized in-memory Data Manager for Stage 2 datasets.
    Optimized for fast lookups (dict keys) and memory efficiency (pandas dtypes).
    """

    def __init__(self, data_dir: Optional[Union[str, Path]] = None) -> None:
        """
        Initialize DataManager.
        
        Args:
            data_dir: Path to directory containing the JSON and CSV files.
                      If None, auto-discovers './stage2' or current directory.
        """
        self.data_dir: Path = self._resolve_data_dir(data_dir)
        self.image_meta: Dict[str, Any] = {}
        self.zones: Dict[str, Any] = {}
        self.tracks_df: Optional[pd.DataFrame] = None
        self.field_reports: List[Dict[str, Any]] = []
        self._is_loaded: bool = False

    def _resolve_data_dir(self, custom_dir: Optional[Union[str, Path]]) -> Path:
        """
        Auto-discovers the directory where project files reside.
        """
        if custom_dir:
            p = Path(custom_dir)
            if p.exists():
                return p

        # Check candidate locations
        candidates = [
            Path(__file__).resolve().parent / "stage2",
            Path(__file__).resolve().parent,
            Path.cwd() / "stage2",
            Path.cwd(),
        ]
        for candidate in candidates:
            if (candidate / "image_meta.json").exists():
                logger.debug(f"Data directory resolved to: {candidate}")
                return candidate

        fallback = Path.cwd() / "stage2"
        logger.warning(f"Could not locate image_meta.json automatically. Defaulting to: {fallback}")
        return fallback

    def load_all_data(self) -> bool:
        """
        Safely loads all 4 data files into memory with appropriate type optimizations.
        
        Returns:
            bool: True if all files loaded successfully, False otherwise.
        """
        logger.info(f"Loading datasets from '{self.data_dir}'...")
        success = True

        # 1. Load image_meta.json
        meta_path = self.data_dir / "image_meta.json"
        try:
            with open(meta_path, "r", encoding="utf-8") as f:
                self.image_meta = json.load(f)
            logger.info(f"[OK] image_meta.json loaded ({len(self.image_meta)} images indexed)")
        except FileNotFoundError:
            logger.error(f"[ERR] File not found: {meta_path}")
            success = False
        except json.JSONDecodeError as e:
            logger.error(f"[ERR] Invalid JSON in {meta_path}: {e}")
            success = False
        except Exception as e:
            logger.error(f"[ERR] Unexpected error loading {meta_path}: {e}")
            success = False

        # 2. Load zones.json
        zones_path = self.data_dir / "zones.json"
        try:
            with open(zones_path, "r", encoding="utf-8") as f:
                self.zones = json.load(f)
            zone_count = len(self.zones.get("zones", []))
            logger.info(f"[OK] zones.json loaded (Base + {zone_count} zones indexed)")
        except FileNotFoundError:
            logger.error(f"[ERR] File not found: {zones_path}")
            success = False
        except json.JSONDecodeError as e:
            logger.error(f"[ERR] Invalid JSON in {zones_path}: {e}")
            success = False
        except Exception as e:
            logger.error(f"[ERR] Unexpected error loading {zones_path}: {e}")
            success = False

        # 3. Load tracks.csv (Optimized memory with categorical track_id and float64 coordinates)
        tracks_path = self.data_dir / "tracks.csv"
        try:
            # Using specific dtypes to save memory and optimize search
            self.tracks_df = pd.read_csv(
                tracks_path,
                dtype={
                    "track_id": "category",
                    "time": "string",
                    "lat": "float64",
                    "lon": "float64",
                },
            )
            # Memory footprint in MB
            mem_mb = self.tracks_df.memory_usage(deep=True).sum() / (1024 * 1024)
            logger.info(f"[OK] tracks.csv loaded ({len(self.tracks_df)} rows, ~{mem_mb:.2f} MB in RAM)")
        except FileNotFoundError:
            logger.error(f"[ERR] File not found: {tracks_path}")
            success = False
        except Exception as e:
            logger.error(f"[ERR] Unexpected error loading {tracks_path}: {e}")
            success = False

        # 4. Load field_reports.json
        reports_path = self.data_dir / "field_reports.json"
        try:
            with open(reports_path, "r", encoding="utf-8") as f:
                self.field_reports = json.load(f)
            logger.info(f"[OK] field_reports.json loaded ({len(self.field_reports)} reports indexed)")
        except FileNotFoundError:
            logger.error(f"[ERR] File not found: {reports_path}")
            success = False
        except json.JSONDecodeError as e:
            logger.error(f"[ERR] Invalid JSON in {reports_path}: {e}")
            success = False
        except Exception as e:
            logger.error(f"[ERR] Unexpected error loading {reports_path}: {e}")
            success = False

        self._is_loaded = success
        return success

    def get_image_metadata(self, img_id: str) -> Optional[Dict[str, Any]]:
        """
        Retrieves metadata for a specific image ID.
        
        Args:
            img_id: Unique image identifier (e.g. 'img_003839' or 'img_003839.jpg')
            
        Returns:
            Dict containing width_px, height_px, capture_time, corner_coordinates, or None.
        """
        if not self.image_meta:
            logger.warning("image_meta is empty. Did you call load_all_data()?")
            return None

        # Normalize key if .jpg or path was supplied
        clean_id = Path(img_id).stem

        meta = self.image_meta.get(clean_id)
        if meta is None:
            logger.debug(f"Metadata for '{clean_id}' not found in image_meta.json")
        return meta

    def get_all_image_ids(self) -> List[str]:
        """Returns a list of all registered image IDs."""
        return list(self.image_meta.keys())

    def get_zones(self) -> Dict[str, Any]:
        """Returns the full zones dictionary (base + 8 zones)."""
        return self.zones

    def get_base_coordinates(self) -> Optional[Tuple[float, float]]:
        """Returns (lat, lon) of the base headquarters (Merkez Us)."""
        base = self.zones.get("base")
        if base and "lat" in base and "lon" in base:
            return float(base["lat"]), float(base["lon"])
        return None

    def get_tracks_df(self) -> Optional[pd.DataFrame]:
        """Returns the cached tracks DataFrame."""
        return self.tracks_df

    def get_track_by_id(self, track_id: str) -> Optional[pd.DataFrame]:
        """
        Filters tracks DataFrame by a specific track ID.
        
        Args:
            track_id: ID of the track (e.g. 'T0001')
        """
        if self.tracks_df is None or self.tracks_df.empty:
            return None
        return self.tracks_df[self.tracks_df["track_id"] == track_id]

    def get_field_reports(self, source: Optional[str] = None) -> List[Dict[str, Any]]:
        """
        Returns field reports, optionally filtered by source ('official' or 'third_party').
        """
        if not source:
            return self.field_reports
        return [r for r in self.field_reports if r.get("source") == source]


# Global singleton instance for module-level convenience functions
_global_data_manager = DataManager()


def load_all_data(data_dir: Optional[Union[str, Path]] = None) -> bool:
    """
    Module-level function to safely load all datasets into the global DataManager.
    
    Args:
        data_dir: Optional custom directory path.
        
    Returns:
        bool: True on success.
    """
    global _global_data_manager
    if data_dir is not None:
        _global_data_manager = DataManager(data_dir=data_dir)
    return _global_data_manager.load_all_data()


def get_image_metadata(img_id: str) -> Optional[Dict[str, Any]]:
    """
    Module-level function to get metadata for a specific image ID.
    
    Args:
        img_id: Image identifier (e.g. 'img_003839')
        
    Returns:
        Dict of metadata or None.
    """
    return _global_data_manager.get_image_metadata(img_id)


def get_data_manager() -> DataManager:
    """Returns the global DataManager instance, auto-loading data if not yet loaded."""
    if not _global_data_manager._is_loaded:
        _global_data_manager.load_all_data()
    return _global_data_manager
