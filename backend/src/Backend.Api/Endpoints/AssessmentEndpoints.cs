using Backend.Api.Messaging;

namespace Backend.Api.Endpoints;

/// <summary>
/// The receiving half of the direct HTTP hand-off with the LLM agent service (no broker - see
/// backend/README.md). The agent POSTs here once its submit_final_assessment_tool call
/// completes; this is the exact counterpart to AssessmentRequestClient's POST to the agent's
/// /assess endpoint.
/// </summary>
public static class AssessmentEndpoints
{
    public static void MapAssessmentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/assessments", async (AssessmentCompleted body, AssessmentIngestionService service,
                CancellationToken ct) =>
            {
                await service.UpsertAsync(body, ct);
                return Results.Ok(new { status = "stored", body.TrackId });
            })
            .WithTags("Assessments")
            .WithSummary("Agent service posts its final verdict here (counterpart to backend's POST {AgentBaseUrl}/assess).");
    }
}
