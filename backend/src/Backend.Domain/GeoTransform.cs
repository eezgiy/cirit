namespace Backend.Domain;

/// <summary>
/// Deterministic pixel-to-WGS84 conversion for the north-up, non-rotated drone images
/// described in gorev_tanimi.pdf: a pixel's coordinate is a linear interpolation between
/// the image's four corner coordinates.
/// </summary>
public static class GeoTransform
{
    /// <param name="x">Pixel x, 0 at the left edge.</param>
    /// <param name="y">Pixel y, 0 at the top edge.</param>
    public static GeoPoint PixelToLatLon(ImageCorners corners, int widthPx, int heightPx, double x, double y)
    {
        if (widthPx <= 0) throw new ArgumentOutOfRangeException(nameof(widthPx));
        if (heightPx <= 0) throw new ArgumentOutOfRangeException(nameof(heightPx));

        var lon = corners.TopLeft.Lon + (x / widthPx) * (corners.TopRight.Lon - corners.TopLeft.Lon);
        var lat = corners.TopLeft.Lat + (y / heightPx) * (corners.BottomLeft.Lat - corners.TopLeft.Lat);
        return new GeoPoint(lat, lon);
    }

    /// <summary>Converts a YOLO-normalized bbox center (0-1 range) to the bbox's pixel center.</summary>
    public static (double X, double Y) NormalizedCenterToPixel(double cxNorm, double cyNorm, int widthPx, int heightPx)
        => (cxNorm * widthPx, cyNorm * heightPx);
}
