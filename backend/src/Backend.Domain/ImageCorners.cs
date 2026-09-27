namespace Backend.Domain;

/// <summary>
/// Four WGS84 corners of a north-up, non-rotated aerial image, as given in image_meta.json.
/// </summary>
public sealed record ImageCorners(GeoPoint TopLeft, GeoPoint TopRight, GeoPoint BottomLeft, GeoPoint BottomRight);
