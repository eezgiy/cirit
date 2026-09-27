namespace Backend.Api.Tools;

// These response shapes are the backend's "Tool API" contracts (plan.md section 5): the future
// LLM agent service calls these synchronously as tool calls during its reasoning loop. For now,
// with no agent wired up yet, they're plain REST endpoints anyone (curl, frontend, tests) can hit.

public sealed record MatchedDetectionDto(
    string ImageId, TimeOnly CaptureTime, int ClassId, string? ClassName, double? Confidence,
    double Lat, double Lon, double? DistanceMeters);

public sealed record TrackDetailsResponse(
    string TrackId, TimeOnly FirstSeen, TimeOnly LastSeen, int TrackPointCount,
    IReadOnlyList<string> ObservedClassNames, IReadOnlyList<MatchedDetectionDto> MatchedDetections);

public sealed record MovementProfileResponse(
    string TrackId, TimeOnly FirstSeen, TimeOnly LastSeen,
    double LatestLat, double LatestLon,
    string Status, double StatusDurationMinutes,
    double CurrentSpeedMetersPerSecond, double AverageSpeedMetersPerSecond,
    double DistanceToBaseKmNow, double DistanceToBaseKmAtWindowStart, double OrbitScore);

public sealed record NearestZoneResponse(string ZoneName, double DistanceMeters, double DistanceToBaseKm);

public sealed record FieldReportMatchDto(
    int ReportId, TimeOnly Time, string Source, string RawText,
    double? ResolvedLat, double? ResolvedLon, string? ResolvedZoneName, string ResolutionConfidence);

public sealed record CompareWithReportsResponse(
    TrackDetailsResponse TrackDetails, MovementProfileResponse MovementProfile,
    IReadOnlyList<FieldReportMatchDto> CandidateFieldReports);

public sealed record AssessmentResponse(
    string TrackId, string Verdict, double Confidence, string Reasoning,
    IReadOnlyList<string> EvidenceRefs, string ModelVersion, DateTimeOffset CreatedAtUtc);

public sealed record BaseLocationResponse(string Name, double Lat, double Lon);
public sealed record ZoneResponse(string Name, double Lat, double Lon);
public sealed record TrackPointDto(TimeOnly Time, double Lat, double Lon);
public sealed record TrackSummaryResponse(string TrackId, int PointCount, TimeOnly FirstSeen, TimeOnly LastSeen);

