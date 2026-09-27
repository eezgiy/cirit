using Backend.Domain;
using Xunit;

namespace Backend.Tests;

public class ZoneResolverTests
{
    private static readonly GeoPoint BasePosition = new(39.92184, 32.85306);

    private static readonly Zone[] Zones =
    [
        new("Kuzey Yolu", new GeoPoint(39.950586, 32.85306)),
        new("Guney Kapisi Yaklasimi", new GeoPoint(39.893094, 32.85306)),
        new("Dogu Yolu", new GeoPoint(39.92184, 32.890542)),
    ];

    [Fact]
    public void FindNearest_ReturnsClosestZoneByHaversineDistance()
    {
        var point = new GeoPoint(39.949, 32.853); // close to Kuzey Yolu

        var result = ZoneResolver.FindNearest(point, Zones, BasePosition);

        Assert.Equal("Kuzey Yolu", result.ZoneName);
        Assert.True(result.DistanceMeters < 5000);
    }

    [Fact]
    public void FindNearest_AlsoReportsDistanceToBase()
    {
        var result = ZoneResolver.FindNearest(BasePosition, Zones, BasePosition);

        Assert.Equal(0, result.DistanceToBaseKm, precision: 6);
    }
}
