<#
.SYNOPSIS
    Concurrency test for POST /api/checkout: N simultaneous requests with the same Idempotency-Key and body
    must produce exactly one transaction (one 2C2P call) and the same invoiceNo for every 200 answer.

.DESCRIPTION
    Needs the API running (MySQL + Redis up). Works in Windows PowerShell 5.1 and PowerShell 7.

    Checks:
      1. Every answer is 200 or 409 (409 = "still in progress", allowed while the first request is running).
      2. Every 200 carries the same invoiceNo and redirectUrl, and at least one 200 came back.
      3. The customer has exactly one new transaction (GET /api/transactions/{customerId}).
      4. A later replay with the same key returns the same invoiceNo.
      5. The same key with a different body is rejected with 422.

.EXAMPLE
    .\scripts\concurrent-checkout.ps1                                   # 10 requests, hosted page (Alice, Single Session Pass)
    .\scripts\concurrent-checkout.ps1 -Count 50
    .\scripts\concurrent-checkout.ps1 -CustomerCardId 1                 # saved card flow (Do Payment)
    .\scripts\concurrent-checkout.ps1 -BaseUrl http://localhost:5080    # docker-compose
#>
param(
    [string]$BaseUrl = "https://{YOUR_PUBLIC_TUNNEL_DOMAIN}",
    [int]$CustomerId = 1,
    [int]$ProductId = 1,
    [Nullable[int]]$CustomerCardId = $null,
    [int]$Count = 20,
    [string]$IdempotencyKey = [guid]::NewGuid().ToString(),
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

# Windows PowerShell 5.1 (.NET Framework) allows only 2 connections per host by default, which would queue the
# requests instead of sending them together.
[System.Net.ServicePointManager]::DefaultConnectionLimit = [Math]::Max(100, $Count * 2)
[System.Net.ServicePointManager]::Expect100Continue = $false

$BaseUrl = $BaseUrl.TrimEnd('/')
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
$ngrokHeaders = @{ "ngrok-skip-browser-warning" = "true" }
$client.DefaultRequestHeaders.Add("ngrok-skip-browser-warning", "true")
$failures = New-Object System.Collections.Generic.List[string]

function New-CheckoutRequest([string]$key, [int]$productId) {
    $body = @{ customerId = $CustomerId; productId = $productId; customerCardId = $CustomerCardId } | ConvertTo-Json -Compress
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$BaseUrl/api/checkout")
    $request.Headers.Add("Idempotency-Key", $key)
    $request.Content = New-Object System.Net.Http.StringContent($body, [System.Text.Encoding]::UTF8, "application/json")
    return $request
}

function Read-Response($response) {
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $json = $null
    try { $json = $text | ConvertFrom-Json } catch { }
    return [pscustomobject]@{ Status = [int]$response.StatusCode; Json = $json; Text = $text }
}

function Get-TransactionCount {
    # ngrok (free plan) answers browser-like clients with an HTML warning page instead of forwarding the request, and
    # Windows PowerShell's User-Agent starts with "Mozilla". This header skips that page; other hosts ignore it.
    $list = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/transactions/$CustomerId" -Headers $ngrokHeaders
    # Anything but a JSON array (e.g. an HTML page) would be counted as 1 transaction and break the before/after check.
    if ($list -is [string]) { throw "GET /api/transactions/$CustomerId did not return JSON: $($list.Substring(0, [Math]::Min(200, $list.Length)))" }
    return @($list).Count
}

function Check([bool]$ok, [string]$message) {
    if ($ok) { Write-Host "  PASS  $message" -ForegroundColor Green }
    else { Write-Host "  FAIL  $message" -ForegroundColor Red; $failures.Add($message) }
}

$cardText = if ($null -eq $CustomerCardId) { "none (hosted page)" } else { "$CustomerCardId (saved card)" }
Write-Host "Checkout concurrency test"
Write-Host "  API:             $BaseUrl"
Write-Host "  Customer:        $CustomerId   Product: $ProductId   Card: $cardText"
Write-Host "  Requests:        $Count"
Write-Host "  Idempotency-Key: $IdempotencyKey"
Write-Host ""

$before = Get-TransactionCount

# Build every request first, then start them back to back so they hit the API together.
$requests = @(1..$Count | ForEach-Object { New-CheckoutRequest $IdempotencyKey $ProductId })
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$tasks = @($requests | ForEach-Object { $client.SendAsync($_) })
try {
    [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$tasks)
} catch {
    Write-Host "Some requests failed to complete: $($_.Exception.InnerException.Message)" -ForegroundColor Red
}
$watch.Stop()

$results = for ($i = 0; $i -lt $tasks.Count; $i++) {
    $task = $tasks[$i]
    if ($task.Status -ne [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
        [pscustomobject]@{ Request = $i + 1; Status = "ERROR"; InvoiceNo = $null; TxnStatus = $null; RedirectUrl = $null; Detail = $task.Exception.InnerException.Message }
        continue
    }
    $r = Read-Response $task.Result
    [pscustomobject]@{
        Request     = $i + 1
        Status      = $r.Status
        InvoiceNo   = if ($r.Json) { $r.Json.invoiceNo } else { $null }
        TxnStatus   = if ($r.Json) { $r.Json.status } else { $null }
        RedirectUrl = if ($r.Json) { $r.Json.redirectUrl } else { $null }
        Detail      = if ($r.Status -ne 200 -and $r.Json) { $r.Json.detail } else { $null }
    }
}

$results | Format-Table Request, Status, InvoiceNo, TxnStatus, Detail -AutoSize
Write-Host ("All {0} requests finished in {1:N0} ms" -f $Count, $watch.Elapsed.TotalMilliseconds)
$results | Group-Object Status | ForEach-Object { Write-Host ("  HTTP {0}: {1}" -f $_.Name, $_.Count) }
Write-Host ""

$ok = @($results | Where-Object { $_.Status -eq 200 })
$invoices = @($ok | Select-Object -ExpandProperty InvoiceNo -Unique)
$redirects = @($ok | ForEach-Object { "$($_.RedirectUrl)" } | Select-Object -Unique)
$after = Get-TransactionCount

Write-Host "Checks"
Check (@($results | Where-Object { $_.Status -ne 200 -and $_.Status -ne 409 }).Count -eq 0) "every response is 200 or 409"
Check ($ok.Count -ge 1) "at least one request succeeded ($($ok.Count) x 200)"
Check ($invoices.Count -le 1) "every 200 has the same invoiceNo ($($invoices -join ', '))"
Check ($redirects.Count -le 1) "every 200 has the same redirectUrl"
Check (($after - $before) -eq 1) "exactly one new transaction for customer $CustomerId (before $before, after $after)"

# Replay once everything has settled: must return the original response, not create a new payment.
$replay = Read-Response ($client.SendAsync((New-CheckoutRequest $IdempotencyKey $ProductId)).GetAwaiter().GetResult())
Check ($replay.Status -eq 200 -and $invoices.Count -eq 1 -and $replay.Json.invoiceNo -eq $invoices[0]) "replay with the same key returns the same invoiceNo ($($replay.Status) $($replay.Json.invoiceNo))"

$mismatch = Read-Response ($client.SendAsync((New-CheckoutRequest $IdempotencyKey ($ProductId + 1000))).GetAwaiter().GetResult())
Check ($mismatch.Status -eq 422) "same key with a different body is rejected with 422 (got $($mismatch.Status))"
Check ((Get-TransactionCount) -eq $after) "replay and mismatch created no transaction"

$client.Dispose()
Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "PASSED: $Count concurrent requests -> 1 transaction ($($invoices[0]))" -ForegroundColor Green
    exit 0
}
Write-Host "FAILED: $($failures.Count) check(s)" -ForegroundColor Red
exit 1
