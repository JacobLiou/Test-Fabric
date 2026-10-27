# Test Fabric V0.1 技术选型

> Decision: V0.1 采用 C#/.NET 构建 Test Fabric Control Plane；后续 AI/ML 使用 Python 扩展。  
> Status: Accepted  
> Date: 2026-10-27

## 1. 技术决策摘要

Test Fabric V0.1 的核心是资源注册、Capability、Discovery、Acquire/Release、Lease/Lock、Heartbeat、Health、Recovery、Audit 和 Dashboard。这类系统本质上是长期运行、强状态一致性、并发访问、设备集成型基础设施，而不是实时算法或 AI 应用。

因此 V0.1 选择 **C#/.NET** 作为主要技术栈。

长期原则：

> **C# manages the test. Python understands the data.**

如果遇到 Vendor Native SDK、高速 DAQ、Camera、Motion 等原生硬件能力，则允许 C++ 作为 Device Runtime/Adapter 存在，但不承担 Test Fabric Control Plane。

## 2. 三种候选方案

| 维度 | C#/.NET | C++ | Python |
|---|---:|---:|---:|
| Fabric Backend | ★★★★★ | ★★★ | ★★★★ |
| 并发/Lease/Lock | ★★★★★ | ★★★★ | ★★★ |
| Windows 工厂环境 | ★★★★★ | ★★★★★ | ★★★ |
| DLL/COM/SCPI/VISA | ★★★★★ | ★★★★★ | ★★★ |
| REST/gRPC | ★★★★★ | ★★★ | ★★★★★ |
| Dashboard | ★★★★★ | ★★ | ★★★★ |
| Driver/Native SDK | ★★★★ | ★★★★★ | ★★★ |
| AI/ML | ★★★ | ★★ | ★★★★★ |
| 开发效率 | ★★★★★ | ★★ | ★★★★★ |
| 类型安全 | ★★★★★ | ★★★★★ | ★★ |
| 长期平台维护 | ★★★★★ | ★★★ | ★★★ |
| V0.1 推荐 | **★★★★★** | ★★★ | ★★★★ |

### 结论

- **C#/.NET**：最适合 Test Fabric Control Plane。
- **C++**：最适合 Native Driver、Vendor SDK、高速采集和特殊硬件。
- **Python**：最适合未来 AI/ML、视觉、数据分析、诊断和 Agent Intelligence。

## 3. 为什么 V0.1 选择 C#/.NET

### 3.1 与问题类型高度匹配

V0.1 的主要问题不是极限计算性能，而是：

- Resource 生命周期；
- Capability Contract；
- 并发 Acquire；
- Exclusive Lease；
- Heartbeat/TTL；
- 故障 Recovery；
- Transaction；
- API；
- 长期运行 Worker；
- Dashboard 和 Audit。

C#/.NET 的强类型、async/await、CancellationToken、BackgroundService、ASP.NET Core 和成熟数据库生态非常适合这些需求。

### 3.2 对现有测试软件友好

现有 Windows 测试环境可以通过 .NET Client SDK 接入 Fabric，同时保留既有 Test Program、UDL、DLL/SDK 和 TAS/MIMS 业务链路。V0.1 不要求一次重写现有测试软件。

### 3.3 Capability 强类型建模

```csharp
public interface ITestCapability
{
    string CapabilityId { get; }
}

public interface IOpticalSpectrumAnalyzer : ITestCapability
{
    Task ConfigureAsync(OsaConfiguration config, CancellationToken ct);
    Task<SpectrumTrace> SweepAsync(CancellationToken ct);
    Task AbortAsync(CancellationToken ct);
}
```

不同品牌 Adapter 实现统一 Capability，上层测试逻辑不再依赖品牌、型号、IP 或 VISA Resource。

## 4. V0.1 推荐技术栈

| Layer | Selection |
|---|---|
| Language | **C#** |
| Runtime | **.NET 10** |
| Backend | **ASP.NET Core** |
| Architecture | **Modular Monolith** |
| Web API | **REST** |
| Internal/Cross-language Contract | **gRPC + Protobuf（按需）** |
| Database | **SQL Server** |
| ORM | **Entity Framework Core** |
| Background Processing | **BackgroundService + Channels** |
| Realtime Dashboard | **SignalR** |
| Dashboard | **Blazor**，V0.1 优先 |
| Logging | **Serilog** |
| Telemetry | **OpenTelemetry** |
| Resilience | **Polly** |
| Testing | **xUnit** |
| Device Integration | **C# Adapter + Existing UDL/SDK** |
| Native Integration | **P/Invoke / C++ Adapter，按需** |
| Future AI/ML | **Python Service** |

### 暂不引入

V0.1 规模仅为少量 Station 和 Instrument，因此暂不使用：

- Kubernetes；
- Kafka；
- Service Mesh；
- Redis Cluster；
- Event Sourcing；
- 大量 Microservices。

第一阶段保持 Modular Monolith，可以显著降低分布式系统复杂度。

## 5. 总体技术 Picture

```text
               MES / MIMS / TAS / TP
                       │
                  REST / gRPC
                       │
                       ▼
┌──────────────────────────────────────────────┐
│             TEST FABRIC - C#/.NET            │
│                                              │
│ Resource Registry     Capability Model       │
│ Discovery             Scheduler              │
│ Lease / Lock          Heartbeat              │
│ Health / Recovery     Audit                  │
│ API                   Dashboard              │
└───────────────────────┬──────────────────────┘
                        │
                 Adapter Contract
                        │
          ┌─────────────┼─────────────┐
          ▼             ▼             ▼
     C# Adapter     C++ Native    Python Service
          │             │             │
     SCPI/VISA      SDK/Driver      AI / ML
      UDL/DLL       DAQ/Motion      Diagnosis
          │             │             │
          └─────────────┼─────────────┘
                        ▼
                 Physical Resources
             OSA / PM / SMU / Camera ...
```

### 职责边界

**C#/.NET = Control Plane**

负责 Resource、Capability、Scheduler、Lease、Lock、Health、Recovery、Audit、API、Dashboard，以及未来 OCS Orchestration 和 Test Recipe Runtime。

**C++ = Device/Data Plane Extension**

仅在需要 Vendor Native SDK、Camera、Motion、DAQ、高速数据采集或现有 C++ 资产时使用。

**Python = Intelligence Plane**

后续负责 ML、Computer Vision、Spectrum Analysis、Anomaly Detection、Failure Diagnosis、Optimization 与 Agent Intelligence。

## 6. 核心工程原则

### 6.1 Fabric 负责治理，Adapter 负责执行

```text
Test Program
    ↓
Test Fabric
    ↓
Acquire Capability
    ↓
Lease / Policy / Safety Check
    ↓
Adapter
    ↓
Physical Device
```

Adapter 不负责资源调度；Test Program 不允许绕过 Fabric 直接改变 Fabric 管理资源的状态。

### 6.2 Lease 正确性优先

Acquire 必须通过数据库事务保证匹配和加锁的原子性。

核心不变量：

> **一个 Exclusive Resource 同一时间最多只有一个 Active Lease。**

不能只依赖应用代码中的 `if (Available)` 判断，数据库层必须提供并发保护和唯一约束。

### 6.3 Fail Closed

当数据库、Lease Service 或资源状态无法确认时，新 Acquire 应失败，而不是冒险分配设备。

### 6.4 AI 不直接绕过 Fabric

未来 Python/AI 可以提出 Test Action 或 Diagnosis Recommendation，但实际动作必须经过 .NET Fabric 的 Lease、Policy、Capability 和 Safety 检查。

```text
Python / AI proposes
        ↓
.NET Fabric governs
        ↓
Adapter executes
        ↓
Measurement verifies
```

## 7. 通信协议选择

### .NET 内部模块

优先使用进程内接口和 Domain/Application Contract，不为了“微服务化”而拆网络。

### Test Program → Fabric

V0.1 首选 REST + `.NET TestFabric.Client` SDK。

### 跨进程、跨机器或跨语言

优先选择 **gRPC + Protobuf**，作为 C#、C++、Python 之间稳定的 Contract Boundary。

### Native Vendor DLL

如果 Adapter 与 Fabric/Station 位于同一 Windows PC，并且厂商只提供本地 DLL，可使用：

```text
C# → P/Invoke → Native DLL
```

如果需要故障隔离、独立部署或跨机器访问，则使用：

```text
C# → gRPC → C++ Adapter Service
```

## 8. 数据层选择

V0.1 推荐 SQL Server。

核心实体：

```text
Resource
Capability
ResourceCapability
Lease
ResourceState
ResourceEvent
AuditLog
```

关键要求：

- Acquire + State Change 使用事务；
- Active Lease 建立数据库级并发约束；
- 使用 RowVersion/Concurrency Token 检测并发写；
- Audit/Event 持久化；
- 服务重启后可恢复 Active Lease；
- 时间统一存 UTC。

V0.1 不引入 Redis Distributed Lock。只有未来多 Fabric Node 和吞吐量证明有实际需求时再评估。

## 9. Solution / Repository 建议

```text
TestFabric.sln

src/
├── TestFabric.Domain/
│   ├── Resources/
│   ├── Capabilities/
│   ├── Leases/
│   └── Events/
│
├── TestFabric.Application/
│   ├── Discovery/
│   ├── Scheduling/
│   ├── Health/
│   └── Recovery/
│
├── TestFabric.Infrastructure/
├── TestFabric.Api/
├── TestFabric.Worker/
├── TestFabric.Dashboard/
├── TestFabric.Contracts/
└── TestFabric.Client/

adapters/
├── TestFabric.Adapters.Abstractions/
├── TestFabric.Adapters.OSA/
├── TestFabric.Adapters.PowerMeter/
└── TestFabric.Adapters.Simulator/

native/
└── cpp/

intelligence/
└── python/

contracts/
└── protobuf/

tests/
├── Unit/
├── Integration/
├── Concurrency/
└── E2E/

docs/
├── Architecture/
├── ADR/
├── API/
└── Runbook/
```

## 10. Architecture Decision Records

建议从第一天建立 ADR：

```text
ADR-001-Use-DotNet-for-Test-Fabric-Control-Plane.md
ADR-002-Modular-Monolith-for-V0.1.md
ADR-003-SQL-Server-for-Lease-Consistency.md
ADR-004-Device-Adapter-Boundary.md
ADR-005-gRPC-for-Cross-Language-Contracts.md
ADR-006-Python-for-Future-Intelligence-Plane.md
ADR-007-Fail-Closed-Lease-Strategy.md
```

这些 ADR 能防止未来项目扩大后重新陷入“为什么这样选”的讨论。

## 11. V0.1 部署建议

```text
Server / VM
│
├─ TestFabric.Api
├─ TestFabric.Worker
├─ TestFabric.Dashboard
└─ SQL Server
        │
        └──── Factory LAN
                 │
        ┌────────┼────────┐
        ▼        ▼        ▼
    Station01 Station02 Resources
```

V0.1 优先简单、可靠、可部署。是否容器化不是项目成功条件。

## 12. 分阶段技术路线

### V0.1 - Software-Defined Resource Pool

重点：

```text
C#/.NET
Resource Registry
Capability
Discovery
Acquire / Release
Lease / Lock
Heartbeat
Health
Recovery
Dashboard
Audit
```

设备：2 个 Station + 2 OSA + 2 Power Meter。

### V0.2 - Production Resource Pool

增加 Queue、Priority、Reservation、更多 Adapter、Capacity/Utilization Analysis，以及必要的 TAS/MIMS Context Integration。

### V0.3 - Connection Fabric

引入 OCS Topology、Port Registry、Path Planning、Connection Lock 和 Resource Plan + Connection Plan。

主体仍使用 C#/.NET。

### V1.0 - Test Orchestration

引入 Test Recipe、Multi-resource Acquisition、Workflow、Compensation、统一 Test Context。

### V2.0 - Intelligence / Agent

此阶段正式引入 Python：

```text
Python
├─ ML
├─ OpenCV
├─ Spectrum Analytics
├─ Anomaly Detection
├─ Failure Diagnosis
└─ Agent
        │
        ▼
C# Test Fabric
        │
Policy / Lease / Safety
        │
        ▼
Physical Test System
```

## 13. 技术验收重点

C# 技术路线必须通过以下工程验证：

- 10 个并发 Acquire 竞争 1 台独占设备时双重分配为 0；
- Client Crash 后 Lease 自动超时和 Recovery；
- Fabric 服务重启后正确恢复 Active Lease；
- 更换 OSA Adapter 时 Test Program 无需修改具体地址和品牌逻辑；
- Dashboard 状态在目标刷新周期内一致；
- 72 小时 Soak Test 无不可恢复 Zombie Lease；
- 新增同类 Adapter 不修改核心 Domain；
- 审计能够通过 SN / Resource / Lease 重建完整使用时间线。

## 14. 最终技术决策

### Accepted

**Test Fabric V0.1 采用 C#/.NET。**

理由不是团队偏好某种语言，而是 Test Fabric 的本质是一个需要长期运行、强类型 Contract、并发一致性、设备集成、事务、API、后台任务和可观测性的 Control Plane，C#/.NET 与该问题类型高度匹配。

### Future Direction

后续 AI/ML 不强行在 .NET 内完成，而是保留 Python Intelligence Plane，通过稳定 Contract 接入 Fabric。

最终技术边界：

> **C#/.NET manages and governs the test.**  
> **C++ integrates native hardware when necessary.**  
> **Python understands data and provides intelligence.**

这让 V0.1 保持简单可靠，同时不会封死未来 AI/ML、Computer Vision、Diagnosis 和 Agentic Test System 的演进空间。
