using System.Globalization;

namespace Backend.Api.Ingestion;

/// <summary>YOLO-normalized detection line: class_id cx cy w h [confidence].</summary>
public sealed record YoloDetectionLine(int ClassId, double Cx, double Cy, double W, double H, double? Confidence);

public static class YoloDetectionParser
{
    public static IReadOnlyList<YoloDetectionLine> Parse(string text)
    {
        var lines = new List<YoloDetectionLine>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                throw new FormatException($"Malformed YOLO detection line (expected >=5 columns): '{line}'");
            }

            var classId = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var cx = double.Parse(parts[1], CultureInfo.InvariantCulture);
            var cy = double.Parse(parts[2], CultureInfo.InvariantCulture);
            var w = double.Parse(parts[3], CultureInfo.InvariantCulture);
            var h = double.Parse(parts[4], CultureInfo.InvariantCulture);
            double? confidence = parts.Length >= 6 ? double.Parse(parts[5], CultureInfo.InvariantCulture) : null;

            lines.Add(new YoloDetectionLine(classId, cx, cy, w, h, confidence));
        }

        return lines;
    }
}
