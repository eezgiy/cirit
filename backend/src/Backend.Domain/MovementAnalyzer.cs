namespace Backend.Domain;

/// <summary>
/// Deterministic feature engineering over a track's raw point history. The LLM agent reasons
/// over these numbers; it never has to eyeball a raw coordinate list itself.
/// </summary>
public static class MovementAnalyzer
{
    private const double StationaryMovementThresholdMeters = 20d;
    private const double StationaryMinDurationMinutes = 20d;
    private const double ApproachRecedeToleranceKm = 0.2d;
    private const double OrbitCoefficientOfVariationThreshold = 0.15d;
    private const double OrbitMinCumulativeHeadingChangeDegrees = 240d;
    private const double HeadingNoiseFloorMeters = 5d;

    public static MovementProfile Analyze(string trackId, IReadOnlyList<TrackPointSample> samples, GeoPoint basePosition)
    {
        if (samples.Count == 0) throw new ArgumentException("At least one sample is required.", nameof(samples));

        var ordered = samples.OrderBy(s => s.Time).ToList();
        var core = AnalyzeCore(ordered, basePosition);
        var segmentSpeeds = ComputeSegmentSpeeds(ordered);
        var currentSpeed = segmentSpeeds.Count > 0 ? segmentSpeeds[^1] : 0d;
        var averageSpeed = segmentSpeeds.Count > 0 ? segmentSpeeds.Average() : 0d;
        var statusDuration = ComputeStatusDurationMinutes(ordered, core.DistancesToBaseKm, core.Status, core.StationaryMinutes);

        return new MovementProfile(
            trackId,
            ordered[0].Time,
            ordered[^1].Time,
            ordered[^1].Position,
            core.Status,
            statusDuration,
            currentSpeed,
            averageSpeed,
            core.DistancesToBaseKm[^1],
            core.DistancesToBaseKm[0],
            core.OrbitCoefficientOfVariation);
    }

    private sealed record CoreResult(MovementStatus Status, IReadOnlyList<double> DistancesToBaseKm,
        double StationaryMinutes, double OrbitCoefficientOfVariation, double CumulativeHeadingChangeDegrees);

    private static CoreResult AnalyzeCore(IReadOnlyList<TrackPointSample> ordered, GeoPoint basePosition)
    {
        var distancesToBaseKm = ordered.Select(s => GeoMath.HaversineMeters(s.Position, basePosition) / 1000d).ToList();
        var stationaryMinutes = ComputeTrailingStationaryMinutes(ordered);
        var orbitCv = ComputeOrbitCoefficientOfVariation(distancesToBaseKm);
        var headingChange = ComputeCumulativeHeadingChangeDegrees(ordered);

        MovementStatus status;
        if (stationaryMinutes >= StationaryMinDurationMinutes)
        {
            status = MovementStatus.Stationary;
        }
        else if (ordered.Count >= 4 && orbitCv < OrbitCoefficientOfVariationThreshold
                 && headingChange >= OrbitMinCumulativeHeadingChangeDegrees)
        {
            status = MovementStatus.Orbiting;
        }
        else
        {
            var diffKm = distancesToBaseKm[^1] - distancesToBaseKm[0];
            status = diffKm <= -ApproachRecedeToleranceKm ? MovementStatus.Approaching
                : diffKm >= ApproachRecedeToleranceKm ? MovementStatus.Receding
                : MovementStatus.Moving;
        }

        return new CoreResult(status, distancesToBaseKm, stationaryMinutes, orbitCv, headingChange);
    }

    /// <summary>
    /// How long (minutes) the current status has held. Stationary reuses the trailing-stillness
    /// measurement; approaching/receding walk backward while the distance-to-base keeps trending
    /// the same direction (within a small noise floor); other statuses report the full window.
    /// </summary>
    private static double ComputeStatusDurationMinutes(IReadOnlyList<TrackPointSample> ordered,
        IReadOnlyList<double> distancesToBaseKm, MovementStatus status, double stationaryMinutes)
    {
        if (status == MovementStatus.Stationary)
        {
            return stationaryMinutes;
        }

        if (status is MovementStatus.Approaching or MovementStatus.Receding)
        {
            return ComputeTrailingMonotonicMinutes(ordered, distancesToBaseKm, approaching: status == MovementStatus.Approaching);
        }

        return (ordered[^1].Time - ordered[0].Time).TotalMinutes;
    }

    private static double ComputeTrailingMonotonicMinutes(IReadOnlyList<TrackPointSample> ordered,
        IReadOnlyList<double> distancesToBaseKm, bool approaching)
    {
        const double noiseFloorKm = 0.02;
        double minutes = 0;
        for (var i = ordered.Count - 1; i > 0; i--)
        {
            var delta = distancesToBaseKm[i] - distancesToBaseKm[i - 1];
            var consistentWithTrend = approaching ? delta <= noiseFloorKm : delta >= -noiseFloorKm;
            if (!consistentWithTrend) break;
            minutes += (ordered[i].Time - ordered[i - 1].Time).TotalMinutes;
        }

        return minutes;
    }

    private static List<double> ComputeSegmentSpeeds(IReadOnlyList<TrackPointSample> ordered)
    {
        var speeds = new List<double>(ordered.Count - 1);
        for (var i = 1; i < ordered.Count; i++)
        {
            var seconds = (ordered[i].Time - ordered[i - 1].Time).TotalSeconds;
            if (seconds <= 0) continue;
            var meters = GeoMath.HaversineMeters(ordered[i - 1].Position, ordered[i].Position);
            speeds.Add(meters / seconds);
        }

        return speeds;
    }

    private static double ComputeTrailingStationaryMinutes(IReadOnlyList<TrackPointSample> ordered)
    {
        double minutes = 0;
        for (var i = ordered.Count - 1; i > 0; i--)
        {
            var meters = GeoMath.HaversineMeters(ordered[i - 1].Position, ordered[i].Position);
            if (meters >= StationaryMovementThresholdMeters) break;
            minutes += (ordered[i].Time - ordered[i - 1].Time).TotalMinutes;
        }

        return minutes;
    }

    private static double ComputeOrbitCoefficientOfVariation(IReadOnlyList<double> distancesToBaseKm)
    {
        var mean = distancesToBaseKm.Average();
        if (mean <= 0) return 0;
        var variance = distancesToBaseKm.Average(d => (d - mean) * (d - mean));
        return Math.Sqrt(variance) / mean;
    }

    private static double ComputeCumulativeHeadingChangeDegrees(IReadOnlyList<TrackPointSample> ordered)
    {
        double total = 0;
        double? previousBearing = null;
        for (var i = 1; i < ordered.Count; i++)
        {
            var meters = GeoMath.HaversineMeters(ordered[i - 1].Position, ordered[i].Position);
            if (meters < HeadingNoiseFloorMeters) continue;

            var bearing = Bearing.DegreesFromTo(ordered[i - 1].Position, ordered[i].Position);
            if (previousBearing is not null)
            {
                total += Math.Abs(Bearing.SignedDelta(previousBearing.Value, bearing));
            }

            previousBearing = bearing;
        }

        return total;
    }
}
