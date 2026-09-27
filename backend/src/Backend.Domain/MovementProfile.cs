namespace Backend.Domain;

public enum MovementStatus
{
    Stationary,
    Approaching,
    Receding,
    Orbiting,
    Moving,
}

public sealed record TrackPointSample(TimeOnly Time, GeoPoint Position);

public sealed record MovementProfile(
    string TrackId,
    TimeOnly FirstSeen,
    TimeOnly LastSeen,
    GeoPoint LatestPosition,
    MovementStatus Status,
    double StatusDurationMinutes,
    double CurrentSpeedMetersPerSecond,
    double AverageSpeedMetersPerSecond,
    double DistanceToBaseKmNow,
    double DistanceToBaseKmAtWindowStart,
    double OrbitScore);
