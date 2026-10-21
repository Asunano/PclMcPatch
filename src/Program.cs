// <copyright file="Program.cs" company="PCL2-McDownload-Patcher contributors">
// Copyright © 2026 the contributors.
//
// Licensed under the MIT License.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PclMcPatch
{
    /// <summary>
    /// PCL2「我的世界本体下载」一键补丁工具 / 启动器外壳。
    /// <para>
    /// 工作方式：通过 dnlib 修改 PCL2 主程序集（Plain Craft Launcher 2.exe）的 IL，
    /// 将上游以「遵守相关法律法规」为由被禁用的原版游戏下载功能恢复。
    /// 工具可作为启动器外壳，在每次启动 PCL2 前自动检查并注入补丁。
    /// </para>
    /// </summary>
    internal static class Program
    {
        /// <summary>发布仓库所有者（更新检测用）。</summary>
        private const string RepoOwner = "Asunano";

        /// <summary>发布仓库名（更新检测用）。</summary>
        private const string RepoName = "PclMcPatch";

        /// <summary>仓库地址（更新检测取不到 html_url 时的回退链接）。</summary>
        private const string RepoUrl = "https://github.com/" + RepoOwner + "/" + RepoName;

        /// <summary>
        /// 展示更新提示前，允许后台检测结果再等多久。
        /// 检测在启动阶段就已并发开始，通常此时早已完成；等不到就直接放弃，不让用户多等。
        /// </summary>
        private static readonly TimeSpan UpdateGracePeriod = TimeSpan.FromSeconds(1.5);

        #region 入口

        /// <summary>
        /// 程序入口。
        /// </summary>
        /// <param name="args">
        /// 支持两种用法：
        /// 1) 不带参数或传入 PCL 主程序路径 → 自动补丁后启动 PCL2（启动器外壳模式）；
        /// 2) 传入 <c>--patch-only</c> / <c>-p</c> → 仅补丁，不启动。
        /// 附加开关：<c>--no-update-check</c> / <c>-nu</c> 跳过更新检测；
        /// <c>--version</c> / <c>-v</c> 只打印版本号。
        /// </param>
        private static int Main(string[] args)
        {
            // 0. --version / -v：只打印版本号（便于反馈问题时附上版本）
            if (args.Any(a => a == "--version" || a == "-v"))
            {
                Console.WriteLine(CurrentVersion());
                return 0;
            }

            Console.WriteLine("PCL2 我的世界本体下载 · 一键补丁 / 启动器外壳");
            Console.WriteLine("================================================");
            Console.WriteLine("版本: " + CurrentVersion());

            bool patchOnly = args.Any(a => a == "--patch-only" || a == "-p");
            bool skipUpdateCheck = args.Any(a => a == "--no-update-check" || a == "-nu");

            // 1. 更新检测挂到后台线程：与接下来的补丁、启动并行，启动路径上绝不等网络
            Task<ReleaseInfo?>? updateCheck = skipUpdateCheck ? null : StartUpdateCheck();

            // 2. 定位 PCL2 主程序
            string? target = ResolveTarget(args);
            if (string.IsNullOrEmpty(target))
            {
                Console.WriteLine("[错误] 未找到 PCL2 主程序（Plain Craft Launcher 2.exe）。");
                Console.WriteLine("        用法：把 PCL2 主程序拖到本工具上，或放在与本工具同一目录。");
                Pause();
                return 1;
            }

            Console.WriteLine("目标文件: " + target);

            // 3. 打补丁（幂等）
            int patchResult = Patch(target);
            if (patchResult != 0)
            {
                Pause();
                return patchResult;
            }

            // 4. 若非仅补丁模式，则启动 PCL2
            if (!patchOnly)
            {
                Console.WriteLine("[启动] 正在启动 PCL2 ...");
                try
                {
                    var startInfo = new ProcessStartInfo(target)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(target),
                    };
                    Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[错误] 启动失败: " + ex.Message);
                    Pause();
                    return 1;
                }

                // PCL2 已经起来了，这时再顺手看一眼后台检测结果，不占用启动时间
                ReportUpdate(updateCheck);
            }
            else
            {
                Console.WriteLine("补丁完成。可关闭本窗口。");
                ReportUpdate(updateCheck);
                Pause();
            }

            return 0;
        }

        #endregion

        #region 版本与更新检测

        /// <summary>
        /// 当前程序版本（取自程序集信息，已去掉 <c>+commit</c> 之类的构建元数据）。
        /// </summary>
        private static string CurrentVersion()
        {
            Assembly assembly = typeof(Program).Assembly;
            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                string value = informational!;
                int plus = value.IndexOf('+');
                return plus > 0 ? value.Substring(0, plus) : value;
            }

            Version? version = assembly.GetName().Version;
            return version == null ? "0.0.0" : version.ToString(3);
        }

        /// <summary>
        /// 在后台线程发起更新检测，立即返回，不阻塞调用方。
        /// <para>
        /// 全部异常在任务内部吞掉：更新检测只是锦上添花，任何失败都不应影响补丁与启动。
        /// </para>
        /// </summary>
        private static Task<ReleaseInfo?> StartUpdateCheck()
        {
            Task<ReleaseInfo?> task = Task.Run(() =>
            {
                try
                {
                    return FetchLatestRelease();
                }
                catch
                {
                    // 无网络 / 被墙 / API 限流 / 仓库无 Release（404）等：静默放弃
                    return null;
                }
            });

            return task;
        }

        /// <summary>
        /// 取后台检测结果并提示用户；若在宽限期内还没拿到结果就直接放弃，避免拖慢退出。
        /// <para>
        /// 调用点都在「补丁完成 / PCL2 已启动」之后，因此这里的等待不会占用启动时间。
        /// </para>
        /// </summary>
        /// <param name="check">后台检测任务；<see langword="null"/> 表示本次跳过了检测（<c>--no-update-check</c>）。</param>
        private static void ReportUpdate(Task<ReleaseInfo?>? check)
        {
            if (check == null)
            {
                return;
            }

            try
            {
                if (!check.Wait(UpdateGracePeriod))
                {
                    // 网络太慢：不耽误用户，本次不提示（后台线程随进程退出）
                    return;
                }
            }
            catch
            {
                return;
            }

            ReleaseInfo? latest = check.Result;
            if (latest == null)
            {
                return;
            }

            Version? current = ParseVersion(CurrentVersion());
            Version? remote = ParseVersion(latest.TagName);

            // 版本号都能解析时才比较；解析不了就保守地不打扰用户
            if (current == null || remote == null || remote <= current)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine("[更新] 检测到新版本 " + latest.TagName + "（当前 " + CurrentVersion() + "）");
            Console.WriteLine("       下载地址: " + latest.Url);
            Console.WriteLine("       更新方式: 下载新版压缩包，用里面的 PclMcPatch.exe 覆盖本程序即可。");

            // 非交互环境（重定向 / 无控制台）不等待，直接继续
            if (Console.IsInputRedirected)
            {
                Console.WriteLine();
                return;
            }

            Console.WriteLine("       按 Y 打开下载页面，其他键跳过（10 秒无操作自动跳过）…");

            bool? open = ReadYesNo(TimeSpan.FromSeconds(10));
            if (open == true)
            {
                OpenUrl(latest.Url);
            }

            Console.WriteLine();
        }

        /// <summary>
        /// 请求 GitHub API 的 latest release，返回标签与下载页地址；无 Release 或结构异常时返回 <see langword="null"/>。
        /// </summary>
        private static ReleaseInfo? FetchLatestRelease()
        {
            string api = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PclMcPatch/" + CurrentVersion());
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string json = client.GetStringAsync(api).GetAwaiter().GetResult();

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string? tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
                ? tagElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            string? htmlUrl = root.TryGetProperty("html_url", out JsonElement urlElement)
                ? urlElement.GetString()
                : null;

            string url = string.IsNullOrWhiteSpace(htmlUrl) ? RepoUrl + "/releases" : htmlUrl!;
            return new ReleaseInfo(tag!, url);
        }

        /// <summary>
        /// 把 <c>v1.2.3</c> / <c>1.2.3-beta</c> 之类的标签解析为可比较的 <see cref="Version"/>。
        /// </summary>
        private static Version? ParseVersion(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string value = text!.Trim();

            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(1);
            }

            int cut = value.IndexOfAny(new[] { '-', '+' });
            if (cut > 0)
            {
                value = value.Substring(0, cut);
            }

            return Version.TryParse(value, out Version? parsed) ? parsed : null;
        }

        /// <summary>
        /// 在 <paramref name="timeout"/> 内等待一次按键。
        /// </summary>
        /// <returns>按 Y 返回 <see langword="true"/>，按其他键返回 <see langword="false"/>，超时返回 <see langword="null"/>。</returns>
        private static bool? ReadYesNo(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    if (Console.KeyAvailable)
                    {
                        ConsoleKey key = Console.ReadKey(intercept: true).Key;
                        Console.WriteLine();
                        return key == ConsoleKey.Y;
                    }

                    Thread.Sleep(100);
                }
            }
            catch
            {
                // 没有真实控制台时无法读键，按「跳过」处理
            }

            return null;
        }

        /// <summary>
        /// 用默认浏览器打开链接。
        /// </summary>
        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Console.WriteLine("[更新] 已在浏览器中打开下载页面。");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[提示] 无法自动打开浏览器（" + ex.Message + "），请手动访问上面的地址。");
            }
        }

        /// <summary>
        /// 更新检测用的 Release 摘要。
        /// </summary>
        /// <param name="TagName">Release 标签，例如 <c>v1.1.0</c>。</param>
        /// <param name="Url">Release 页面地址。</param>
        private sealed record ReleaseInfo(string TagName, string Url);

        #endregion

        #region 目标定位

        /// <summary>
        /// 定位 PCL2 主程序路径。按以下顺序尝试：
        /// <list type="number">
        /// <item>命令行中传入的第一个存在的 *.exe 参数；</item>
        /// <item>与本工具同目录的 <c>Plain Craft Launcher 2.exe</c>；</item>
        /// <item>在本工具同目录递归查找含有 <c>PCL.Application::Main</c> 入口的 exe。</item>
        /// </list>
        /// 注意：定位基于程序集内部特征，与目标文件名无关（支持改名）。
        /// </summary>
        private static string? ResolveTarget(string[] args)
        {
            // 1) 命令行第一个存在的 exe 参数
            foreach (string arg in args)
            {
                if (!arg.StartsWith("-", StringComparison.Ordinal)
                    && arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(arg))
                {
                    return Path.GetFullPath(arg);
                }
            }

            // 2) 同目录的 Plain Craft Launcher 2.exe
            string selfDir = AppContext.BaseDirectory;
            string named = Path.Combine(selfDir, "Plain Craft Launcher 2.exe");
            if (File.Exists(named))
            {
                return named;
            }

            // 3) 同目录递归查找含 PCL.Application::Main 入口的 exe
            try
            {
                foreach (string file in Directory.EnumerateFiles(selfDir, "*.exe", SearchOption.AllDirectories))
                {
                    if (IsPclExe(file))
                    {
                        return file;
                    }
                }
            }
            catch
            {
                // 忽略目录遍历 / 加载异常，继续返回 null
            }

            return null;
        }

        /// <summary>
        /// 判断给定文件是否为 PCL2 主程序（含 <c>PCL.Application.Main</c> 入口点）。
        /// 加载失败时返回 <see langword="false"/>，不抛出。
        /// </summary>
        private static bool IsPclExe(string path)
        {
            try
            {
                using ModuleDefMD module = ModuleDefMD.Load(path);
                foreach (TypeDef type in module.Types)
                {
                    if (type.FullName != "PCL.Application")
                    {
                        continue;
                    }

                    foreach (MethodDef method in type.Methods)
                    {
                        if (method.Name == "Main")
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // 非托管宿主 / 无法解析的程序集，视为非 PCL2
            }

            return false;
        }

        #endregion

        #region 补丁主流程

        /// <summary>
        /// 对目标程序集执行补丁。补丁为幂等操作。
        /// </summary>
        /// <returns>
        /// 0 表示成功（含「已补丁过，无需修改」的情况）；非 0 表示失败。
        /// </returns>
        private static int Patch(string exePath)
        {
            // PCL2 正在运行时其自身文件被锁定，先给出明确提示，避免用户看到晦涩的 IO 异常。
            if (IsFileLocked(exePath))
            {
                Console.WriteLine("[错误] 目标程序正在运行（文件被占用）。");
                Console.WriteLine("        请先完全退出 PCL2（含托盘 / 后台进程）后重试。");
                return 1;
            }

            ModuleDefMD? module = null;
            try
            {
                module = ModuleDefMD.Load(exePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[错误] 无法加载程序集: " + ex.Message);
                return 1;
            }

            bool patchedLoader = PatchMcDownloadLoader(module);
            bool patchedCard = PatchMcCardVisibility(module);

            if (!patchedLoader && !patchedCard)
            {
                Console.WriteLine("[提示] 未检测到需要修改的特征，文件可能已补丁过、不是 PCL2，或版本过新暂不支持。");
                Console.WriteLine("        若确实是新版 PCL2 且本体下载仍被禁用，请把版本号反馈给作者更新特征。");
                module.Dispose();
                return 0; // 非失败：仍可正常启动
            }

            string tempPath = exePath + ".patched.tmp";
            try
            {
                // 先写临时文件，验证可重新加载后再替换原文件，
                // 避免写入过程中异常导致原程序损坏。
                module.Write(tempPath);
                using (ModuleDefMD verify = ModuleDefMD.Load(tempPath))
                {
                    verify.Dispose();
                }

                // 首次补丁自动备份（保存的是“未打过补丁”的原始文件）
                string backupPath = exePath + ".bak";
                if (!File.Exists(backupPath))
                {
                    File.Copy(exePath, backupPath);
                }

                // 用 Move 覆盖而不是「先 Delete 再 Move」：同卷上是原子替换，
                // 中途中断不会出现“exe 已删除、新文件还没到位”的空窗。
                File.Move(tempPath, exePath, overwrite: true);

                // 写回后再校验一次，确认最终文件仍可被正常加载
                using (ModuleDefMD reload = ModuleDefMD.Load(exePath))
                {
                    reload.Dispose();
                }

                if (patchedLoader)
                {
                    Console.WriteLine("[补丁A] 已恢复「原版游戏」下载（假 Loader -> 真实 DlClientListLoader）。");
                }

                if (patchedCard)
                {
                    Console.WriteLine("[补丁B] 已恢复修改页「我的世界」卡片显示。");
                }

                Console.WriteLine("[完成] 已写入补丁。");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[错误] 写入/校验失败: " + ex.Message);
                if (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Console.WriteLine("        请确认 PCL2 已完全退出、文件未被安全软件占用且可写。");
                }

                TryDelete(tempPath);
                module.Dispose();
                return 1;
            }

            module.Dispose();
            return 0;
        }

        #endregion

        #region 补丁 A：恢复原版游戏下载 Loader

        /// <summary>
        /// 补丁 A：在 <c>PCL.PageDownloadInstall.LoaderInit</c> 中，
        /// 将「永远报错的假 LoaderTask」替换为真实的 <c>ModDownload.DlClientListLoader</c> 字段。
        /// <para>
        /// 背景：上游将 <c>RestrictMcDownload</c> 设为编译期常量，VB 编译器做常量折叠，
        /// 把 <c>Else</c> 分支（真实 Loader）直接从 IL 中删除，仅留下构造假 LoaderTask 的代码。
        /// 因此补丁不是「改一个跳转」，而是把整段假 Loader 构造替换为对真实字段的 <c>ldsfld</c>。
        /// </para>
        /// </summary>
        /// <returns>是否执行了修改。</returns>
        private static bool PatchMcDownloadLoader(ModuleDefMD module)
        {
            TypeDef? installPage = module.Types.FirstOrDefault(t => t.FullName == "PCL.PageDownloadInstall");
            if (installPage == null)
            {
                return false;
            }

            MethodDef? loaderInit = installPage.Methods
                .FirstOrDefault(m => m.Name == "LoaderInit" && m.Body != null);
            if (loaderInit == null)
            {
                return false;
            }

            // 定位真实 Loader 字段（多版本兼容：字段名以 DlClientList 开头）
            FieldDef? loaderField = module.Types
                .Where(t => t.FullName.StartsWith("PCL.ModDownload"))
                .SelectMany(t => t.Fields)
                .FirstOrDefault(f => f.Name.StartsWith("DlClientList"));
            if (loaderField == null)
            {
                return false;
            }

            IList<Instruction> instructions = loaderInit.Body!.Instructions;

            // 策略 1（精确）：从 ldstr "关闭下载" 到其后的 newobj LoaderTask`2<> 之间整段删除
            int startIdx = -1;
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].OpCode == OpCodes.Ldstr
                    && (instructions[i].Operand as string)?.Contains("关闭下载") == true)
                {
                    startIdx = i;
                    break;
                }
            }

            int endIdx = FindLoaderTaskCtor(instructions, startIdx >= 0 ? startIdx + 1 : 0);

            // 策略 2（宽松回退）：方法内仅有一个 newobj LoaderTask`2<> 时也视为目标
            if (endIdx < 0)
            {
                var candidates = new List<int>();
                for (int i = 0; i < instructions.Count; i++)
                {
                    if (instructions[i].OpCode == OpCodes.Newobj
                        && IsLoaderTaskCtor(instructions[i].Operand))
                    {
                        candidates.Add(i);
                    }
                }

                if (candidates.Count == 1)
                {
                    endIdx = candidates[0];
                }
            }

            if (endIdx < 0)
            {
                return false;
            }

            // 幂等检查：若 loader 已经是 ldsfld DlClientListLoader，则无需修改
            if (instructions.Any(i => i.OpCode == OpCodes.Ldsfld
                && (i.Operand as IField)?.Name == loaderField.Name))
            {
                return false;
            }

            // 确定删除起点
            int deleteStart = startIdx >= 0 ? startIdx : endIdx;
            if (startIdx < 0)
            {
                // 宽松回退：从 endIdx 往前删到最近的 ldstr / ldc.i4（假 loader 参数起点）
                for (int i = endIdx - 1; i >= 0; i--)
                {
                    if (instructions[i].OpCode == OpCodes.Ldstr
                        || instructions[i].OpCode == OpCodes.Ldc_I4)
                    {
                        deleteStart = i;
                        break;
                    }
                }
            }

            // 从后往前删除 [deleteStart, endIdx]，再在 deleteStart 处插入 ldsfld 真实字段
            for (int i = endIdx; i >= deleteStart; i--)
            {
                instructions.RemoveAt(i);
            }

            instructions.Insert(deleteStart, new Instruction(OpCodes.Ldsfld, loaderField));

            // 栈深度保持不变（删除「构造 LoaderTask」与插入「ldsfld 字段」均压入 1 个引用），
            // 保留原始 maxstack 以跳过 dnlib 的重新计算。
            loaderInit.Body.KeepOldMaxStack = true;
            loaderInit.Body.UpdateInstructionOffsets();
            return true;
        }

        /// <summary>
        /// 在指令序列中查找从 <paramref name="from"/> 开始的第一个 <c>newobj LoaderTask`2&lt;&gt;</c> 构造，
        /// 返回其指令索引；未找到返回 -1。
        /// </summary>
        private static int FindLoaderTaskCtor(IList<Instruction> instructions, int from)
        {
            for (int i = from; i < instructions.Count; i++)
            {
                if (instructions[i].OpCode == OpCodes.Newobj
                    && IsLoaderTaskCtor(instructions[i].Operand))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 判断给定操作数是否为 <c>LoaderTask`2&lt;&gt;</c> 的构造函数（排除被包裹在 Action`1&lt;&gt; 中的情况）。
        /// </summary>
        private static bool IsLoaderTaskCtor(object? operand)
        {
            if (operand is not IMethod ctor)
            {
                return false;
            }

            return ctor.DeclaringType is ITypeDefOrRef typeRef
                && typeRef.Name == "LoaderTask`2";
        }

        #endregion

        #region 补丁 B：恢复修改页「我的世界」卡片

        /// <summary>
        /// 补丁 B：在 <c>PCL.PageDownloadModify.LoadInstanceFinished</c> 中，
        /// 将 <c>CardMinecraft.Visibility = Collapsed</c> 改为 <c>Visible</c>
        /// （即把紧随 <c>get_CardMinecraft</c> 之后、传给 <c>set_Visibility</c> 的 <c>ldc.i4.2</c> 改为 <c>ldc.i4.0</c>）。
        /// </summary>
        /// <returns>是否执行了修改。</returns>
        private static bool PatchMcCardVisibility(ModuleDefMD module)
        {
            TypeDef? modifyPage = module.Types.FirstOrDefault(t => t.FullName == "PCL.PageDownloadModify");
            if (modifyPage == null)
            {
                return false;
            }

            MethodDef? loadFinished = modifyPage.Methods
                .FirstOrDefault(m => m.Name == "LoadInstanceFinished" && m.Body != null);
            if (loadFinished == null)
            {
                return false;
            }

            IList<Instruction> instructions = loadFinished.Body!.Instructions;
            for (int i = 0; i < instructions.Count - 1; i++)
            {
                Instruction current = instructions[i];
                Instruction next = instructions[i + 1];

                bool isSetVisibility = next.OpCode == OpCodes.Callvirt
                    && (next.Operand as IMethod)?.FullName.Contains("set_Visibility") == true;
                bool isCollapsed = current.OpCode == OpCodes.Ldc_I4_2;

                if (isSetVisibility && isCollapsed && i >= 1)
                {
                    Instruction prev = instructions[i - 1];
                    if (prev.OpCode == OpCodes.Callvirt
                        && (prev.Operand as IMethod)?.FullName.Contains("get_CardMinecraft") == true)
                    {
                        current.OpCode = OpCodes.Ldc_I4_0;
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion

        #region 工具方法

        /// <summary>
        /// 判断文件是否被其他进程独占（典型场景：PCL2 正在运行，占用了自己的 exe）。
        /// </summary>
        /// <returns>被占用返回 <see langword="true"/>；可读写返回 <see langword="false"/>。</returns>
        private static bool IsFileLocked(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // 只读属性 / 权限受限：不算“正在运行”，交给后续写入步骤报更准确的错
                return false;
            }
        }

        /// <summary>
        /// 尽力删除文件，忽略一切异常（仅用于清理临时产物）。
        /// </summary>
        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 忽略清理异常
            }
        }

        /// <summary>
        /// 在支持控制台输入的环境下等待用户按键，便于查看输出。
        /// </summary>
        private static void Pause()
        {
            Console.WriteLine("\n按任意键退出…");
            try
            {
                Console.ReadKey();
            }
            catch
            {
                // 重定向到文件 / 无控制台时忽略
            }
        }

        #endregion
    }
}
