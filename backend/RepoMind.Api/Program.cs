using System.Text.Json;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient<EmbeddingService>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"]
        ?? throw new InvalidOperationException("Ollama:BaseUrl is required."));
    // Loading a local model on the first request can take longer than later requests.
    client.Timeout = TimeSpan.FromMinutes(2);
});

var app = builder.Build();

app.MapPost("/api/embeddings", async (
    EmbeddingRequest request,
    EmbeddingService service,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["text"] = ["Text must not be empty."]
        });
    }

    try
    {
        return Results.Ok(await service.GenerateAsync(request.Text, cancellationToken));
    }
    catch (HttpRequestException exception)
    {
        logger.LogWarning(exception, "Ollama embedding request failed.");
        return Results.Problem("Check that Ollama is running and embeddinggemma is installed. Also check the input length.",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("Ollama did not respond within two minutes.",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (JsonException exception)
    {
        logger.LogWarning(exception, "Ollama returned an invalid response.");
        return Results.Problem("Ollama returned an invalid embedding response.",
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();
