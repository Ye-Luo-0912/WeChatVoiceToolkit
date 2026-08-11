# WeChatVoiceToolkit 后续实施计划

> 目标：先把当前产品链路补到“可持续日常使用”的程度，再继续扩展高级能力。
>
> 本文面向后续 Agent / Codex 开发。实现时必须继续遵守根目录 `AGENTS.md`、`docs/architecture.md`、`docs/security.md` 和现有 Verified/Workflow 边界。

## 0. 当前阶段判断

当前底层主链已经具备较强的正确性、安全性和恢复能力：

- Stable Snapshot 与 group-level 校验已经存在；
- 4.1.11.55 的受控 Key Broker / SQLCipher materialization 主链已跑通；
- `VerifiedRawSnapshot -> VerifiedLocalWorkspace -> contact -> scan -> raw SILK export` 已可工作；
- Export 已使用 `SourceStableKey`、content/hash 校验、transaction/journal/manifest，并能安全跳过已存在且验证一致的 artifact；
- Materialization 已有 recover/adopt/verify/delete；
- Dataset curation / build / verify / repair / delete 已有基础实现；
- Duration resolver / cache、SILK decoder boundary、prepared-selection spool cleanup 已有底层能力。

当前主要问题已经从“能否提取”转向：

1. **产品没有以“本地已有可信状态”为第一入口，用户仍容易重复走昂贵流程；**
2. **Snapshot / Workspace / temp / failed state 缺少统一生命周期管理，容易形成孤儿数据；**
3. **Duration/decoder/quality 仍偏开发能力，没有形成默认可用的产品体验；**
4. **Dataset 虽能构建，但距离真正训练可直接消费的数据集仍有最后一段；**
5. **Desktop 仍偏“按步骤跑流程”，缺少“继续上次工作 / 使用已有数据 / 明确刷新”的产品模型。**

后续优先级：**先完成现有功能闭环，不先扩展新的复杂模型能力。**

---

# 1. 总体原则：Reuse -> Recover -> Recompute

以后所有昂贵阶段统一采用：

```text
Inspect local state
    |
    +-- Valid + reusable  -> Reuse
    |
    +-- Recoverable       -> Recover
    |
    +-- Stale / invalid   -> Recompute
    |
    +-- Missing           -> Create
```

## 1.1 非协商约束

### A. 不重复运行可验证复用的流程

只要本地已有输出满足当前输入身份且重新验证通过，就不应再次：

- 重新创建相同 Snapshot；
- 重新做相同 Snapshot 的 Materialization；
- 重新弹 UAC / 重新获取 key；
- 重新生成已存在且 hash 一致的 SILK；
- 重新构建 selection fingerprint 完全一致且验证通过的 Dataset；
- 重复启动 decoder 做已有有效 cache 的 duration 分析。

### B. 复用之前必须验证

“文件存在”永远不等于“可复用”。

复用必须依赖现有可信边界，例如：

- Snapshot manifest + source/snapshot identity；
- materialization state + manifest + output hashes；
- `VerifiedLocalWorkspace`；
- Export metadata commit + artifact hash；
- Dataset build manifest + selection/profile fingerprint；
- duration cache + decoder identity/version。

### C. 不要把旧 Snapshot 当成“最新微信数据”

Snapshot 是一个冻结时间点。

已有 Snapshot 只有两种情况下可以直接复用：

1. 用户明确选择“继续使用这个已有 Snapshot”；
2. 能证明当前源数据集与该 Snapshot 对应的源状态未变化。

如果用户明确执行“刷新微信数据 / 获取最新语音”，必须重新判断源数据是否变化；不能因为本地已有 Snapshot 就静默使用旧数据。

### D. Recover 优先于重新创建

例如：

- `Completed` materialization -> verify + reuse；
- `FailedRecoverable` -> recover/adopt；
- Workspace JSON 丢失但 materialization 已完成 -> repair/adopt；
- export metadata alias 缺失 -> repair；
- dataset metadata 损坏但 audio 正常 -> repair derived metadata。

只有无法安全恢复时才重新计算。

---

# 2. P0-A：项目级 Resume / Local State Reuse

这是下一阶段最高优先级。

## 2.1 新增 Project Resume/State Inspection Workflow

建议新增一个共享 Workflow，而不是把逻辑写进 Desktop：

```text
IProjectStateWorkflow
  InspectAsync(...)
  ResumeAsync(...)
```

或者拆成：

```text
ProjectStateInspector
ProjectResumePlanner
```

必须位于 `WeChatVoice.Workflows` 的产品编排边界；Desktop/CLI 只展示状态和执行用户选择。

### 输出建议

```text
ProjectStageState
- Environment
- Snapshot
- Materialization
- Workspace
- Scan
- Export
- Curation
- Dataset
```

每个阶段至少有：

```text
Missing
ValidReusable
Recoverable
Stale
Invalid
Busy
```

并携带：

- canonical identity；
- verified/recovery reason；
- input binding；
- next recommended action；
- 是否需要 UAC；
- 是否会产生新磁盘数据。

## 2.2 Desktop 启动行为改为 Resume-first

现有 Desktop 不应默认让用户每次都从：

```text
Environment -> Snapshot -> Materialization -> Contact -> Scan -> Export
```

重新走一遍。

建议启动后优先显示：

```text
继续上次工作
- 已验证 Workspace
- 上次 Export
- 已构建 Dataset

刷新微信数据
- 重新检查源数据库
- 仅在数据变化时创建新 Snapshot
```

### 验收标准

第二次打开应用、继续同一项目时，如果已有：

- verified materialization；
- valid workspace；
- valid export；

则：

- 不弹 UAC；
- 不重新获取 key；
- 不重新 materialize；
- 不创建新 Workspace GUID fallback；
- 直接恢复到 Contact / Scan / Export / Dataset 状态。

---

# 3. P0-B：Snapshot / Workspace 去重与复用

## 3.1 Snapshot：减少重复副本

当前默认 Snapshot 目录每次创建唯一 operation 目录，这是安全的，但长期会产生大量完整副本。

不要简单删除该安全 staging 机制；应在 commit 层增加“验证后复用”。

建议：

```text
copy -> staging
     -> verify + derive SnapshotId
     -> canonical object exists?
          yes -> verify existing -> delete staging -> reuse
          no  -> atomic publish
```

### 可选目录模型

```text
Data/Snapshots/<account-fingerprint>/<snapshot-id>/
```

如果暂时不改目录模型，也必须至少实现：

- 同内容 Snapshot 检测；
- UI 提示“已有相同快照”；
- 不长期保留重复内容。

## 3.2 Workspace：已有 canonical output 时先 Inspect

`WorkspaceOutputDirectoryFactory` 不能继续把：

```text
canonical occupied -> allocate GUID fallback
```

作为默认路径。

应改为：

```text
canonical occupied
    |
    +-- Completed        -> verify/reuse
    +-- FailedRecoverable-> recover
    +-- Active           -> OperationBusy
    +-- Invalid/Stale    -> cleanup/replace after validation
```

只有用户明确要求“另建一份”时才创建 GUID fallback。

### 验收标准

同一个 Snapshot 连续执行 materialization 两次：

- 第二次默认不重新 materialize；
- 不创建 `<fingerprint>-<guid>`；
- 验证成功后直接复用已有 Workspace；
- Workspace JSON 丢失时优先 repair/adopt。

---

# 4. P0-C：Managed Storage Lifecycle / GC

需要统一管理：

```text
%LocalAppData%/WeChatVoiceToolkit/Data/Snapshots
%LocalAppData%/WeChatVoiceToolkit/Data/Workspaces
Export roots
Dataset builds
%TEMP%/WeChatVoiceToolkit/...
%TEMP%/wechatvoice-duration
%TEMP%/wechatvoice-decoder
```

## 4.1 不要把 RecentWorkspaceStore 当所有权数据库

`RecentWorkspaceStore` 只应该表示“最近使用”。

必须明确：

```text
not recent != orphan
not in recent list != safe to delete
```

建议新增：

```text
ManagedStorageInventory
StorageLifecyclePlanner
StorageCleanupWorkflow
```

Source of truth 优先使用现有 manifest/state marker；中心 catalog 只保存：

- last access；
- pinned；
- user-owned；
- cleanup policy；
- display metadata。

即使 catalog 丢失，也应该能从磁盘 Manifest 重新发现应用自有对象。

## 4.2 资产类型

至少区分：

```text
Transient
RecoverableIntermediate
ReusableIntermediate
UserAsset
DerivedUserAsset
```

建议默认：

| 类型 | 示例 | 默认策略 |
|---|---|---|
| Transient | decoder temp / spool / staging | 自动清理 |
| RecoverableIntermediate | FailedRecoverable materialization | TTL 后清理，可恢复窗口 |
| ReusableIntermediate | Snapshot / completed plaintext Workspace | 根据引用/最近使用策略清理 |
| UserAsset | raw SILK Export + private/public manifests | 不自动删除 |
| DerivedUserAsset | curated Dataset | 不自动删除 |

## 4.3 明文 Workspace 优先清理

Materialized Workspace 是普通 SQLite，敏感程度高且占用空间大。

产品应提供：

- “Export 成功后自动清除明文 Workspace”选项；
- 默认保留一个短恢复窗口；
- 删除必须复用 `DeleteMaterializedWorkspaceWorkflow` 的验证边界；
- 不能直接 `Directory.Delete` 绕过 manifest/state 检查。

## 4.4 Startup orphan sweep

已有 `PreparedSelectionSpool.CleanupOrphans()` 可作为模式参考。

统一补：

- stale Snapshot staging；
- stale materialization staging；
- stale decoder input/output；
- stale duration WAV；
- stale transaction staging；
- dangling recent metadata；
- app-owned orphan Workspace。

注意：

- 只允许清理已知应用目录和已知命名协议；
- 拒绝 reparse point；
- 有 active lock/lease 的对象不能清；
- 先 preview，再执行大对象删除；
- raw SILK Export / Dataset 不得被 startup GC 默认删除。

## 4.5 Storage 页面

增加一个存储管理页，显示：

```text
Snapshots               xxx MB
Plaintext Workspaces     xxx MB
Exports                  xxx MB
Datasets                 xxx MB
Temp / Recoverable       xxx MB
Safely reclaimable       xxx MB
```

提供：

- 立即清理临时文件；
- 删除已验证无引用旧 Snapshot；
- 清理可恢复但过期 Workspace；
- 打开目录；
- pin/unpin；
- 设置 retention。

---

# 5. P0-D：Duration 功能产品化

当前 duration 底层接口和 decoder boundary 已有，但普通用户不应该依赖手动配置环境变量后才“突然可用”。

## 5.1 Decoder Discovery / Configuration

需要一个正式产品路径：

```text
Decoder status
- Available
- Missing
- Untrusted / unsupported
- Failed self-test
```

实现优先级：

1. 如果许可证/分发条件允许，随正式包提供 reviewed decoder；
2. 如果不能内置，在 UI 提供明确的 decoder 配置/检测入口；
3. 环境变量保留为高级/开发入口，不作为唯一用户入口。

不要为了 duration 把 WAV 变成 Raw Export 的强制依赖。

## 5.2 Persistent duration enrichment

已有 duration cache 应成为正式流程的一部分。

要求：

- 只对 unknown / stale decoder-version 项重新计算；
- cache key 必须包含 payload identity/hash 与 decoder identity/version；
- decoder 升级后旧 cache 不可错误复用；
- duration 结果最终能进入 curation/export metadata；
- 单条失败不影响其他条目；
- temp WAV 仍然即时清理。

### 验收标准

同一批 1,000 条 SILK：

- 首次 duration analysis 执行 decoder；
- 第二次相同 decoder + 相同 payload 不再解码；
- 更新 decoder identity 后仅失效相关 cache；
- UI 不再长期显示全量 `duration-unknown`。

---

# 6. P0-E：训练数据真正可用

当前 Dataset build 更接近“精选 SILK 的可验证副本”。需要再补训练消费层。

## 6.1 保留 Raw Export 与 Training Build 两层

不要把原始导出改成自动全量 WAV。

推荐：

```text
Raw Export
  original/*.silk
      |
      v
Curation
      |
      v
Training Build
  audio/*.wav
  dataset.json
  dataset.csv
  build-manifest.json
```

这样：

- 原始 SILK 始终是 source of truth；
- 只对最终选择条目产生 WAV；
- 节省磁盘；
- decoder/normalization 改动后可以重建派生训练集。

## 6.2 Training Build 支持 WAV

增加明确的 build profile，例如：

```text
AudioBuildProfile
- output format: WAV PCM
- sample rate
- mono/stereo policy
- normalization policy
- decoder identity
```

第一版只需要可靠，不先堆复杂 DSP。

至少做到：

- SILK -> validated PCM WAV；
- 保留时长；
- 保存 decoder identity；
- 输出 hash；
- build 可验证；
- profile 相同 + source 相同 -> reuse；
- profile 改变 -> 新 build identity，不覆盖旧 build。

## 6.3 音频预览

Dataset Curation 页至少需要：

- 播放/停止；
- 当前时长；
- 文件大小；
- 日期；
- incoming/outgoing；
- quality flags；
- duplicate group；
- selected 状态。

预览可以临时 decode，不要求先持久生成 WAV。

## 6.4 Direction 功能补全

底层已经有 direction 概念，Desktop 最终应明确支持：

```text
Incoming
Outgoing
Both（如果语义和去重规则明确）
```

不要把“incoming first-pass”永久固化成产品限制。

---

# 7. P1：基础音频质量分析

P0 完成后再补，不要阻塞基本可用。

第一批只做低成本确定性分析：

- decode success；
- duration；
- sample rate / channels / PCM format；
- silence ratio；
- clipping ratio；
- RMS / peak；
- obvious empty/corrupt audio；
- optional loudness estimate。

输出统一进入 `QualityFlags` / structured audio metadata。

不要第一版就引入复杂神经网络 quality model。

> **状态：已完成。** `VoiceQualityAnalysis` / `VoiceQualityAnalyzer`（bounded streaming
> PCM 分析：decode success、duration、sample rate/channels/PCM、silence ratio、
> clipping ratio、RMS/peak、empty/silent/clipping/low-level/decode-failed/
> duration-mismatch flags）已实现并接入 WAV dataset build；每个派生条目合并
> quality flags，Dataset Repair 从磁盘 WAV 重算，保证重建元数据一致。已覆盖
> Core 单元测试与 WAV build 集成测试。

---

# 8. P1：Scan / Prepared Selection 持久复用

当前大型 selection 可以落临时 spool，但它本质上仍是短期 retry 数据。

可以增加一个持久的、绑定 verified Workspace + query fingerprint 的 Scan Cache：

```text
workspace identity
+ catalog fingerprint
+ query fingerprint
+ selection engine version
+ duration resolver version
-> prepared selection cache
```

只有所有绑定一致时才复用。

用途：

- 用户关闭应用后回来，不需要重新扫大量 metadata；
- Export retry 不需要重新扫描；
- curation 可以继续从稳定 result-set 开始。

注意：不要把 cache 变成第二套 authoritative database。

> **状态：已完成。** `ScanCacheService`（绑定 verified Workspace + query fingerprint 的持久
> scan cache，JSONL 序列化 VoiceRecords + `ScanCacheReportDto` 报告，SHA-256 完整性校验，
> 大结果经临时 spool 落盘）已接入 `VoiceScanWorkflow`：指纹一致时直接复用缓存，指纹变化或
> 校验失败时重新扫描并写缓存。缓存目录 `Data/scan-cache` 已纳入 `ManagedStorageInventory`
> 的 transient 扫描。已覆盖 `ScanCacheService` 单元测试与 workflow 级 cache reuse/miss 测试。

---

# 9. P1：用户可理解的“刷新”语义

必须区分：

```text
Continue existing project
Refresh from Weixin source
Re-scan current Workspace
Re-analyze duration/quality
Rebuild training dataset
```

现在用户容易把这些看成“重新跑流程”。后续 UI 要把动作语义拆开。

建议：

- **继续**：尽可能复用一切有效状态；
- **刷新微信数据**：检查源变化，必要时新 Snapshot；
- **重新扫描**：不重新 Snapshot/Materialization；
- **重新分析音频**：不重新导出 SILK；
- **重建 Dataset**：不修改 Raw Export。

---

# 10. P1：Run / Metadata Retention

`runs/`、journal、transaction、per-run manifests 长期会增长，但它们通常比音频/DB 小很多。

先不要激进删除。

后续实现：

- 保留最近 N 个完整 run；
- 旧 run 可以 compact 成 summary；
- 如果某 Dataset / profile 仍引用旧 run manifest，则不可删除；
- `latest.metadata-commit.json` 永远不是删除旧 run 的唯一依据。

---

# 11. 暂缓功能

以下能力先不要抢占 P0：

- ASR / 自动转写；
- Speaker embedding；
- 神经网络音质评分；
- RVC/SVC/TTS 一键训练；
- 多模型训练调度；
- 未验证新版 Weixin schema 的 heuristic fallback；
- Agent 化 orchestration。

这些可以后续增加，但当前先把“提取 -> 复用 -> 整理 -> 可训练数据”闭环做好。

---

# 12. 建议实施顺序

## Phase 1 — Resume / Reuse

### 目标

用户第二次打开应用时，不再重复昂贵主链。

### 工作项

- [ ] Project state inspector / resume planner；
- [ ] Completed materialization verify + reuse；
- [ ] FailedRecoverable automatic recovery path；
- [ ] Workspace JSON repair/adopt path 接入 Resume；
- [ ] canonical Workspace 先 inspect，不直接 GUID fallback；
- [ ] Existing Snapshot 显式 continue / refresh 语义；
- [ ] Desktop Resume-first UI；
- [ ] integration tests：第二次运行不触发 Broker/UAC/materialization。

### 完成定义

同一项目关闭并重新打开：

```text
0 次重复 Snapshot（继续旧数据时）
0 次重复 materialization
0 次重复 UAC
0 次重复已验证 SILK 写入
```

---

## Phase 2 — Storage Lifecycle

### 工作项

- [x] Managed storage inventory；
- [x] ownership/reachability 分类；
- [x] cleanup preview；
- [x] temp orphan startup sweep；
- [x] stale/recoverable Workspace retention；
- [x] Snapshot duplicate detection；
- [x] dangling Recent metadata repair；
- [x] Storage UI；
- [x] safe-delete tests / crash tests / reparse-point tests。

### 完成定义

连续重复运行/失败/取消 50 次后：

- app-owned temp/staging 不无限增长；
- 同一 Snapshot 不产生无意义 Workspace GUID 链；
- 可恢复数据不会被误删；
- raw Export / Dataset 不会被自动 GC；
- UI 可解释磁盘占用来源。

---

## Phase 3 — Duration Productization

### 工作项

- [x] decoder status / discovery；
- [x] user-facing decoder configuration；
- [ ] optional packaged reviewed decoder（如果许可允许）；
- [x] duration cache reuse；
- [x] unknown/stale only re-analysis；
- [x] metadata enrichment；
- [x] Desktop progress / error summary。

### 完成定义

导出数据不再长期全是 `duration-unknown`，相同数据重复打开不会重复 decode。

---

## Phase 4 — Training-ready Dataset

### 工作项

- [x] Dataset audio preview；
- [x] selected SILK -> WAV build；
- [x] AudioBuildProfile；
- [x] build fingerprint / reuse；
- [x] duration/quality metadata；
- [x] direction selection；
- [x] verify/repair/delete 覆盖 WAV derived artifacts。

### 完成定义

用户能完成：

```text
选择联系人
-> 查看/试听语音
-> 过滤/去重/选择
-> 构建 WAV 训练集
-> 关闭应用
-> 再打开后继续使用同一数据集
```

而无需重新读取微信进程或重新 materialize。

---

# 13. Agent 开发注意事项

1. **先复用现有实现，不创建第二套生命周期。**
   - Snapshot verification；
   - Materialization recovery/delete；
   - Export verification/repair；
   - Dataset verification/repair/delete；
   - duration cache；
   - cleanup queue；
   都已经存在部分基础。

2. **不要为了 Resume 绕过 Verified 边界。**
   Resume 的本质是“重新验证后复用”，不是“信任磁盘存在”。

3. **不要让 Desktop 自己判断数据库/manifest。**
   状态检查必须进入 Workflows/Infrastructure 的正式边界。

4. **不要让 Recent list 变成真相来源。**
   Recent 是 UX index；manifest/state 才是恢复依据。

5. **任何自动删除先做明确 Ownership + Preview。**
   只有 app-owned transient/recoverable 数据允许默认 GC。

6. **保留 SILK source of truth。**
   WAV、duration、quality、ASR 都是可重建 derived artifact。

7. **性能目标优先避免重复 I/O，而不是先做低层微优化。**
   当前收益最大的优化依次是：
   - 不重复 snapshot/materialize；
   - 不重复 hash/decode；
   - 不重复 scan；
   - 不重复 dataset copy；
   然后才是局部 allocation/parallelism 优化。

8. 每个阶段完成后更新：
   - `README.md`；
   - `docs/roadmap.md`；
   - `docs/architecture.md`（如果边界变化）；
   - `docs/agent-handoff.md`；
   - 对应单元/集成/Avalonia Headless 测试。

---

# 14. 最终产品目标

完成以上 P0 后，普通用户的实际体验应从：

```text
每次启动
-> 找微信
-> 快照
-> UAC
-> 解密
-> Workspace
-> 扫描
-> 导出
```

变成：

```text
启动
-> 发现已有项目
-> 验证本地状态
-> 继续整理/试听/构建数据集
```

只有用户明确选择 **“刷新微信数据”**，或者现有状态无法验证/恢复时，才重新进入 Snapshot / Materialization 主链。

这应作为下一阶段所有实现的核心验收标准。
