using System.Globalization;
using System.Text.RegularExpressions;

namespace Backend.Domain;

public enum LocationResolutionConfidence
{
    /// <summary>An explicit "12.3456N 78.9012E" style coordinate was found in the text.</summary>
    Coordinate,

    /// <summary>No coordinate, but a known zone name from zones.json appears in the text.</summary>
    ZoneName,

    /// <summary>Neither a coordinate nor a known zone name could be found (e.g. weather/status chatter).</summary>
    Unresolved,
}

public sealed record FieldReportLocation(GeoPoint? Coordinate, string? ZoneName, LocationResolutionConfidence Confidence);

/// <summary>
/// One-time, deterministic extraction of a location hint from a free-text field report, run once
/// at ingestion (FieldReportIndexer) rather than re-parsed on every agent tool call. This never
/// judges whether the report is *true* - only where it claims to be about, if anywhere.
/// </summary>
public static partial class FieldReportParser
{
    [GeneratedRegex(@"(?<lat>\d{1,2}\.\d+)\s*N\s+(?<lon>\d{1,3}\.\d+)\s*E", RegexOptions.IgnoreCase)]
    private static partial Regex CoordinatePattern();

    public static FieldReportLocation Resolve(string text, IReadOnlyList<string> knownZoneNames)
    {
        var match = CoordinatePattern().Match(text);
        if (match.Success)
        {
            var lat = double.Parse(match.Groups["lat"].Value, CultureInfo.InvariantCulture);
            var lon = double.Parse(match.Groups["lon"].Value, CultureInfo.InvariantCulture);
            return new FieldReportLocation(new GeoPoint(lat, lon), null, LocationResolutionConfidence.Coordinate);
        }

        foreach (var zoneName in knownZoneNames)
        {
            if (text.Contains(zoneName, StringComparison.OrdinalIgnoreCase))
            {
                return new FieldReportLocation(null, zoneName, LocationResolutionConfidence.ZoneName);
            }
        }

        return new FieldReportLocation(null, null, LocationResolutionConfidence.Unresolved);
    }
}
