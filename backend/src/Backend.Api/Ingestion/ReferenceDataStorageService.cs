using System.Text;

namespace Backend.Api.Ingestion;

/// <summary>
/// Persists the raw uploaded reference-data files (POST /api/reference-data/*) to disk under a
/// fixed, canonical name per dataset - same idea as ImageStorageService for images. This keeps
/// an on-disk copy of whatever was last uploaded (audit trail / can be re-read on a future
/// restart) in addition to the DB upsert ReferenceDataLoader performs from its content.
/// </summary>
public sealed class ReferenceDataStorageService(IConfiguration configuration, IHostEnvironment environment)
{
    private string StorageDirectory =>
        AppPaths.Resolve(configuration, environment, "ReferenceDataStorageDirectory", "uploaded-reference-data");

    public async Task<string> SaveAsync(string canonicalFileName, byte[] content, CancellationToken ct = default)
    {
        var dir = StorageDirectory;
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, canonicalFileName);
        await File.WriteAllBytesAsync(path, content, ct);
        return path;
    }

    public static string DecodeText(byte[] content) => Encoding.UTF8.GetString(content);
}
