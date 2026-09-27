namespace Backend.Domain;

public sealed record Zone(string Name, GeoPoint Center);

public sealed record NearestZoneResult(string ZoneName, double DistanceMeters, double DistanceToBaseKm);

public static class ZoneResolver
{
    public static NearestZoneResult FindNearest(GeoPoint point, IReadOnlyList<Zone> zones, GeoPoint basePosition)
    {
        if (zones.Count == 0) throw new ArgumentException("At least one zone is required.", nameof(zones));

        var nearest = zones
            .Select(z => (Zone: z, Distance: GeoMath.HaversineMeters(point, z.Center)))
            .MinBy(z => z.Distance);

        var distanceToBaseKm = GeoMath.HaversineMeters(point, basePosition) / 1000d;
        return new NearestZoneResult(nearest.Zone.Name, nearest.Distance, distanceToBaseKm);
    }
}
