# Test Fabric V0.1 — 脚手架

Software-Defined Test Infrastructure：测试程序申请**能力**而非具体设备。
需求文档见 `Test_Fabric_V0.1_功能需求与验收标准.md`（PRD）。

## 快速开始

```bash
# 1. 启动 PostgreSQL（Docker）
docker compose up -d

# 2. 运行 API（自动迁移 + 启动租约宽限）
dotnet run --project src/TestFabric.Api
# → http://localhost:5080        Dashboard（资源看板 + SSE 实时事件）
# → http://localhost:5080/swagger  OpenAPI

# 3. 冒烟测试（注册→发现→申请→测量→释放→幂等→审计）
powershell -ExecutionPolicy Bypass -File scripts/smoke.ps1

# 4. 不变量金丝雀（E2E-06 的持续断言版本，5 分钟）
dotnet run --project tools/TestFabric.SoakCanary -- --duration 00:05:00 --chaos
```

开发令牌（`appsettings.json`，生产环境用环境变量覆盖）：

| 令牌 | 角色 |
|---|---|
| dev-admin-token | Administrator |
| dev-engineer-token | EquipmentEngineer |
| dev-client-token | TestClient |
| dev-viewer-token | Viewer |

## 测试

```bash
dotnet test tests/TestFabric.UnitTests          # 纯逻辑：matcher 真值表、心跳策略、状态机
dotnet test tests/TestFabric.IntegrationTests   # 需要 Docker（Testcontainers）
```

集成测试优先级：`TESTFABRIC_TEST_DB` 连接串（现成 Postgres）→ Docker 容器 → 跳过。
覆盖：FR-01 注册 / FR-03 发现 / FR-04/05/06 租约（并发互斥、幂等、过期恢复）/ FR-08 执行、
故障注入（E2E-05）/ 权限（FR-11）/ 管理员处置（FR-12）。

## 项目结构

```
src/
  TestFabric.Contracts   DTO + 错误码（Api 与 Sdk 共享，防漂移）
  TestFabric.Core        领域模型、匹配器、Registry/Discovery/Lease 服务、后台扫描、迁移（嵌入式 SQL）
  TestFabric.Adapters    IInstrumentAdapter 端口、每资源命令队列网关、Simulator（故障注入）
  TestFabric.Api         ASP.NET Core 宿主（REST + SSE + Dashboard + Token 认证）
  TestFabric.Sdk         客户端 SDK（自动心跳、LeaseLost 回调）
tests/
  TestFabric.UnitTests / TestFabric.IntegrationTests
tools/
  TestFabric.SoakCanary  不变量金丝雀（无双重分配 / 无僵尸租约）
```

## 关键设计决策（评审意见的落地）

| 决策 | 位置 |
|---|---|
| **独占性在数据库层兜底**：`UNIQUE INDEX ... WHERE status='Active'`，不依赖应用层先查后写 | `Persistence/Migrations/001_core.sql` |
| **原子分配**：条件 UPDATE（Available→Busy）+ 唯一索引拦截并发，失败自动换候选 | `LeaseService.TryCommitLeaseAsync` |
| **启动租约宽限**：重启后 Active Lease 的 TTL 从恢复时刻重新起算，避免 Reset 风暴 | `Persistence/StartupPolicy.cs` |
| **每资源串行命令队列**：业务/健康检查/Recovery Reset 互斥；ForceRelease 先取消在途指令 | `Adapters/InstrumentGateway.cs` |
| **健康检查分级**：L1=TCP（永远安全）、L2=Adapter 查询（队列忙则 Skipped≠失败） | `Background/HealthMonitorService.cs` |
| **Release/Expire/ForceRelease 互斥**：全部以 `status='Active'` 条件更新决出唯一赢家 | `LeaseService` |
| **注册期 schema 校验**：属性名拼错当场报 400，不会 Discovery 静默不匹配 | `CapabilityService.ValidateProperties` |
| **约束语义**：统一为「资源属性值 (operator) 请求值」，真值表单测钉死 | `Matching/ConstraintEvaluator.cs` |
| **校准过期是硬门槛**（FR-04-3），date-only 按"当日 23:59:59Z"解释 | `ResourceMatcher` / `RegistryService.ParseCalibrationDate` |
| **SDK 心跳自动化**：后台线程 + 服务端权威时间 + LeaseLost 事件 | `Sdk/LeaseHandle.cs` |

## 如何接入真实仪器（How to add an adapter）

1. 在 `TestFabric.Adapters` 新建 `YokogawaAq6370DAdapter : IOsaAdapter`；
2. 用 VISA.NET（NI-VISA / Keysight IO Libraries）实现 `ConfigureAsync / SingleSweepAsync / ...`；
3. 在 `Program.cs` 的 `AdapterRegistry` 注册 `visa.osa.yokogawa`；
4. 注册资源时 `adapterId: "visa.osa.yokogawa"`、`addressType: "TCPIP"`、`address: "10.10.20.31"`。

Test Program 不需要任何修改（NFR-03）。长耗时扫描建议后续加异步作业变体（评审 P0-4，V0.1 为同步契约）。

## 运维备忘

- 数据库：`docker compose down` 保留卷；`docker compose down -v` 删除数据。
- V0.1 已知限制（详见 PRD §3.2 非目标）：单能力申请（无组合原子申请）、无队列/预约、Token 认证（非企业 SSO）。
- `.tools/` 下的便携 PostgreSQL 仅供本机验证，可整体删除。
