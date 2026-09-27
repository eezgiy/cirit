namespace Backend.Domain;

public static class Bearing
{
    /// <summary>Initial bearing from a to b, in degrees, 0-360 (0 = north).</summary>
    public static double DegreesFromTo(GeoPoint a, GeoPoint b)
    {
        var lat1 = DegreesToRadians(a.Lat);
        var lat2 = DegreesToRadians(b.Lat);
        var dLon = DegreesToRadians(b.Lon - a.Lon);

        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        var bearing = Math.Atan2(y, x) * 180d / Math.PI;
        return (bearing + 360d) % 360d;
    }

    /// <summary>Smallest signed angular difference between two bearings, in degrees (-180, 180].</summary>
    public static double SignedDelta(double fromDegrees, double toDegrees)
    {
        var delta = (toDegrees - fromDegrees + 540d) % 360d - 180d;
        return delta;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
