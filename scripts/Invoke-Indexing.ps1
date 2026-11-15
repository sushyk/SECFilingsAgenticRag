#Requires -Version 7.3
<#
.SYNOPSIS
Requests the indexing phase from the running indexing Function and prints its report.
.DESCRIPTION
Exit codes: 0 done, 1 a document failed, 2 the Function cannot be reached, 3 another indexing run is active,
4 the connection ended before a report arrived (indexing may still be running).
#>
param(
    [switch]   $Force,
    [string[]] $Tickers,
    [int]      $TimeoutSec = 3600,
    [string]   $BaseUrl
)

if (-not $BaseUrl) {
    # Parsed, not regex-matched: the description also carries the storage connection strings, whose Azurite URLs come first.
    try {
        $resource = (aspire describe indexing --format json --non-interactive 2>$null | Out-String | ConvertFrom-Json).resources |
            Where-Object displayName -eq 'indexing'
        $BaseUrl = ($resource.urls | Where-Object name -eq 'http' | Select-Object -First 1).url
    }
    catch { }
}
if (-not $BaseUrl) {
    Write-Error 'Cannot find the indexing endpoint. Start the AppHost, or pass -BaseUrl (the "Index: [POST]" line in the indexing log).' -ErrorAction Continue
    exit 2
}

$body = @{ tickers = $Tickers; force = [bool]$Force } | ConvertTo-Json
try {
    $report = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/index" -ContentType 'application/json' -Body $body -TimeoutSec $TimeoutSec
}
catch {
    if ($_.Exception.Response?.StatusCode -eq [System.Net.HttpStatusCode]::Conflict) {
        Write-Error 'Indexing already running. Wait for it to finish and retry.' -ErrorAction Continue
        exit 3
    }
    if ($_.Exception.HttpRequestError -eq 'ConnectionError') {
        Write-Error "Cannot reach the indexing Function at $BaseUrl." -ErrorAction Continue
        exit 2
    }
    # The Functions host proxy drops requests after about 5 minutes, but the run itself continues and finishes.
    Write-Error "No report received ($($_.Exception.Message)). Indexing may still be running: watch 'aspire logs indexing', then re-run this script; finished filings report as skipped." -ErrorAction Continue
    exit 4
}

$report.documents | Format-Table documentId, status, chunkCount, duration, error -AutoSize | Out-Host
"Indexing: indexed=$($report.indexed) skipped=$($report.skipped) failed=$($report.failed) in $(([timespan]$report.duration).ToString('hh\:mm\:ss'))"
exit ($report.documents.status -contains 'failed' ? 1 : 0)
