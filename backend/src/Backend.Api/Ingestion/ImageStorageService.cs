namespace Backend.Api.Ingestion;

public sealed record SavedImage(string ImageId, string FileName, string Extension, long SizeBytes);

/// <summary>
/// Stores the raw image files (POST /api/images) uploaded separately from their metadata
/// (POST /api/reference-data/image-meta, which only carries geo-calibration - width/height/
/// capture_time/corners - not the pixel bytes). Uploaded files are saved under
/// ImageStorageDirectory keyed by image_id (the filename stem); the backend never reads image
/// files from anywhere else on disk - GET /api/images/{id}/file only ever serves what was
/// uploaded through this service.
/// </summary>
public sealed class ImageStorageService(IConfiguration configuration, IHostEnvironment environment)
{
    public static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };

    /// <summary>image_meta.json keys never carry a file extension - strips one off if a caller
    /// passes e.g. "img_000267.jpg" instead of "img_000267" (an easy, common mistake).</summary>
    public static string NormalizeImageId(string imageId) =>
        SupportedExtensions.Contains(Path.GetExtension(imageId)) ? Path.GetFileNameWithoutExtension(imageId) : imageId;

    private string UploadDirectory => AppPaths.Resolve(configuration, environment, "ImageStorageDirectory", "uploaded-images");

    public async Task<SavedImage> SaveAsync(string fileName, byte[] content, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new FormatException(
                $"Unsupported image extension '{extension}' for '{fileName}'. Supported: {string.Join(", ", SupportedExtensions)}");
        }

        var imageId = Path.GetFileNameWithoutExtension(fileName);
        var dir = UploadDirectory;
        Directory.CreateDirectory(dir);

        var savedFileName = imageId + extension;
        var path = Path.Combine(dir, savedFileName);

        await File.WriteAllBytesAsync(path, content, ct);

        return new SavedImage(imageId, savedFileName, extension, content.LongLength);
    }

    /// <summary>Lists the image_ids (filename stems) that actually have an uploaded file on disk -
    /// used to filter metadata-frame pickers down to frames a real image exists for, instead of
    /// showing all of image_meta.json's calibration-only entries.</summary>
    public IReadOnlyList<string> ListUploadedImageIds()
    {
        var dir = UploadDirectory;
        if (!Directory.Exists(dir)) return [];

        return Directory.GetFiles(dir)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .Select(f => Path.GetFileNameWithoutExtension(f)!)
            .Distinct()
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Looks only in the upload directory - never reads image files from anywhere else on disk.</summary>
    public string? FindFilePath(string imageId)
    {
        imageId = NormalizeImageId(imageId);

        var dir = UploadDirectory;
        if (!Directory.Exists(dir)) return null;

        foreach (var extension in SupportedExtensions)
        {
            var candidate = Path.Combine(dir, imageId + extension);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
