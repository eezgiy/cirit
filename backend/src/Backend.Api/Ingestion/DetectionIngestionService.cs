using Backend.Api.Data;
using Backend.Api.Messaging;
using Backend.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Ingestion;

public sealed record IngestedDetectionResult(
    int DetectionId, int ClassId, string? ClassName, double? Confidence,
    double Lat, double Lon, string? TrackId, double? DistanceMeters);

/// <summary>
/// The in-process, synchronous part of the pipeline (plan.md section 1): takes a YOLO-normalized
/// detection txt for one already-seeded image, converts each bbox center to WGS84
/// (GeoTransform, using that image's own capture_time + corners from image_meta.json), and
/// matches it to a track_id via nearest-neighbor among the track points recorded at the same
/// capture time (TrackMatcher). No Kafka involved - this all happens inline in one request.
/// </summary>
public sealed class DetectionIngestionService(
    AppDbContext db, IConfiguration configuration, AssessmentRequestPublisher assessmentRequestPublisher,
    ILogger<DetectionIngestionService> logger)
{
    public async Task<IReadOnlyList<IngestedDetectionResult>> IngestAsync(string imageId, string yoloText,
        CancellationToken cancellationToken = default)
    {
        // image_meta.json keys (and thus image_id everywhere else) never carry a file extension -
        // a caller passing "img_000267.jpg" here is an easy, common mistake, so strip it rather
        // than fail on something the ImageStorageService/detection-service side already treats
        // as equivalent.
        imageId = ImageStorageService.NormalizeImageId(imageId);

        var image = await db.Images.FirstOrDefaultAsync(i => i.ImageId == imageId, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Unknown image_id '{imageId}'. Seed image_meta.json before ingesting its detections.");

        var corners = new ImageCorners(
            new GeoPoint(image.TopLeftLat, image.TopLeftLon),
            new GeoPoint(image.TopRightLat, image.TopRightLon),
            new GeoPoint(image.BottomLeftLat, image.BottomLeftLon),
            new GeoPoint(image.BottomRightLat, image.BottomRightLon));

        var classNames = configuration.GetSection("DetectionClassNames").Get<Dictionary<string, string>>() ?? [];
        var lines = YoloDetectionParser.Parse(yoloText);

        // Remove existing detections & matches for this image if it was previously ingested
        // to avoid duplicate stacking of bounding boxes and detections
        var existingDetectionIds = await db.Detections
            .Where(d => d.ImageId == imageId)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        if (existingDetectionIds.Count > 0)
        {
            var oldMatches = await db.DetectionTrackMatches
                .Where(m => existingDetectionIds.Contains(m.DetectionId))
                .ToListAsync(cancellationToken);
            db.DetectionTrackMatches.RemoveRange(oldMatches);

            var oldDetections = await db.Detections
                .Where(d => existingDetectionIds.Contains(d.Id))
                .ToListAsync(cancellationToken);
            db.Detections.RemoveRange(oldDetections);

            await db.SaveChangesAsync(cancellationToken);
        }

        var candidatesAtCaptureTime = await db.TrackPoints
            .Where(t => t.Time == image.CaptureTime)
            .Select(t => new TrackCandidate(t.TrackId, new GeoPoint(t.Lat, t.Lon)))
            .ToListAsync(cancellationToken);

        // Precompute all detection pixel coordinates and WGS84 GeoPoints
        var parsedItems = new List<(YoloDetectionLine Line, double PixelX, double PixelY, GeoPoint Geo, string? ClassName)>();
        var detectionPoints = new List<GeoPoint>(lines.Count);

        foreach (var line in lines)
        {
            var (pixelX, pixelY) = GeoTransform.NormalizedCenterToPixel(line.Cx, line.Cy, image.WidthPx, image.HeightPx);
            var geo = GeoTransform.PixelToLatLon(corners, image.WidthPx, image.HeightPx, pixelX, pixelY);
            var className = classNames.GetValueOrDefault(line.ClassId.ToString());

            parsedItems.Add((line, pixelX, pixelY, geo, className));
            detectionPoints.Add(geo);
        }

        // 1-to-1 optimal assignment: each track candidate is assigned to at most ONE detection
        // (the closest one within threshold). If multiple vehicles are close to the same candidate,
        // only the single closest vehicle gets that track ID.
        var matches = TrackMatcher.MatchAll(detectionPoints, candidatesAtCaptureTime);

        var results = new List<IngestedDetectionResult>();

        for (var i = 0; i < parsedItems.Count; i++)
        {
            var item = parsedItems[i];
            var match = matches[i];

            var detection = new DetectionEntity
            {
                ImageId = imageId,
                ClassId = item.Line.ClassId,
                ClassName = item.ClassName,
                Confidence = item.Line.Confidence,
                CxNorm = item.Line.Cx,
                CyNorm = item.Line.Cy,
                WNorm = item.Line.W,
                HNorm = item.Line.H,
                PixelX = item.PixelX,
                PixelY = item.PixelY,
                Lat = item.Geo.Lat,
                Lon = item.Geo.Lon,
            };
            db.Detections.Add(detection);
            await db.SaveChangesAsync(cancellationToken); // need the generated Id for the match row below

            db.DetectionTrackMatches.Add(new DetectionTrackMatchEntity
            {
                DetectionId = detection.Id,
                TrackId = match.TrackId,
                DistanceMeters = match.DistanceMeters,
            });

            results.Add(new IngestedDetectionResult(detection.Id, item.Line.ClassId, item.ClassName, item.Line.Confidence,
                item.Geo.Lat, item.Geo.Lon, match.TrackId, match.DistanceMeters));
        }

        await db.SaveChangesAsync(cancellationToken);

        await PublishAssessmentRequestsAsync(imageId, results, cancellationToken);

        return results;
    }

    /// <summary>
    /// Crosses the Kafka boundary (plan.md section 3): for every track newly matched in this
    /// batch, hand its full context to the LLM agent service by enqueuing on
    /// agent.assessment.requested. Best-effort - a broker hiccup here must not fail ingestion,
    /// since the assessment can always be re-requested later (see the manual replay endpoint).
    /// </summary>
    private async Task PublishAssessmentRequestsAsync(string imageId, IReadOnlyList<IngestedDetectionResult> results,
        CancellationToken ct)
    {
        var newlyMatchedTrackIds = results.Select(r => r.TrackId).Where(id => id is not null).Distinct().ToList();

        foreach (var trackId in newlyMatchedTrackIds)
        {
            try
            {
                await assessmentRequestPublisher.PublishForTrackAsync(trackId!, $"ingestion:{imageId}", ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to publish assessment request for {TrackId} (image {ImageId})", trackId, imageId);
            }
        }
    }
}
