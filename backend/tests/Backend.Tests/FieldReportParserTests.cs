using Backend.Domain;
using Xunit;

namespace Backend.Tests;

public class FieldReportParserTests
{
    private static readonly string[] KnownZoneNames =
    [
        "Kuzey Yolu", "Kuzeydogu Kavsagi", "Dogu Yolu", "Guneydogu Yerlesimi",
        "Guney Kapisi Yaklasimi", "Guneybati Yolu", "Bati Yerlesimi", "Kuzeybati Yolu",
    ];

    [Theory]
    [InlineData("39.9374N 32.8483E civarinda 1 kamyon goruldu, yukleri tespit edilemedi.", 39.9374, 32.8483)]
    [InlineData("39.94643N 32.82861E konumunda yuklu bir kamyonun uzun suredir park halinde oldugu bildirildi.", 39.94643, 32.82861)]
    public void Resolve_ExtractsExplicitCoordinate(string text, double expectedLat, double expectedLon)
    {
        var result = FieldReportParser.Resolve(text, KnownZoneNames);

        Assert.Equal(LocationResolutionConfidence.Coordinate, result.Confidence);
        Assert.NotNull(result.Coordinate);
        Assert.Equal(expectedLat, result.Coordinate!.Value.Lat, precision: 5);
        Assert.Equal(expectedLon, result.Coordinate!.Value.Lon, precision: 5);
    }

    [Fact]
    public void Resolve_FallsBackToKnownZoneName()
    {
        var result = FieldReportParser.Resolve("Kuzeybati Yolu cevresinde kayda deger bir hareketlilik bulunmuyor.", KnownZoneNames);

        Assert.Equal(LocationResolutionConfidence.ZoneName, result.Confidence);
        Assert.Equal("Kuzeybati Yolu", result.ZoneName);
        Assert.Null(result.Coordinate);
    }

    [Fact]
    public void Resolve_NoCoordinateOrZoneName_IsUnresolved()
    {
        var result = FieldReportParser.Resolve("Hava acik, gorus mesafesi iyi.", KnownZoneNames);

        Assert.Equal(LocationResolutionConfidence.Unresolved, result.Confidence);
        Assert.Null(result.Coordinate);
        Assert.Null(result.ZoneName);
    }

    [Fact]
    public void Resolve_CoordinateTakesPrecedenceOverZoneNameMention()
    {
        var result = FieldReportParser.Resolve(
            "Kuzey Yolu bolgesinden 39.9374N 32.8483E civarinda bir arac bildirildi.", KnownZoneNames);

        Assert.Equal(LocationResolutionConfidence.Coordinate, result.Confidence);
    }
}
