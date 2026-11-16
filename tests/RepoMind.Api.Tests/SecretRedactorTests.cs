using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("const key = 'AKIAABCDEFGHIJKLMNOP';", "AKIAABCDEFGHIJKLMNOP")]
    [InlineData("token: ghp_abcdefghijklmnopqrstuvwxyz0123456789AB", "ghp_abcdefghijklmnopqrstuvwxyz0123456789AB")]
    [InlineData("const stripe = \"sk_live_1234567890abcdefXYZ\";", "sk_live_1234567890abcdefXYZ")]
    [InlineData("var password = \"hunter2hunter2\";", "hunter2hunter2")]
    [InlineData("\"apiKey\": \"abcd1234efgh5678\",", "abcd1234efgh5678")]
    [InlineData("Host=db;Username=app;Password=Sup3rS3cret;", "Sup3rS3cret")]
    [InlineData("auth = 'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U'",
        "eyJhbGciOiJIUzI1NiJ9")]
    public void High_confidence_secret_values_are_replaced(string line, string secret)
    {
        var result = SecretRedactor.Redact(line);
        Assert.DoesNotContain(secret, result.Content);
        Assert.Contains(SecretRedactor.Marker, result.Content);
        Assert.True(result.Redactions >= 1);
    }

    [Fact]
    public void Private_key_blocks_keep_their_line_count()
    {
        var content = "const a = 1;\n-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA\nabc\n-----END RSA PRIVATE KEY-----\nconst b = 2;";
        var result = SecretRedactor.Redact(content);
        var lines = result.Content.Split('\n');
        Assert.Equal(content.Split('\n').Length, lines.Length);
        Assert.Equal("const a = 1;", lines[0]);
        Assert.All(lines[1..5], line => Assert.Equal(SecretRedactor.Marker, line));
        Assert.Equal("const b = 2;", lines[5]);
        Assert.Equal(4, result.Redactions);
    }

    [Theory]
    [InlineData("headers.set('Authorization', `Bearer ${token}`);")]
    [InlineData("const password = process.env.DB_PASSWORD;")]
    [InlineData("password: 'your-password-here'")]
    [InlineData("if (password === confirmPassword) return true;")]
    [InlineData("const apiKey = '${API_KEY}';")]
    [InlineData("const secret = 'short';")]
    public void Ordinary_code_and_placeholders_are_unchanged(string line)
    {
        var result = SecretRedactor.Redact(line);
        Assert.Equal(line, result.Content);
        Assert.Equal(0, result.Redactions);
    }
}
