using System.Text.Json;
using RepoMind.Api.Agents;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Tests;

// Partial evidence: agent results keep the cited part and name what the code does not establish.
public sealed class PartialEvidenceParsingTests
{
    private static readonly AgentEvidence Evidence = new("E1", "app/src/apiClient.ts", 1, 30, "code", "read_file");

    [Fact]
    public void Agent_gaps_are_trimmed_deduplicated_stripped_of_markers_and_capped()
    {
        using var json = JsonDocument.Parse("""
            {"summary":"s","findings":[],"coverage":"partial",
             "gaps":["Remote behavior [source:4]","", "Remote behavior", "Retries", "Logging", "Extra"]}
            """);
        Assert.Equal(["Remote behavior", "Retries", "Logging"], AgentSynthesis.Parse(json.RootElement, [Evidence], false).Gaps);
    }

    [Theory]
    [InlineData("""{"summary":"s","findings":[],"coverage":"partial","gaps":"one"}""")]
    [InlineData("""{"summary":"s","findings":[],"coverage":"partial","gaps":[5]}""")]
    public void Agent_rejects_malformed_gaps(string output)
    {
        using var json = JsonDocument.Parse(output);
        Assert.Throws<JsonException>(() => AgentSynthesis.Parse(json.RootElement, [Evidence], false));
    }

    [Theory]
    [InlineData("""{"summary":"s","findings":[],"gaps":["Remote side"],"coverage":"partial"}""", "partial", 1)]
    [InlineData("""{"summary":"s","findings":[],"gaps":[],"coverage":"full"}""", "full", 0)]
    [InlineData("""{"summary":"s","findings":[],"coverage":"none"}""", "none", 0)]
    // Legacy boolean output is still understood: true means full, false means none.
    [InlineData("""{"summary":"s","findings":[],"sufficient":true}""", "full", 0)]
    [InlineData("""{"summary":"s","findings":[],"sufficient":false}""", "none", 0)]
    [InlineData("""{"summary":"s","findings":[],"coverage":" Partial ","gaps":["Remote side"]}""", "partial", 1)]
    // No verdict at all: validated findings imply full coverage.
    [InlineData("""{"summary":"s","findings":[{"title":"t","detail":"d","evidenceId":"E1","startLine":2,"endLine":3}]}""", "full", 0)]
    public void Agent_synthesis_reads_coverage_and_gaps(string output, string coverage, int gaps)
    {
        using var json = JsonDocument.Parse(output);
        var parsed = AgentSynthesis.Parse(json.RootElement, [Evidence], false);
        Assert.Equal((coverage, gaps), (parsed.Coverage, parsed.Gaps.Count));
    }

    [Theory]
    [InlineData("""{"summary":"s","findings":[],"coverage":"maybe"}""")]
    [InlineData("""{"summary":"s","findings":[]}""")]
    public void Agent_synthesis_rejects_unknown_or_missing_coverage(string output)
    {
        using var json = JsonDocument.Parse(output);
        Assert.Throws<JsonException>(() => AgentSynthesis.Parse(json.RootElement, [Evidence], false));
    }

    [Fact]
    public void Over_long_summary_is_shortened_at_a_sentence_instead_of_failing()
    {
        var first = "The client sends the request without a timeout. " + new string('a', 300) + ".";
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            summary = first + " " + new string('b', 400), findings = Array.Empty<object>(), coverage = "partial", gaps = new[] { "Remote side" }
        }));
        var parsed = AgentSynthesis.Parse(json.RootElement, [Evidence], false);
        Assert.Equal(first, parsed.Summary);
        Assert.True(parsed.Summary.Length <= AgentSynthesis.MaxSummaryLength);
    }

    [Fact]
    public void Agent_schema_asks_for_gaps_before_the_coverage_verdict()
    {
        var request = AgentSynthesis.Request("system", "user", [Evidence], false, AgentOptions.From(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
        var schema = JsonSerializer.SerializeToElement(request.Schema);
        Assert.Equal(["summary", "findings", "gaps", "coverage"],
            schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }
}
