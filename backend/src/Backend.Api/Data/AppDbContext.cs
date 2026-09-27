using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ImageMetaEntity> Images => Set<ImageMetaEntity>();
    public DbSet<DetectionEntity> Detections => Set<DetectionEntity>();
    public DbSet<TrackPointEntity> TrackPoints => Set<TrackPointEntity>();
    public DbSet<DetectionTrackMatchEntity> DetectionTrackMatches => Set<DetectionTrackMatchEntity>();
    public DbSet<ZoneEntity> Zones => Set<ZoneEntity>();
    public DbSet<BaseLocationEntity> BaseLocations => Set<BaseLocationEntity>();
    public DbSet<FieldReportEntity> FieldReports => Set<FieldReportEntity>();
    public DbSet<TrackAssessmentEntity> TrackAssessments => Set<TrackAssessmentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImageMetaEntity>(e =>
        {
            e.HasKey(x => x.ImageId);
            e.HasMany(x => x.Detections).WithOne(x => x.Image).HasForeignKey(x => x.ImageId);
        });

        modelBuilder.Entity<DetectionEntity>(e =>
        {
            e.HasIndex(x => x.ImageId);
            e.HasOne(x => x.TrackMatch).WithOne(x => x.Detection!)
                .HasForeignKey<DetectionTrackMatchEntity>(x => x.DetectionId);
        });

        modelBuilder.Entity<TrackPointEntity>(e =>
        {
            e.HasIndex(x => new { x.TrackId, x.Time }).IsUnique();
            e.HasIndex(x => x.Time);
        });

        modelBuilder.Entity<DetectionTrackMatchEntity>(e =>
        {
            e.HasIndex(x => x.TrackId);
        });

        modelBuilder.Entity<ZoneEntity>(e => e.HasKey(x => x.Name));

        modelBuilder.Entity<TrackAssessmentEntity>(e => e.HasIndex(x => x.TrackId).IsUnique());
    }
}
