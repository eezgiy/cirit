using Backend.Api.Data;
using Backend.Api.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Endpoints;

/// <summary>
/// Upload/serve boundary for the actual image files (separate from image_meta.json's geo
/// calibration data - see ImageStorageService). Uploading an image immediately triggers the
/// full pipeline: stage-1 detection (DetectionServiceClient, direct HTTP to the Python service)
/// -> geo-enrichment + track-matching (DetectionIngestionService, same code path as manually
/// posting a YOLO txt) -> assessment request to the LLM agent. No manual "now run detection"
/// step is needed.
/// </summary>
public static class ImageEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    // Caps how many files from one bulk upload run their detect+ingest pipeline at the same
    // time. The detection service (ThreadingHTTPServer + one shared CPU YOLO model) and the
    // backend's SQLite database (WAL mode) were verified to tolerate concurrent requests, so
    // this is a resource-usage cap, not a correctness workaround - matches the concurrency used
    // by the flow wizard's client-side upload workers for consistency.
    private const int BulkUploadConcurrency = 3;

    public static void MapImageEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/images").WithTags("Images");

        group.MapGet("/metadata", async (AppDbContext db, CancellationToken ct) =>
        {
            var metaList = await db.Images
                .OrderBy(i => i.CaptureTime)
                .ThenBy(i => i.ImageId)
                .Select(i => new
                {
                    imageId = i.ImageId,
                    captureTime = i.CaptureTime.ToString("HH:mm"),
                    widthPx = i.WidthPx,
                    heightPx = i.HeightPx,
                    topLeft = new { lat = i.TopLeftLat, lon = i.TopLeftLon },
                    topRight = new { lat = i.TopRightLat, lon = i.TopRightLon },
                    bottomLeft = new { lat = i.BottomLeftLat, lon = i.BottomLeftLon },
                    bottomRight = new { lat = i.BottomRightLat, lon = i.BottomRightLon },
                    candidateTrackCount = db.TrackPoints.Count(t => t.Time == i.CaptureTime)
                })
                .ToListAsync(ct);

            return Results.Ok(metaList);
        })
        .WithSummary("Lists all frame metadata records from image_meta.json with capture time and candidate track counts.");

        group.MapGet("/uploaded", (ImageStorageService storage) =>
        {
            return Results.Ok(storage.ListUploadedImageIds());
        })
        .WithSummary("Lists the image_ids that have an actual uploaded image file on disk (as opposed to " +
                     "/metadata, which lists every calibration-only image_meta.json entry).");

        group.MapPost("/single", async (HttpRequest request, ImageStorageService storage,
                IDetectionServiceClient detectionClient, DetectionIngestionService ingestion,
                AppDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "multipart/form-data formatı beklenmektedir." });
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null)
            {
                return Results.BadRequest(new { error = "En az bir görsel dosyası gönderilmeli (form alanı: 'file')." });
            }

            var metadataId = form["metadataId"].FirstOrDefault()
                             ?? form["imageId"].FirstOrDefault()
                             ?? request.Query["metadataId"].FirstOrDefault()
                             ?? request.Query["imageId"].FirstOrDefault();

            return await EndpointResultHelper.RunAsync(async () =>
                (object)await SaveAndProcessAsync(file, metadataId, storage, detectionClient, ingestion, db, logger, ct));
        })
        .WithSummary("Upload exactly one image; automatically runs detection + geo-enrichment + track-matching. Supports explicit or auto metadata linking.")
        .DisableAntiforgery();

        group.MapPost("", async (IFormFileCollection files, IServiceScopeFactory scopeFactory,
                ILogger<Program> logger, CancellationToken ct) =>
        {
            if (files.Count == 0)
            {
                return Results.BadRequest(new { error = "En az bir görsel dosyası gönderilmeli (form alanı: 'files')." });
            }

            // Each parallel unit of work gets its own DI scope (and therefore its own AppDbContext) -
            // a single DbContext instance can't be used from more than one thread at a time, so
            // reusing one shared instance across files (as the old sequential loop did) would break
            // the moment this runs in parallel. A semaphore bounds how many run at once.
            using var semaphore = new SemaphoreSlim(BulkUploadConcurrency);
            var results = new object[files.Count];

            var tasks = files.Select(async (file, index) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var storage = scope.ServiceProvider.GetRequiredService<ImageStorageService>();
                    var detectionClient = scope.ServiceProvider.GetRequiredService<IDetectionServiceClient>();
                    var ingestion = scope.ServiceProvider.GetRequiredService<DetectionIngestionService>();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    try
                    {
                        results[index] = await SaveAndProcessAsync(file, null, storage, detectionClient, ingestion, db, logger, ct);
                    }
                    catch (FormatException ex)
                    {
                        results[index] = new { file.FileName, error = ex.Message };
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            return Results.Ok(new { results, count = results.Length });
        })
        .Accepts<IFormFileCollection>("multipart/form-data")
        .WithSummary("Bulk upload multiple image files in one request (form field 'files', repeated); each one runs " +
                     $"detection + geo-enrichment automatically, up to {BulkUploadConcurrency} in parallel.")
        .DisableAntiforgery();

        group.MapGet("/{imageId}/file", (string imageId, ImageStorageService storage) =>
        {
            var path = storage.FindFilePath(imageId);
            if (path is null)
            {
                return Results.NotFound(new { error = $"'{imageId}' için yüklenmiş bir görsel dosyası bulunamadı." });
            }

            if (!ContentTypeProvider.TryGetContentType(path, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            return Results.File(path, contentType);
        })
        .WithSummary("Serves the raw image file for an image_id (only ever an uploaded file).");

        group.MapGet("/{imageId}/detections", async (string imageId, AppDbContext db, CancellationToken ct) =>
        {
            var normId = ImageStorageService.NormalizeImageId(imageId);
            var detections = await db.Detections
                .Where(d => d.ImageId == normId || d.ImageId == imageId)
                .Include(d => d.TrackMatch)
                .Select(d => new
                {
                    id = d.Id,
                    classId = d.ClassId,
                    className = d.ClassName,
                    confidence = d.Confidence,
                    cxNorm = d.CxNorm,
                    cyNorm = d.CyNorm,
                    wNorm = d.WNorm,
                    hNorm = d.HNorm,
                    pixelX = d.PixelX,
                    pixelY = d.PixelY,
                    lat = d.Lat,
                    lon = d.Lon,
                    trackId = d.TrackMatch != null ? d.TrackMatch.TrackId : null,
                    distanceMeters = d.TrackMatch != null ? d.TrackMatch.DistanceMeters : (double?)null
                })
                .ToListAsync(ct);

            return Results.Ok(detections);
        })
        .WithSummary("Returns all vehicle detections with bounding boxes and matched track IDs for an image.");
    }

    /// <summary>
    /// Saves the file, then runs detection + ingestion with linked metadata. If the uploaded
    /// filename does not match an image_meta entry, the specified metadataId or the first
    /// available frame metadata is used to calibrate coordinates and track candidates.
    /// </summary>
    private static async Task<object> SaveAndProcessAsync(IFormFile file, string? metadataId,
        ImageStorageService storage, IDetectionServiceClient detectionClient,
        DetectionIngestionService ingestion, AppDbContext db, ILogger logger,
        CancellationToken ct)
    {
        byte[] content;
        await using (var stream = file.OpenReadStream())
        await using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }

        var saved = await storage.SaveAsync(file.FileName, content, ct);

        // Determine which metadata frame to use for calibration & tracking
        string targetMetaId = saved.ImageId;
        if (!string.IsNullOrWhiteSpace(metadataId))
        {
            targetMetaId = ImageStorageService.NormalizeImageId(metadataId);
        }

        var metaEntity = await db.Images.FirstOrDefaultAsync(i => i.ImageId == targetMetaId, ct);
        if (metaEntity == null)
        {
            // If neither the explicit metadataId nor the filename stem exists in db.Images,
            // fallback to the earliest metadata frame so detection and tracking never fail!
            metaEntity = await db.Images.OrderBy(i => i.CaptureTime).FirstOrDefaultAsync(ct);
            if (metaEntity != null)
            {
                logger.LogInformation("Image '{ImageId}' not in metadata; falling back to '{FallbackMetaId}' ({Time})",
                    saved.ImageId, metaEntity.ImageId, metaEntity.CaptureTime);
                targetMetaId = metaEntity.ImageId;
            }
        }

        string detectionStatus;
        object? detectionDetail = null;

        try
        {
            var yoloText = await detectionClient.DetectAsync(saved.ImageId, saved.Extension, content, ct);
            var detections = await ingestion.IngestAsync(targetMetaId, yoloText, ct);
            var matchedTrackIds = detections.Where(d => d.TrackId is not null).Select(d => d.TrackId!).Distinct().ToList();

            detectionStatus = "completed";
            detectionDetail = new
            {
                metadataId = targetMetaId,
                captureTime = metaEntity?.CaptureTime.ToString("HH:mm"),
                detectionCount = detections.Count,
                matchedTrackIds,
                detections = detections.Select(d => new
                {
                    detectionId = d.DetectionId,
                    classId = d.ClassId,
                    className = d.ClassName,
                    confidence = d.Confidence,
                    lat = d.Lat,
                    lon = d.Lon,
                    trackId = d.TrackId,
                    distanceMeters = d.DistanceMeters
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            detectionStatus = "failed";
            detectionDetail = new { reason = ex.Message, metadataId = targetMetaId };
            logger.LogError(ex, "Detection failed for {ImageId} (metadata: {TargetMetaId})", saved.ImageId, targetMetaId);
        }

        return new
        {
            saved.ImageId,
            saved.FileName,
            saved.SizeBytes,
            metadataId = targetMetaId,
            captureTime = metaEntity?.CaptureTime.ToString("HH:mm"),
            detection = new { status = detectionStatus, detail = detectionDetail },
        };
    }
}
