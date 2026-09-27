using Backend.Domain;
using Xunit;

namespace Backend.Tests;

/// <summary>
/// Reproduces the worked example from gorev_tanimi.pdf (Uctan uca ornek, sayfa 2):
/// img_000123, 1360x765, corners (39.94510,32.86200)/(39.94510,32.86519)/(39.94373,32.86200),
/// bbox (610,380,60,28) -> center pixel (640,394) -> lon 32.86350, lat 39.94439.
/// </summary>
public class GeoTransformTests
{
    private static readonly ImageCorners PdfExampleCorners = new(
        TopLeft: new GeoPoint(39.94510, 32.86200),
        TopRight: new GeoPoint(39.94510, 32.86519),
        BottomLeft: new GeoPoint(39.94373, 32.86200),
        BottomRight: new GeoPoint(39.94373, 32.86519));

    [Fact]
    public void PixelToLatLon_MatchesPdfWorkedExample()
    {
        var result = GeoTransform.PixelToLatLon(PdfExampleCorners, widthPx: 1360, heightPx: 765, x: 640, y: 394);

        Assert.Equal(32.86350, result.Lon, precision: 4);
        Assert.Equal(39.94439, result.Lat, precision: 4);
    }

    [Fact]
    public void NormalizedCenterToPixel_MatchesPdfBoundingBoxCenter()
    {
        // bbox (610, 380, 60, 28) in pixel x,y,w,h -> center (610+60/2, 380+28/2) = (640, 394).
        const int width = 1360;
        const int height = 765;
        var cxNorm = (610 + 60 / 2.0) / width;
        var cyNorm = (380 + 28 / 2.0) / height;

        var (x, y) = GeoTransform.NormalizedCenterToPixel(cxNorm, cyNorm, width, height);

        Assert.Equal(640, x, precision: 6);
        Assert.Equal(394, y, precision: 6);
    }

    [Fact]
    public void PixelToLatLon_TopLeftCorner_ReturnsTopLeftCoordinate()
    {
        var result = GeoTransform.PixelToLatLon(PdfExampleCorners, widthPx: 1360, heightPx: 765, x: 0, y: 0);

        Assert.Equal(PdfExampleCorners.TopLeft.Lat, result.Lat, precision: 9);
        Assert.Equal(PdfExampleCorners.TopLeft.Lon, result.Lon, precision: 9);
    }

    [Fact]
    public void PixelToLatLon_BottomRightCorner_ReturnsBottomRightCoordinate()
    {
        var result = GeoTransform.PixelToLatLon(PdfExampleCorners, widthPx: 1360, heightPx: 765, x: 1360, y: 765);

        Assert.Equal(PdfExampleCorners.BottomRight.Lat, result.Lat, precision: 9);
        Assert.Equal(PdfExampleCorners.BottomRight.Lon, result.Lon, precision: 9);
    }
}
