using Backend.Api.Tools;

namespace Backend.Api.Messaging;

/// <summary>
/// Bridges the backend's in-process Tool API (TrackContextService) to the agent service: gathers
/// a track's full context (details + movement profile + candidate reports) and hands it off over
/// HTTP as one assessment request. Called after ingestion for every newly matched track, and
/// available for manual/replay use via the request-assessment endpoint.
/// </summary>
public sealed class AssessmentRequestPublisher(TrackContextService trackContext, IAssessmentRequestClient client)
{
    public async Task PublishForTrackAsync(string trackId, string reason, CancellationToken ct = default)
    {
        var context = await trackContext.CompareWithReportsAsync(trackId, ct);
        var message = new AgentAssessmentRequested(
            trackId, DateTimeOffset.UtcNow, reason,
            context.TrackDetails, context.MovementProfile, context.CandidateFieldReports);

        await client.RequestAsync(message, ct);
    }
}
