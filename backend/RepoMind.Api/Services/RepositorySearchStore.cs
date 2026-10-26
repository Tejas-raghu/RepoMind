using Npgsql;
using Pgvector;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class RepositorySearchStore(NpgsqlDataSource dataSource, IConfiguration configuration)
{
    // Provisional cutoff for the local sample repositories; tune with labeled queries.
    public const double DefaultMinimumSimilarity = 0.35;
    private readonly double minimumSimilarity = configuration.GetValue<double?>(
        "RepositorySearch:MinimumSimilarity") ?? DefaultMinimumSimilarity;

    public async Task<bool> ExistsAsync(long repositoryId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM repositories WHERE id = $1)");
        command.Parameters.AddWithValue(repositoryId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Repository existence query returned no result."));
    }

    public async Task<bool> HasChunksAsync(long repositoryId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM repository_chunks WHERE repository_id = $1)");
        command.Parameters.AddWithValue(repositoryId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Repository chunk query returned no result."));
    }

    public async Task<IReadOnlyList<RepositorySearchResult>> SearchAsync(
        long repositoryId, string model, float[] embedding, int topK,
        CancellationToken cancellationToken, double? minimumSimilarityOverride = null)
        => await SearchVectorAsync(repositoryId, model, embedding, topK,
            minimumSimilarityOverride ?? minimumSimilarity, cancellationToken);

    public async Task<IReadOnlyList<RepositorySearchResult>> SearchVectorCandidatesAsync(
        long repositoryId, string model, float[] embedding, int topK,
        CancellationToken cancellationToken)
        => await SearchVectorAsync(repositoryId, model, embedding, topK, null, cancellationToken);

    private async Task<IReadOnlyList<RepositorySearchResult>> SearchVectorAsync(
        long repositoryId, string model, float[] embedding, int topK,
        double? cutoff, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, source_path, start_line, end_line, content, " +
            "1 - (embedding <=> $1) AS similarity " +
            "FROM repository_chunks " +
            "WHERE repository_id = $2 AND model = $3 " +
            (cutoff is null ? "" : "AND 1 - (embedding <=> $1) >= $5 ") +
            "ORDER BY embedding <=> $1 ASC, id ASC LIMIT $4");
        command.Parameters.AddWithValue(new Vector(embedding));
        command.Parameters.AddWithValue(repositoryId);
        command.Parameters.AddWithValue(model);
        command.Parameters.AddWithValue(topK);
        if (cutoff is not null) command.Parameters.AddWithValue(cutoff.Value);

        var results = new List<RepositorySearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new RepositorySearchResult(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetDouble(5)));
        }
        return results;
    }

    public async Task<IReadOnlyList<RepositorySearchResult>> SearchLexicalCandidatesAsync(
        long repositoryId, string model, float[] embedding, IReadOnlyList<string> terms,
        int topK, CancellationToken cancellationToken)
    {
        if (terms.Count == 0) return [];
        // Terms are reduced to letters, digits and underscores before to_tsquery sees them.
        var tsQuery = string.Join(" | ", terms);
        await using var command = dataSource.CreateCommand(
            "WITH query AS (SELECT to_tsquery('simple', $3) AS tsq) " +
            "SELECT c.id, c.source_path, c.start_line, c.end_line, c.content, " +
            "1 - (c.embedding <=> $4) AS similarity, " +
            "ts_rank_cd(to_tsvector('simple', c.content || ' ' || c.source_path), query.tsq) AS text_rank, " +
            "(SELECT count(*) FROM unnest($5::text[]) term " +
            " WHERE strpos(lower(c.content), term) > 0 OR strpos(lower(c.source_path), term) > 0) AS exact_hits, " +
            "(c.source_path ~* '\\.(cs|ts|tsx|js|jsx|vue)$') AS is_code " +
            "FROM repository_chunks c CROSS JOIN query " +
            "WHERE c.repository_id = $1 AND c.model = $2 " +
            "AND (to_tsvector('simple', c.content || ' ' || c.source_path) @@ query.tsq " +
            "OR EXISTS (SELECT 1 FROM unnest($5::text[]) term " +
            " WHERE strpos(lower(c.content), term) > 0 OR strpos(lower(c.source_path), term) > 0)) " +
            "ORDER BY is_code DESC, exact_hits DESC, text_rank DESC, c.id ASC LIMIT $6");
        command.Parameters.AddWithValue(repositoryId);
        command.Parameters.AddWithValue(model);
        command.Parameters.AddWithValue(tsQuery);
        command.Parameters.AddWithValue(new Vector(embedding));
        command.Parameters.AddWithValue(terms.ToArray());
        command.Parameters.AddWithValue(topK);

        var results = new List<RepositorySearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new RepositorySearchResult(reader.GetInt64(0), reader.GetString(1),
                reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4), reader.GetDouble(5)));
        }
        return results;
    }
}
