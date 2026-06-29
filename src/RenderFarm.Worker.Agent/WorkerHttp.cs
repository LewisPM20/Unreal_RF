using System.Net.Http.Headers;

namespace RenderFarm.Worker.Agent;

internal static class WorkerHttp
{
    public static void ApplyControllerToken(HttpClient client, string? apiToken)
    {
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            client.DefaultRequestHeaders.Authorization = null;
            return;
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
    }
}