using System.Globalization;
using System.Text.Json;
using Backend.Api.Data;
using Backend.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Ingestion;

/// <summary>
/// Upserts the reference datasets that are not part of the streaming detection pipeline: zones,
/// tracks, image metadata and field reports. Every method here is safe to call repeatedly with
/// updated content - existing rows are updated in place by their natural key, nothing is deleted -
/// so re-posting the same or an updated file via the /api/reference-data/* upload endpoints is
/// always safe. The backend never reads these datasets from local disk itself - every row comes
/// in through an upload. Field reports are resolved to a location here (FieldReportParser) so the
/// search tool never re-parses raw text on every call.
/// </summary>
public sealed class ReferenceDataLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> UpsertZonesAsync(AppDbContext db, string json, CancellationToken ct = default)
    {
        var zonesFile = JsonSerializer.Deserialize<ZonesFile>(json, JsonOptions)
            ?? throw new FormatException("Could not parse zones.json content.");

        var baseLocation = await db.BaseLocations.FirstOrDefaultAsync(ct);
        if (baseLocation is null)
        {
            baseLocation = new BaseLocationEntity { Name = zonesFile.Base.Name, Lat = zonesFile.Base.Lat, Lon = zonesFile.Base.Lon };
            db.BaseLocations.Add(baseLocation);
        }
        else
        {
            baseLocation.Name = zonesFile.Base.Name;
            baseLocation.Lat = zonesFile.Base.Lat;
            baseLocation.Lon = zonesFile.Base.Lon;
        }

        var existingZones = await db.Zones.ToDictionaryAsync(z => z.Name, ct);
        foreach (var zone in zonesFile.Zones)
        {
            if (existingZones.TryGetValue(zone.Name, out var entity))
            {
                entity.Lat = zone.Center[0];
                entity.Lon = zone.Center[1];
            }
            else
            {
                db.Zones.Add(new ZoneEntity { Name = zone.Name, Lat = zone.Center[0], Lon = zone.Center[1] });
            }
        }

        await db.SaveChangesAsync(ct);
        return zonesFile.Zones.Count;
    }

    public async Task<int> UpsertTracksAsync(AppDbContext db, string csv, CancellationToken ct = default)
    {
        var existing = await db.TrackPoints.ToDictionaryAsync(t => (t.TrackId, t.Time), ct);
        var upserted = 0;

        using var reader = new StringReader(csv);
        var header = await reader.ReadLineAsync(ct); // track_id,time,lat,lon
        _ = header;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0) continue;
            var parts = line.Split(',');
            if (parts.Length < 4)
            {
                throw new FormatException($"Malformed tracks.csv line (expected 4 columns): '{line}'");
            }

            var trackId = parts[0];
            var time = TimeOnly.ParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture);
            var lat = double.Parse(parts[2], CultureInfo.InvariantCulture);
            var lon = double.Parse(parts[3], CultureInfo.InvariantCulture);

            if (existing.TryGetValue((trackId, time), out var entity))
            {
                entity.Lat = lat;
                entity.Lon = lon;
            }
            else
            {
                db.TrackPoints.Add(new TrackPointEntity { TrackId = trackId, Time = time, Lat = lat, Lon = lon });
            }

            upserted++;
        }

        await db.SaveChangesAsync(ct);
        return upserted;
    }

    public async Task<int> UpsertImageMetaAsync(AppDbContext db, string json, CancellationToken ct = default)
    {
        var images = JsonSerializer.Deserialize<Dictionary<string, ImageMetaJson>>(json, JsonOptions)
            ?? throw new FormatException("Could not parse image_meta.json content.");

        var existing = await db.Images.ToDictionaryAsync(i => i.ImageId, ct);

        foreach (var (imageId, meta) in images)
        {
            if (!existing.TryGetValue(imageId, out var entity))
            {
                entity = new ImageMetaEntity { ImageId = imageId };
                db.Images.Add(entity);
            }

            entity.WidthPx = meta.WidthPx;
            entity.HeightPx = meta.HeightPx;
            entity.CaptureTime = TimeOnly.ParseExact(meta.CaptureTime, "HH:mm", CultureInfo.InvariantCulture);
            entity.TopLeftLat = meta.CornerCoordinates.TopLeft[0];
            entity.TopLeftLon = meta.CornerCoordinates.TopLeft[1];
            entity.TopRightLat = meta.CornerCoordinates.TopRight[0];
            entity.TopRightLon = meta.CornerCoordinates.TopRight[1];
            entity.BottomLeftLat = meta.CornerCoordinates.BottomLeft[0];
            entity.BottomLeftLon = meta.CornerCoordinates.BottomLeft[1];
            entity.BottomRightLat = meta.CornerCoordinates.BottomRight[0];
            entity.BottomRightLon = meta.CornerCoordinates.BottomRight[1];
        }

        await db.SaveChangesAsync(ct);
        return images.Count;
    }

    /// <summary>
    /// Field reports have no natural id in the source data, so a (time, source, text) triple is
    /// used as the dedup key: re-uploading the same day's reports is a no-op, genuinely new
    /// reports are appended and resolved against whichever zones are in the DB right now.
    /// </summary>
    public async Task<int> UpsertFieldReportsAsync(AppDbContext db, string json, CancellationToken ct = default)
    {
        var reports = JsonSerializer.Deserialize<List<FieldReportJson>>(json, JsonOptions)
            ?? throw new FormatException("Could not parse field_reports.json content.");

        var knownZoneNames = await db.Zones.Select(z => z.Name).ToListAsync(ct);
        var existingKeys = await db.FieldReports
            .Select(r => new { r.Time, r.Source, r.RawText })
            .ToListAsync(ct);
        var seen = existingKeys.Select(r => (r.Time, r.Source, r.RawText)).ToHashSet();

        var added = 0;
        foreach (var report in reports)
        {
            var time = TimeOnly.ParseExact(report.Time, "HH:mm", CultureInfo.InvariantCulture);
            var key = (time, report.Source, report.Text);
            if (!seen.Add(key)) continue; // already have this exact report

            var resolution = FieldReportParser.Resolve(report.Text, knownZoneNames);
            db.FieldReports.Add(new FieldReportEntity
            {
                Time = time,
                Source = report.Source,
                RawText = report.Text,
                ResolvedLat = resolution.Coordinate?.Lat,
                ResolvedLon = resolution.Coordinate?.Lon,
                ResolvedZoneName = resolution.ZoneName,
                ResolutionConfidence = resolution.Confidence.ToString(),
            });
            added++;
        }

        await db.SaveChangesAsync(ct);
        return added;
    }
}
