using System.Text.Json.Serialization;

namespace Backend.Api.Ingestion;

public sealed class ZonesFile
{
    [JsonPropertyName("base")] public required BaseJson Base { get; set; }
    [JsonPropertyName("zones")] public required List<ZoneJson> Zones { get; set; }
}

public sealed class BaseJson
{
    [JsonPropertyName("name")] public required string Name { get; set; }
    [JsonPropertyName("lat")] public double Lat { get; set; }
    [JsonPropertyName("lon")] public double Lon { get; set; }
}

public sealed class ZoneJson
{
    [JsonPropertyName("name")] public required string Name { get; set; }
    [JsonPropertyName("center")] public required double[] Center { get; set; } // [lat, lon]
}

public sealed class FieldReportJson
{
    [JsonPropertyName("time")] public required string Time { get; set; }
    [JsonPropertyName("source")] public required string Source { get; set; }
    [JsonPropertyName("text")] public required string Text { get; set; }
}

public sealed class ImageMetaJson
{
    [JsonPropertyName("width_px")] public int WidthPx { get; set; }
    [JsonPropertyName("height_px")] public int HeightPx { get; set; }
    [JsonPropertyName("capture_time")] public required string CaptureTime { get; set; }
    [JsonPropertyName("corner_coordinates")] public required CornerCoordinatesJson CornerCoordinates { get; set; }
}

public sealed class CornerCoordinatesJson
{
    [JsonPropertyName("top_left")] public required double[] TopLeft { get; set; }
    [JsonPropertyName("top_right")] public required double[] TopRight { get; set; }
    [JsonPropertyName("bottom_left")] public required double[] BottomLeft { get; set; }
    [JsonPropertyName("bottom_right")] public required double[] BottomRight { get; set; }
}
