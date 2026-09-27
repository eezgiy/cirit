using Backend.Api.Ingestion;
using Microsoft.OpenApi.Any;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Backend.Api.Endpoints;

/// <summary>
/// Ingestion boundary (plan.md section 1, step 1-3): accepts a YOLO-normalized detection txt for
/// an already-seeded image_id and runs it, in-process, through geo-enrichment + track-matching.
/// No Kafka here - that boundary only exists between backend and the (not-yet-built) LLM agent.
/// </summary>
public static class IngestionEndpoints
{
    public static void MapIngestionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/images").WithTags("Ingestion");

        group.MapPost("/{imageId}/detections", async (string imageId, HttpRequest request,
            DetectionIngestionService service, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var yoloText = await reader.ReadToEndAsync(ct);

            return await EndpointResultHelper.RunAsync(() => service.IngestAsync(imageId, yoloText, ct));
        })
        .Accepts<string>("text/plain")
        .WithSummary("Ingest a YOLO-normalized detection .txt for one image (geo-enrich + track-match, synchronously).");
    }
}

/// <summary>
/// Swagger UI's default placeholder for a plain-string body is literally "string", which
/// silently fails YoloDetectionParser's validation if a user clicks "Try it out" and submits it
/// as-is - this replaces it with a real example so the request works without editing anything.
/// </summary>
public sealed class YoloDetectionRequestExampleFilter : IOperationFilter
{
    public void Apply(Microsoft.OpenApi.Models.OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.RelativePath != "api/images/{imageId}/detections") return;

        if (operation.RequestBody?.Content.TryGetValue("text/plain", out var mediaType) == true)
        {
            mediaType.Example = new OpenApiString(
                "0 0.189792 0.756981 0.085042 0.145593\n1 0.233563 0.486676 0.054437 0.061907");
        }
    }
}
