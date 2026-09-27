"""
Agent Loop Module for Z.ai GLM-5.3-Flash / OpenAI Tool-Calling
Performs autonomous intelligence fusion, reasoning, and multi-turn tool calling.
Supports both official 'openai' library and zero-dependency native urllib fallback.

Design principle: the backend has already done every deterministic computation for the
track being assessed (geo-transform, track-matching, movement features, report resolution)
and hands it to this agent as INPUT (see run_agent_for_track's track_context parameter,
which is exactly the deserialized agent.assessment.requested Kafka message). Tools (tools.py)
exist only for what the agent might additionally need beyond that snapshot.
"""

import json
import logging
import os
import sys
import time
import urllib.error
import urllib.request
from typing import Any, Dict, List, Optional, Union

# Reconfigure stdout/stderr encoding for Windows compatibility (CP1254/UTF-8)
if sys.platform == "win32":
    try:
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        if hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

# Automatic .env loading
try:
    from dotenv import load_dotenv
    load_dotenv(override=False)
except ImportError:
    pass

# Fallback simple .env reader if python-dotenv is missing
_env_path = os.path.join(os.path.dirname(__file__), ".env")
if os.path.exists(_env_path):
    try:
        with open(_env_path, "r", encoding="utf-8") as _f:
            for _line in _f:
                _line = _line.strip()
                if _line and not _line.startswith("#") and "=" in _line:
                    _k, _v = _line.split("=", 1)
                    os.environ.setdefault(_k.strip(), _v.strip().strip('"').strip("'"))
    except Exception:
        pass

# Try importing official OpenAI client, fallback gracefully to native HTTP client
try:
    from openai import OpenAI  # type: ignore
    HAS_OPENAI_LIB = True
except ImportError:
    OpenAI = None
    HAS_OPENAI_LIB = False

from tools import (
    OPENAI_TOOLS,
    SUBMIT_ASSESSMENT_TOOL_NAME,
    execute_tool,
    tool,
    TOOL_REGISTRY,
    TOOL_MAP,
    get_all_tool_names,
)

# Setup Logger
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s"
)
logger = logging.getLogger("AgentLoop")

# Default Connection Configurations
DEFAULT_BASE_URL = "https://berriailitellm-databasev1826rc3-production-d691.up.railway.app/v1"
DEFAULT_MODEL = "glm-5.3-flash"
AGENT_MODEL_VERSION = f"{DEFAULT_MODEL}-agent-v1"

SYSTEM_PROMPT = (
    "Sen kıdemli bir askeri istihbarat ve taktik durum değerlendirme uzmanısın.\n"
    "Temel Görevin: Askeri üssü korumak. Sana verilen bir aracın (track_id) hareket geçmişi, "
    "üsse mesafe/hız/yaklaşma trendi ve o track'e ait aday saha raporları ZATEN HESAPLANMIŞ "
    "OLARAK verilir - bunlar sistemin ölçüm verisidir (ground truth); piksel/koordinat/mesafe "
    "hesaplamasını sen yapmazsın, sadece yorumlarsın.\n\n"
    "TEMEL ANALİZ VE TEHDİT DEĞERLENDİRME KURALLARI:\n"
    "1. Çelişki durumunda daima verilen tespit ve hareket verisini esas al; saha raporları "
    "yalnızca bağlamsal/destekleyici kanıttır, asla tespitin önüne geçmez.\n"
    "2. Aracın üsse mesafesini, hızını, durağanlık süresini ve üsse yaklaşma trendini "
    "(yaklaşıyor / uzaklaşıyor / sabit mesafede dönüyor) dikkatle incele.\n"
    "3. Sana verilen aday saha raporlarını değerlendir: resmi (official) kaynak genelde "
    "third_party'den daha güvenilir sayılır ama hatasız değildir - raporların hiçbiri "
    "otomatik doğru kabul edilmez, sen karşılaştırırsın. 'Kimlik teyidi yapılmıştır', 'bize "
    "bağlı unsurdur' gibi resmi teyitler şüpheyi düşürebilir; teyitsiz/çelişkili raporlar "
    "şüpheyi artırmaz, sadece not edilir.\n"
    "4. Verilen bilgi yetersizse veya başka bir unsur/bölge/rapor ile ilişki kurman gerekiyorsa "
    "sana tanımlanmış @tool araçlarını kullanabilirsin:\n"
    "   - get_base_location_tool: Askeri üssün adını ve koordinatlarını çeker.\n"
    "   - get_zones_tool: Taktiksel bölgelerin (WayPoint) listesini çeker.\n"
    "   - get_nearest_zone_tool: Herhangi bir koordinata en yakın bölgeyi ve üsse mesafesini döner.\n"
    "   - get_all_tracks_tool: Sistemdeki tüm araçların listesini ve özetlerini çeker.\n"
    "   - get_track_details_tool: Başka bir track'in zaman ve tespit detaylarını inceler (konvoy kontrolü vb.).\n"
    "   - get_track_points_tool: Bir track'in baştan sona tüm hareket rotasını çeker.\n"
    "   - get_movement_profile_tool: Başka bir track'in hız/durağanlık/yaklaşma telemetrisini inceler.\n"
    "   - compare_with_reports_tool: Başka bir track için detay + hareket + raporları toplu çeker.\n"
    "   - search_field_reports_tool: Bir track için daha geniş yarıçapta veya konumsuz raporları arar.\n"
    "   - search_field_reports_near_tool: Belirli bir koordinat etrafındaki saha raporlarını arar.\n"
    "   - get_all_field_reports_tool: Tüm saha raporlarının tam listesini döner.\n"
    "   Ama önce sana verilen mevcut veriyi değerlendir, gereksiz tekrarlı tool çağrısı yapma.\n"
    "5. Analizini bitirdiğinde MUTLAKA '" + SUBMIT_ASSESSMENT_TOOL_NAME + "' tool'unu çağırarak "
    "nihai kararını yapılandırılmış şekilde bildir (serbest metinle karar yazma, sadece bu "
    "tool ile bildir)."
)


# ==============================================================================
# NATIVE HTTP FALLBACK CLIENT (No 'openai' pip package required)
# ==============================================================================

class _NativeFunction:
    def __init__(self, name: str, arguments: str) -> None:
        self.name = name
        self.arguments = arguments


class _NativeToolCall:
    def __init__(self, tc_id: str, fn_name: str, fn_args: str) -> None:
        self.id = tc_id
        self.type = "function"
        self.function = _NativeFunction(fn_name, fn_args)


class _NativeMessage:
    def __init__(self, msg_dict: Dict[str, Any]) -> None:
        self.role = msg_dict.get("role", "assistant")
        self.content = msg_dict.get("content")
        self.reasoning_content = msg_dict.get("reasoning_content")
        self.tool_calls: Optional[List[_NativeToolCall]] = None

        raw_calls = msg_dict.get("tool_calls")
        if raw_calls:
            self.tool_calls = []
            for tc in raw_calls:
                fn = tc.get("function", {})
                args = fn.get("arguments", "{}")
                if isinstance(args, dict):
                    args = json.dumps(args, ensure_ascii=False)
                self.tool_calls.append(_NativeToolCall(
                    tc_id=tc.get("id", "call_default"),
                    fn_name=fn.get("name", ""),
                    fn_args=args,
                ))


class _NativeChoice:
    def __init__(self, choice_dict: Dict[str, Any]) -> None:
        self.message = _NativeMessage(choice_dict.get("message", {}))


class _NativeChatResponse:
    def __init__(self, resp_dict: Dict[str, Any]) -> None:
        self.choices = [_NativeChoice(c) for c in resp_dict.get("choices", [])]


class _NativeChatCompletions:
    def __init__(self, api_key: str, base_url: str, max_retries: int = 5) -> None:
        self.api_key = api_key
        self.base_url = base_url.rstrip("/")
        self.max_retries = max_retries

    def create(self, **kwargs: Any) -> _NativeChatResponse:
        endpoint = f"{self.base_url}/chat/completions"
        headers = {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {self.api_key}",
        }

        # Filter out incompatible kwargs if any
        payload = {k: v for k, v in kwargs.items() if v is not None}
        if "extra_body" in payload:
            extra = payload.pop("extra_body")
            if isinstance(extra, dict):
                payload.update(extra)

        req_data = json.dumps(payload, ensure_ascii=False).encode("utf-8")

        last_error = None
        for attempt in range(1, self.max_retries + 1):
            try:
                req = urllib.request.Request(endpoint, data=req_data, headers=headers, method="POST")
                with urllib.request.urlopen(req, timeout=120) as response:
                    body = response.read().decode("utf-8")
                    data = json.loads(body)
                    return _NativeChatResponse(data)
            except urllib.error.HTTPError as e:
                err_body = e.read().decode("utf-8", errors="replace")
                logger.warning(f"HTTPError on attempt {attempt}/{self.max_retries} (Code {e.code}): {err_body}")
                last_error = RuntimeError(f"HTTP {e.code}: {err_body}")
            except Exception as e:
                logger.warning(f"Connection error on attempt {attempt}/{self.max_retries}: {e}")
                last_error = e

            if attempt < self.max_retries:
                time.sleep(1.5 * attempt)

        raise last_error or RuntimeError("Max retries exceeded")


class NativeOpenAIClient:
    """Zero-dependency HTTP client mimicking the OpenAI SDK interface."""
    def __init__(self, api_key: str, base_url: str, max_retries: int = 5) -> None:
        class Chat:
            pass
        self.chat = Chat()
        self.chat.completions = _NativeChatCompletions(api_key, base_url, max_retries)


# ==============================================================================
# MAIN AGENT CLASS
# ==============================================================================

class DroneIntelligenceAgent:
    """
    Autonomous intelligence agent utilizing GLM-5.3-Flash with multi-turn tool calling.
    """

    def __init__(
        self,
        api_key: Optional[str] = None,
        base_url: str = DEFAULT_BASE_URL,
        model: str = DEFAULT_MODEL,
        max_retries: int = 5,
    ) -> None:
        """
        Initializes the OpenAI or Native client and agent configuration.
        """
        self.api_key = (
            api_key
            or os.environ.get("OPENAI_API_KEY")
            or os.environ.get("ZAI_API_KEY")
            or "sk-dummy-key"
        )
        self.base_url = base_url
        self.model = model
        self.max_retries = max_retries

        if HAS_OPENAI_LIB and OpenAI is not None:
            logger.info(f"Using official 'openai' library with Base URL: {self.base_url}, Model: {self.model}")
            self.client = OpenAI(
                api_key=self.api_key,
                base_url=self.base_url,
                max_retries=self.max_retries,
            )
        else:
            logger.info(
                f"'openai' library not installed. Using native standard library HTTP client. "
                f"Base URL: {self.base_url}, Model: {self.model}"
            )
            self.client = NativeOpenAIClient(
                api_key=self.api_key,
                base_url=self.base_url,
                max_retries=self.max_retries,
            )

    def _call_llm_with_reasoning(
        self,
        messages: List[Dict[str, Any]],
        tools: Optional[List[Dict[str, Any]]] = None,
        tool_choice: Optional[Union[str, Dict[str, Any]]] = None,
        max_tokens: int = 2000,
    ) -> Any:
        """
        Calls the chat completion endpoint supporting reasoning_effort="low" safely.
        """
        kwargs: Dict[str, Any] = {
            "model": self.model,
            "messages": messages,
            "max_tokens": max_tokens,
        }
        if tools:
            kwargs["tools"] = tools
            kwargs["tool_choice"] = tool_choice or "auto"

        # Try passing reasoning_effort directly; if SDK version doesn't support it, pass via extra_body
        try:
            return self.client.chat.completions.create(
                reasoning_effort="low",
                **kwargs
            )
        except TypeError:
            return self.client.chat.completions.create(
                extra_body={"reasoning_effort": "low"},
                **kwargs
            )

    def ask_and_print(self, prompt: str, system_prompt: Optional[str] = None) -> str:
        """
        Sends a single prompt directly to the LLM, prints the thinking process and
        the returned response to the console, and returns the response string.
        """
        messages = [
            {"role": "system", "content": system_prompt or SYSTEM_PROMPT},
            {"role": "user", "content": prompt},
        ]
        print(f"\n" + "=" * 80)
        print(f"[LLM ISTEMI]:\n{prompt}")
        print("=" * 80)
        print("[BILGI] LLM yaniti bekleniyor...")

        try:
            response = self._call_llm_with_reasoning(messages=messages, max_tokens=2000)
            choice = response.choices[0]
            message = choice.message

            reasoning = getattr(message, "reasoning_content", None)
            if not reasoning and hasattr(message, "model_extra") and message.model_extra:
                reasoning = message.model_extra.get("reasoning_content")
            if reasoning:
                print("\n[MODEL DUSUNCE SURECI (REASONING)]:")
                print("-" * 60)
                print(reasoning.strip())
                print("-" * 60)

            content = message.content or ""
            print("\n" + "=" * 80)
            print("[LLM'DEN DONEN SONUC (KONSOL CIKTISI)]:")
            print("=" * 80)
            print(content)
            print("=" * 80 + "\n")
            return content

        except Exception as e:
            err_msg = f"[HATA] LLM sorgusu basarisiz oldu: {e}"
            print(f"\n{err_msg}\n")
            return err_msg

    @staticmethod
    def _build_track_prompt(track_context: Dict[str, Any]) -> str:
        """Formats the already-enriched agent.assessment.requested payload into a Turkish
        user prompt. No geo/track math happens here - it's all already computed upstream."""
        track_id = track_context.get("trackId", "?")
        reason = track_context.get("requestReason", "?")
        details = track_context.get("trackDetails", {}) or {}
        movement = track_context.get("movementProfile", {}) or {}
        reports = track_context.get("candidateFieldReports", []) or []

        matched = details.get("matchedDetections", []) or []
        matched_lines = "\n".join(
            f"  - Görüntü {d.get('imageId')} ({d.get('captureTime')}): sınıf={d.get('className')}, "
            f"güven={d.get('confidence')}, konum=({d.get('lat')}, {d.get('lon')}), "
            f"track eşleşme mesafesi={d.get('distanceMeters')}m"
            for d in matched
        ) or "  (bu track için eşleşen görüntü/tespit yok)"

        report_lines = "\n".join(
            f"  - [{r.get('time')}] ({r.get('source')}) \"{r.get('rawText')}\" "
            f"[çözümleme: {r.get('resolutionConfidence')}]"
            for r in reports
        ) or "  (bu track'in zaman/konum penceresine düşen aday saha raporu yok)"

        return (
            f"Aşağıdaki araç (track_id: {track_id}) için kapsamlı bir askeri istihbarat ve "
            f"taktik durum değerlendirmesi yap. Bu değerlendirme isteği şu sebeple tetiklendi: "
            f"{reason}.\n\n"
            f"--- TRACK DETAYI (backend'den, zaten hesaplanmış) ---\n"
            f"İlk görülme: {details.get('firstSeen')} | Son görülme: {details.get('lastSeen')} | "
            f"Kayıtlı nokta sayısı: {details.get('trackPointCount')}\n"
            f"Gözlenen sınıflar: {details.get('observedClassNames')}\n"
            f"Eşleşen görüntü/tespitler:\n{matched_lines}\n\n"
            f"--- HAREKET PROFİLİ (backend'den, zaten hesaplanmış) ---\n"
            f"Durum: {movement.get('status')} (bu durumda {movement.get('statusDurationMinutes')} dakikadır)\n"
            f"Anlık hız: {movement.get('currentSpeedMetersPerSecond')} m/s | "
            f"Ortalama hız: {movement.get('averageSpeedMetersPerSecond')} m/s\n"
            f"Üsse mesafe (şu an): {movement.get('distanceToBaseKmNow')} km | "
            f"Üsse mesafe (pencere başında): {movement.get('distanceToBaseKmAtWindowStart')} km\n"
            f"Orbit skoru (düşük = üs etrafında sabit yarıçapta dönme paterni): {movement.get('orbitScore')}\n\n"
            f"--- ADAY SAHA RAPORLARI (backend'den, zaman/konum penceresine göre önceden filtrelenmiş,"
            f" DOĞRULUĞU DEĞERLENDİRİLMEMİŞ) ---\n{report_lines}\n\n"
            f"Görev: Yukarıdaki veriyi sentezleyerek bu aracın üs için TEHDİT mi (unsafe), ŞÜPHELİ mi (suspicious), "
            f"yoksa GÜVENLİ/DOST mu (safe) olduğuna karar ver. Gerekirse (ör. konvoy şüphesi, başka "
            f"track ile ilişki, daha geniş rapor araması, üs konumu sorgulama) tanımlanmış @tool araçlarını kullanabilirsin, "
            f"ama önce yukarıdaki veriyi kullan. Analizini bitirdiğinde MUTLAKA "
            f"'{SUBMIT_ASSESSMENT_TOOL_NAME}' tool'unu çağırarak nihai kararını bildir."
        )

    def run_agent_for_track(
        self,
        track_context: Dict[str, Any],
        max_turns: int = 6,
    ) -> Dict[str, Any]:
        """
        Runs the full reasoning and tool-calling agent loop for one track's already-enriched
        context (the deserialized agent.assessment.requested Kafka message).

        Returns a dict shaped exactly like the backend's AssessmentCompleted contract:
        {trackId, verdict, confidence, reasoning, evidenceRefs, modelVersion, createdAtUtc}
        so main.py can publish it to assessments.completed without any translation.
        """
        track_id = track_context.get("trackId", "unknown")
        user_content = self._build_track_prompt(track_context)

        messages: List[Dict[str, Any]] = [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": user_content},
        ]

        print("\n" + "=" * 80)
        print(f"[AGENT LOOP BASLATILDI] Track: {track_id}")
        print("=" * 80)

        submitted: Optional[Dict[str, Any]] = None
        turn = 0

        while turn < max_turns and submitted is None:
            turn += 1
            print(f"\n--- [TUR {turn} / {max_turns}] Model Sorgulanıyor... ---")

            # On the very last allowed turn, force the model to submit instead of calling
            # more tools - this is the "loop protection" the task brief warns about.
            force_submit = turn == max_turns
            tool_choice = (
                {"type": "function", "function": {"name": SUBMIT_ASSESSMENT_TOOL_NAME}}
                if force_submit else "auto"
            )

            try:
                response = self._call_llm_with_reasoning(
                    messages=messages, tools=OPENAI_TOOLS, tool_choice=tool_choice, max_tokens=2000,
                )
            except Exception as e:
                logger.error(f"LLM API çağrısı sırasında hata oluştu: {e}")
                return self._error_result(track_id, f"LLM çağrısı başarısız: {e}")

            choice = response.choices[0]
            message = choice.message

            reasoning = getattr(message, "reasoning_content", None)
            if not reasoning and hasattr(message, "model_extra") and message.model_extra:
                reasoning = message.model_extra.get("reasoning_content")
            if reasoning:
                print("\n[MODEL DUSUNCE SURECI (REASONING)]:")
                print("-" * 60)
                print(reasoning.strip())
                print("-" * 60)

            if message.content and message.content.strip():
                print("\n[LLM ARA YANITI / MESAJI]:")
                print("-" * 60)
                print(message.content.strip())
                print("-" * 60)

            tool_calls = getattr(message, "tool_calls", None)
            if not tool_calls:
                # Model answered in free text without calling submit - nudge it once more
                # instead of accepting an unstructured "final answer".
                messages.append({"role": "assistant", "content": message.content or ""})
                messages.append({
                    "role": "user",
                    "content": f"Lütfen kararını serbest metin yerine '{SUBMIT_ASSESSMENT_TOOL_NAME}' tool'u ile bildir.",
                })
                continue

            assistant_msg: Dict[str, Any] = {
                "role": "assistant",
                "content": message.content or "",
                "tool_calls": [
                    {
                        "id": tc.id,
                        "type": "function",
                        "function": {"name": tc.function.name, "arguments": tc.function.arguments},
                    }
                    for tc in tool_calls
                ],
            }
            messages.append(assistant_msg)

            for tc in tool_calls:
                fn_name = tc.function.name
                fn_args_raw = tc.function.arguments
                print(f"\n[TOOL CAGRISI]: {fn_name}")
                print(f"    Argümanlar: {fn_args_raw}")

                try:
                    tool_output = execute_tool(fn_name, fn_args_raw)
                except Exception as ex:
                    tool_output = json.dumps({"status": "error", "message": str(ex)}, ensure_ascii=False)

                preview = tool_output[:180] + "..." if len(tool_output) > 180 else tool_output
                print(f"    Çıktı Özeti: {preview}")

                messages.append({
                    "role": "tool",
                    "tool_call_id": tc.id,
                    "name": fn_name,
                    "content": tool_output,
                })

                if fn_name == SUBMIT_ASSESSMENT_TOOL_NAME:
                    try:
                        args = json.loads(fn_args_raw)
                        parsed = json.loads(tool_output)
                        if parsed.get("status") == "success":
                            submitted = {
                                "trackId": track_id,
                                "verdict": args["verdict"],
                                "confidence": float(args.get("confidence", 0.5)),
                                "reasoning": args.get("reasoning", ""),
                                "evidenceRefs": args.get("evidence_refs", []) or [],
                            }
                    except Exception as ex:
                        logger.error(f"submit_final_assessment_tool argümanları ayrıştırılamadı: {ex}")

        if submitted is None:
            logger.warning(f"Track {track_id}: {max_turns} tur içinde nihai karar alınamadı.")
            submitted = self._error_result(
                track_id, f"{max_turns} tur içinde '{SUBMIT_ASSESSMENT_TOOL_NAME}' çağrılmadı.",
            )

        import datetime
        result = {
            **submitted,
            "modelVersion": AGENT_MODEL_VERSION,
            "createdAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        }

        print("\n" + "#" * 80)
        print(f"🚨 [NIHAI KARAR]: {result['verdict'].upper()} (güven: {result['confidence']})")
        print(f"   [GEREKÇE]: {result['reasoning']}")
        print("#" * 80 + "\n")

        return result

    @staticmethod
    def _error_result(track_id: str, message: str) -> Dict[str, Any]:
        return {
            "trackId": track_id,
            "verdict": "suspicious",
            "confidence": 0.0,
            "reasoning": f"[HATA] {message}",
            "evidenceRefs": [],
        }


# Global agent instance for direct functional usage
_global_agent: Optional[DroneIntelligenceAgent] = None


def _get_global_agent(api_key: Optional[str] = None, base_url: str = DEFAULT_BASE_URL,
                       model: str = DEFAULT_MODEL) -> DroneIntelligenceAgent:
    global _global_agent
    if _global_agent is None or api_key is not None or base_url != DEFAULT_BASE_URL:
        _global_agent = DroneIntelligenceAgent(api_key=api_key, base_url=base_url, model=model)
    return _global_agent


def run_agent_for_track(
    track_context: Dict[str, Any],
    api_key: Optional[str] = None,
    base_url: str = DEFAULT_BASE_URL,
    model: str = DEFAULT_MODEL,
    max_turns: int = 6,
) -> Dict[str, Any]:
    """Main entry point: analyze one track's enriched context (an agent.assessment.requested
    message) and return an AssessmentCompleted-shaped dict."""
    agent = _get_global_agent(api_key=api_key, base_url=base_url, model=model)
    return agent.run_agent_for_track(track_context, max_turns=max_turns)


def ask_llm(
    prompt: str,
    api_key: Optional[str] = None,
    base_url: str = DEFAULT_BASE_URL,
    model: str = DEFAULT_MODEL,
    system_prompt: Optional[str] = None,
) -> str:
    """Doğrudan LLM'e bir soru/istem gönderir, dönen sonucu konsola yazdırır ve metin olarak döndürür."""
    agent = DroneIntelligenceAgent(api_key=api_key, base_url=base_url, model=model)
    return agent.ask_and_print(prompt, system_prompt=system_prompt)


# ==============================================================================
# Standalone Execution and Verification Block
# ==============================================================================
if __name__ == "__main__":
    import requests

    print("=" * 80)
    print("Z.AI GLM-5.3-FLASH DRONE INTELLIGENCE AGENT - TRACK BAZLI CANLI TEST")
    print("=" * 80)

    api_key = os.environ.get("OPENAI_API_KEY") or os.environ.get("ZAI_API_KEY")
    backend_url = os.environ.get("BACKEND_BASE_URL", "http://localhost:5299")

    for arg in sys.argv[1:]:
        if arg.startswith("sk-") or len(arg) > 25:
            api_key = arg
            break

    track_id = next((a for a in sys.argv[1:] if a.upper().startswith("T") and a[1:].isdigit()), "T0001")

    print(f"Backend       : {backend_url}")
    print(f"Track         : {track_id}")
    print(f"Model         : {DEFAULT_MODEL}")
    print(f"Endpoint      : {DEFAULT_BASE_URL}\n")

    if not api_key or api_key.startswith("sk-dummy"):
        print("[UYARI] Geçerli bir API Key bulunamadı (OPENAI_API_KEY / ZAI_API_KEY veya argüman).")
        print("Kullanım: python agent_loop.py <track_id> [sk-...]")
        sys.exit(1)

    print(f"'{backend_url}/api/tracks/{track_id}/compare-reports' üzerinden bağlam çekiliyor...")
    try:
        resp = requests.get(f"{backend_url}/api/tracks/{track_id}/compare-reports", timeout=10)
        resp.raise_for_status()
        compare = resp.json()
    except Exception as ex:
        print(f"[HATA] Backend'den track bağlamı alınamadı: {ex}")
        sys.exit(1)

    track_context = {
        "trackId": track_id,
        "requestedAtUtc": None,
        "requestReason": "manual-cli-test",
        "trackDetails": compare["trackDetails"],
        "movementProfile": compare["movementProfile"],
        "candidateFieldReports": compare["candidateFieldReports"],
    }

    agent = DroneIntelligenceAgent(api_key=api_key)
    result = agent.run_agent_for_track(track_context)

    print("\n" + "#" * 80)
    print("[NIHAI SONUC - JSON]")
    print("#" * 80)
    print(json.dumps(result, indent=2, ensure_ascii=False))
    print("#" * 80 + "\n")
