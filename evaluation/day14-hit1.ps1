param(
    [string]$BaseUrl = 'http://127.0.0.1:5000',
    [string]$ReportPath = 'evaluation/day14-hit1-report.md'
)

$ErrorActionPreference = 'Stop'
$fixture = Get-Content (Join-Path $PSScriptRoot 'day14-tasktrail.json') -Raw | ConvertFrom-Json
$base = $BaseUrl.TrimEnd('/')
$files = @(Invoke-RestMethod "$base/api/repositories/$($fixture.repositoryId)/files")
$expected = @($fixture.indexedPaths | Sort-Object) -join '|'
$actual = @($files.path | Sort-Object) -join '|'
if ($actual -ne $expected) { throw 'Indexed file paths differ from the labeled fixture.' }

$supported = @($fixture.questions | Where-Object { $null -ne $_.expectedPath })
$rows = foreach ($question in $supported) {
    $path = [uri]::EscapeDataString($question.expectedPath)
    $source = Invoke-RestMethod "$base/api/repositories/$($fixture.repositoryId)/files/content?path=$path"
    $line = @($source.lines | Where-Object { $_.number -eq $question.anchorLine })
    if ($line.Count -ne 1 -or $line[0].status -ne 'covered' -or
        !$line[0].content.Contains([string]$question.anchor)) {
        throw "Source anchor changed for $($question.id)."
    }

    $body = @{ query = $question.query; topK = 5 } | ConvertTo-Json
    $response = Invoke-RestMethod "$base/api/repositories/$($fixture.repositoryId)/hybrid/search" `
        -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 60
    $rank = $null
    for ($index = 0; $index -lt @($response.results).Count; $index++) {
        $candidate = $response.results[$index]
        if ($candidate.filePath -eq $question.expectedPath -and
            $candidate.startLine -le $question.anchorLine -and
            $candidate.endLine -ge $question.anchorLine -and
            $candidate.content.Contains([string]$question.anchor)) {
            $rank = $index + 1
            break
        }
    }
    [pscustomobject]@{
        Question = $question.id
        Rank = $rank
        FirstPath = if (@($response.results).Count) { $response.results[0].filePath } else { '' }
        Reranking = $response.rerankingStatus
    }
}

$hit1 = @($rows | Where-Object { $_.Rank -eq 1 }).Count
$hit5 = @($rows | Where-Object { $null -ne $_.Rank -and $_.Rank -le 5 }).Count
$lines = @(
    '# Day 14 - Hit@1 retrieval check',
    '',
    "- API: ``$base``; repository: $($fixture.repositoryId)",
    "- Hit@1: $hit1/$($supported.Count)",
    "- Hit@5: $hit5/$($supported.Count)",
    '',
    '| Question | Labeled chunk rank | First returned file | Reranking |',
    '|---|---:|---|---|'
)
foreach ($row in $rows) {
    $rankText = if ($null -eq $row.Rank) { 'miss' } else { [string]$row.Rank }
    $lines += "| $($row.Question) | $rankText | $($row.FirstPath) | $($row.Reranking) |"
}
$report = Join-Path (Get-Location) $ReportPath
Set-Content -LiteralPath $report -Value ($lines -join [Environment]::NewLine) -Encoding utf8
$rows | Format-Table -AutoSize
"Hit@1: $hit1/$($supported.Count); Hit@5: $hit5/$($supported.Count); report: $report"
