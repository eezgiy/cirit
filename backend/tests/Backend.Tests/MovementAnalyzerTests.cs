using Backend.Domain;
using Xunit;

namespace Backend.Tests;

public class MovementAnalyzerTests
{
    private static readonly GeoPoint Base = new(0, 0);

    private static TrackPointSample Sample(int minute, double lat, double lon)
        => new(new TimeOnly(10, 0).AddMinutes(minute), new GeoPoint(lat, lon));

    [Fact]
    public void Analyze_VehicleNotMovingFor30Minutes_IsStationary()
    {
        var samples = Enumerable.Range(0, 7)
            .Select(i => Sample(i * 5, 0.03, 0.03))
            .ToList();

        var profile = MovementAnalyzer.Analyze("T-STATIONARY", samples, Base);

        Assert.Equal(MovementStatus.Stationary, profile.Status);
        Assert.True(profile.StatusDurationMinutes >= 25);
        Assert.Equal(0, profile.CurrentSpeedMetersPerSecond, precision: 3);
    }

    [Fact]
    public void Analyze_DistanceToBaseSteadilyShrinking_IsApproaching()
    {
        var samples = Enumerable.Range(0, 6)
            .Select(i => Sample(i * 5, 0.05 - i * 0.008, 0))
            .ToList();

        var profile = MovementAnalyzer.Analyze("T-APPROACH", samples, Base);

        Assert.Equal(MovementStatus.Approaching, profile.Status);
        Assert.True(profile.DistanceToBaseKmNow < profile.DistanceToBaseKmAtWindowStart);
        Assert.True(profile.CurrentSpeedMetersPerSecond > 1);
    }

    [Fact]
    public void Analyze_DistanceToBaseSteadilyGrowing_IsReceding()
    {
        var samples = Enumerable.Range(0, 6)
            .Select(i => Sample(i * 5, 0.01 + i * 0.008, 0))
            .ToList();

        var profile = MovementAnalyzer.Analyze("T-RECEDE", samples, Base);

        Assert.Equal(MovementStatus.Receding, profile.Status);
        Assert.True(profile.DistanceToBaseKmNow > profile.DistanceToBaseKmAtWindowStart);
    }

    [Fact]
    public void Analyze_ConstantRadiusLoopAroundBase_IsOrbiting()
    {
        const double radiusDeg = 0.01;
        var samples = Enumerable.Range(0, 7)
            .Select(i =>
            {
                var theta = i * Math.PI / 3; // 60 degrees per step, one full loop over 6 steps
                return Sample(i * 5, radiusDeg * Math.Cos(theta), radiusDeg * Math.Sin(theta));
            })
            .ToList();

        var profile = MovementAnalyzer.Analyze("T-ORBIT", samples, Base);

        Assert.Equal(MovementStatus.Orbiting, profile.Status);
        Assert.True(profile.OrbitScore < 0.15);
    }

    [Fact]
    public void Analyze_SinglePoint_DoesNotThrowAndReportsZeroSpeed()
    {
        var samples = new[] { Sample(0, 0.02, 0.02) };

        var profile = MovementAnalyzer.Analyze("T-SINGLE", samples, Base);

        Assert.Equal(0, profile.CurrentSpeedMetersPerSecond);
        Assert.Equal(0, profile.AverageSpeedMetersPerSecond);
    }
}
