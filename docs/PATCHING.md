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

## 二、两处补丁的定位方法

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
callvirt get_CardMinecraft()
ldc.i4.2                       ; Visibility.Collapsed = 2
callvirt set_Visibility(...)
```

**修改**：把紧跟在 `get_CardMinecraft()` 之后、且下一条是 `set_Visibility` 的 `ldc.i4.2` 改成 `ldc.i4.0`（Visible）。

---

## 三、版本适配策略（代码中的 `PatchMcDownloadLoader`）

1. **精确策略（默认）**：在 `LoaderInit` 的指令流中找到 `ldstr` 含 “关闭下载” 的位置，向后定位第一个 `newobj LoaderTask\`2<>`（注意要与 `Action\`1<…LoaderTask…>` 的构造区分——后者 `DeclaringType.Name == "Action\`1"`，不是 `"LoaderTask\`2"`），删除 [ldstr, newobj] 区间并插入 `ldsfld DlClientListLoader`。

2. **宽松回退**：若精确特征缺失，则在该方法内寻找**唯一**的 `newobj LoaderTask\`2<>`；若存在且唯一，从 `newobj` 向前删除到最近的 `ldstr` / `ldc.i4`（假 Loader 参数起点），再插入 `ldsfld`。

3. **不匹配**：以上都失败 → 返回 `false`，主流程打印“暂不支持”提示并安全退出，**不修改文件**。

---

## 四、如何为新版本更新特征

当官方大改下载页导致补丁失效时：

1. 用 dnSpy / ILSpy 打开新版本 `Plain Craft Launcher 2.exe`。
2. 找到 `PageDownloadInstall.LoaderInit`，确认“假 Loader”的构造形态：
   - 若仍是 `ldstr "关闭下载"` + `newobj LoaderTask\`2<>` → 无需改动，精确策略已覆盖。
   - 若 loader 名称字符串变了 → 调整 `PatchMcDownloadLoader` 中 `Contains("关闭下载")` 的匹配文本（建议改为更稳定的特征，例如改为匹配 “假 Loader 构造段” 而非具体文案）。
   - 若 `DlClientListLoader` 字段改名 → 调整字段名前缀匹配（当前为 `StartsWith("DlClientList")`）。
3. 找到 `PageDownloadModify.LoadInstanceFinished`，确认 `get_CardMinecraft` → `ldc.i4.2` → `set_Visibility` 链是否仍成立。
4. 在本地用官方 exe 跑一遍工具，并用 dnSpy 复核 IL 是否如预期（见下）。

---

## 五、验证清单（自测流程）

- [ ] 改名后的 exe 也能补丁成功（特征定位不依赖文件名）
- [ ] 已补丁的文件再次运行 → 幂等跳过，文件不变
- [ ] 写回后文件能被 dnlib 重新 `Load`（工具内部已做此校验）
- [ ] PCL2 正在运行时执行 → 提示「请先完全退出 PCL2」，而不是抛出裸 IO 异常
- [ ] `PclMcPatch.exe --version` 输出的版本号与 `.csproj` 的 `<Version>` 一致
- [ ] 断网 / 仓库无 Release 时启动 → 无更新提示、无卡顿，补丁与启动照常
- [ ] 用 dnSpy 打开产物，确认：
  -  `LoaderInit` 中含 `ldsfld …DlClientListLoader`、不再有 `ldstr "关闭下载"`
  -  `LoadInstanceFinished` 中 `CardMinecraft.Visibility` 对应 `ldc.i4.0`

---

## 六、已知边界

- **整合包**：拖拽安装入口（`FormMain.FileDrag`）与带参 `ModpackInstall(String)` 未被禁用，故不在补丁范围内；仅“手动选文件”按钮入口（`ModpackInstall()` 无参重载）被折叠为 `Hint(qwq)`，如需解锁可后续扩展。
- **强签名 / 自校验**：已确认 PCL2 未强签名、无自身哈希校验，补丁可长期生效。若未来版本加入，则需相应调整（重签名或绕过校验）。
- **补丁 B 的取值硬编码**：`ldc.i4.2 → ldc.i4.0` 依赖 WPF `Visibility.Collapsed = 2`。若上游改用 `Visibility.Hidden`（1）或换用别的 setter / 绑定，特征不匹配会**安全跳过**（不改文件），此时按第四节重新取特征。

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
| 预发布不参与 | API 取的是 `latest`，`prerelease` 不会触发提示 |

检测失败（断网、限流、404）一律静默；`--no-update-check` / `-nu` 可跳过；`--version` / `-v` 只打印当前版本。
