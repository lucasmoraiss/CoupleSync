#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Pilot seed script for CoupleSync — creates 2 users, 1 couple, sample transactions,
    a goal, and a device token via the live backend API.

.PARAMETER BaseUrl
    Base URL of the running CoupleSync API. Default: http://localhost:5000

.EXAMPLE
    .\seed.ps1 -BaseUrl http://localhost:5000

TRACES: T-029
#>
param(
    [string]$BaseUrl = "http://localhost:5000"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"

# ── Helpers ── #
function Write-Step([string]$msg) { Write-Host "`n[STEP] $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "  [OK] $msg"   -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "  [WARN] $msg" -ForegroundColor Yellow }
function Write-Fail([string]$msg) { Write-Host "  [FAIL] $msg" -ForegroundColor Red }

function Invoke-Api {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Body,
        [string]$Token
    )
    $uri = "$BaseUrl$Path"
    $headers = @{ "Content-Type" = "application/json" }
    if ($Token) { $headers["Authorization"] = "Bearer $Token" }
    try {
        $bodyJson = if ($Body) { $Body | ConvertTo-Json -Depth 10 } else { $null }
        $params = @{
            Method  = $Method
            Uri     = $uri
            Headers = $headers
        }
        if ($bodyJson) { $params["Body"] = $bodyJson }
        $response = Invoke-WebRequest @params -UseBasicParsing
        return @{
            StatusCode = $response.StatusCode
            Content    = $response.Content | ConvertFrom-Json -ErrorAction SilentlyContinue
            Raw        = $response.Content
            Ok         = $true
        }
    }
    catch [System.Net.WebException] {
        $statusCode = [int]$_.Exception.Response?.StatusCode
        $body = ""
        try {
            $stream = $_.Exception.Response.GetResponseStream()
            $reader = [System.IO.StreamReader]::new($stream)
            $body = $reader.ReadToEnd()
        } catch {}
        return @{
            StatusCode = $statusCode
            Content    = $null
            Raw        = $body
            Ok         = $false
        }
    }
    catch {
        return @{
            StatusCode = 0
            Content    = $null
            Raw        = $_.Exception.Message
            Ok         = $false
        }
    }
}

Write-Host "`n========================================" -ForegroundColor Magenta
Write-Host "  CoupleSync Pilot Seed Script" -ForegroundColor Magenta
Write-Host "  Target: $BaseUrl" -ForegroundColor Magenta
Write-Host "========================================`n" -ForegroundColor Magenta

$runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$emailA = "pilot-a-$runId@example.com"
$emailB = "pilot-b-$runId@example.com"
$password = "PilotPass123!"

# ── Step 1: Register User A ── #
Write-Step "1/7 — Register User A ($emailA)"
$resp = Invoke-Api -Method POST -Path "/api/v1/auth/register" -Body @{
    Email    = $emailA
    Name     = "Pilot User A"
    Password = $password
}
if ($resp.Ok -and $resp.StatusCode -eq 201) {
    $tokenA = $resp.Content.accessToken
    $userAId = $resp.Content.user.id
    Write-Ok "User A created — id=$userAId"
} elseif ($resp.StatusCode -eq 409) {
    Write-Warn "User A already exists — attempting login"
    $loginResp = Invoke-Api -Method POST -Path "/api/v1/auth/login" -Body @{ Email = $emailA; Password = $password }
    if ($loginResp.Ok) {
        $tokenA = $loginResp.Content.accessToken
        $userAId = $loginResp.Content.user.id
        Write-Ok "User A logged in — id=$userAId"
    } else {
        Write-Fail "Cannot obtain User A token: $($loginResp.Raw)"; exit 1
    }
} else {
    Write-Fail "Register User A failed ($($resp.StatusCode)): $($resp.Raw)"; exit 1
}

# ── Step 2: Register User B ── #
Write-Step "2/7 — Register User B ($emailB)"
$resp = Invoke-Api -Method POST -Path "/api/v1/auth/register" -Body @{
    Email    = $emailB
    Name     = "Pilot User B"
    Password = $password
}
if ($resp.Ok -and $resp.StatusCode -eq 201) {
    $tokenB = $resp.Content.accessToken
    $userBId = $resp.Content.user.id
    Write-Ok "User B created — id=$userBId"
} elseif ($resp.StatusCode -eq 409) {
    Write-Warn "User B already exists — attempting login"
    $loginResp = Invoke-Api -Method POST -Path "/api/v1/auth/login" -Body @{ Email = $emailB; Password = $password }
    if ($loginResp.Ok) {
        $tokenB = $loginResp.Content.accessToken
        $userBId = $loginResp.Content.user.id
        Write-Ok "User B logged in — id=$userBId"
    } else {
        Write-Fail "Cannot obtain User B token: $($loginResp.Raw)"; exit 1
    }
} else {
    Write-Fail "Register User B failed ($($resp.StatusCode)): $($resp.Raw)"; exit 1
}

# ── Step 3: User A creates couple ── #
Write-Step "3/7 — User A creates couple"
$resp = Invoke-Api -Method POST -Path "/api/v1/couples" -Token $tokenA -Body @{}
if ($resp.Ok -and $resp.StatusCode -eq 201) {
    $coupleId = $resp.Content.coupleId
    $joinCode = $resp.Content.joinCode
    Write-Ok "Couple created — coupleId=$coupleId  joinCode=$joinCode"
} elseif ($resp.StatusCode -eq 409) {
    Write-Warn "User A already in a couple — fetching couple info"
    $meResp = Invoke-Api -Method GET -Path "/api/v1/couples/me" -Token $tokenA
    if ($meResp.Ok) {
        $coupleId = $meResp.Content.coupleId
        $joinCode = $meResp.Content.joinCode
        Write-Ok "Existing couple — coupleId=$coupleId  joinCode=$joinCode"
    } else {
        Write-Fail "Cannot fetch couple info: $($meResp.Raw)"; exit 1
    }
} else {
    Write-Fail "Create couple failed ($($resp.StatusCode)): $($resp.Raw)"; exit 1
}

# Refresh User A token to embed couple_id claim
$loginResp = Invoke-Api -Method POST -Path "/api/v1/auth/login" -Body @{ Email = $emailA; Password = $password }
if ($loginResp.Ok) { $tokenA = $loginResp.Content.accessToken; Write-Ok "User A token refreshed with couple_id" }

# ── Step 4: User B joins couple ── #
Write-Step "4/7 — User B joins couple (code=$joinCode)"
$resp = Invoke-Api -Method POST -Path "/api/v1/couples/join" -Token $tokenB -Body @{ JoinCode = $joinCode }
if ($resp.Ok -and $resp.StatusCode -eq 200) {
    Write-Ok "User B joined couple — members: $($resp.Content.members.Count)"
    $loginResp = Invoke-Api -Method POST -Path "/api/v1/auth/login" -Body @{ Email = $emailB; Password = $password }
    if ($loginResp.Ok) { $tokenB = $loginResp.Content.accessToken; Write-Ok "User B token refreshed with couple_id" }
} elseif ($resp.StatusCode -eq 409) {
    Write-Warn "User B already in couple — continuing"
    $loginResp = Invoke-Api -Method POST -Path "/api/v1/auth/login" -Body @{ Email = $emailB; Password = $password }
    if ($loginResp.Ok) { $tokenB = $loginResp.Content.accessToken }
} else {
    Write-Warn "Join couple returned $($resp.StatusCode): $($resp.Raw) — continuing"
}

# ── Step 5: Ingest 5 notification events ── #
Write-Step "5/7 — Ingest 5 notification events (Nubank, Itaú, Inter, C6, Bradesco)"
$events = @(
    @{ Bank = "Nubank";   Amount = 150.00; Currency = "BRL"; Description = "Supermercado Extra";         Merchant = "Extra Supermercados"; Raw = "Nubank: Compra R$150,00 Extra Supermercados" }
    @{ Bank = "Itau";     Amount = 89.90;  Currency = "BRL"; Description = "Farmácia Pague Menos";       Merchant = "Pague Menos";          Raw = "Itaú: Compra aprovada R$ 89,90 PAGUE MENOS" }
    @{ Bank = "Inter";    Amount = 320.00; Currency = "BRL"; Description = "Restaurante Central";        Merchant = "Restaurante Central";   Raw = "Inter: Transação R$320,00 RESTAURANTE CENTRAL" }
    @{ Bank = "C6 Bank";  Amount = 45.50;  Currency = "BRL"; Description = "Uber";                      Merchant = "Uber Brasil";           Raw = "C6: Débito R$ 45,50 UBER BRASIL" }
    @{ Bank = "Bradesco"; Amount = 200.00; Currency = "BRL"; Description = "Conta de Luz CEMIG";        Merchant = "CEMIG";                 Raw = "Bradesco: Pagamento R$200,00 CEMIG ENERGIA" }
)
foreach ($ev in $events) {
    $payload = @{
        Bank                = $ev.Bank
        Amount              = $ev.Amount
        Currency            = $ev.Currency
        EventTimestamp      = (Get-Date).ToUniversalTime().ToString("o")
        Description         = $ev.Description
        Merchant            = $ev.Merchant
        RawNotificationText = $ev.Raw
    }
    $resp = Invoke-Api -Method POST -Path "/api/v1/integrations/events" -Token $tokenA -Body $payload
    if ($resp.Ok -and $resp.StatusCode -eq 201) {
        Write-Ok "[$($ev.Bank)] ingested — id=$($resp.Content.ingestId) status=$($resp.Content.status)"
    } else {
        Write-Warn "[$($ev.Bank)] ingest returned $($resp.StatusCode): $($resp.Raw)"
    }
    Start-Sleep -Milliseconds 100
}

# ── Step 6: Create goal ── #
Write-Step "6/7 — Create goal 'Viagem Europa'"
$resp = Invoke-Api -Method POST -Path "/api/v1/goals" -Token $tokenA -Body @{
    Title        = "Viagem Europa"
    Description  = "Economia conjunta para viagem á Europa"
    TargetAmount = 5000.00
    Currency     = "BRL"
    Deadline     = "2027-06-01T00:00:00Z"
}
if ($resp.Ok -and $resp.StatusCode -eq 201) {
    $goalId = $resp.Content.id
    Write-Ok "Goal created — id=$goalId title='$($resp.Content.title)' target=$($resp.Content.targetAmount)"
} elseif ($resp.StatusCode -eq 409) {
    Write-Warn "Goal already exists (409) — continuing"
} else {
    Write-Warn "Create goal returned $($resp.StatusCode): $($resp.Raw) — continuing"
}

# ── Step 7: Register device token for User A ── #
Write-Step "7/7 — Register device token for User A"
# FCM raw token format: 152-char alphanumeric registration token (placeholder format for seed)
$fakeToken = "APA91bHPRgkFLJu6zbnRiCalvOZ9GdtB0x8Pd5GqBFgn8BjxFOGdRLdFRt_$($runId)XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"
$resp = Invoke-Api -Method POST -Path "/api/v1/devices/token" -Token $tokenA -Body @{
    Token    = $fakeToken
    Platform = "android"
}
if ($resp.Ok -and $resp.StatusCode -in @(200, 204)) {
    Write-Ok "Device token registered for User A"
} else {
    Write-Warn "Register device token returned $($resp.StatusCode): $($resp.Raw)"
}

Write-Host "`n========================================" -ForegroundColor Magenta
Write-Host "  Pilot seed complete!" -ForegroundColor Green
Write-Host "  User A: $emailA" -ForegroundColor White
Write-Host "  User B: $emailB" -ForegroundColor White
Write-Host "  Couple: $coupleId" -ForegroundColor White
Write-Host "  Join Code: $joinCode" -ForegroundColor White
Write-Host "========================================`n" -ForegroundColor Magenta
