param(
    [Parameter(Mandatory = $true)][long]$RepositoryId,
    [string]$BaseUrl = 'http://localhost:5000'
)

$questions = Get-Content -Raw (Join-Path $PSScriptRoot 'day11-tasktrail.json') | ConvertFrom-Json
foreach ($item in $questions) {
    $body = @{ query = $item.query; topK = $item.topK } | ConvertTo-Json
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $result = Invoke-RestMethod -Uri "$BaseUrl/api/repositories/$RepositoryId/rag/answer" `
            -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 240
        $timer.Stop()
        $paths = @($result.sources | ForEach-Object filePath)
        $sourceIds = @($result.sources | ForEach-Object { [string]$_.chunkId })
        $markerIds = @([regex]::Matches($result.answer, '\[source:(\d+)\]') |
            ForEach-Object { $_.Groups[1].Value })
        $citationValid = $sourceIds.Count -eq $markerIds.Count -and
            (($sourceIds | Sort-Object) -join ',') -eq (($markerIds | Sort-Object) -join ',')
        [pscustomobject]@{
            query = $item.query
            topK = $item.topK
            status = $result.status
            expectedPath = $item.expectedPath
            paths = $paths -join ','
            expectedPathHit = if ($null -eq $item.expectedPath) { $null } else {
                [bool]@($paths | Where-Object { $_.EndsWith($item.expectedPath) }).Count
            }
            citationValid = $citationValid
            answer = $result.answer
            promptTokens = $result.promptTokens
            outputTokens = $result.generatedTokens
            durationMs = [math]::Round($timer.Elapsed.TotalMilliseconds)
        } | ConvertTo-Json -Compress
    } catch {
        $timer.Stop()
        [pscustomobject]@{
            query = $item.query
            topK = $item.topK
            error = $_.Exception.Message
            durationMs = [math]::Round($timer.Elapsed.TotalMilliseconds)
        } | ConvertTo-Json -Compress
    }
}
