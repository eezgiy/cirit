using Backend.Domain;
using Xunit;

namespace Backend.Tests;

public class TrackMatcherTests
{
    private static readonly GeoPoint Detection = new(39.94441, 32.86353);

    [Fact]
    public void Match_PicksNearestCandidateWithinThreshold()
    {
        var candidates = new[]
        {
            new TrackCandidate("T0187", new GeoPoint(39.94439, 32.86350)), // ~2.5m away
            new TrackCandidate("T9999", new GeoPoint(39.95000, 32.87000)), // far away
        };

        var result = TrackMatcher.Match(Detection, candidates);

        Assert.Equal("T0187", result.TrackId);
        Assert.NotNull(result.DistanceMeters);
        Assert.InRange(result.DistanceMeters!.Value, 0, 10);
    }

    [Fact]
    public void Match_NoCandidateWithinThreshold_ReturnsUnmatched()
    {
        var candidates = new[]
        {
            new TrackCandidate("T0001", new GeoPoint(39.0, 32.0)), // far away
        };

        var result = TrackMatcher.Match(Detection, candidates, maxDistanceMeters: 100);

        Assert.Null(result.TrackId);
        Assert.Null(result.DistanceMeters);
    }

    [Fact]
    public void Match_NoCandidates_ReturnsUnmatched()
    {
        var result = TrackMatcher.Match(Detection, Array.Empty<TrackCandidate>());

        Assert.Null(result.TrackId);
    }

    [Fact]
    public void MatchAll_WhenMultipleDetectionsNearSameTrack_AssignsTrackOnlyToClosestDetection()
    {
        // Track candidate at (39.895913, 32.828656) - e.g. T0107
        var candPos = new GeoPoint(39.895913, 32.828656);
        var candidates = new[] { new TrackCandidate("T0107", candPos) };

        // Detection 0 is 0.17m away (closest)
        var det0 = new GeoPoint(39.895911, 32.828656);
        // Detection 1 is ~2.6m away
        var det1 = new GeoPoint(39.895897, 32.828679);
        // Detection 2 is ~5.8m away
        var det2 = new GeoPoint(39.895960, 32.828686);
        // Detection 3 is ~18.2m away
        var det3 = new GeoPoint(39.895966, 32.828858);

        var detections = new[] { det0, det1, det2, det3 };
        var matches = TrackMatcher.MatchAll(detections, candidates);

        // Only the closest detection (det0) receives T0107
        Assert.Equal("T0107", matches[0].TrackId);
        Assert.True(matches[0].DistanceMeters < 1.0);

        // Other detections must NOT receive T0107
        Assert.Null(matches[1].TrackId);
        Assert.Null(matches[2].TrackId);
        Assert.Null(matches[3].TrackId);
    }

    [Fact]
    public void MatchAll_MultipleTracksAndDetections_PerformsOneToOneExclusiveMatching()
    {
        var candA = new TrackCandidate("T0029", new GeoPoint(39.895795, 32.828792));
        var candB = new TrackCandidate("T0107", new GeoPoint(39.895913, 32.828656));
        var candidates = new[] { candA, candB };

        var detA = new GeoPoint(39.895795, 32.828793); // ~0.1m from T0029
        var detB = new GeoPoint(39.895911, 32.828656); // ~0.2m from T0107
        var detC = new GeoPoint(39.895960, 32.828686); // ~5.8m from T0107, ~20m from T0029

        var detections = new[] { detA, detB, detC };
        var matches = TrackMatcher.MatchAll(detections, candidates);

        Assert.Equal("T0029", matches[0].TrackId);
        Assert.Equal("T0107", matches[1].TrackId);
        Assert.Null(matches[2].TrackId); // T0107 is already claimed by closer detB
    }
}

