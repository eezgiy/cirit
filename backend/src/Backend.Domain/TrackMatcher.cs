namespace Backend.Domain;

public sealed record TrackCandidate(string TrackId, GeoPoint Position);

public sealed record TrackMatchResult(string? TrackId, double? DistanceMeters);

/// <summary>
/// Matches detected vehicle locations to track candidates by nearest-neighbor distance among the
/// track points recorded at the same capture time.
/// Uses 1-to-1 optimal assignment (greedy minimum distance matching):
/// - A single track candidate is assigned to AT MOST ONE detection (the closest one within threshold).
/// - If multiple detections are close to the same track candidate, ONLY THE CLOSEST vehicle gets that track ID.
/// - Other vehicles remain unmatched (or match other nearby unassigned candidates).
/// </summary>
public static class TrackMatcher
{
    public const double DefaultMaxDistanceMeters = 100d;

    /// <summary>
    /// Matches a single detection to the nearest candidate track within threshold.
    /// </summary>
    public static TrackMatchResult Match(GeoPoint detection, IReadOnlyList<TrackCandidate> candidatesAtSameTime,
        double maxDistanceMeters = DefaultMaxDistanceMeters)
    {
        var all = MatchAll(new[] { detection }, candidatesAtSameTime, maxDistanceMeters);
        return all.Count > 0 ? all[0] : new TrackMatchResult(null, null);
    }

    /// <summary>
    /// Matches a batch of detections to track candidates at the same capture time using 1-to-1 optimal
    /// nearest-neighbor assignment. If multiple vehicles are close to the same candidate coordinate,
    /// ONLY THE VEHICLE CLOSEST to that coordinate receives the track ID.
    /// </summary>
    public static IReadOnlyList<TrackMatchResult> MatchAll(
        IReadOnlyList<GeoPoint> detections,
        IReadOnlyList<TrackCandidate> candidatesAtSameTime,
        double maxDistanceMeters = DefaultMaxDistanceMeters)
    {
        if (detections.Count == 0)
        {
            return Array.Empty<TrackMatchResult>();
        }

        var results = new TrackMatchResult[detections.Count];
        for (var i = 0; i < detections.Count; i++)
        {
            results[i] = new TrackMatchResult(null, null);
        }

        if (candidatesAtSameTime.Count == 0)
        {
            return results;
        }

        // Build list of valid (detIndex, candIndex, distance) pairs within threshold
        var pairs = new List<(int DetIndex, int CandIndex, double Distance)>();
        for (var i = 0; i < detections.Count; i++)
        {
            for (var j = 0; j < candidatesAtSameTime.Count; j++)
            {
                var dist = GeoMath.HaversineMeters(detections[i], candidatesAtSameTime[j].Position);
                if (dist <= maxDistanceMeters)
                {
                    pairs.Add((i, j, dist));
                }
            }
        }

        // Sort by distance ascending: the closest pairs are assigned first
        pairs.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        var assignedDetections = new HashSet<int>();
        var assignedTracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (detIndex, candIndex, dist) in pairs)
        {
            var candidate = candidatesAtSameTime[candIndex];
            if (!assignedDetections.Contains(detIndex) && !assignedTracks.Contains(candidate.TrackId))
            {
                results[detIndex] = new TrackMatchResult(candidate.TrackId, dist);
                assignedDetections.Add(detIndex);
                assignedTracks.Add(candidate.TrackId);
            }
        }

        return results;
    }
}

