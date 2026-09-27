using Backend.Api.Tools;

namespace Backend.Api.Messaging;

/// <summary>
/// POSTed by the backend to the LLM agent service's /assess endpoint (see AssessmentClient),
/// keyed by track_id. Carries everything the agent's tool-calling loop would otherwise have to
/// fetch itself - the agent can also re-fetch/refresh any of this live via the Tool API
/// (plan.md section 5) mid-reasoning, but starts from a consistent snapshot instead of a bare
/// track_id.
/// </summary>
public sealed record AgentAssessmentRequested(
    string TrackId,
    DateTimeOffset RequestedAtUtc,
    string RequestReason, // e.g. "ingestion:img_003839", "manual-replay"
    TrackDetailsResponse TrackDetails,
    MovementProfileResponse MovementProfile,
    IReadOnlyList<FieldReportMatchDto> CandidateFieldReports);

/// <summary>
/// POSTed back by the LLM agent service to POST /api/assessments once its tool-calling loop
/// calls submit_final_assessment_tool. The backend only consumes and persists this - it does not
/// judge or second-guess the verdict.
/// </summary>
public sealed record AssessmentCompleted(
    string TrackId,
    string Verdict, // "safe" | "suspicious" | "unsafe"
    double Confidence,
    string Reasoning,
    IReadOnlyList<string> EvidenceRefs,
    string ModelVersion,
    DateTimeOffset CreatedAtUtc);
