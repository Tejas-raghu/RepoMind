param(
    [Parameter(Mandatory = $true)][long]$RepositoryId,
    [string]$BaseUrl = 'http://localhost:5000',
    [ValidateRange(1, 5)][int]$TopK = 3,
    [ValidateRange(1, 240)][int]$RequestTimeoutSeconds = 120,
    [string]$Dataset,
    [string]$OutputPath
)

if (-not $Dataset) { $Dataset = Join-Path $PSScriptRoot 'day95c-tasktrail.json' }
$questions = (Get-Content -LiteralPath $Dataset -Raw | ConvertFrom-Json).questions
$rows = foreach ($item in $questions) {
    $body = @{ query = $item.query; topK = $TopK } | ConvertTo-Json
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-RestMethod -Uri "$BaseUrl/api/repositories/$RepositoryId/rag/verify" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec $RequestTimeoutSeconds
        $timer.Stop()
        [pscustomobject]@{
            Expected = $item.expected
            Query = $item.query
            Status = $response.status
            Answerable = $response.answerable
            EvidencePaths = (($response.sources | ForEach-Object filePath) -join '|')
            ExpectedPathMatched = if ($item.expected -eq 'supported') {
                @($response.sources | Where-Object {
                    $_.filePath.EndsWith($item.expectedPath, [StringComparison]::OrdinalIgnoreCase)
                }).Count -gt 0
            } else { $null }
            VerifierMs = $response.verifierDurationMs
            TotalMs = [math]::Round($timer.Elapsed.TotalMilliseconds)
            RerankingStatus = $response.rerankingStatus
        }
    } catch {
        $timer.Stop()
        [pscustomobject]@{
            Expected = $item.expected
            Query = $item.query
            Status = 'request_failed'
            Answerable = $false
            EvidencePaths = ''
            ExpectedPathMatched = $false
            VerifierMs = $null
            TotalMs = [math]::Round($timer.Elapsed.TotalMilliseconds)
            RerankingStatus = ''
        }
    }
}
$rows | Format-Table -AutoSize
$supported = @($rows | Where-Object Expected -eq 'supported')
$unsupported = @($rows | Where-Object Expected -eq 'insufficient_evidence')
$measured = @($rows | Where-Object { $_.Status -ne 'request_failed' })
$verifierTimes = @($rows | Where-Object { $null -ne $_.VerifierMs })
$summary = [pscustomobject]@{
    SupportedAcceptance = "$( @($supported | Where-Object { $_.Status -eq 'supported' -and $_.ExpectedPathMatched }).Count )/$($supported.Count)"
    UnsupportedFalseAccept = "$( @($unsupported | Where-Object Status -eq 'supported').Count )/$($unsupported.Count)"
    Uncertain = @($rows | Where-Object Status -eq 'uncertain').Count
    VerificationUnavailable = @($rows | Where-Object Status -eq 'verification_unavailable').Count
    RequestsCompleted = $measured.Count
    AverageVerifierMs = if ($verifierTimes.Count) { [math]::Round(($verifierTimes | Measure-Object VerifierMs -Average).Average) } else { $null }
}
$summary | Format-List
if ($OutputPath) {
    [pscustomobject]@{
        MeasuredAtUtc = [DateTime]::UtcNow.ToString('o')
        RepositoryId = $RepositoryId
        TopK = $TopK
        Dataset = $Dataset
        Rows = @($rows)
        Summary = $summary
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
}
