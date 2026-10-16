using System.Text.Json;
using Npgsql;
using Pgvector.Npgsql;
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
builder.Services.AddSingleton(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("RepoMind")
        ?? throw new InvalidOperationException("ConnectionStrings:RepoMind is required.");
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.UseVector();
    return dataSourceBuilder.Build();
});
builder.Services.AddScoped<EmbeddingStore>();

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

app.MapPost("/api/embeddings/store", async (
    EmbeddingRequest request,
    EmbeddingService service,
    EmbeddingStore store,
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
        var result = await service.GenerateAsync(request.Text, cancellationToken);
        var id = await store.SaveAsync(request.Text, result.Model, result.Embedding, cancellationToken);
        return Results.Ok(new StoredEmbeddingResponse(id, result.Model, result.Dimensions));
    }
    catch (NpgsqlException exception)
    {
        logger.LogWarning(exception, "Could not store the embedding.");
        return Results.Problem("Check that PostgreSQL is running and the Day 4 table exists.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException exception)
    {
        logger.LogWarning(exception, "Ollama embedding request failed.");
        return Results.Problem("Check that Ollama is running and embeddinggemma is installed. Also check the input length.",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("Ollama or PostgreSQL did not respond within two minutes.",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (JsonException exception)
    {
        logger.LogWarning(exception, "Ollama returned an invalid response.");
        return Results.Problem("Ollama returned an invalid embedding response.",
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/embeddings/search", async (
    EmbeddingRequest request,
    EmbeddingService service,
    EmbeddingStore store,
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
        var query = await service.GenerateAsync(request.Text, cancellationToken);
        return Results.Ok(await store.SearchAsync(query.Model, query.Embedding, cancellationToken));
    }
    catch (NpgsqlException exception)
    {
        logger.LogWarning(exception, "Could not search embeddings.");
        return Results.Problem("Check that PostgreSQL is running and the Day 4 table exists.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException exception)
    {
        logger.LogWarning(exception, "Ollama embedding request failed.");
        return Results.Problem("Check that Ollama is running and embeddinggemma is installed. Also check the input length.",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("Ollama or PostgreSQL did not respond within two minutes.",
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
