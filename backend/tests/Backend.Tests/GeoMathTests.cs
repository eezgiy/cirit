using Backend.Domain;
using Xunit;

namespace Backend.Tests;

public class GeoMathTests
{
    [Fact]
    public void HaversineMeters_SamePoint_IsZero()
    {
        var p = new GeoPoint(39.92184, 32.85306);
        Assert.Equal(0, GeoMath.HaversineMeters(p, p), precision: 6);
    }

    [Fact]
    public void HaversineMeters_OneDegreeLatitude_IsApproximately111Km()
    {
        var a = new GeoPoint(0, 0);
        var b = new GeoPoint(1, 0);

        var meters = GeoMath.HaversineMeters(a, b);

        Assert.InRange(meters, 110_000, 112_000);
    }
}
