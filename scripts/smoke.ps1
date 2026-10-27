# Test Fabric 冒烟测试：注册 → 发现 → 申请 → 测量 → 释放 → 幂等重放 → 审计
# 前置：API 已启动（dotnet run --project src/TestFabric.Api），数据库就绪
# 用法：powershell -ExecutionPolicy Bypass -File scripts/smoke.ps1 [-BaseUrl http://localhost:5080]

param(
    [string]$BaseUrl = "http://localhost:5080"
)

$ErrorActionPreference = "Stop"
$Admin = @{ Authorization = "Bearer dev-admin-token" }
$Engineer = @{ Authorization = "Bearer dev-engineer-token" }
$Client = @{ Authorization = "Bearer dev-client-token" }

$script:Failures = 0
function Step($name, [scriptblock]$action) {
    try {
        &$action
        Write-Host "  [PASS] $name" -ForegroundColor Green
    } catch {
        $script:Failures++
        Write-Host "  [FAIL] $name : $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Json($obj) { $obj | ConvertTo-Json -Depth 10 | Out-String }

Write-Host "`n=== Test Fabric 冒烟测试 ($BaseUrl) ===`n" -ForegroundColor Cyan

# 1. 健康检查
Step "GET /healthz" {
    $health = Invoke-RestMethod "$BaseUrl/healthz"
    if ($health.status -ne "ok") { throw "unexpected status: $($health.status)" }
}

# 2. 注册能力
Step "注册能力 OpticalSpectrumAnalysis" {
    try {
        $cap = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/capabilities" -Headers $Engineer -ContentType "application/json" -Body (Json(@{
            capabilityId = "OpticalSpectrumAnalysis"; version = 1
            schema = @{ properties = @{
                wavelengthMinNm = @{ type = "number" }; wavelengthMaxNm = @{ type = "number" }
                resolutionMinNm = @{ type = "number" }
            } }
        }))
    } catch {
        if ($_.Exception.Message -notmatch "RESOURCE_ID_DUPLICATED|409") { throw }
    }
}

# 3. 注册 OSA 资源（幂等：已存在则跳过）
foreach ($i in 1..2) {
    Step "注册资源 OSA-SM-$i" {
        try {
            Invoke-RestMethod -Method Post "$BaseUrl/api/v1/resources" -Headers $Engineer -ContentType "application/json" -Body (Json(@{
                resourceId = "OSA-SM-$i"; resourceType = "OpticalSpectrumAnalyzer"
                manufacturer = "Yokogawa"; model = "AQ6370D"
                addressType = "SIMULATOR"; address = "sim://OSA-SM-$i"
                adapterId = "sim.osa"; location = "Lab-A/Rack-0$i"
                calibrationDueDate = "2099-12-31"
                capabilities = @(@{ capabilityId = "OpticalSpectrumAnalysis"; properties = @{
                    wavelengthMinNm = 600; wavelengthMaxNm = 1700; resolutionMinNm = $(if ($i -eq 1) { 0.02 } else { 0.1 })
                } })
            })) | Out-Null
        } catch {
            if ($_.Exception.Message -notmatch "RESOURCE_ID_DUPLICATED|409") { throw }
        }
    }
}

# 4. 等待健康检查置为 Available
Step "等待资源进入 Available（健康监控 ≤30s，正常几秒）" {
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        $discovery = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/resources/discover" -Headers $Client -ContentType "application/json" -Body (Json(@{ capability = "OpticalSpectrumAnalysis" }))
        if ($discovery.candidates.Count -ge 2) { return }
        Start-Sleep -Seconds 1
    }
    throw "resources did not become Available within 45s (check health monitor logs)"
}

# 5. 发现：分辨率约束
Step "Discovery：分辨率 ≤ 0.05 → 只有 OSA-SM-1" {
    $discovery = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/resources/discover" -Headers $Client -ContentType "application/json" -Body (Json(@{
        capability = "OpticalSpectrumAnalysis"
        constraints = @{ resolutionMinNm = @{ operator = "lte"; value = 0.05 } }
    }))
    if ($discovery.candidates.Count -ne 1 -or $discovery.candidates[0].resourceId -ne "OSA-SM-1") {
        throw "expected 1 candidate OSA-SM-1, got: $($discovery.candidates.resourceId -join ',')"
    }
}

# 6. 申请 + 测量 + 释放
$key = "SMOKE-$(Get-Date -Format yyyyMMddHHmmss)"
$leaseId = $null
Step "Acquire（Idempotency-Key=$key）" {
    $script:lease = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/leases/acquire" -Headers ($Client + @{ "Idempotency-Key" = $key }) -ContentType "application/json" -Body (Json(@{
        clientId = "STATION-SMOKE"; capability = "OpticalSpectrumAnalysis"
        context = @{ workOrder = "WO-SMOKE-001"; serialNumber = "SN-SMOKE-001"; operation = "FinalTest" }
        leaseTtlSeconds = 60
    }))
    $script:leaseId = $script:lease.leaseId
    if ($script:lease.status -ne "Active") { throw "lease not Active: $($script:lease.status)" }
}

Step "OSA Configure + SingleSweep + GetTrace + GetPeak" {
    Invoke-RestMethod -Method Post "$BaseUrl/api/v1/leases/$leaseId/osa/configure" -Headers $Client -ContentType "application/json" -Body (Json(@{ clientId = "STATION-SMOKE"; startNm = 1260.0; stopNm = 1360.0; resolutionNm = 0.05 })) | Out-Null
    $sweep = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/leases/$leaseId/osa/single-sweep" -Headers $Client -ContentType "application/json" -Body (Json(@{ clientId = "STATION-SMOKE" }))
    if ($sweep.points -le 0) { throw "sweep returned no points" }
    $trace = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/leases/$leaseId/osa/get-trace" -Headers $Client -ContentType "application/json" -Body (Json(@{ clientId = "STATION-SMOKE"; sweepId = $sweep.sweepId }))
    if ($trace.valuesDbm.Count -le 0) { throw "empty trace" }
    $peak = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/leases/$leaseId/osa/get-peak" -Headers $Client -ContentType "application/json" -Body (Json(@{ clientId = "STATION-SMOKE"; sweepId = $sweep.sweepId }))
    Write-Host "        峰值: $($peak.peakWavelengthNm) nm / $($peak.peakPowerDbm) dBm" -ForegroundColor DarkGray
}

Step "Release" {
    $release = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/leases/$leaseId/release" -Headers $Client -ContentType "application/json" -Body (Json(@{ clientId = "STATION-SMOKE"; reason = "Completed" }))
    if ($release.result -ne "Released") { throw "unexpected release result: $($release.result)" }
}

# 7. 幂等重放
Step "幂等重放：同 key 返回同一 Lease" {
    $replay = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/leases/acquire" -Headers ($Client + @{ "Idempotency-Key" = $key }) -ContentType "application/json" -Body (Json(@{
        clientId = "STATION-SMOKE"; capability = "OpticalSpectrumAnalysis"; leaseTtlSeconds = 60
    }))
    if ($replay.leaseId -ne $leaseId) { throw "expected lease $leaseId, got $($replay.leaseId)" }
    if (-not $replay.idempotentReplay) { throw "idempotentReplay flag missing" }
}

# 8. 未认证 → 401
Step "未认证请求 → 401" {
    try {
        Invoke-RestMethod "$BaseUrl/api/v1/resources" | Out-Null
        throw "expected 401"
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 401) { throw }
    }
}

# 9. 审计事件链
Step "审计：Lease 完整时间线（AC-10-01）" {
    $events = Invoke-RestMethod "$BaseUrl/api/v1/events?leaseId=$leaseId" -Headers $Admin
    $types = $events | ForEach-Object { $_.eventType }
    foreach ($expected in @("AcquireSucceeded", "LeaseReleased")) {
        if ($types -notcontains $expected) { throw "event '$expected' missing from lease timeline" }
    }
}

Write-Host ""
if ($script:Failures -eq 0) {
    Write-Host "=== 冒烟测试全部通过 ===" -ForegroundColor Green
    exit 0
} else {
    Write-Host "=== $script:Failures 项失败 ===" -ForegroundColor Red
    exit 1
}
