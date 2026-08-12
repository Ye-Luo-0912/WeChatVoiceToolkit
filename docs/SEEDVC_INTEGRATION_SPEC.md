# Seed-VC 集成搭建要求（WeChatVoiceToolkit）

> 目标：在现有「扫描 → 导出 → 数据集整理 → 生成训练集 WAV」产品链之后，接上 **Seed-VC 微调**（非零样本），并可混入手机直录朗读数据。  
> 给实现 Agent：先读仓库根目录 `AGENTS.md`、`docs/architecture.md`（若存在）、`docs/security.md`（若存在），以及本文件。不要绕过 Verified / Workflow 边界。

## 0. 背景与已有能力（不要重做）

项目路径：`D:\Users\Ye_Luo\APP\Test\WeChatVoiceToolkit`  
技术栈：.NET 10 / CLI + Avalonia Desktop；数据集相关已有：

- `DatasetCurationWorkflow` / Desktop「数据集整理」
- `DatasetBuildService`：从已校验 export + selection profile 生成派生训练集
- `AudioBuildProfile`：默认 **48 kHz / mono / 16-bit PCM WAV**，SILK 为源、WAV 为派生
- CLI：`CliDatasetCommand`（build / verify / repair / delete 等）

**本阶段不要重做微信扫库、解密、SILK 导出。** 只在 Dataset 产物之后增加「训练准备 + Seed-VC 微调编排」。

## 1. 用户目标（验收口径）

1. 用现有工具导出的微信训练集 WAV（约 16 分钟 / 75 条量级）+ 另有约 **3 分钟手机直录朗读**（非微信压缩）。
2. **不做一体 TTS 训练**；只要高质量音色克隆（VC）。
3. 选用 **Seed-VC fine-tune**（用户零样本试过，一般，但比多数 TTS 零样本好；现改微调一步到位）。
4. 训练机：**NVIDIA RTX 3060 Ti**（按 8GB 显存设计默认参数，可配置）。
5. 后续可接任意 TTS：TTS 出声 → Seed-VC 转换。
6. 产品内体验：从 Desktop/CLI 能走到「准备好 Seed-VC 数据集 → 一键启动/继续微调 → 看到 ckpt 与试听入口」，失败有可读错误。

## 2. 架构原则（必须遵守）

1. **复用现有 Dataset build**，不要平行再写一套 SILK→WAV。
2. UI / CLI 只编排 Workflow + Ports；**不要**在 Desktop 里直接拼 Python/CUDA 细节到业务核。
3. Seed-VC 以 **外部工具链** 存在（推荐 `tools/seed-vc/` 或用户可配置的已有 Seed-VC 安装目录），本仓库负责：
   - 数据规整与清单
   - 调用训练/推理脚本
   - 记录 run manifest / 日志路径 / checkpoint 指纹
4. 训练数据与 checkpoint **默认落在 LocalApplicationData 或用户指定目录**，禁止写进 git；更新 `.gitignore`。
5. 遵守 `AGENTS.md`：不记录联系人、密钥、明文敏感路径到普通日志；诊断只保留阶段/错误码/耗时。
6. Prefer **Reuse → Recover → Recompute**（见 `NEXT_STEPS.md`）：同一 selection + prep profile 已验证则复用，不重切。

## 3. 建议新增产品面

### 3.1 新 Workflow（名称可调整，职责固定）

例如 `SeedVcPrepareWorkflow` + `SeedVcTrainWorkflow`（或合并为 `VoiceCloneWorkflow` 两阶段）：

**A. Prepare（数据准备）输入**
- 已验证的 Dataset build 目录（含 WAV + `build-manifest.json` / `dataset.json`）
- 可选：外部「高质量锚点」音频目录（手机朗读）
- Prep profile：
  - `min_seconds` / `max_seconds`（Seed-VC 要求 **1–30 秒/文件**，建议默认切 **3–10 秒**）
  - `loudness`：轻度峰值/响度归一（可先接现有 `FfmpegWavNormalizer`）
  - `denoise`：默认 off；仅可选轻度
  - `anchor_weight`：锚点文件复制倍数（默认 2）
  - `wechat_keep_ratio` 或质量门槛：宁可少要干净

**Prepare 输出目录结构（建议）**
```text
seedvc-prep/<prepFingerprint>/
  audio/                 # 最终送训的 1–30s wav
  manifests/
    sources.jsonl        # 每条：source_type(wechat|phone), src_path_hash, duration, kept/rejected reason
    prep-manifest.json   # profile、统计、依赖的 dataset build fingerprint
  rejected/              # 可选，便于人工复查
```

**B. Train（微调编排）输入**
- prep 目录
- Seed-VC 根目录 / python 环境
- config 预设（默认说话/离线）：
  - `configs/presets/config_dit_mel_seed_uvit_whisper_small_wavenet.yml`
  - **不要**默认唱歌 44k 配置
- `batch_size` 默认 1 或 2（3060 Ti）
- `max_steps` / `max_epochs` / `save_every` 可配
- `run_name`

**Train 输出**
```text
seedvc-runs/<runName>/
  train.log
  run-manifest.json      # config hash、prep fingerprint、gpu、命令行、开始/结束时间、最佳 ckpt 提示
  checkpoints/...        # 或软链到 Seed-VC 默认输出，但必须在 manifest 记下绝对/相对路径
```

### 3.2 CLI

在现有 dataset 命令旁增加（命名随项目风格）：

- `seedvc prepare --dataset <path> [--anchor <path>] [--out <path>] ...`
- `seedvc train --prep <path> --seedvc-root <path> [--config ...] [--batch-size 1] ...`
- `seedvc doctor`：检查 Python、CUDA/torch、Seed-VC checkout、ffmpeg、磁盘
- `seedvc infer`（可 P1）：源音频 + 参考/目标 run → 输出转换结果，方便试听

### 3.3 Desktop

在「数据集整理」成功之后增加一步（新页或同页折叠区即可）：

1. 选择/确认当前 Dataset build  
2. 选择手机锚点文件夹（可选）  
3. 一键 Prepare，展示保留/剔除统计  
4. 配置 Seed-VC 路径与训练参数 → 启动 Train（进度：日志尾部 + step）  
5. 完成后展示 checkpoint 路径 +「打开目录 / 试听」  

不得在 UI 层重实现 dataset verify；先走现有 verify。

## 4. 数据处理规则（业务硬约束）

| 规则 | 说明 |
|---|---|
| 时长 | 每条最终 wav **1–30 秒**；<1s 丢弃；>30s 按气口/静音切开，切不了再硬切并避免吞字 |
| 格式 | wav PCM；单声道；采样率可保持 48k（Seed-VC 训练会自行重采样） |
| 微信数据 | 先按现有「可训练/已选」集合；再二次听感规则：过糊、叠音、非本人、强烈噪声 → reject |
| 手机朗读 | 全量优先保留；可按 `anchor_weight` 复制；作为音色锚点 |
| 降噪 | 默认关闭；仅用户显式开启轻度 |
| 合成数据 | **第一期不做**；预留目录/开关即可 |
| 清单 | 每条要能追溯到 dataset item id 或外部文件哈希，便于复现 |

切片实现：优先 ffmpeg（项目已有相关能力则扩展，不要引入第二个体系）。

## 5. Seed-VC 调用约定

- 上游：https://github.com/Plachtaa/seed-vc  
- 微调数据要求与官方一致：文件夹内音频、1–30s、常见音视频后缀；说话人标签非必须。  
- Windows：`num_workers=0`  
- 本仓库通过 **明确 argv 的 Process 启动** 训练脚本，捕获 stdout/stderr 到 `train.log`；禁止 `shell=true` 拼接不可信路径。  
- `seedvc doctor` 失败时给出可行动提示（缺 CUDA / 缺权重 / 路径不对），不要静默。

## 6. 非目标（本阶段明确不做）

- 不训练 GPT-SoVITS / RVC（可在 docs 留扩展点）
- 不把 Seed-VC Python 训练循环搬进 C#
- 不自动爬取/上传任何云；默认本地
- 不在未确认授权下改用户全局 Python
- 不把真实语音样本提交进 git

## 7. 测试与验收

**自动化**
- Prepare：给定夹具 wav（短于 1s / 正常 / 长于 30s / 立体声）→ 断言过滤、切片、mono、manifest 统计
- 指纹：相同输入 + profile → 复用同一 prep 目录；改 anchor_weight → 新指纹
- Doctor：模拟缺目录/缺配置时的错误码
- 回归：现有 Dataset build/verify 测试全绿

**人工（3060 Ti）**
1. 用现有 Desktop 做出微信训练集  
2. 放入 3 分钟手机朗读锚点  
3. Prepare → 目检 `audio/` 条数与时长分布  
4. Train 至少跑通保存 1 个 ckpt  
5. 用一段非本人 TTS/录音做转换试听：应明显贴近目标音色，且优于用户之前的零样本

## 8. 建议实现顺序（给 Agent 的迭代切片）

1. **P0** `seedvc doctor` + 配置模型（Seed-VC root、python）  
2. **P0** Prepare 服务 + CLI + 单元测试  
3. **P0** Train 编排 + run-manifest + 日志  
4. **P1** Desktop 向导页（接在数据集整理后）  
5. **P1** Infer/试听  
6. **P2** 合成数据增强开关（默认关）

## 9. 给实现 Agent 的开工指令（可原样粘贴）

```text
在 D:\Users\Ye_Luo\APP\Test\WeChatVoiceToolkit 上实现 Seed-VC 微调集成。
先读 AGENTS.md 与 docs/SEEDVC_INTEGRATION_SPEC.md，严格按该 spec 的边界与阶段做。
不要重做微信导出；扩展 Dataset 之后的 Prepare/Train 编排。
默认面向 RTX 3060 Ti、Seed-VC fine-tune（whisper-small 说话配置）、微信 WAV + 手机朗读锚点。
先完成 P0（doctor/prepare/train CLI + 测试），再做 Desktop。
每完成一个切片说明改动文件与如何本地验证。
```

## 10. 决策记录（截至需求提出时）

- 克隆路线：Seed-VC 微调优先于 RVC（用户决定先 Seed-VC 一步到位）
- 数据：微信约 16 分钟覆盖 + 手机朗读约 3 分钟锚点
- TTS：后期再接，不进本期训练
