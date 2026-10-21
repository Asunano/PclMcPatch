# PCL2-McDownload-Patcher

为 [PCL2（Plain Craft Launcher 2）](https://github.com/Meloong-Git/PCL) 启动器恢复「原版游戏（我的世界本体）」下载功能的一键补丁工具，同时可作为**启动器外壳**在每次启动时自动注入补丁。

> [!IMPORTANT]
> 该限制是上游以 “遵守相关法律法规” 为由，在 2.99.0.0 起加入的。本工具仅做技术层面的功能恢复，**请在你所在地区合规的前提下使用**。

---

## 功能特性

- **一键补丁**：把 PCL2 的“原版游戏下载”从被禁用的假 Loader 恢复为真实的版本列表下载。
- **启动器外壳**：把工具放到 PCL2 同目录后，双击工具即可「自动补丁 + 启动 PCL2」；官方更新后会在启动前自动重新注入。
- **改名 / 换路径可用**：按程序**内部 IL 特征**定位，不依赖文件名。
- **多版本兼容**：精确特征匹配为主，匹配不到时回退到宽松策略；两者都不匹配则安全跳过并提示，绝不乱改文件。
- **安全可回退**：写回前校验产物、同卷原子替换；首次补丁自动备份为 `*.exe.bak`。
- **幂等**：已补丁的文件重复运行不会损坏。
- **更新提示**：启动时**在后台**对比 GitHub Release 的版本号，有新版本会提示并可一键打开下载页；检测不阻塞补丁与启动，失败静默跳过。

---

## 使用方法

### 方式一：当启动器用（推荐，支持更新后自动注入）

1. 将 `PclMcPatch.exe` 放到 PCL2 主程序 `Plain Craft Launcher 2.exe` **同一个文件夹**。
2. 以后**双击 `PclMcPatch.exe`** 代替双击 PCL2。
3. 它会先自动补丁（若官方更新版本则重新注入），然后拉起 PCL2。

### 方式二：手动补丁一次

- 把 PCL2 主程序**拖到 `PclMcPatch.exe` 上**；或命令行：
  ```powershell
  PclMcPatch.exe "D:\Games\PCL\Plain Craft Launcher 2.exe"
  ```
- 出现 `[完成]` 后，原 PCL2 已解锁，正常双击 PCL2 启动即可。

### 命令行开关

| 开关 | 作用 |
|------|------|
| `--patch-only` / `-p` | 只补丁，不启动 PCL2 |
| `--no-update-check` / `-nu` | 跳过启动时的更新检测（离线使用更干脆） |
| `--version` / `-v` | 只打印当前版本号（反馈问题时请附上） |



## 技术原理

上游把 `RestrictMcDownload` 设为 `Const True`，VB 编译器做了**常量折叠**，把 `Else` 分支（真实 `DlClientListLoader`）从 IL 中整段删除，只留下一个永远抛异常的“假 Loader”。因此无法“改个跳转”恢复，必须在 IL 层面把假 Loader 替换为真实字段。

本工具基于 [dnlib](https://github.com/0xd4d/dnlib) 修改 IL，做两处补丁：

| 补丁 | 位置 | 修改 |
|------|------|------|
| A | `PCL.PageDownloadInstall.LoaderInit` | 把构造“假 LoaderTask（报错）”的指令段替换为 `ldsfld PCL.ModDownload::DlClientListLoader`，恢复原版游戏列表下载 |
| B | `PCL.PageDownloadModify.LoadInstanceFinished` | 把 `CardMinecraft.Visibility = Collapsed` 改为 `Visible`，恢复版本修改页的“我的世界”卡片 |

> 整合包下载未被完全禁用（拖拽入口、带参 `ModpackInstall(String)` 完好），故不在补丁范围内。

### 兼容性策略

1. **精确策略**：在 `LoaderInit` 中按 `ldstr "关闭下载"` → `newobj LoaderTask\`2<>` 的指令段定位并替换。
2. **宽松回退**：若精确特征缺失，则把方法内**唯一**的 `LoaderTask\`2<>` 构造视为待替换点。
3. **不匹配**：两者都失败则跳过、不改文件，并提示反馈版本号。

---

## 从源码构建

### 环境

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows（修补目标为 Windows 桌面程序；工具本身运行环境也是 Windows）

### 构建 / 发布

```powershell
# 仅编译（开发 / 自测）
dotnet build -c Release

# 发布单文件 exe（推荐分发方式，约 1.3 MB，无需安装运行时）
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ./publish
```

产物：`publish/PclMcPatch.exe`（单文件，双击即用）。

> 依赖框架版（本仓库默认）需要目标机器已安装 **.NET 8 桌面运行时**——而 PCL2 本身就需要它，因此用户的电脑通常已经具备。

---

## 仓库结构

```
PclMcPatch-Repo/
├── LICENSE                       # MIT 许可证
├── README.md                     # 本文件（用户向文档）
├── .gitignore                    # 忽略 bin/obj/发布产物
├── .github/
│   └── workflows/
│       └── release.yml           # 推 v* tag 时构建并发布 Release
├── docs/
│   └── PATCHING.md               # 补丁特征与版本适配（维护者向文档）
└── src/
    ├── PclMcPatch.csproj         # 项目文件（net8.0, dnlib, 单文件发布）
    └── Program.cs                # 工具主程序（补丁逻辑 + 启动器外壳 + 更新检测）
```

---

## 安全与回退

- 每次写回前会验证生成的新文件能被重新加载；替换用「临时文件 + 同卷原子 Move 覆盖」，中途中断不会出现主程序缺失的空窗。
- PCL2 正在运行时目标文件会被占用，工具会直接提示「请先完全退出 PCL2」，不会留下半成品。
- 首次补丁自动备份原文件为 `Plain Craft Launcher 2.exe.bak`；异常时把 `.bak` 改回原名即可。
- 已确认 PCL2 程序集**未强签名**、**无自身哈希自校验**，补丁可长期生效；重复运行幂等安全。

---

## 免责声明

本工具仅供学习与研究 IL / .NET 程序集修改之用。使用该工具修改第三方软件可能违反其许可协议或当地法律法规，风险由使用者自行承担。作者对因使用本工具导致的任何问题不承担责任。

---

## 下载

### 下载已构建版本

前往本仓库 **[Releases](https://github.com/Asunano/PclMcPatch/releases)** 页面，下载最新版本的 `PclMcPatch-vX.Y.Z.zip`（内含 `PclMcPatch.exe` 与说明），解压即用。

