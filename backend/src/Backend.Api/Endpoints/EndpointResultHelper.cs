using System.Text.Json;

namespace Backend.Api.Endpoints;

internal static class EndpointResultHelper
{
    public static async Task<IResult> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (FormatException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = $"Malformed JSON: {ex.Message}" });
        }
    }
}
