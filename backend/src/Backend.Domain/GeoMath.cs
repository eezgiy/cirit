namespace Backend.Domain;

public static class GeoMath
{
    private const double EarthRadiusMeters = 6_371_000d;

    public static double HaversineMeters(GeoPoint a, GeoPoint b)
    {
        var lat1 = DegreesToRadians(a.Lat);
        var lat2 = DegreesToRadians(b.Lat);
        var dLat = DegreesToRadians(b.Lat - a.Lat);
        var dLon = DegreesToRadians(b.Lon - a.Lon);

        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
        return EarthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
