# 补丁特征与版本适配说明

本文档面向**维护者**，说明补丁工具是如何识别并修改 PCL2 的，以及如何为未来的 PCL2 版本更新特征。

---

## 一、为什么是 IL 补丁，而不是改配置

上游在 `Modules/ModSecret.vb` 中定义了：

```vb
Public Const RestrictMcDownload As Boolean = True
```

`Const` 在 VB 编译期会被**常量折叠**：所有 `If RestrictMcDownload Then … Else …` 中，恒为 `True` 的分支被保留、`Else`（真实下载逻辑）被**整段删除**。因此编译后的 `Plain Craft Launcher 2.exe` 里：

- 已经**没有** `RestrictMcDownload` 这个字段；
- `PageDownloadInstall.LoaderInit` 里只剩一个构造“假 LoaderTask”的调用，其加载委托是 `Sub() Throw New Exception("…qwq…")`；
- 真实字段 `PCL.ModDownload::DlClientListLoader` 仍然存在（在 `Init` / `LoadMinecraft_OnFinish` 中照常使用）。

所以补丁的目标不是“改一个开关”，而是**把假 Loader 替换回真实字段**。

---

## 二、七处补丁的定位方法

### 补丁 A —— 恢复原版游戏下载

**目标方法**：`PCL.PageDownloadInstall.LoaderInit`

该方法的职责是调用 `MyPageRight.PageLoaderInit(...)`，其中第 5 个参数是一个 `LoaderTask(Of String, DlClientListResult)`。被禁用后，这个参数被构造为一个“假 LoaderTask”：

```
ldstr "关闭下载"
…（构造 action 闭包）
ldnull
ldc.i4.2                       ; ThreadPriority
newobj LoaderTask`2<...>::.ctor(...)   ; 假 Loader
```

**修改**：删除从 `ldstr` 到 `newobj` 的整段（即“构造假 Loader 的参数”），在起点插入一条：

```
ldsfld PCL.ModDownload::DlClientListLoader
```

由于删除段和插入都只向栈上压入 **1 个 LoaderTask**，栈深度不变，因此把方法体的 `KeepOldMaxStack` 设为 `true` 以跳过 dnlib 的重新计算（避免写回时 maxstack 校验失败）。

**真实字段的查找**（多版本兼容）：在 `PCL.ModDownload` 类型的字段中，取名字以 `DlClientList` 开头的字段，而非硬编码完整名。

### 补丁 B —— 恢复版本修改页的“我的世界”卡片

**目标方法**：`PCL.PageDownloadModify.LoadInstanceFinished`

被折叠为：

```
ldfld CardMinecraft             ; x:Name 生成的是字段（旧版本可能是 get_CardMinecraft）
ldc.i4.2                       ; Visibility.Collapsed = 2
callvirt set_Visibility(...)
```

**修改**：把紧跟在 `CardMinecraft` 访问（`ldfld` 或 `callvirt get_CardMinecraft`）之后、且下一条是 `set_Visibility` 的 `ldc.i4.2` 改成 `ldc.i4.0`（Visible）。

### 2.99.0.1 新增的五处（C、D1–D4）

2.99.0.1 把更多下载入口也接到了 `RestrictMcDownload` 上。由于 `Const True` 同样会被折叠，这些位置在 2.99.0.1 的 IL 里都只剩一个“永远隐藏”的常量。与 A 不同，它们不需要重建整段逻辑，只需按固定特征改常量或补一个判断：

| 补丁 | 目标 | 折叠后的 IL 特征 | 修改 |
|------|------|------------------|------|
| C | `PCL.PageDownloadLeft..ctor` | `ldfld ItemInstall` / `ldfld LabResource` 后接 `ldc.i4.2; callvirt set_Visibility`；`PageID` 被赋为 `2`（DownloadMod）；`ItemMod.Checked = True` | 两处可见性 `2 → 0`；`PageID` `2 → 0`（DownloadInstall）；`ItemMod.Checked` `1 → 0` |
| D1 | `PCL.PageSelectRight.McInstanceListUI` | 提示文案 `"未找到任何版本的游戏…"` 之后出现 `ldfld BtnEmptyDownload; ldc.i4.2; set_Visibility` | `ldc.i4.2 → ldc.i4.0`（只改 Else 分支，`ShowHidden` 分支保持隐藏） |
| D2 | `PCL.PageLaunchLeft.RefreshButtonsUI` | 无版本分支被折叠为 `ldc.i4.1; stloc CurrentState; br; ldc.i4.3; stloc CurrentState` | `ldc.i4.1 → ldc.i4.2`，恢复「下载游戏」状态 |
| D3 | `PCL.PageResource.set_PageType` | `ldfld BtnSearchInstallModPack` 后是条件求值残渣（`ldarg.1; ldc.i4.2; pop; pop`），最后 `ldc.i4.2; set_Visibility` | 删除残渣，插入 `Value = ResourceTypes.ModPack` 判断：相等 `Visible`，否则 `Collapsed` |
| D4 | `PCL.PageInstanceModDisabled.BtnDownload_Loaded` | 方法体第一条即 `ldc.i4.2`（可带 `box`）后接 `stloc` | `ldc.i4.2 → ldc.i4.0` |

这些特征都带**类型名 + 方法名 + 字段名**，且只匹配折叠后的直线形态；2.99.0.0 同名位置仍是带分支的正常逻辑，不会命中。补丁后形态改变，重复运行也不会再次命中（幂等）。

> D3 插入 `bne.un` / `br` 后会使栈深从 2 增加到 3，因此该方法必须让 dnlib 重新计算 `MaxStack`（`KeepOldMaxStack = false`）；其余补丁只改常量，栈深不变。

---

## 三、版本适配策略（代码中的 `Patch*` 方法）

1. **精确策略（默认）**：在 `LoaderInit` 的指令流中找到 `ldstr` 含 “关闭下载” 的位置，向后定位第一个 `newobj LoaderTask\`2<>`（注意要与 `Action\`1<…LoaderTask…>` 的构造区分——后者 `DeclaringType.Name == "Action\`1"`，不是 `"LoaderTask\`2"`），删除 [ldstr, newobj] 区间并插入 `ldsfld DlClientListLoader`。

2. **宽松回退**：若精确特征缺失，则在该方法内寻找**唯一**的 `newobj LoaderTask\`2<>`；若存在且唯一，从 `newobj` 向前删除到最近的 `ldstr` / `ldc.i4`（假 Loader 参数起点），再插入 `ldsfld`。

3. **C–D4 的匹配**：按上表的类型/方法/字段特征逐条匹配；找不到就跳过该补丁，不影响其它补丁。全部失败才打印“暂不支持”并安全退出，**不修改文件**。

4. **2.99.0.0 与 2.99.0.1 的区分**：不读取版本号，完全靠 IL 形态。模拟程序集回归结果：
   - 2.99.0.0：只命中 A、B；
   - 2.99.0.1：A、B、C、D1–D4 全部命中；
   - 两者第二次运行均提示“未检测到需要修改的特征”，文件字节不变。

---

## 四、如何为新版本更新特征

当官方大改下载页导致补丁失效时：

1. 用 dnSpy / ILSpy 打开新版本 `Plain Craft Launcher 2.exe`。
2. 找到 `PageDownloadInstall.LoaderInit`，确认“假 Loader”的构造形态：
   - 若仍是 `ldstr "关闭下载"` + `newobj LoaderTask\`2<>` → 无需改动，精确策略已覆盖。
   - 若 loader 名称字符串变了 → 调整 `PatchMcDownloadLoader` 中 `Contains("关闭下载")` 的匹配文本（建议改为更稳定的特征，例如改为匹配 “假 Loader 构造段” 而非具体文案）。
   - 若 `DlClientListLoader` 字段改名 → 调整字段名前缀匹配（当前为 `StartsWith("DlClientList")`）。
3. 找到 `PageDownloadModify.LoadInstanceFinished`，确认 `CardMinecraft` → `ldc.i4.2` → `set_Visibility` 链是否仍成立（`CardMinecraft` 可能是字段或属性）。
4. 若新版本又新增了下载入口隐藏，按上表格式补充：先在源码里确认 `If RestrictMcDownload …` 包住的是哪个控件的哪个属性，再在编译产物里找到折叠后的常量与 setter。
5. 在本地用官方 exe 跑一遍工具，并用 dnSpy 复核 IL 是否如预期（见下）。

---

## 五、验证清单（自测流程）

- [ ] 改名后的 exe 也能补丁成功（特征定位不依赖文件名）
- [ ] 已补丁的文件再次运行 → 幂等跳过，文件不变
- [ ] 写回后文件能被 dnlib 重新 `Load`（工具内部已做此校验）
- [ ] PCL2 正在运行时执行 → 提示「请先完全退出 PCL2」，而不是抛出裸 IO 异常
- [ ] `PclMcPatch.exe --version` 输出的版本号与 `.csproj` 的 `<Version>` 一致
- [ ] 断网 / 仓库无 Release 时启动 → 无更新提示、无卡顿，补丁与启动照常
- [ ] 2.99.0.0 上只出现 `[补丁A]` / `[补丁B]`，不出现 C–D4
- [ ] 2.99.0.1 上 A、B、C、D1–D4 全部出现
- [ ] 用 dnSpy 打开产物，确认：
  -  `LoaderInit` 中含 `ldsfld …DlClientListLoader`、不再有 `ldstr "关闭下载"`
  -  `LoadInstanceFinished` 中 `CardMinecraft.Visibility` 对应 `ldc.i4.0`
  -  `PageDownloadLeft..ctor` 中 `ItemInstall` / `LabResource` 对应 `ldc.i4.0`，`PageID` 为 `0`
  -  `McInstanceListUI` 中空列表分支的 `BtnEmptyDownload` 对应 `ldc.i4.0`
  -  `RefreshButtonsUI` 中无版本分支为 `ldc.i4.2; stloc`
  -  `set_PageType` 中可见 `ldarg.1; ldc.i4.2; bne.un` 判断
  -  `BtnDownload_Loaded` 首条指令为 `ldc.i4.0`

---

## 六、已知边界

- **整合包**：拖拽安装入口（`FormMain.FileDrag`）与带参 `ModpackInstall(String)` 未被禁用；D3 只恢复资源页「安装已有整合包」按钮，不恢复无参 `ModpackInstall()` 的选文件入口（仍会 `Hint`）。
- **强签名 / 自校验**：已确认 PCL2 未强签名、无自身哈希校验，补丁可长期生效。若未来版本加入，则需相应调整（重签名或绕过校验）。
- **可见性常量硬编码**：`ldc.i4.2 → ldc.i4.0` 依赖 WPF `Visibility.Collapsed = 2`、`Visible = 0`。若上游改用 `Visibility.Hidden`（1）或换用别的 setter / 绑定，特征不匹配会**安全跳过**（不改文件），此时按第四节重新取特征。
- **D2 / D4 忽略「隐藏下载页面」设置**：2.99.0.0 原本会根据 `UiHiddenPageDownload` 决定按钮/状态；补丁后的 IL 不再读取该设置，下载入口始终可用。这是“恢复下载”的预期取舍。
- **D3 的 `ResourceTypes.ModPack = 2`**：以 `ldc.i4.2` 硬编码（来自 `PCLCS/Constants.cs`）。若上游调整枚举值，需要同步修改 `PatchResourceModPackButton` 中的常量。

---

## 七、写回安全与 Release 约定

### 写回流程

1. `IsFileLocked` 先用 `FileShare.None` 试开目标文件：PCL2 运行中会因文件被占用而命中，直接提示「请先完全退出 PCL2」。
2. `module.Write(tmp)` 写到 `*.patched.tmp`。
3. 重新 `ModuleDefMD.Load(tmp)` 校验产物可解析。
4. 首次补丁把原文件复制为 `*.bak`（保留的是未打补丁的原始文件，后续不再覆盖）。
5. `File.Move(tmp, exe, overwrite: true)` —— 同卷原子替换，避免「先删后移」出现 exe 缺失的空窗。
6. 再次 `Load` 最终文件，确认写回结果可用。

### 版本号与更新检测

程序启动时会**在后台线程**向 GitHub API 查询 `releases/latest`，把 `tag_name` 与自身程序集版本（取自 `.csproj` 的 `<Version>`，即 `AssemblyInformationalVersion`，已剥离 `+commit`）比较；只有远端**严格更新**且两者都能解析成 `Version` 时才提示。

时序：检测任务与补丁、启动**并行**开始 → 补丁 → 拉起 PCL2 → 回头取结果（`UpdateGracePeriod` = 1.5 秒，取不到就放弃），因此网络慢也不会推迟 PCL2 启动；提示与按键交互都发生在 PCL2 已经起来之后。

因此发版必须遵守：

| 约定 | 说明 |
|---|---|
| Tag 形如 `vX.Y.Z` | `release.yml` 在推 `v*` tag 时构建；`v` 前缀会被忽略 |
| 版本号只递增 | 比较是 `System.Version` 语义，`1.2.0 > 1.10.0` 之类错误靠规范避免 |
| `.csproj` 的 `<Version>` 与 tag 一致 | 否则用户会一直被提示更新，或永远收不到提示 |
| 两个变体共用同一 tag | `release.yml` 一次产出 `PclMcPatch-vX.Y.Z.zip`（框架依赖）与 `PclMcPatch-vX.Y.Z-full.zip`（自包含，内置运行时）；更新检测只比对 tag，与下载哪个变体无关 |
| 预发布不参与 | API 取的是 `latest`，`prerelease` 不会触发提示 |

检测失败（断网、限流、404）一律静默；`--no-update-check` / `-nu` 可跳过；`--version` / `-v` 只打印当前版本。
