using System.Net.Http.Headers;

namespace Backend.Api.Ingestion;

public interface IDetectionServiceClient
{
    /// <summary>Runs the stage-1 model on one image and returns the YOLO-normalized detection
    /// txt (same format DetectionIngestionService.IngestAsync already accepts).</summary>
    Task<string> DetectAsync(string imageId, string extension, byte[] imageContent, CancellationToken ct = default);
}

/// <summary>
/// Direct HTTP call to the Python stage-1 detection service (llm/detection/server.py). Raw image
/// bytes go in, a YOLO-normalized txt comes back - no broker, no shared filesystem, just one
/// request per image (see ImageEndpoints for where this is wired into the upload flow).
/// </summary>
public sealed class DetectionServiceClient(IHttpClientFactory httpClientFactory) : IDetectionServiceClient
{
    public async Task<string> DetectAsync(string imageId, string extension, byte[] imageContent, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("DetectionService");
        using var content = new ByteArrayContent(imageContent);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/detect") { Content = content };
        request.Headers.Add("X-Image-Id", imageId);
        request.Headers.Add("X-Extension", extension);

        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
