using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace RepoMind.Api.Tests;

// Hosted-demo behavior: health check, same-origin UI, hidden learning endpoints and
// rate limits. Each test builds its own app so limiter counters never leak between tests.
public sealed class PublicDemoTests : IAsyncLifetime
{
    private readonly List<long> repositories = [];

    [Fact]
    public async Task Local_mode_keeps_learning_endpoints_and_reports_no_demo()
    {
        await using var factory = new HostingFactory(new() { ["Demo:Enabled"] = "false" });
        var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("nosniff", health.Headers.GetValues("X-Content-Type-Options").Single());
        var info = await client.GetFromJsonAsync<JsonElement>("/api/system/info");
        Assert.False(info.GetProperty("publicDemo").GetBoolean());
        // Model-free validation proves the endpoint is reachable (400, not 404).
        var chat = await client.PostAsJsonAsync("/api/llm/chat", new { prompt = "" });
        Assert.Equal(HttpStatusCode.BadRequest, chat.StatusCode);
    }

    [Fact]
    public async Task Demo_hides_learning_endpoints_but_keeps_the_ui_api()
    {
        await using var factory = new HostingFactory(Demo());
        var client = factory.CreateClient();

        var info = await client.GetFromJsonAsync<JsonElement>("/api/system/info");
        Assert.True(info.GetProperty("publicDemo").GetBoolean());
        foreach (var path in new[] { "/api/llm/chat", "/api/embeddings", "/api/embeddings/search",
                     "/api/repositories/1/rag/answer", "/api/repositories/1/hybrid/search" })
        {
            var response = await client.PostAsJsonAsync(path, new { prompt = "x", text = "x", query = "x" });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("disabled in the public demo", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/repositories")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Asks_are_limited_per_ip_and_by_a_shared_daily_cap()
    {
        await using var factory = new HostingFactory(Demo(("AsksPerIpPerHour", "2"), ("AsksPerDay", "3")));
        var client = factory.CreateClient();
        var id = await UploadAsync(client, "203.0.113.1");

        // Unsupported tasks finish without a model call, so only the limiter is exercised.
        Assert.Equal(HttpStatusCode.OK, await AskAsync(client, id, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await AskAsync(client, id, "203.0.113.1"));
        using var limited = await AskRawAsync(client, id, "203.0.113.1");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) > 0);
        Assert.Contains("public demo limit", await limited.Content.ReadAsStringAsync());

        // The per-IP rejection did not consume the shared cap: another visitor gets the
        // third ask, then the daily cap stops everyone.
        Assert.Equal(HttpStatusCode.OK, await AskAsync(client, id, "198.51.100.7"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await AskAsync(client, id, "198.51.100.7"));
        // Browsing is never limited.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/repositories/{id}/files")).StatusCode);
    }

    [Fact]
    public async Task Uploads_are_limited_per_ip()
    {
        await using var factory = new HostingFactory(Demo(("UploadsPerIpPerDay", "1")));
        var client = factory.CreateClient();
        await UploadAsync(client, "203.0.113.9");
        using var second = await PostZipAsync(client, "203.0.113.9");
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task Serves_the_built_ui_with_cache_headers_when_wwwroot_exists()
    {
        var webRoot = Directory.CreateTempSubdirectory("repomind-wwwroot-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
            await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>RepoMind</title>");
            await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "index-abc123.js"), "console.log(1)");
            await using var factory = new HostingFactory(Demo(), webRoot);
            var client = factory.CreateClient();

            var page = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("<title>RepoMind</title>", await page.Content.ReadAsStringAsync());
            Assert.True(page.Headers.CacheControl?.NoCache);
            Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
            var asset = await client.GetAsync("/assets/index-abc123.js");
            Assert.Equal(TimeSpan.FromDays(365), asset.Headers.CacheControl?.MaxAge);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/repositories")).StatusCode);
        }
        finally { Directory.Delete(webRoot, recursive: true); }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var dataSource = TestDatabase.Create();
        await TestDatabase.CleanupAsync(dataSource, repositories);
    }

    private static Dictionary<string, string?> Demo(params (string Key, string Value)[] limits)
    {
        var settings = new Dictionary<string, string?> { ["Demo:Enabled"] = "true", ["Demo:BehindProxy"] = "true" };
        foreach (var (key, value) in limits) settings[$"Demo:{key}"] = value;
        return settings;
    }

    private async Task<long> UploadAsync(HttpClient client, string ip)
    {
        using var response = await PostZipAsync(client, ip);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = JsonDocument.Parse(body).RootElement.GetProperty("repositoryId").GetInt64();
        repositories.Add(id);
        return id;
    }

    private static async Task<HttpResponseMessage> PostZipAsync(HttpClient client, string ip)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(
            RepositoryUploadIntegrationTests.Zip(("demo/src/a.ts", "export const a = 1\n")).ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", $"demo-test-{Guid.NewGuid():N}.zip");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/repositories/upload") { Content = form };
        request.Headers.Add("X-Forwarded-For", ip);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private static async Task<HttpStatusCode> AskAsync(HttpClient client, long id, string ip)
    {
        using var response = await AskRawAsync(client, id, ip);
        return response.StatusCode;
    }

    private static Task<HttpResponseMessage> AskRawAsync(HttpClient client, long id, string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/repositories/{id}/agents/run")
        {
            Content = JsonContent.Create(new { task = "Delete the AuthController file" })
        };
        request.Headers.Add("X-Forwarded-For", ip);
        return client.SendAsync(request);
    }

    private sealed class HostingFactory(Dictionary<string, string?> settings, string? webRoot = null)
        : RepoMindApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            if (webRoot is not null) builder.UseWebRoot(webRoot);
        }
    }
}
