# Test Fabric V0.1 功能需求规格与验收标准

> 文档定位：Test Fabric V0.1 产品需求、系统设计和验收基线  
> 适用场景：光通信研发实验室、试制线及制造测试工位  
> 建议试点：2 个测试工位、2 台 OSA、2 台 Optical Power Meter  
> 版本：V0.1 Draft  
> 日期：2026-10-27

---

## 1. 执行摘要

### 1.1 产品定义

Test Fabric 是一套 **Software-Defined Test Infrastructure（软件定义测试基础设施）**。V0.1 的核心任务不是自动设计测试流程，也不是通过 OCS 动态建立光路，而是先把分散、固定绑定在工位上的测试设备抽象为可注册、可发现、可分配、可锁定、可监控的共享测试资源。

V0.1 要完成一次关键架构转变：

```text
传统模式：测试程序指定并连接 OSA01

目标模式：测试程序申请 OpticalSpectrumAnalysis 能力
          ↓
      Test Fabric
          ↓
根据能力、状态、校准和占用情况分配 OSA02
```

### 1.2 核心价值

1. **设备解耦**：Test Program 不再硬编码设备 IP、COM、GPIB/VISA 地址和具体资源编号。
2. **资源共享**：多个工位可从统一资源池申请测试能力。
3. **故障隔离**：单台设备离线时，可在满足约束的候选设备中重新分配。
4. **状态透明**：实时看到设备是否 Available、Busy、Offline、Error、Maintenance 或 CalibrationExpired。
5. **架构铺垫**：为后续 OCS 动态连接、Test Recipe 编排、Test Agent 和 AI Diagnosis 提供基础。

### 1.3 V0.1 北极星指标

> 测试程序申请的是“测试能力”，而不是“某一台具体设备”。

试点验收时必须证明：任一试点工位在不修改测试业务代码和设备地址配置的情况下，可以获得任一满足需求且可用的 OSA 或 Power Meter。

---

## 2. 背景与问题定义

### 2.1 当前典型架构

```text
Station 01                     Station 02
├─ Test Program                ├─ Test Program
├─ OSA01                       ├─ OSA02
├─ Power Meter 01              ├─ Power Meter 02
└─ Local Configuration         └─ Local Configuration
```

测试程序通常直接依赖：

- 仪器品牌和型号；
- IP、串口、GPIB 或 VISA Resource；
- 本地 DLL/SDK 版本；
- 工位本地配置文件；
- 固定设备与工位的物理关系。

### 2.2 主要痛点

| 编号 | 痛点 | 业务影响 |
|---|---|---|
| P-01 | 测试程序绑定具体设备地址 | 更换设备、复制工位或迁线时需要修改配置，甚至修改代码 |
| P-02 | 设备资源固定归属工位 | 一边闲置、一边等待，昂贵设备利用率低 |
| P-03 | 缺少统一状态模型 | 软件无法可靠判断设备可用、占用、离线、故障或校准过期 |
| P-04 | 缺少跨工位互斥机制 | 多个程序同时访问同一设备，产生指令冲突和错误数据 |
| P-05 | 异常退出后资源无法自动释放 | 产生僵尸占用，依赖人工重启或修改配置 |
| P-06 | 缺少统一使用记录 | 无法回答设备由谁、何时、为哪个 SN、执行什么测试使用 |
| P-07 | 品牌、型号和驱动差异暴露给业务层 | Test Program 难以复用，测试平台维护成本高 |

---

## 3. 产品目标与范围

### 3.1 V0.1 目标

V0.1 应交付以下六项最小闭环能力：

1. **Resource Registry**：测试资源注册与配置管理；
2. **Capability Model**：统一测试能力及约束描述；
3. **Health & Status**：设备健康和运行状态管理；
4. **Discovery & Allocation**：按能力发现和分配资源；
5. **Lease & Lock**：租约、互斥锁、心跳及超时回收；
6. **Observability**：资源状态、占用、事件和基础指标可视化。

### 3.2 V0.1 非目标

以下内容明确不进入 V0.1，避免第一版范围失控：

- 不实现 OCS 光路自动规划和动态连接；
- 不实现复杂 Test Recipe 图形化编排；
- 不实现 AI Agent 自主决策；
- 不替代现有 TAS、MIMS、MES 数据上传流程；
- 不实现跨厂区资源池；
- 不实现复杂成本优化和智能排程算法；
- 不要求一次覆盖所有仪器类型；
- 不直接改变现有 UDL 驱动内部实现。

### 3.3 建议试点边界

| 项目 | V0.1 建议范围 |
|---|---|
| 工位 | 2 个测试工位 |
| 设备类型 | OSA、Optical Power Meter |
| 设备数量 | 每类各 2 台，共 4 台 |
| 网络范围 | 同一实验室或同一产线局域网 |
| 并发规模 | 2 至 5 个客户端，最多 10 个并发申请 |
| 业务场景 | 光谱测量、光功率测量 |
| 运行周期 | 连续稳定性测试至少 72 小时 |

---

## 4. 总体业务 Picture

```text
           MES / MIMS / TAS / Test Program
                       │
         Request Capability + Constraints
                       │
                       ▼
┌─────────────────────────────────────────────────────┐
│                    Test Fabric API                  │
├─────────────────────────────────────────────────────┤
│  Resource Discovery  │  Allocation  │ Lease/Lock   │
├─────────────────────────────────────────────────────┤
│  Capability Model    │  Health      │ Audit/Event  │
├─────────────────────────────────────────────────────┤
│               Resource Registry                     │
└───────────────────────┬─────────────────────────────┘
                        │
                  UDL / Adapter
                        │
       ┌────────────────┼────────────────┐
       ▼                ▼                ▼
     OSA01            OSA02          PowerMeter01/02
       ▲                ▲                ▲
       └──────── Resource Pool ──────────┘
```

### 4.1 关键业务流程

```text
Discover → Match → Acquire → Lock → Execute → Heartbeat
    → Complete/Fail → Release → Audit
```

### 4.2 关键角色

| 角色 | 职责 |
|---|---|
| Test Program Developer | 按能力申请资源，不直接选择设备地址 |
| Test Operator | 查看当前可用资源、占用和异常状态 |
| Equipment Engineer | 注册设备、维护能力、校准和维护状态 |
| System Administrator | 管理权限、服务配置和异常释放 |
| Test Fabric Service | 完成匹配、分配、互斥、回收和审计 |
| UDL/Adapter | 屏蔽具体品牌、协议和驱动差异 |

---

## 5. 核心领域模型

### 5.1 Resource

一台可被管理和分配的物理或逻辑测试资源。

```yaml
resourceId: OSA-ZH-001
resourceType: OpticalSpectrumAnalyzer
manufacturer: Yokogawa
model: AQ6370D
addressType: TCPIP
address: 10.10.20.31
adapterId: UDL.OSA.Yokogawa
adapterVersion: 1.0.0
location: Lab-A/Rack-02
status: Available
health: Healthy
calibrationDueDate: 2027-03-31
enabled: true
```

### 5.2 Capability

资源能够提供、并可被业务申请的标准测试能力。

```yaml
capabilityId: OpticalSpectrumAnalysis
properties:
  wavelengthMinNm: 600
  wavelengthMaxNm: 1700
  resolutionMinNm: 0.02
  supportedTraces: [A, B, C]
```

### 5.3 Requirement

调用方对能力、性能、状态和合规性的要求。

```yaml
capability: OpticalSpectrumAnalysis
constraints:
  wavelengthMinNm: "<= 1260"
  wavelengthMaxNm: ">= 1650"
  resolutionMinNm: "<= 0.05"
  calibrationValid: true
  health: Healthy
```

### 5.4 Lease

客户端获得资源独占使用权的有时限凭证。

```yaml
leaseId: L-20261027-00001
resourceId: OSA-ZH-001
ownerId: STATION-01
workOrder: WO-00125
serialNumber: SN-99881
operation: FinalTest
expiresAt: 2026-10-27T10:30:00+08:00
heartbeatIntervalSeconds: 10
status: Active
```

---

# 6. 详细功能需求与验收标准

## FR-01 资源注册

### 用户故事

作为 Equipment Engineer，我希望把测试设备登记到统一资源注册中心，使设备可被查询、监控和分配。

### 功能要求

1. 支持新增、编辑、启用、禁用和查看资源；
2. Resource ID 必须全局唯一，创建后不可直接修改；
3. 必填字段：资源编号、类型、厂商、型号、连接地址、Adapter、位置；
4. 支持配置校准到期日、资产编号、序列号和说明；
5. 禁用资源不得参与匹配和分配；
6. 删除已被使用过的资源时采用逻辑删除，保留历史记录；
7. 对地址、类型和必填项执行格式校验；
8. 所有新增、编辑、启用和禁用操作写入审计日志。

### 验收标准

| AC ID | Given | When | Then |
|---|---|---|---|
| AC-01-01 | 管理员提供完整且合法的设备字段 | 创建资源 | 系统成功创建，并返回唯一 Resource ID |
| AC-01-02 | Resource ID 已存在 | 再次创建相同 ID | 系统拒绝并返回 `RESOURCE_ID_DUPLICATED` |
| AC-01-03 | 缺少资源类型或连接地址 | 提交创建 | 系统拒绝并指出缺失字段 |
| AC-01-04 | 资源状态为 Disabled | 客户端查询可分配资源 | 该资源不出现在候选集合中 |
| AC-01-05 | 资源有历史租约 | 管理员执行删除 | 系统逻辑删除，历史使用记录仍可查询 |
| AC-01-06 | 管理员修改设备地址 | 保存配置 | 新配置生效，审计日志记录修改人、时间、旧值和新值 |

---

## FR-02 能力与属性管理

### 用户故事

作为 Test Program Developer，我希望按统一能力申请设备，而不是依赖品牌和型号。

### 功能要求

1. 支持定义标准能力类型，如 `OpticalSpectrumAnalysis`、`OpticalPowerMeasurement`；
2. 一个资源可关联一个或多个能力；
3. 能力支持数值、布尔、枚举和字符串属性；
4. 支持 `=、>=、<=、in` 等约束操作符；
5. 设备型号差异由 Adapter 映射到统一能力模型；
6. 能力字段需要版本号，避免后续升级破坏兼容性；
7. 不允许资源声明 Adapter 实际不支持的能力。

### 验收标准

| AC ID | Given | When | Then |
|---|---|---|---|
| AC-02-01 | OSA01 支持 600 至 1700 nm、0.02 nm 分辨率 | 请求 1260 至 1650 nm、分辨率不大于 0.05 nm | OSA01 匹配成功 |
| AC-02-02 | OSA02 最小分辨率为 0.1 nm | 请求分辨率不大于 0.05 nm | OSA02 被排除，并返回不匹配原因 |
| AC-02-03 | 资源未绑定相关能力 | 请求该能力 | 该资源不得进入候选集合 |
| AC-02-04 | 请求包含系统不支持的约束字段 | 执行匹配 | 系统返回 `UNSUPPORTED_CONSTRAINT`，不得静默忽略 |
| AC-02-05 | 两种品牌设备映射到相同标准能力 | 客户端调用统一接口 | 客户端不需要包含品牌判断逻辑 |

---

## FR-03 资源发现与查询

### 用户故事

作为调用方，我希望查询满足能力和约束的资源集合，以判断资源池是否能够执行测试。

### 功能要求

1. 支持按能力、资源类型、位置、状态、健康度、校准有效性查询；
2. Discovery 只返回候选信息，不锁定资源；
3. 返回匹配结果及排除原因；
4. 支持查询单个资源详情；
5. 支持分页和稳定排序；
6. 默认不返回 Disabled、Deleted、Offline、Maintenance 资源作为可分配候选。

### 验收标准

| AC ID | Given | When | Then |
|---|---|---|---|
| AC-03-01 | 资源池有 2 台满足能力且 Available 的 OSA | 执行 Discovery | 返回 2 个候选资源及能力摘要 |
| AC-03-02 | OSA01 Busy，OSA02 Available | 查询可分配资源 | 仅 OSA02 标记为可分配 |
| AC-03-03 | 无资源满足分辨率要求 | 执行 Discovery | 返回空候选和结构化不匹配原因 |
| AC-03-04 | 资源数量超过单页上限 | 分页查询 | 无重复、无遗漏且排序稳定 |
| AC-03-05 | 执行 Discovery | 不执行 Acquire | 任一资源状态和租约均不发生变化 |

---

## FR-04 资源分配 Acquire

### 用户故事

作为 Test Program，我希望提交能力需求后获得一个满足条件的可用资源和 Lease，而不需要知道设备位置与地址。

### 功能要求

1. Acquire 输入至少包含 Client ID、能力和约束；
2. 可选输入包含工单、SN、工序和测试名称；
3. 系统只从 Enabled、Healthy、CalibrationValid、Available 资源中选择；
4. 匹配和加锁必须是原子操作；
5. 同一请求重复提交时支持 Idempotency Key；
6. 分配成功返回 Lease ID、资源引用、能力接口和过期时间；
7. 分配失败返回明确错误码，不得返回未锁定资源；
8. V0.1 默认策略可采用“最早空闲优先”，相同条件按 Resource ID 稳定排序；
9. 可配置 Acquire 等待超时，V0.1 默认 5 秒。

### 验收标准

| AC ID | Given | When | Then |
|---|---|---|---|
| AC-04-01 | 存在至少一台符合约束的可用资源 | 调用 Acquire | 5 秒内返回资源与有效 Lease |
| AC-04-02 | 两个客户端同时申请唯一可用 OSA | 并发调用 Acquire | 只有一个成功，另一个返回 `NO_RESOURCE_AVAILABLE` 或进入限定等待 |
| AC-04-03 | 客户端以相同 Idempotency Key 重试 | 多次调用 Acquire | 返回同一 Lease，不产生重复占用 |
| AC-04-04 | 所有候选资源均 Busy | 调用 Acquire | 超时后返回明确状态，不错误分配 Busy 资源 |
| AC-04-05 | 校准已经过期 | 调用要求校准有效的 Acquire | 该资源不得被分配 |
| AC-04-06 | 设备在匹配后、锁定前变为不可用 | 执行 Acquire | 原子检查失败并重新匹配或返回失败，不产生脏 Lease |

---

## FR-05 租约、互斥锁与心跳

### 用户故事

作为平台，我需要确保一台独占设备在同一时间只被一个客户端控制，并能够识别客户端异常退出。

### 功能要求

1. 每次成功 Acquire 必须产生唯一 Lease；
2. 同一独占资源最多只能存在一个 Active Lease；
3. 默认 Lease TTL 建议 60 秒，允许按资源类型配置；
4. 客户端建议每 10 秒发送一次心跳；
5. 心跳成功后延长租约有效期；
6. 连续超过 TTL 未收到心跳，Lease 转为 Expired；
7. Expired 后资源进入短暂 Recovering 状态，执行 Adapter Reset/Disconnect；
8. Recovering 成功后资源恢复 Available，失败则进入 Error；
9. 所有锁状态变更必须持久化并产生事件；
10. 服务重启后必须恢复 Active Lease，不得把正在使用的资源直接置为 Available。

### 验收标准

| AC ID | Given | When | Then |
|---|---|---|---|
| AC-05-01 | OSA01 有 Active Lease | 第二客户端申请 OSA01 | 系统拒绝重复锁定 |
| AC-05-02 | 客户端按周期发送心跳 | Lease 接近过期 | TTL 被续期，资源保持 Busy |
| AC-05-03 | 客户端进程异常终止 | 超过 TTL 未收到心跳 | Lease 变为 Expired，资源进入 Recovering |
| AC-05-04 | Reset/Disconnect 成功 | Recovery 完成 | 资源恢复 Available，可被重新申请 |
| AC-05-05 | Reset/Disconnect 失败 | Recovery 完成 | 资源进入 Error，不参与自动分配 |
| AC-05-06 | Fabric 服务重启时存在 Active Lease | 服务恢复 | Lease 和 Busy 状态被恢复，不发生双重分配 |
| AC-05-07 | 10 个请求并发竞争 1 台资源 | 执行 Acquire | 任意时刻 Active Lease 数始终为 1 |

---

## FR-06 资源释放 Release

### 用户故事

作为 Test Program，我希望测试结束后明确释放资源，使其他工位能够立即使用。

### 功能要求

1. Release 必须携带有效 Lease ID；
2. 仅 Lease Owner 或管理员可以释放；
3. Release 为幂等操作；
4. 释放前调用 Adapter 的安全清理动作，如 Stop、Reset、Disconnect；
5. 清理成功后资源进入 Available；
6. 清理失败后资源进入 Error，不得直接 Available；
7. 保存使用开始时间、结束时间、持续时间和结束原因；
8. 支持正常完成、测试失败、取消、超时和管理员强制释放等结束原因。

### 验收标准

| AC ID | Given | When | Then |
|---|---|---|---|
| AC-06-01 | Lease 有效且归当前客户端所有 | 调用 Release | 清理完成，Lease Closed，资源 Available |
| AC-06-02 | 对同一 Lease 重复 Release | 再次调用 | 返回成功或 AlreadyReleased，不执行重复破坏性操作 |
| AC-06-03 | 非 Owner 尝试释放 | 调用 Release | 返回 `LEASE_OWNER_MISMATCH` |
| AC-06-04 | Adapter 清理失败 | 调用 Release | 资源进入 Error，并产生告警事件 |
| AC-06-05 | 管理员强制释放 Busy 资源 | 确认操作 | 记录管理员和原因，资源执行 Recovery 流程 |

---

## FR-07 健康检查与状态机

### 用户故事

作为 Equipment Engineer，我希望系统持续检查设备连通性和基础健康，使不可用设备不会被错误分配。

### 状态定义

```text
Administrative State: Enabled / Disabled / Maintenance

Operational State:
Available → Busy → Recovering → Available
    │         │          │
    └──────→ Error ←─────┘

Connectivity: Online / Offline
Calibration: Valid / Expiring / Expired / Unknown
```

### 功能要求

1. 健康检查包含 TCP/串口连通和 Adapter 基础查询；
2. 默认健康检查周期建议 30 秒；
3. 连续 3 次失败后判定 Offline，避免瞬时网络抖动造成频繁切换；
4. 连续 2 次成功后可从 Offline 恢复 Online；
5. Busy 状态下健康检查不得发送会干扰测试的 Reset 类指令；
6. Calibration Expired 的设备不得参与有校准要求的申请；
7. Maintenance 与 Disabled 必须由授权人员人工设置；
8. 状态变化产生时间戳事件和告警。

### 验收标准

| AC ID | Given | When | Then |
|---|---|---|---|
| AC-07-01 | Available 设备网络断开 | 连续 3 次检查失败 | 状态变为 Offline，并退出分配候选 |
| AC-07-02 | Offline 设备恢复连接 | 连续 2 次检查成功 | 状态恢复 Online，并按其他状态决定是否 Available |
| AC-07-03 | 设备处于 Busy | 执行健康检查 | 测试指令不被中断，日志中无 Reset 操作 |
| AC-07-04 | 校准日期早于当前日期 | 每日状态刷新 | Calibration 状态为 Expired |
| AC-07-05 | 管理员设置 Maintenance | 客户端申请资源 | 设备不参与分配，Dashboard 清晰显示维护状态 |

---

## FR-08 UDL/Adapter 集成

### 用户故事

作为 Test Program Developer，我希望通过统一能力接口执行测量，使业务代码不依赖仪器品牌和协议。

### 功能要求

1. 每一种能力定义统一接口和统一数据模型；
2. Adapter 负责品牌指令、单位、枚举和错误码转换；
3. Adapter 必须实现 Connect、HealthCheck、Reset、Disconnect；
4. OSA 最小接口建议包括 Configure、SingleSweep、GetTrace、GetPeak、Abort；
5. Power Meter 最小接口建议包括 Configure、MeasurePower、Zero、Abort；
6. 测量结果必须带 Resource ID、Timestamp、Unit、Raw/Processed 标识；
7. Adapter 异常转换为平台统一错误码，同时保留原始错误信息；
8. Adapter 版本必须可查询并进入审计记录。

### 验收标准

| AC ID | Given | When | Then |
|---|---|---|---|
| AC-08-01 | 两台不同型号 OSA 实现同一能力 | 执行统一 SingleSweep | 客户端使用相同接口获得标准化结果 |
| AC-08-02 | 仪器返回品牌专用错误 | Adapter 处理 | 平台返回统一错误码并保存原始错误 |
| AC-08-03 | 测量成功 | 返回数据 | 数据包含资源编号、时间戳、数值、单位和 Adapter 版本 |
| AC-08-04 | 客户端未持有有效 Lease | 尝试执行设备指令 | 平台拒绝并返回 `LEASE_REQUIRED` |
| AC-08-05 | Lease 已过期 | 尝试继续测量 | 请求被拒绝，不向设备发送业务指令 |

---

## FR-09 Dashboard 与资源可观测性

### 用户故事

作为操作员或工程师，我希望在一个界面看到全部资源状态、当前占用和最近异常。

### 功能要求

1. 显示资源总数及 Available、Busy、Offline、Error、Maintenance 数量；
2. 资源列表展示 Resource ID、类型、型号、位置、健康、校准、Owner、SN、Test、Lease 剩余时间；
3. 支持按状态、类型、位置过滤；
4. 支持查看资源详情和状态时间线；
5. Busy 资源必须显示占用客户端和 Lease 开始时间；
6. Error/Offline 资源必须显示最近错误摘要；
7. 数据刷新周期不大于 5 秒，或支持实时事件推送；
8. 仅授权管理员可执行禁用、维护和强制释放。

### 验收标准

| AC ID | Given | When | Then |
|---|---|---|---|
| AC-09-01 | 资源状态发生变化 | Dashboard 刷新 | 5 秒内显示最新状态 |
| AC-09-02 | OSA01 被 Station01 占用 | 查看列表 | 显示 Busy、Owner、SN、Test 和 Lease 剩余时间 |
| AC-09-03 | PM01 连续健康检查失败 | 查看资源详情 | 显示 Offline、发生时间和最近错误 |
| AC-09-04 | 普通操作员登录 | 查看资源 | 可以查看，但看不到管理操作按钮 |
| AC-09-05 | 选择 OSA + Error 过滤 | 执行过滤 | 仅显示符合两个条件的资源 |

---

## FR-10 审计、事件与基础报表

### 用户故事

作为系统管理员和质量工程师，我希望追踪资源的完整生命周期，以便问题诊断和责任界定。

### 功能要求

1. 记录 ResourceCreated、ResourceUpdated、AcquireSucceeded、AcquireFailed、HeartbeatLost、LeaseExpired、Release、HealthChanged、AdminOverride 等事件；
2. 事件最少包含 Event ID、时间、资源、Lease、Client、用户、SN、工单、操作和结果；
3. 审计记录不可由普通用户修改或删除；
4. 支持按时间、资源、Lease、SN 和 Client 查询；
5. V0.1 提供基础指标：设备使用时长、占用率、申请成功率、平均等待时间、异常次数；
6. 日志保留期限试点阶段建议不少于 90 天；
7. 禁止审计日志记录密码、Token 等敏感信息。

### 验收标准

| AC ID | Given | When | Then |
|---|---|---|---|
| AC-10-01 | 一次完整 Acquire、Execute、Release | 查询 Lease ID | 可以看到完整时间线 |
| AC-10-02 | Acquire 因无资源失败 | 查询 Client ID | 记录需求、失败原因及时间 |
| AC-10-03 | 通过 SN 查询 | 执行查询 | 返回该 SN 使用过的资源及测试时间 |
| AC-10-04 | 普通用户尝试修改审计记录 | 提交操作 | 系统拒绝并记录安全事件 |
| AC-10-05 | 统计某天 OSA 使用率 | 查看报表 | 结果可由 Busy Duration / Available Window 复算一致 |

---

## FR-11 权限和安全

### 功能要求

建议采用最小权限 RBAC：

| 角色 | 主要权限 |
|---|---|
| Viewer | 查看资源、状态、事件 |
| TestClient | Discovery、Acquire、Heartbeat、Execute、Release |
| EquipmentEngineer | 注册、编辑、维护状态、查看日志 |
| Administrator | 全部权限、强制释放、角色管理 |

补充要求：

1. 所有 API 必须认证；
2. 设备连接凭据不得明文展示在 Dashboard 和普通日志中；
3. Force Release、Disable、Maintenance 等高风险操作必须记录原因；
4. 服务到设备的网络访问应限制在必要端口；
5. V0.1 至少支持内部服务身份或 API Token，生产化建议接入企业统一身份。

### 验收标准

| AC ID | Given | When | Then |
|---|---|---|---|
| AC-11-01 | 未认证请求 | 调用 Acquire | 返回 401，不执行资源操作 |
| AC-11-02 | Viewer 用户 | 尝试强制释放 | 返回 403 |
| AC-11-03 | 管理员强制释放 | 提交原因 | 操作成功并记录人员、原因和时间 |
| AC-11-04 | 查看资源详情 | 页面加载 | 密码、Token 和敏感连接参数不明文显示 |

---

## FR-12 管理员异常处置

### 功能要求

1. 支持管理员将资源置为 Maintenance、Disabled 或 Error；
2. Busy 资源默认禁止直接禁用，必须先完成或强制释放；
3. 支持对 Error 资源执行 HealthCheck、Reset 和恢复确认；
4. 支持查看僵尸 Lease 和执行 Force Release；
5. 所有高风险操作要求二次确认和原因说明。

### 验收标准

| AC ID | Given | When | Then |
|---|---|---|---|
| AC-12-01 | 资源 Busy | 尝试直接 Disabled | 系统阻止，并提示先释放 Lease |
| AC-12-02 | 资源 Error 且 Reset 成功 | 管理员确认恢复 | 状态回到 Available |
| AC-12-03 | Lease 超时但 Recovery 卡住 | 管理员 Force Release | 系统结束 Lease，执行安全清理并记录原因 |
| AC-12-04 | 未填写原因 | 执行高风险操作 | 系统拒绝提交 |

---

# 7. 非功能需求

## NFR-01 性能

| 项目 | 验收目标 |
|---|---|
| Discovery 响应时间 | 资源规模小于 100 时，P95 ≤ 500 ms |
| Acquire 响应时间 | 有可用资源时，P95 ≤ 1 s；整体超时默认 5 s |
| Heartbeat 响应时间 | P95 ≤ 300 ms |
| Dashboard 状态延迟 | ≤ 5 s |
| 并发正确性 | 10 个并发请求竞争 1 个资源，双重分配为 0 |

## NFR-02 可靠性

- 试点连续运行 72 小时，Fabric 核心服务无非计划中断；
- 核心服务重启后，资源状态和 Active Lease 可恢复；
- 网络短暂抖动不能立即释放仍在执行中的资源；
- 数据库暂时不可用时不得发生错误分配；
- 关键状态写入应具备事务一致性。

## NFR-03 可维护性

- 新增同类设备型号原则上只新增或升级 Adapter，不修改 Test Program；
- 配置变更有版本和审计；
- 错误码、日志字段和状态机必须形成开发文档；
- 应提供本地仿真 Adapter，允许无真实仪器开发和自动测试。

## NFR-04 可观测性

系统至少提供：

- 结构化日志；
- API 请求追踪 ID；
- Resource ID、Lease ID、Client ID 贯穿日志；
- Acquire 成功率和失败原因；
- Lease 超时次数；
- 设备状态变化次数；
- Adapter 调用耗时及错误率。

## NFR-05 数据一致性

- Resource 只能有一个当前有效运行状态；
- Exclusive Resource 最多一个 Active Lease；
- 状态和 Lease 更新必须原子提交；
- 使用历史不得因资源配置修改而丢失；
- 所有时间统一保存为 UTC，UI 按所在地时区展示。

---

# 8. 建议 API 基线

## 8.1 Discovery

```http
POST /api/v1/resources/discover
```

```json
{
  "capability": "OpticalSpectrumAnalysis",
  "constraints": {
    "wavelengthMinNm": { "operator": "lte", "value": 1260 },
    "wavelengthMaxNm": { "operator": "gte", "value": 1650 },
    "resolutionMinNm": { "operator": "lte", "value": 0.05 },
    "calibrationValid": true
  }
}
```

## 8.2 Acquire

```http
POST /api/v1/leases/acquire
Idempotency-Key: ST01-SN99881-FINALTEST
```

```json
{
  "clientId": "STATION-01",
  "capability": "OpticalSpectrumAnalysis",
  "context": {
    "workOrder": "WO-00125",
    "serialNumber": "SN-99881",
    "operation": "FinalTest"
  },
  "leaseTtlSeconds": 60
}
```

## 8.3 Heartbeat

```http
POST /api/v1/leases/{leaseId}/heartbeat
```

## 8.4 Release

```http
POST /api/v1/leases/{leaseId}/release
```

```json
{
  "reason": "Completed"
}
```

## 8.5 Execute Capability

```http
POST /api/v1/leases/{leaseId}/osa/single-sweep
```

重要约束：业务能力 API 必须先验证 Lease 是否 Active、资源和 Lease 是否一致、调用者是否为 Owner。

---

# 9. 关键异常场景

| 场景 | 系统预期行为 |
|---|---|
| 客户端申请资源后立即崩溃 | TTL 到期后进入 Recovering，安全清理成功后重新开放 |
| Fabric 服务重启 | 从持久化存储恢复 Active Lease，禁止直接把资源全部标为 Available |
| 仪器测量过程中断网 | 当前调用失败，资源进入 Error 或 Unknown，不立即分配给其他客户端 |
| 同一客户端重复请求 | 通过 Idempotency Key 返回原 Lease |
| 同一设备被两个请求同时匹配 | 数据库事务或分布式锁保证只有一个获得 Active Lease |
| 校准在 Lease 期间到期 | 当前测试按质量规则处理；新的 Acquire 不再分配，事件中完整记录 |
| Adapter 进程异常 | Lease 保留到恢复或超时，不把设备错误标为 Available |
| 数据库不可用 | Acquire Fail Closed，拒绝新分配，避免双重占用 |
| 管理员误操作释放 | 二次确认、原因记录、执行安全 Abort/Reset，留下完整审计链 |

---

# 10. 端到端验收场景

## E2E-01 能力替代具体设备

**目标**：证明测试程序不绑定 OSA01。

1. 注册 OSA01、OSA02，并映射为相同标准能力；
2. Station01 只提交 `OpticalSpectrumAnalysis` 需求；
3. 第一次分配 OSA01 并完成测试；
4. 将 OSA01 设置为 Maintenance；
5. 不修改 Test Program 和地址配置，再次执行；
6. 系统应分配 OSA02，并成功返回标准化测量结果。

**通过标准**：业务代码和测试配置中没有 OSA01/OSA02 IP、VISA 地址或型号分支。

## E2E-02 并发互斥

1. 资源池仅保留一台可用 OSA；
2. 两个 Station 在 100 ms 内同时 Acquire；
3. 仅一个客户端获得 Active Lease；
4. 另一个客户端明确收到无资源或等待超时；
5. 第一个客户端 Release 后，第二个客户端可成功获得资源。

**通过标准**：全过程不存在两个 Active Lease 指向同一独占资源。

## E2E-03 客户端崩溃恢复

1. Station01 Acquire OSA01；
2. 模拟客户端进程强制结束；
3. 停止心跳；
4. Lease 在 TTL 后 Expired；
5. Fabric 执行安全 Reset/Disconnect；
6. OSA01 恢复 Available；
7. Station02 可重新 Acquire。

**通过标准**：无需人工修改数据库或重启仪器；完整事件链可查询。

## E2E-04 Fabric 服务重启

1. Station01 持有 OSA01 Active Lease；
2. 重启 Fabric 服务；
3. 服务恢复后 OSA01 仍为 Busy；
4. Station02 无法获得 OSA01；
5. Station01 心跳恢复后 Lease 延续，或超时后进入 Recovery。

**通过标准**：服务重启过程中双重分配次数为 0。

## E2E-05 健康与校准过滤

1. OSA01 Calibration Expired；
2. OSA02 连续 3 次 HealthCheck 失败；
3. 客户端申请 Healthy 且 CalibrationValid 的能力；
4. 两台设备均不得被分配；
5. 返回结构化失败原因。

**通过标准**：不能通过忽略约束的方式返回设备。

## E2E-06 72 小时稳定性

测试条件：

- 2 个 Station；
- 4 台资源；
- 每 1 至 5 分钟随机执行 Acquire、模拟测量、Release；
- 随机注入客户端退出、设备断网、Adapter 错误和服务重启。

**通过标准**：

- 双重分配：0；
- 无法解释的僵尸 Lease：0；
- 审计链完整率：100%；
- 正常资源 Acquire 成功率：≥ 99%；
- 服务恢复后状态一致性：100%。

---

# 11. 测试策略

## 11.1 自动化测试分层

| 层级 | 重点 |
|---|---|
| Unit Test | 匹配规则、状态机、TTL、错误码、权限判断 |
| Component Test | Registry、Lease Service、Health Service、Adapter |
| Contract Test | Test Fabric API 与 UDL Adapter 接口兼容性 |
| Integration Test | 数据库、真实设备、网络异常和服务重启 |
| Concurrency Test | Acquire 原子性、重复请求、锁竞争 |
| E2E Test | Station 到 Fabric 到真实仪器完整流程 |
| Soak Test | 72 小时运行与故障注入 |

## 11.2 必测边界

- TTL 为 0、负数、过大值；
- 重复 Resource ID；
- 未知能力或未知约束；
- 校准日期为空、刚好到期和跨时区；
- Heartbeat 恰好在过期边界到达；
- Release 与 Expire 并发发生；
- 管理员 Force Release 与设备指令并发发生；
- 数据库短暂断开；
- Adapter 超时但设备实际仍在运行。

---

# 12. 数据与基础表结构建议

```text
Resource
├─ ResourceId
├─ Type / Manufacturer / Model
├─ Address / AdapterId / AdapterVersion
├─ Location
├─ AdministrativeState
├─ OperationalState
├─ HealthState
├─ CalibrationDueDate
└─ CreatedAt / UpdatedAt

Capability
├─ CapabilityId
├─ Version
└─ Schema

ResourceCapability
├─ ResourceId
├─ CapabilityId
└─ Properties

Lease
├─ LeaseId
├─ ResourceId
├─ ClientId
├─ Context: WO / SN / Operation
├─ Status
├─ AcquiredAt / ExpiresAt / ReleasedAt
└─ EndReason

ResourceEvent
├─ EventId
├─ ResourceId / LeaseId
├─ EventType
├─ Actor
├─ Timestamp
└─ Payload
```

数据库实现应通过唯一约束或事务保证同一独占资源只有一个 Active Lease，不能只依赖应用层先查后写。

---

# 13. 交付物清单

## 13.1 产品与设计

- 本功能需求与验收标准；
- 资源、能力、Lease 和状态机定义；
- API Contract；
- 错误码清单；
- 权限矩阵；
- 试点部署拓扑图。

## 13.2 软件

- Resource Registry Service；
- Capability & Matching Service；
- Lease/Lock Service；
- Health Monitor；
- Audit/Event Service；
- Web Dashboard；
- OSA Adapter 至少 1 至 2 个型号；
- Power Meter Adapter 至少 1 至 2 个型号；
- Simulator Adapter；
- Station Client SDK，建议优先提供 .NET 版本。

## 13.3 测试与运维

- 自动化测试报告；
- 并发测试报告；
- 72 小时稳定性报告；
- 部署和回滚手册；
- 设备接入指南；
- 异常处理 Runbook；
- Dashboard 使用说明。

---

# 14. 建议实施计划

| 阶段 | 周期 | 主要任务 | 退出条件 |
|---|---:|---|---|
| Sprint 0 | 1 周 | 能力模型、状态机、API、试点设备确认 | 设计评审通过 |
| Sprint 1 | 2 周 | Registry、配置、Simulator Adapter | 资源可注册和模拟连接 |
| Sprint 2 | 2 周 | Discovery、Matching、Acquire/Release | 单客户端闭环通过 |
| Sprint 3 | 2 周 | Lease、Heartbeat、Recovery、并发控制 | 并发互斥测试通过 |
| Sprint 4 | 2 周 | Health、Dashboard、Audit、RBAC | 可观测和权限测试通过 |
| Pilot | 2 至 3 周 | 接入真实 OSA/PM、故障注入、72 小时测试 | E2E 验收通过 |

**建议总周期：约 11 至 12 周。** 其中真实设备 Adapter 的成熟度和设备可用窗口是主要进度风险。

---

# 15. 成功指标

| 指标 | V0.1 目标 |
|---|---:|
| Test Program 中具体设备地址硬编码 | 0 |
| 同一资源双重分配 | 0 |
| 异常客户端导致永久僵尸 Lease | 0 |
| 有可用资源时 Acquire 成功率 | ≥ 99% |
| Acquire P95 | ≤ 1 s |
| Dashboard 状态延迟 | ≤ 5 s |
| 资源使用审计完整率 | 100% |
| 通过 Adapter 新增同类型号时业务代码修改 | 0 |
| 连续稳定运行 | ≥ 72 h |

设备利用率提升不建议作为 V0.1 硬性承诺。试点应先建立使用基线，再比较 Busy Duration、等待时间和设备闲置分布，为 V0.2 的池化收益提供数据。

---

# 16. Go / No-Go 验收门槛

只有同时满足以下条件，V0.1 才允许进入下一阶段：

- [ ] Station 只按能力申请，不包含具体设备地址；
- [ ] 两个 Station 可以共享两台同类型设备；
- [ ] 并发竞争时双重分配为 0；
- [ ] 客户端异常退出后资源能够自动恢复；
- [ ] Fabric 服务重启不会丢失 Active Lease；
- [ ] Offline、Error、Maintenance、CalibrationExpired 资源不会被错误分配；
- [ ] 每次使用均可追溯到 Client、SN、工单、测试和 Lease；
- [ ] OSA 和 Power Meter 至少各完成一个真实设备 Adapter；
- [ ] 72 小时稳定性测试通过；
- [ ] 现有测试结果上传链路不受影响。

如果双重分配、租约恢复或审计链任一项不通过，则判定 **No-Go**，不得进入 OCS 动态连接或 Agent 集成阶段。

---

# 17. 后续版本路线

## V0.2：资源池生产化

- 队列和优先级；
- 预约与计划性占用；
- 更丰富的调度策略；
- 设备利用率与容量分析；
- 与 TAS/MIMS 测试上下文集成；
- 多种 OSA、PM、SMU、VOA 扩展。

## V0.3：Connection Fabric

- OCS 拓扑模型；
- Port Registry；
- Optical Path Planning；
- 路径互斥和衰减预算；
- Connect、Verify、Disconnect；
- Resource Plan + Connection Plan 原子化执行。

## V1.0：Test Orchestration

- Test Recipe；
- 多资源组合申请；
- 工作流执行与补偿；
- Test Data Context；
- 统一结果和追溯。

## V2.0：Agent-ready Test Fabric

- 以 Test Intent 提交目标；
- Agent 生成候选 Test Plan；
- Fabric 执行确定性资源和安全约束；
- Human Approval；
- Diagnosis、Re-plan 和闭环优化。

---

# 18. 最终结论

Test Fabric V0.1 不应被定义为“大而全的测试平台”，而应被定义为一个可验证的基础设施最小闭环：

```text
Register
   ↓
Describe Capability
   ↓
Discover
   ↓
Acquire + Exclusive Lease
   ↓
Execute through Adapter
   ↓
Heartbeat + Observe
   ↓
Release / Recover
   ↓
Audit
```

V0.1 最关键的验收不是 Dashboard 是否漂亮，也不是接入设备数量，而是以下三个不变量：

1. **业务申请能力，不指定设备；**
2. **独占资源永不双重分配；**
3. **任何异常都能恢复并留下证据链。**

只要这三个不变量在真实 OSA、Power Meter 和两个测试工位上成立，Test Fabric 就完成了从传统 Test Automation 向 Software-Defined Testing 的第一步，并具备继续演进到 OCS、Test Orchestration 和 Test Agent 的工程基础。
