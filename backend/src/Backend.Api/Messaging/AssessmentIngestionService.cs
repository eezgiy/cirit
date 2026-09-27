using System.Text.Json;
using Backend.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Messaging;

/// <summary>
/// Receiving side of the direct HTTP hand-off (POST /api/assessments): upserts the agent
/// service's verdict into TrackAssessments, latest-wins per track_id. This is the same upsert
/// logic the old Kafka-based AssessmentCompletedConsumer used to run off a topic.
/// </summary>
public sealed class AssessmentIngestionService(AppDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task UpsertAsync(AssessmentCompleted message, CancellationToken ct = default)
    {
        var existing = await db.TrackAssessments.FirstOrDefaultAsync(a => a.TrackId == message.TrackId, ct);
        var evidenceRefsJson = JsonSerializer.Serialize(message.EvidenceRefs, JsonOptions);

        if (existing is null)
        {
            db.TrackAssessments.Add(new TrackAssessmentEntity
            {
                TrackId = message.TrackId,
                Verdict = message.Verdict,
                Confidence = message.Confidence,
                Reasoning = message.Reasoning,
                EvidenceRefsJson = evidenceRefsJson,
                ModelVersion = message.ModelVersion,
                CreatedAtUtc = message.CreatedAtUtc,
            });
        }
        else
        {
            existing.Verdict = message.Verdict;
            existing.Confidence = message.Confidence;
            existing.Reasoning = message.Reasoning;
            existing.EvidenceRefsJson = evidenceRefsJson;
            existing.ModelVersion = message.ModelVersion;
            existing.CreatedAtUtc = message.CreatedAtUtc;
        }

        await db.SaveChangesAsync(ct);
    }
}
