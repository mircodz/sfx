namespace Sfx.Web;

/// <summary>An action the browser ran, followed until the cluster has made the change.</summary>
/// <param name="State">"running", "done" or "failed".</param>
/// <param name="Detail">How far it got, or why it failed; null until the first look at the cluster.</param>
/// <param name="Started">UTC.</param>
/// <param name="Finished">UTC; null while running.</param>
/// <param name="Progress">How far it is, 0 to 1, while that can be counted (an upload); null otherwise.</param>
public sealed record OperationDto(
    string Id, string ActionId, string Title, DateTime Started, string State, string? Detail, DateTime? Finished, double? Progress = null);

internal static class ApiOperations
{
    public static void Map(IEndpointRouteBuilder api)
    {
        // The current session's last 50 operations, newest first; a context switch starts a fresh list.
        api.MapGet("/operations", (UiSession s) => s.GetOperations());
    }
}
