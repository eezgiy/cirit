using System.Text;
using System.Text.Json;

namespace Backend.Api.Messaging;

public interface IAssessmentRequestClient
{
    Task RequestAsync(AgentAssessmentRequested message, CancellationToken ct = default);
}

/// <summary>
/// Direct HTTP hand-off to the LLM agent service (no broker - see backend/README.md "Neden
/// Kafka/Redpanda değil" for why this replaced the earlier Kafka-based boundary). The agent's
/// /assess endpoint is expected to respond immediately (it queues the actual LLM work) and POST
/// the result back to POST /api/assessments once done - this call does not wait for a verdict.
/// </summary>
public sealed class AssessmentRequestClient(IHttpClientFactory httpClientFactory, ILogger<AssessmentRequestClient> logger)
    : IAssessmentRequestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RequestAsync(AgentAssessmentRequested message, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("AgentService");

        // Explicit StringContent (not PostAsJsonAsync) so Content-Length is always set: for
        // large payloads HttpClient can otherwise pick chunked transfer-encoding, which the
        // agent's plain http.server does not parse (it only reads Content-Length).
        var json = JsonSerializer.Serialize(message, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/assess", content, ct);
        response.EnsureSuccessStatusCode();

        logger.LogInformation("Requested assessment for {TrackId} from agent service", message.TrackId);
    }
}
