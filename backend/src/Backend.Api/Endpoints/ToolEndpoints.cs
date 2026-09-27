using Backend.Api.Messaging;
using Backend.Api.Tools;

namespace Backend.Api.Endpoints;

/// <summary>
/// The backend's Tool API (plan.md section 5): read-only endpoints the future LLM agent service
/// will call synchronously during its tool-calling loop. No LLM is wired up yet - these are
/// plain, directly-callable REST endpoints today.
/// </summary>
public static class ToolEndpoints
{
    public static void MapToolEndpoints(this WebApplication app)
    {
        var tracks = app.MapGroup("/api/tracks").WithTags("Tools");

        tracks.MapGet("", (TrackContextService service, CancellationToken ct)
            => EndpointResultHelper.RunAsync(() => service.GetAllTracksAsync(ct)))
            .WithSummary("get_all_tracks: list of all track summaries.");

        tracks.MapGet("/{trackId}", (string trackId, TrackContextService service, CancellationToken ct)
            => EndpointResultHelper.RunAsync(() => service.GetTrackDetailsAsync(trackId, ct)))
            .WithSummary("get_track_details: identity, observed classes, matched images/detections for a track_id.");

        tracks.MapGet("/{trackId}/points", (string trackId, TrackContextService service, CancellationToken ct)
            => EndpointResultHelper.RunAsync(() => service.GetTrackTrajectoryAsync(trackId, ct)))
            .WithSummary("get_track_points: all trajectory coordinates and timestamps for a track.");

        tracks.MapGet("/{trackId}/movement-profile", (string trackId, TrackContextService service, CancellationToken ct)
            => EndpointResultHelper.RunAsync(() => service.GetMovementProfileAsync(trackId, ct)))
            .WithSummary("get_movement_profile: deterministic status/speed/loiter/orbit metrics for a track_id.");

        tracks.MapGet("/{trackId}/field-reports", (string trackId, double? radiusMeters, bool? includeUnresolved,
                TrackContextService service, CancellationToken ct)
            => EndpointResultHelper.RunAsync(() => service.SearchFieldReportsForTrackAsync(
                trackId, radiusMeters ?? 800, includeUnresolved: includeUnresolved ?? false, ct: ct)))
            .WithSummary("search_field_reports (by track): candidate reports near this track's time/location window.");

        tracks.MapGet("/{trackId}/compare-reports", (string trackId, TrackContextService service, CancellationToken ct)
            => EndpointResultHelper.RunAsync(() => service.CompareWithReportsAsync(trackId, ct)))
            .WithSummary("compare_with_reports: track details + movement profile + candidate reports, unjudged.");

        tracks.MapPost("/{trackId}/request-assessment", async (string trackId,
                AssessmentRequestPublisher publisher, CancellationToken ct) =>
            {
                await publisher.PublishForTrackAsync(trackId, "manual-replay", ct);
                return Results.Accepted();
            })
            .WithSummary("Crosses the Kafka boundary manually: (re-)enqueue agent.assessment.requested for a track_id.");

        tracks.MapGet("/{trackId}/assessment", (string trackId, TrackContextService service, CancellationToken ct)
            => EndpointResultHelper.RunAsync(() => service.GetLatestAssessmentAsync(trackId, ct)))
            .WithSummary("Latest verdict consumed off assessments.completed for this track_id, if any.");

        var baseLocation = app.MapGroup("/api/base-location").WithTags("Tools");

        baseLocation.MapGet("", (TrackContextService service, CancellationToken ct)
            => EndpointResultHelper.RunAsync(() => service.GetBaseLocationDetailsAsync(ct)))
            .WithSummary("get_base_location: coordinates and name of the military base.");

        var zones = app.MapGroup("/api/zones").WithTags("Tools");

        zones.MapGet("", (TrackContextService service, CancellationToken ct)
            => EndpointResultHelper.RunAsync(() => service.GetZoneResponsesAsync(ct)))
            .WithSummary("get_zones: all defined tactical zones.");

        zones.MapGet("/nearest", (double lat, double lon, TrackContextService service, CancellationToken ct)
            => EndpointResultHelper.RunAsync(() => service.FindNearestZoneAsync(lat, lon, ct)))
            .WithSummary("get_nearest_zone: closest named zone + distance to base for a coordinate.");

        var fieldReports = app.MapGroup("/api/field-reports").WithTags("Tools");

        fieldReports.MapGet("", (TrackContextService service, CancellationToken ct)
            => EndpointResultHelper.RunAsync(() => service.GetAllFieldReportsAsync(ct)))
            .WithSummary("get_all_field_reports: list of all intelligence field reports.");

        fieldReports.MapGet("/search", (double lat, double lon, double? radiusMeters, bool? includeUnresolved,
                TrackContextService service, CancellationToken ct)
            => EndpointResultHelper.RunAsync(() => service.SearchFieldReportsNearAsync(
                lat, lon, radiusMeters ?? 800, includeUnresolved: includeUnresolved ?? false, ct: ct)))
            .WithSummary("search_field_reports (by coordinate): candidate reports near an arbitrary point.");
    }
}
