using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

internal sealed class NoOpReranker : IRerankingService
{
    public Task<IReadOnlyDictionary<long, double>> ScoreAsync(string query,
        IReadOnlyList<HybridSearchResult> candidates, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Reranking should be disabled in this test.");
}

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = "RepoMind.Tests";
    public string ContentRootPath { get; set; } = ".";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
