using Backend.Api.Data;
using Backend.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Tools;

/// <summary>
/// Read-only queries backing the backend's Tool API (plan.md section 5). Everything here is
/// deterministic lookups/derivations over already-enriched data - no judgement calls, no LLM.
/// </summary>
public sealed class TrackContextService(AppDbContext db)
{
    private const double DefaultReportRadiusMeters = 800d;
    private const double DefaultReportTimePaddingMinutes = 15d;

    public async Task<GeoPoint> GetBasePositionAsync(CancellationToken ct = default)
    {
        var b = await db.BaseLocations.FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Base location has not been seeded.");
        return new GeoPoint(b.Lat, b.Lon);
    }

    public async Task<BaseLocationResponse> GetBaseLocationDetailsAsync(CancellationToken ct = default)
    {
        var b = await db.BaseLocations.FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Base location has not been seeded.");
        return new BaseLocationResponse(b.Name, b.Lat, b.Lon);
    }

    public async Task<IReadOnlyList<Zone>> GetZonesAsync(CancellationToken ct = default)
        => await db.Zones.Select(z => new Zone(z.Name, new GeoPoint(z.Lat, z.Lon))).ToListAsync(ct);

    public async Task<IReadOnlyList<ZoneResponse>> GetZoneResponsesAsync(CancellationToken ct = default)
        => await db.Zones.Select(z => new ZoneResponse(z.Name, z.Lat, z.Lon)).ToListAsync(ct);

    public async Task<IReadOnlyList<TrackPointDto>> GetTrackTrajectoryAsync(string trackId, CancellationToken ct = default)
    {
        var points = await GetTrackPointsAsync(trackId, ct);
        return points.Select(p => new TrackPointDto(p.Time, p.Lat, p.Lon)).ToList();
    }

    public async Task<IReadOnlyList<TrackSummaryResponse>> GetAllTracksAsync(CancellationToken ct = default)
    {
        // SQLite's EF Core provider can't translate Min/Max over TimeOnly inside a GroupBy
        // projection ("The LINQ expression ... could not be translated"), so only the two
        // lightweight columns needed for the aggregation are pulled into memory - grouping and
        // aggregating a few thousand (TrackId, Time) rows client-side is trivial.
        var points = await db.TrackPoints
            .Select(t => new { t.TrackId, t.Time })
            .ToListAsync(ct);

        return points
            .GroupBy(p => p.TrackId)
            .Select(g => new TrackSummaryResponse(
                g.Key,
                g.Count(),
                g.Min(p => p.Time),
                g.Max(p => p.Time)
            ))
            .OrderBy(t => t.TrackId)
            .ToList();
    }

    public async Task<IReadOnlyList<FieldReportMatchDto>> GetAllFieldReportsAsync(CancellationToken ct = default)
    {
        var reports = await db.FieldReports
            .OrderBy(r => r.Time)
            .Select(r => new FieldReportMatchDto(
                r.Id, r.Time, r.Source, r.RawText,
                r.ResolvedLat, r.ResolvedLon, r.ResolvedZoneName, r.ResolutionConfidence))
            .ToListAsync(ct);
        return reports;
    }

    private async Task<List<TrackPointEntity>> GetTrackPointsAsync(string trackId, CancellationToken ct)
    {
        var points = await db.TrackPoints.Where(t => t.TrackId == trackId).OrderBy(t => t.Time).ToListAsync(ct);
        if (points.Count == 0)
        {
            throw new KeyNotFoundException($"Unknown track_id '{trackId}'.");
        }

        return points;
    }

    public async Task<TrackDetailsResponse> GetTrackDetailsAsync(string trackId, CancellationToken ct = default)
    {
        var points = await GetTrackPointsAsync(trackId, ct);

        var matches = await db.DetectionTrackMatches
            .Where(m => m.TrackId == trackId)
            .Include(m => m.Detection!).ThenInclude(d => d.Image)
            .ToListAsync(ct);

        var matchedDetections = matches
            .Select(m => m.Detection!)
            .OrderBy(d => d.Image!.CaptureTime)
            .Select(d => new MatchedDetectionDto(
                d.ImageId, d.Image!.CaptureTime, d.ClassId, d.ClassName ?? $"class_{d.ClassId}", d.Confidence,
                d.Lat, d.Lon, d.TrackMatch?.DistanceMeters))
            .ToList();

        var observedClassNames = matchedDetections.Select(d => d.ClassName!).Distinct().ToList();

        return new TrackDetailsResponse(trackId, points[0].Time, points[^1].Time, points.Count,
            observedClassNames, matchedDetections);
    }

    public async Task<MovementProfileResponse> GetMovementProfileAsync(string trackId, CancellationToken ct = default)
    {
        var points = await GetTrackPointsAsync(trackId, ct);
        var basePosition = await GetBasePositionAsync(ct);

        var samples = points.Select(p => new TrackPointSample(p.Time, new GeoPoint(p.Lat, p.Lon))).ToList();
        var profile = MovementAnalyzer.Analyze(trackId, samples, basePosition);

        return new MovementProfileResponse(
            profile.TrackId, profile.FirstSeen, profile.LastSeen,
            profile.LatestPosition.Lat, profile.LatestPosition.Lon,
            profile.Status.ToString(), profile.StatusDurationMinutes,
            profile.CurrentSpeedMetersPerSecond, profile.AverageSpeedMetersPerSecond,
            profile.DistanceToBaseKmNow, profile.DistanceToBaseKmAtWindowStart, profile.OrbitScore);
    }

    public async Task<NearestZoneResponse> FindNearestZoneAsync(double lat, double lon, CancellationToken ct = default)
    {
        var zones = await GetZonesAsync(ct);
        var basePosition = await GetBasePositionAsync(ct);
        var result = ZoneResolver.FindNearest(new GeoPoint(lat, lon), zones, basePosition);
        return new NearestZoneResponse(result.ZoneName, result.DistanceMeters, result.DistanceToBaseKm);
    }

    public async Task<IReadOnlyList<FieldReportMatchDto>> SearchFieldReportsForTrackAsync(string trackId,
        double radiusMeters = DefaultReportRadiusMeters, double timePaddingMinutes = DefaultReportTimePaddingMinutes,
        bool includeUnresolved = false, CancellationToken ct = default)
    {
        var points = await GetTrackPointsAsync(trackId, ct);
        var zones = await GetZonesAsync(ct);
        var basePosition = await GetBasePositionAsync(ct);

        var trackPositions = points.Select(p => new GeoPoint(p.Lat, p.Lon)).ToList();
        var nearestZoneNames = trackPositions
            .Select(p => ZoneResolver.FindNearest(p, zones, basePosition).ZoneName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var windowStart = points[0].Time.Add(TimeSpan.FromMinutes(-timePaddingMinutes));
        var windowEnd = points[^1].Time.Add(TimeSpan.FromMinutes(timePaddingMinutes));

        return await SearchAsync(windowStart, windowEnd, trackPositions, radiusMeters, nearestZoneNames, includeUnresolved, ct);
    }

    public async Task<IReadOnlyList<FieldReportMatchDto>> SearchFieldReportsNearAsync(double lat, double lon,
        double radiusMeters = DefaultReportRadiusMeters, TimeOnly? fromTime = null, TimeOnly? toTime = null,
        bool includeUnresolved = false, CancellationToken ct = default)
    {
        var zones = await GetZonesAsync(ct);
        var basePosition = await GetBasePositionAsync(ct);
        var point = new GeoPoint(lat, lon);
        var nearestZoneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ZoneResolver.FindNearest(point, zones, basePosition).ZoneName,
        };

        return await SearchAsync(fromTime ?? TimeOnly.MinValue, toTime ?? TimeOnly.MaxValue,
            [point], radiusMeters, nearestZoneNames, includeUnresolved, ct);
    }

    private async Task<IReadOnlyList<FieldReportMatchDto>> SearchAsync(TimeOnly windowStart, TimeOnly windowEnd,
        IReadOnlyList<GeoPoint> candidatePositions, double radiusMeters, HashSet<string> nearestZoneNames,
        bool includeUnresolved, CancellationToken ct)
    {
        var inWindow = windowStart <= windowEnd
            ? await db.FieldReports.Where(r => r.Time >= windowStart && r.Time <= windowEnd).ToListAsync(ct)
            : await db.FieldReports.Where(r => r.Time >= windowStart || r.Time <= windowEnd).ToListAsync(ct);

        var matched = new List<FieldReportMatchDto>();
        foreach (var report in inWindow)
        {
            var isMatch = report.ResolutionConfidence switch
            {
                nameof(LocationResolutionConfidence.Coordinate) => candidatePositions.Any(p =>
                    GeoMath.HaversineMeters(p, new GeoPoint(report.ResolvedLat!.Value, report.ResolvedLon!.Value)) <= radiusMeters),
                nameof(LocationResolutionConfidence.ZoneName) => report.ResolvedZoneName is not null
                    && nearestZoneNames.Contains(report.ResolvedZoneName),
                _ => includeUnresolved,
            };

            if (isMatch)
            {
                matched.Add(new FieldReportMatchDto(report.Id, report.Time, report.Source, report.RawText,
                    report.ResolvedLat, report.ResolvedLon, report.ResolvedZoneName, report.ResolutionConfidence));
            }
        }

        return matched.OrderBy(m => m.Time).ToList();
    }

    public async Task<CompareWithReportsResponse> CompareWithReportsAsync(string trackId, CancellationToken ct = default)
    {
        var details = await GetTrackDetailsAsync(trackId, ct);
        var movement = await GetMovementProfileAsync(trackId, ct);
        var reports = await SearchFieldReportsForTrackAsync(trackId, ct: ct);
        return new CompareWithReportsResponse(details, movement, reports);
    }

    public async Task<AssessmentResponse> GetLatestAssessmentAsync(string trackId, CancellationToken ct = default)
    {
        var assessment = await db.TrackAssessments.FirstOrDefaultAsync(a => a.TrackId == trackId, ct)
            ?? throw new KeyNotFoundException($"No assessment has been recorded yet for track_id '{trackId}'.");

        var evidenceRefs = System.Text.Json.JsonSerializer.Deserialize<List<string>>(assessment.EvidenceRefsJson) ?? [];
        return new AssessmentResponse(assessment.TrackId, assessment.Verdict, assessment.Confidence,
            assessment.Reasoning, evidenceRefs, assessment.ModelVersion, assessment.CreatedAtUtc);
    }
}
