namespace Backend.Api.Data;

public sealed class ImageMetaEntity
{
    public required string ImageId { get; set; }
    public int WidthPx { get; set; }
    public int HeightPx { get; set; }
    public TimeOnly CaptureTime { get; set; }
    public double TopLeftLat { get; set; }
    public double TopLeftLon { get; set; }
    public double TopRightLat { get; set; }
    public double TopRightLon { get; set; }
    public double BottomLeftLat { get; set; }
    public double BottomLeftLon { get; set; }
    public double BottomRightLat { get; set; }
    public double BottomRightLon { get; set; }

    public List<DetectionEntity> Detections { get; set; } = [];
}

public sealed class DetectionEntity
{
    public int Id { get; set; }
    public required string ImageId { get; set; }
    public ImageMetaEntity? Image { get; set; }

    public int ClassId { get; set; }
    public string? ClassName { get; set; }
    public double? Confidence { get; set; }

    public double CxNorm { get; set; }
    public double CyNorm { get; set; }
    public double WNorm { get; set; }
    public double HNorm { get; set; }

    public double PixelX { get; set; }
    public double PixelY { get; set; }

    public double Lat { get; set; }
    public double Lon { get; set; }

    public DetectionTrackMatchEntity? TrackMatch { get; set; }
}

public sealed class TrackPointEntity
{
    public int Id { get; set; }
    public required string TrackId { get; set; }
    public TimeOnly Time { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
}

public sealed class DetectionTrackMatchEntity
{
    public int Id { get; set; }
    public int DetectionId { get; set; }
    public DetectionEntity? Detection { get; set; }
    public string? TrackId { get; set; }
    public double? DistanceMeters { get; set; }
}

public sealed class ZoneEntity
{
    public required string Name { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
}

public sealed class BaseLocationEntity
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
}

public sealed class FieldReportEntity
{
    public int Id { get; set; }
    public TimeOnly Time { get; set; }
    public required string Source { get; set; }
    public required string RawText { get; set; }

    public double? ResolvedLat { get; set; }
    public double? ResolvedLon { get; set; }
    public string? ResolvedZoneName { get; set; }
    public required string ResolutionConfidence { get; set; }
}

/// <summary>
/// Persisted result of <see cref="Messaging.AssessmentCompleted"/>, consumed off the Kafka
/// boundary from the (not yet built) LLM agent service. Latest verdict wins per track_id - this
/// table is the single source of truth every image/track view joins against (plan.md section 4).
/// </summary>
public sealed class TrackAssessmentEntity
{
    public int Id { get; set; }
    public required string TrackId { get; set; }
    public required string Verdict { get; set; }
    public double Confidence { get; set; }
    public required string Reasoning { get; set; }
    public required string EvidenceRefsJson { get; set; }
    public required string ModelVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
