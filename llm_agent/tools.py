"""
Tools Module for the LLM agent (GLM-5.3-Flash / OpenAI tool-calling format).
Provides the @tool decorator and complete tool registry for autonomous intelligence fusion.

Design principle (backend is the single source of truth for every deterministic
computation - geo-transform, track-matching, movement features, report resolution):
the Kafka message the agent receives (agent.assessment.requested) already carries the
INITIAL deterministic context for the track being assessed (trackDetails, movementProfile,
candidateFieldReports) - the agent does NOT need a tool call to get that. Tools below exist
only for what the agent might need BEYOND that initial snapshot: checking a different
track (e.g. a suspected convoy member), a wider/different field-report search, or the
nearest zone for an arbitrary coordinate the agent derives during reasoning.

Every tool here is a thin HTTP client over the backend's Tool API (see backend/README.md
"Tool API" section) - no local recomputation of geo/track/report logic, so there is exactly
one implementation of that math in the whole system.
"""

import functools
import inspect
import json
import logging
import os
from typing import Any, Callable, Dict, List, Optional, Union

import requests

logger = logging.getLogger("ToolsModule")

BACKEND_BASE_URL = os.environ.get("BACKEND_BASE_URL", "http://localhost:5299").rstrip("/")
_HTTP_TIMEOUT_SECONDS = 10

# Sentinel used by agent_loop to recognize the final-answer tool without a magic string
SUBMIT_ASSESSMENT_TOOL_NAME = "submit_final_assessment_tool"

# Tool registries
TOOL_REGISTRY: Dict[str, Callable[..., str]] = {}
TOOL_MAP: Dict[str, Callable[..., str]] = {}
OPENAI_TOOLS: List[Dict[str, Any]] = []


def _get(path: str, params: Optional[Dict[str, Any]] = None) -> str:
    """GETs a backend Tool API endpoint and returns the body as a JSON string, uniformly
    wrapping HTTP/network errors into the same {"status": "error", ...} shape."""
    url = f"{BACKEND_BASE_URL}{path}"
    try:
        response = requests.get(url, params=params, timeout=_HTTP_TIMEOUT_SECONDS)
    except requests.RequestException as ex:
        return json.dumps({
            "status": "error",
            "message": f"Backend'e ulaşılamadı ({url}): {ex}",
        }, ensure_ascii=False)

    if response.status_code == 404:
        return json.dumps({
            "status": "not_found",
            "message": response.json().get("error", response.text) if response.content else "Bulunamadı.",
        }, ensure_ascii=False)

    if not response.ok:
        return json.dumps({
            "status": "error",
            "message": f"Backend {response.status_code} döndürdü: {response.text}",
        }, ensure_ascii=False)

    body = response.json()
    wrapped = {"status": "success", "results": body} if isinstance(body, list) else {"status": "success", **body}
    return json.dumps(wrapped, indent=2, ensure_ascii=False)


def _infer_parameters_schema(fn: Callable) -> Dict[str, Any]:
    """Infers an OpenAI-compatible JSON Schema from python type hints."""
    sig = inspect.signature(fn)
    properties: Dict[str, Any] = {}
    required: List[str] = []

    type_mapping = {
        str: "string",
        int: "integer",
        float: "number",
        bool: "boolean",
        list: "array",
        dict: "object",
    }

    for param_name, param in sig.parameters.items():
        if param_name in ("self", "cls"):
            continue

        ann = param.annotation
        is_optional = False
        prop_type = "string"

        origin = getattr(ann, "__origin__", None)
        args = getattr(ann, "__args__", ())

        if origin is Union and type(None) in args:
            is_optional = True
            real_types = [a for a in args if a is not type(None)]
            if real_types:
                ann = real_types[0]
                origin = getattr(ann, "__origin__", None)

        if ann in type_mapping:
            prop_type = type_mapping[ann]
        elif origin in (list, List):
            prop_type = "array"
        elif origin in (dict, Dict):
            prop_type = "object"

        prop_def: Dict[str, Any] = {"type": prop_type}
        if prop_type == "array":
            prop_def["items"] = {"type": "string"}

        properties[param_name] = prop_def
        if param.default is inspect.Parameter.empty and not is_optional:
            required.append(param_name)

    return {
        "type": "object",
        "properties": properties,
        "required": required,
    }


# ==============================================================================
# @tool DECORATOR
# ==============================================================================

def tool(
    func_or_name: Optional[Union[Callable, str]] = None,
    *,
    name: Optional[str] = None,
    description: Optional[str] = None,
    parameters: Optional[Dict[str, Any]] = None,
):
    """
    @tool decorator to define and register an agent tool.
    Can be used with or without arguments:
        @tool
        def my_tool(arg: str) -> str: ...

        @tool(name="custom_name", description="custom description")
        def my_tool(arg: str) -> str: ...
    """
    def decorator(fn: Callable) -> Callable:
        tool_name = name or (func_or_name if isinstance(func_or_name, str) else fn.__name__)
        doc = (description or fn.__doc__ or "").strip()
        doc_clean = " ".join([line.strip() for line in doc.splitlines() if line.strip()])

        param_schema = parameters or _infer_parameters_schema(fn)

        openai_tool = {
            "type": "function",
            "function": {
                "name": tool_name,
                "description": doc_clean,
                "parameters": param_schema,
            },
        }

        # Register in registries
        TOOL_REGISTRY[tool_name] = fn
        TOOL_MAP[tool_name] = fn

        existing_idx = next((i for i, t in enumerate(OPENAI_TOOLS) if t.get("function", {}).get("name") == tool_name), -1)
        if existing_idx >= 0:
            OPENAI_TOOLS[existing_idx] = openai_tool
        else:
            OPENAI_TOOLS.append(openai_tool)

        fn.is_tool = True
        fn.tool_name = tool_name
        fn.tool_description = doc_clean
        fn.openai_tool = openai_tool

        @functools.wraps(fn)
        def wrapper(*args, **kwargs):
            return fn(*args, **kwargs)

        return wrapper

    if callable(func_or_name):
        return decorator(func_or_name)
    return decorator


# ==============================================================================
# TOOL IMPLEMENTATIONS (DECORATED WITH @tool)
# ==============================================================================

@tool(
    name="get_base_location_tool",
    description="Askeri üssün adını ve WGS84 coğrafi koordinatlarını (lat, lon) döner.",
    parameters={"type": "object", "properties": {}, "required": []}
)
def get_base_location_tool() -> str:
    """Askeri üssün adını ve WGS84 koordinatlarını döner."""
    return _get("/api/base-location")


@tool(
    name="get_zones_tool",
    description="Sistemde tanımlı tüm taktiksel bölgeleri (WayPoint/Zone) ve merkez koordinatlarını listeler.",
    parameters={"type": "object", "properties": {}, "required": []}
)
def get_zones_tool() -> str:
    """Tüm taktiksel bölgeleri listeler."""
    return _get("/api/zones")


@tool(
    name="get_nearest_zone_tool",
    description="Keyfi bir WGS84 koordinatına en yakın taktik bölgeyi (Zone) ve üsse olan mesafesini döner.",
    parameters={
        "type": "object",
        "properties": {
            "lat": {"type": "number", "description": "Enlem (WGS84 koordinatı)."},
            "lon": {"type": "number", "description": "Boylam (WGS84 koordinatı)."}
        },
        "required": ["lat", "lon"]
    }
)
def get_nearest_zone_tool(lat: float, lon: float) -> str:
    """Verilen koordinata en yakın bölgeyi ve üsse mesafesini döner."""
    return _get("/api/zones/nearest", params={"lat": lat, "lon": lon})


@tool(
    name="get_all_tracks_tool",
    description="Sistemdeki tüm araçların (track_id) özet listesini ve genel durumlarını döner.",
    parameters={"type": "object", "properties": {}, "required": []}
)
def get_all_tracks_tool() -> str:
    """Tüm track özetlerini döner."""
    return _get("/api/tracks")


@tool(
    name="get_track_details_tool",
    description=(
        "Başka bir track_id'nin kimlik/özetini (ilk/son görülme zamanı, gözlenen "
        "sınıflar, eşleşen görüntüler) backend'den çeker. Değerlendirdiğin ana track "
        "için bu bilgi zaten girdinde mevcut - bu tool sadece İLİŞKİLİ/BAŞKA bir "
        "track'i (ör. şüpheli konvoy üyesi) kontrol etmek içindir."
    ),
    parameters={
        "type": "object",
        "properties": {
            "track_id": {"type": "string", "description": "Sorgulanacak track kimliği (Örn: 'T0187')."}
        },
        "required": ["track_id"]
    }
)
def get_track_details_tool(track_id: str) -> str:
    """Belirli bir track'in detaylarını çeker."""
    return _get(f"/api/tracks/{track_id}")


@tool(
    name="get_track_points_tool",
    description="Belirli bir track_id'nin baştan sona tüm hareket rotası koordinatlarını (lat, lon) ve zaman damgalarını döner.",
    parameters={
        "type": "object",
        "properties": {
            "track_id": {"type": "string", "description": "Sorgulanacak track kimliği."}
        },
        "required": ["track_id"]
    }
)
def get_track_points_tool(track_id: str) -> str:
    """Bir track'in tüm rota noktalarını döner."""
    return _get(f"/api/tracks/{track_id}/points")


@tool(
    name="get_movement_profile_tool",
    description=(
        "Başka bir track_id'nin hareket profilini (durum: Approaching/Stationary/Orbiting/MovingAway, "
        "anlık/ortalama hız, durağanlık süresi, üsse mesafe/trend, orbit skoru) backend'den çeker. "
        "Ana track için bu bilgi zaten girdinde mevcut."
    ),
    parameters={
        "type": "object",
        "properties": {
            "track_id": {"type": "string", "description": "Sorgulanacak track kimliği."}
        },
        "required": ["track_id"]
    }
)
def get_movement_profile_tool(track_id: str) -> str:
    """Bir track'in hareket profilini döner."""
    return _get(f"/api/tracks/{track_id}/movement-profile")


@tool(
    name="compare_with_reports_tool",
    description="Başka bir track_id için track detayı + hareket profili + aday saha raporlarını tek seferde birleşik getirir.",
    parameters={
        "type": "object",
        "properties": {
            "track_id": {"type": "string", "description": "Track kimliği."}
        },
        "required": ["track_id"]
    }
)
def compare_with_reports_tool(track_id: str) -> str:
    """Track detay + hareket + raporları tek seferde getirir."""
    return _get(f"/api/tracks/{track_id}/compare-reports")


@tool(
    name="get_all_field_reports_tool",
    description="Sistemdeki tüm istihbarat saha raporlarının tam listesini döner.",
    parameters={"type": "object", "properties": {}, "required": []}
)
def get_all_field_reports_tool() -> str:
    """Tüm saha raporlarını döner."""
    return _get("/api/field-reports")


@tool(
    name="search_field_reports_tool",
    description=(
        "Değerlendirdiğin track için, girdide zaten verilen aday raporlardan DAHA GENİŞ "
        "yarıçap (metre) veya konumsuz/genel raporlar dahil edilerek saha raporu araması yapar."
    ),
    parameters={
        "type": "object",
        "properties": {
            "track_id": {"type": "string", "description": "Track kimliği."},
            "radius_meters": {"type": "number", "description": "Arama yarıçapı (metre)."},
            "include_unresolved": {"type": "boolean", "description": "Konumu çözümlenememiş genel durum raporlarını dahil et."}
        },
        "required": ["track_id"]
    }
)
def search_field_reports_tool(track_id: str, radius_meters: Optional[float] = None, include_unresolved: bool = False) -> str:
    """Bir track için genişletilmiş saha raporu araması yapar."""
    params: Dict[str, Any] = {"includeUnresolved": str(include_unresolved).lower()}
    if radius_meters is not None:
        params["radiusMeters"] = radius_meters
    return _get(f"/api/tracks/{track_id}/field-reports", params=params)


@tool(
    name="search_field_reports_near_tool",
    description="Belirli bir WGS84 koordinatı (enlem, boylam) civarındaki aday saha istihbarat raporlarını arar.",
    parameters={
        "type": "object",
        "properties": {
            "lat": {"type": "number", "description": "Enlem (WGS84 koordinatı)."},
            "lon": {"type": "number", "description": "Boylam (WGS84 koordinatı)."},
            "radius_meters": {"type": "number", "description": "Arama yarıçapı (metre, varsayılan 800m)."},
            "include_unresolved": {"type": "boolean", "description": "Konumu çözümlenememiş genel raporları da dahil et."}
        },
        "required": ["lat", "lon"]
    }
)
def search_field_reports_near_tool(lat: float, lon: float, radius_meters: Optional[float] = None, include_unresolved: bool = False) -> str:
    """Belirli bir koordinat civarında saha raporu arar."""
    params: Dict[str, Any] = {
        "lat": lat,
        "lon": lon,
        "includeUnresolved": str(include_unresolved).lower()
    }
    if radius_meters is not None:
        params["radiusMeters"] = radius_meters
    return _get("/api/field-reports/search", params=params)


@tool(
    name=SUBMIT_ASSESSMENT_TOOL_NAME,
    description="Analizi bitirdiğinde NİHAİ kararını bu tool ile bildir - bu, döngünün son adımıdır, bundan sonra başka tool çağırma veya serbest metin yazma.",
    parameters={
        "type": "object",
        "properties": {
            "verdict": {
                "type": "string",
                "enum": ["safe", "suspicious", "unsafe"],
                "description": "safe=dost/güvenli, suspicious=şüpheli/dikkat gerektirir, unsafe=tehdit."
            },
            "confidence": {"type": "number", "description": "0.0-1.0 arası güven skoru."},
            "reasoning": {
                "type": "string",
                "description": "Kısa ve net gerekçe: mesafe/hız/üsse yaklaşma durumu, saha raporu teyidi veya çelişkisi, tehdit seviyesi (yüksek/orta/düşük) ve önerilen taktik eylem dahil."
            },
            "evidence_refs": {
                "type": "array",
                "items": {"type": "string"},
                "description": "Kararı destekleyen kanıt referansları (image_id, rapor zamanı, track_id vb.)."
            }
        },
        "required": ["verdict", "confidence", "reasoning"]
    }
)
def submit_final_assessment_tool(
    verdict: str,
    confidence: float,
    reasoning: str,
    evidence_refs: Optional[List[str]] = None,
) -> str:
    """Nihai değerlendirmeyi yapılandırılmış olarak bildirir."""
    allowed = {"safe", "suspicious", "unsafe"}
    if verdict not in allowed:
        return json.dumps({
            "status": "error",
            "message": f"Geçersiz verdict '{verdict}'. Beklenen: {sorted(allowed)}.",
        }, ensure_ascii=False)

    return json.dumps({
        "status": "success",
        "message": "Nihai değerlendirme kaydedildi.",
        "verdict": verdict,
        "confidence": confidence,
        "reasoning": reasoning,
        "evidence_refs": evidence_refs or [],
    }, ensure_ascii=False)


# Alias for standard OpenAI API parameter
TOOLS = OPENAI_TOOLS


# ==============================================================================
# TOOL DISPATCHER FOR LLM TOOL-CALLING
# ==============================================================================

def execute_tool(name: str, arguments: Union[str, Dict[str, Any]]) -> str:
    """
    Executes a registered tool by name with arguments (either parsed dict or JSON string).
    Directly compatible with GLM-5.3-Flash / OpenAI tool_calls payload.
    """
    fn = TOOL_REGISTRY.get(name) or TOOL_MAP.get(name)
    if fn is None:
        return json.dumps({
            "status": "error",
            "message": f"Tool '{name}' bulunamadı. Tanımlı tool'lar: {list(TOOL_MAP.keys())}",
        }, ensure_ascii=False)

    if isinstance(arguments, str):
        try:
            arguments = json.loads(arguments) if arguments.strip() else {}
        except Exception as e:
            return json.dumps({
                "status": "error",
                "message": f"Argümanlar JSON olarak çözümlenemedi: {e}",
            }, ensure_ascii=False)
    elif arguments is None:
        arguments = {}

    try:
        return fn(**arguments)
    except TypeError as te:
        return json.dumps({"status": "error", "message": f"Geçersiz parametreler: {te}"}, ensure_ascii=False)
    except Exception as e:
        logger.error(f"Error executing tool '{name}': {e}", exc_info=True)
        return json.dumps({"status": "error", "message": str(e)}, ensure_ascii=False)


def get_all_tool_names() -> List[str]:
    """Returns the names of all registered tools."""
    return list(TOOL_REGISTRY.keys())


# ==============================================================================
# STANDALONE VERIFICATION AND DEMO (requires a running backend)
# ==============================================================================
if __name__ == "__main__":
    import sys

    print("=" * 70)
    print("BACKEND TOOL API - MANUEL DOGRULAMA")
    print(f"BACKEND_BASE_URL = {BACKEND_BASE_URL}")
    print(f"Toplam Kayıtlı Tool Sayısı: {len(TOOL_REGISTRY)}")
    print(f"Tool İsimleri: {get_all_tool_names()}")
    print("=" * 70)

    sample_track_id = sys.argv[1] if len(sys.argv) > 1 else "T0001"

    print("\n--- 1. get_base_location_tool() ---")
    print(execute_tool("get_base_location_tool", {}))

    print("\n--- 2. get_zones_tool() ---")
    print(execute_tool("get_zones_tool", {}))

    print(f"\n--- 3. get_track_details_tool('{sample_track_id}') ---")
    print(execute_tool("get_track_details_tool", {"track_id": sample_track_id}))

    print(f"\n--- 4. get_movement_profile_tool('{sample_track_id}') ---")
    print(execute_tool("get_movement_profile_tool", {"track_id": sample_track_id}))

    print("\n--- 5. get_nearest_zone_tool(39.937228, 32.849073) ---")
    print(execute_tool("get_nearest_zone_tool", {"lat": 39.937228, "lon": 32.849073}))

    print(f"\n--- 6. submit_final_assessment_tool (örnek) ---")
    print(execute_tool(SUBMIT_ASSESSMENT_TOOL_NAME, {
        "verdict": "suspicious", "confidence": 0.6, "reasoning": "Test.", "evidence_refs": [],
    }))
