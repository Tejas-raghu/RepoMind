using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RepoMind.Api.Services;

public sealed class CloudflareAiException(string message, int statusCode = 502) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class CloudflareAiClient(HttpClient httpClient, IConfiguration configuration)
{
    private readonly string accountId = ValidAccount(configuration["Cloudflare:AccountId"]);
    private readonly string token = configuration["Cloudflare:ApiToken"] is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Cloudflare:ApiToken is required in environment variables or User Secrets.");

    public async Task<JsonDocument> RunAsync(string model, object input, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"client/v4/accounts/{accountId}/ai/run/{model}")
            { Content = JsonContent.Create(input) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try { response = await httpClient.SendAsync(request, cancellationToken); }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), cancellationToken);
                continue;
            }
            catch (HttpRequestException)
            {
                throw new CloudflareAiException("Cloudflare Workers AI could not be reached.");
            }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var code = await ErrorCodeAsync(response, cancellationToken);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        if (code == 3040 && attempt < 2)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), cancellationToken);
                            continue;
                        }
                        throw new CloudflareAiException(code == 3036
                            ? "Cloudflare Workers AI daily free quota is exhausted."
                            : "Cloudflare Workers AI is rate limited or out of capacity.", 429);
                    }
                    if (((int)response.StatusCode is 408 or >= 500) && attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), cancellationToken);
                        continue;
                    }
                    throw new CloudflareAiException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "Cloudflare Workers AI rejected the account or token. Check its permissions and model access."
                        : $"Cloudflare Workers AI request failed (HTTP {(int)response.StatusCode}).");
                }
                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
                    !root.TryGetProperty("result", out var result) || result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    throw new JsonException("Cloudflare returned an invalid Workers AI envelope.");
                return JsonDocument.Parse(result.GetRawText());
            }
        }
        throw new CloudflareAiException("Cloudflare Workers AI did not respond after bounded retries.");
    }

    private static async Task<int?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("errors", out var errors)) return null;
            return errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0 &&
                errors[0].ValueKind == JsonValueKind.Object &&
                errors[0].TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt32(out var value)
                ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static string ValidAccount(string? value) =>
        value is { Length: 32 } && value.All(Uri.IsHexDigit) ? value :
        throw new InvalidOperationException("Cloudflare:AccountId must be the 32-character account ID.");
}
