namespace Backend.Api;

/// <summary>Resolves a configured path relative to the content root, unless it's already absolute.</summary>
public static class AppPaths
{
    public static string Resolve(IConfiguration configuration, IHostEnvironment environment, string configKey,
        string? defaultValue = null)
    {
        var configured = configuration[configKey] ?? defaultValue
            ?? throw new InvalidOperationException($"{configKey} is not configured.");
        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
    }
}
