using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Pgvector.Npgsql;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

var builder = WebApplication.CreateBuilder(args);
// Local runs may not have permission to write to the Windows Event Log.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = RepositoryZipReader.MaxUploadBytes + 256 * 1024);

builder.Services.AddHttpClient<IEmbeddingService, EmbeddingService>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"]
        ?? throw new InvalidOperationException("Ollama:BaseUrl is required."));
    // Loading a local model on the first request can take longer than later requests.
    client.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddHttpClient<ILlmService, OllamaLlmService>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"]
        ?? throw new InvalidOperationException("Ollama:BaseUrl is required."));
    var timeoutSeconds = builder.Configuration.GetValue<int>("Ollama:ChatTimeoutSeconds");
    if (timeoutSeconds <= 0)
        throw new InvalidOperationException("Ollama:ChatTimeoutSeconds must be positive.");
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
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
builder.Services.AddSingleton<RepositoryZipReader>();
builder.Services.AddScoped<RepositoryUploadService>();
builder.Services.AddScoped<RepositorySearchStore>();

var app = builder.Build();

app.MapPost("/api/llm/chat", async (
    LlmChatRequest request,
    ILlmService service,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var errors = request.Validate();
    if (errors.Count > 0) return Results.ValidationProblem(errors);

    try
    {
        return Results.Ok(await service.ChatAsync(request.Prompt!.Trim(), cancellationToken));
    }
    catch (HttpRequestException exception)
    {
        logger.LogWarning(exception, "Ollama chat request failed.");
        return Results.Problem("Check that Ollama is running and the configured chat model is installed.",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("Ollama chat request timed out.",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (JsonException exception)
    {
        logger.LogWarning(exception, "Ollama returned an invalid chat response.");
        return Results.Problem("Ollama returned an invalid chat response.",
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/embeddings", async (
    EmbeddingRequest request,
    IEmbeddingService service,
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
    IEmbeddingService service,
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
    IEmbeddingService service,
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

app.MapPost("/api/repositories/upload", async (
    HttpRequest request,
    RepositoryUploadService upload,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
        return Results.Problem("Send multipart/form-data with one field named file.", statusCode: 415);
    if (request.ContentLength > RepositoryZipReader.MaxUploadBytes + 256 * 1024)
        return Results.Problem("Upload exceeds the 5 MB ZIP limit.", statusCode: 413);

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromMinutes(30));
    try
    {
        var form = await request.ReadFormAsync(timeout.Token);
        if (form.Files.Count != 1 || form.Files[0].Name != "file")
            return Results.Problem("Provide exactly one ZIP file in the file field.", statusCode: 400);
        var file = form.Files[0];
        if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return Results.Problem("Only .zip files are supported.", statusCode: 415);
        if (file.Length == 0)
            return Results.Problem("ZIP file is empty.", statusCode: 400);
        if (file.Length > RepositoryZipReader.MaxUploadBytes)
            return Results.Problem("Upload exceeds the 5 MB ZIP limit.", statusCode: 413);

        await using var fileStream = file.OpenReadStream();
        using var zipBytes = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = await fileStream.ReadAsync(block, timeout.Token)) > 0)
        {
            if (zipBytes.Length + read > RepositoryZipReader.MaxUploadBytes)
                return Results.Problem("Upload exceeds the 5 MB ZIP limit.", statusCode: 413);
            zipBytes.Write(block, 0, read);
        }
        zipBytes.Position = 0;

        return Results.Ok(await upload.UploadAsync(file.FileName, zipBytes, timeout.Token));
    }
    catch (UnsafeZipPathException)
    {
        return Results.Problem("ZIP contains an absolute or traversal path.", statusCode: 400);
    }
    catch (InvalidDataException)
    {
        return Results.Problem("ZIP is invalid or contains no supported source files.", statusCode: 400);
    }
    catch (NotSupportedException)
    {
        return Results.Problem("ZIP uses an unsupported format.", statusCode: 400);
    }
    catch (IOException)
    {
        return Results.Problem("ZIP could not be read.", statusCode: 400);
    }
    catch (NpgsqlException)
    {
        logger.LogWarning("Repository upload could not reach PostgreSQL.");
        return Results.Problem("PostgreSQL is unavailable or the Day 6 migration is missing.", statusCode: 503);
    }
    catch (HttpRequestException)
    {
        logger.LogWarning("Repository upload could not reach Ollama.");
        return Results.Problem("Ollama could not generate an embedding.", statusCode: 502);
    }
    catch (JsonException)
    {
        logger.LogWarning("Ollama returned an invalid embedding during repository upload.");
        return Results.Problem("Ollama returned an invalid embedding.", statusCode: 502);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("Repository upload timed out.", statusCode: 504);
    }
});

app.MapPost("/api/repositories/{repositoryId}/search", async (
    string repositoryId,
    RepositorySearchRequest request,
    IEmbeddingService embeddings,
    RepositorySearchStore store,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!long.TryParse(repositoryId, out var parsedRepositoryId))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["repositoryId"] = ["Repository ID must be a positive integer."]
        });
    var errors = request.Validate(parsedRepositoryId);
    if (errors.Count > 0) return Results.ValidationProblem(errors);

    try
    {
        if (!await store.ExistsAsync(parsedRepositoryId, cancellationToken))
            return Results.Problem("Repository not found.", statusCode: 404);
        if (!await store.HasChunksAsync(parsedRepositoryId, cancellationToken))
            return Results.Ok(new RepositorySearchResponse(parsedRepositoryId, []));

        var query = await embeddings.GenerateAsync(request.Query!.Trim(), cancellationToken);
        var results = await store.SearchAsync(parsedRepositoryId, query.Model, query.Embedding,
            request.TopK!.Value, cancellationToken);
        return Results.Ok(new RepositorySearchResponse(parsedRepositoryId, results));
    }
    catch (NpgsqlException)
    {
        logger.LogWarning("Repository search could not reach PostgreSQL.");
        return Results.Problem("PostgreSQL is unavailable or the Day 6 tables are missing.", statusCode: 503);
    }
    catch (HttpRequestException)
    {
        logger.LogWarning("Repository search could not reach Ollama.");
        return Results.Problem("Ollama could not generate the query embedding.", statusCode: 502);
    }
    catch (JsonException)
    {
        logger.LogWarning("Ollama returned an invalid query embedding.");
        return Results.Problem("Ollama returned an invalid embedding.", statusCode: 502);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("Repository search timed out.", statusCode: 504);
    }
});

app.Run();
