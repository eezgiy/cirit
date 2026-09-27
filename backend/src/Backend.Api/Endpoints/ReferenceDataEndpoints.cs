using Backend.Api.Data;
using Backend.Api.Ingestion;

namespace Backend.Api.Endpoints;

/// <summary>
/// Upload boundary for the four reference/seed datasets (plan.md: zones.json, tracks.csv,
/// image_meta.json, field_reports.json were only readable from disk at startup - there was no
/// ingestion endpoint for them). Each upload is a real file (IFormFile, so Swagger UI shows a
/// proper file picker like /api/images does) which is saved to disk under a canonical name
/// (ReferenceDataStorageService) AND upserted into the DB by natural key (ReferenceDataLoader) -
/// never a destructive replace, so re-posting the same or an updated file is always safe.
/// </summary>
public static class ReferenceDataEndpoints
{
    public static void MapReferenceDataEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/reference-data").WithTags("ReferenceData");

        group.MapPost("/zones", (IFormFile file, ReferenceDataStorageService storage, ReferenceDataLoader loader,
                AppDbContext db, CancellationToken ct) =>
            UploadAsync(file, "zones.json", storage, ct, content => loader.UpsertZonesAsync(db, content, ct)))
            .WithSummary("Upload zones.json (form field 'file'): saved to disk and upserted by zone name.")
            .DisableAntiforgery();

        group.MapPost("/tracks", (IFormFile file, ReferenceDataStorageService storage, ReferenceDataLoader loader,
                AppDbContext db, CancellationToken ct) =>
            UploadAsync(file, "tracks.csv", storage, ct, content => loader.UpsertTracksAsync(db, content, ct)))
            .WithSummary("Upload tracks.csv (form field 'file'): saved to disk and upserted by (track_id, time).")
            .DisableAntiforgery();

        group.MapPost("/image-meta", (IFormFile file, ReferenceDataStorageService storage, ReferenceDataLoader loader,
                AppDbContext db, CancellationToken ct) =>
            UploadAsync(file, "image_meta.json", storage, ct, content => loader.UpsertImageMetaAsync(db, content, ct)))
            .WithSummary("Upload image_meta.json (form field 'file'): saved to disk and upserted by image_id.")
            .DisableAntiforgery();

        group.MapPost("/field-reports", (IFormFile file, ReferenceDataStorageService storage, ReferenceDataLoader loader,
                AppDbContext db, CancellationToken ct) =>
            UploadAsync(file, "field_reports.json", storage, ct, content => loader.UpsertFieldReportsAsync(db, content, ct)))
            .WithSummary("Upload field_reports.json (form field 'file'): saved to disk, appended deduped by (time, source, text).")
            .DisableAntiforgery();
    }

    private static async Task<IResult> UploadAsync(IFormFile file, string canonicalFileName,
        ReferenceDataStorageService storage, CancellationToken ct, Func<string, Task<int>> upsert)
    {
        return await EndpointResultHelper.RunAsync(async () =>
        {
            byte[] bytes;
            await using (var stream = file.OpenReadStream())
            await using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer, ct);
                bytes = buffer.ToArray();
            }

            var savedPath = await storage.SaveAsync(canonicalFileName, bytes, ct);
            var content = ReferenceDataStorageService.DecodeText(bytes);
            var upserted = await upsert(content);

            return new { upserted, savedPath };
        });
    }
}
