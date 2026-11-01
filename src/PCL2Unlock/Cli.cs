using System;
using System.IO;
using System.Linq;
using PCL2Unlock.Core;

namespace PCL2Unlock
{
    public static class Cli
    {
        public const string TargetFileName = "Plain Craft Launcher 2.exe";

        public static bool LooksLikeCli(string[] args)
        {
            if (args == null || args.Length == 0) return false;
            var a = args[0];
            if (a.StartsWith("-", StringComparison.Ordinal)) return true;
            return a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        public static int Run(string[] args)
        {
            try
            {
                bool check = false, revert = false, help = false;
                string target = null;
                foreach (var a in args)
                {
                    switch (a.ToLowerInvariant())
                    {
                        case "--check": case "-c": check = true; break;
                        case "--revert": case "-r": revert = true; break;
                        case "--help": case "-h": case "/?": help = true; break;
                        default:
                            if (!a.StartsWith("-")) target = a;
                            break;
                    }
                }

                if (help)
                {
                    PrintUsage();
                    return 0;
                }

                if (string.IsNullOrEmpty(target)) target = AutoDetect();
                if (string.IsNullOrEmpty(target) || !File.Exists(target))
                {
                    ConsoleBridge.ErrorLine("找不到 PCL 主程序，请把 Plain Craft Launcher 2.exe 的路径作为参数传入。");
                    PrintUsage();
                    return 2;
                }

                if (revert)
                {
                    var r = Pcl2Patcher.Revert(target, ConsoleBridge.WriteLine);
                    ConsoleBridge.WriteLine(r.Message);
                    return r.Success ? 0 : 1;
                }

                var analysis = Pcl2Patcher.Analyze(target);
                if (!analysis.IsSupported)
                {
                    ConsoleBridge.ErrorLine(analysis.Error ?? "不支持的文件");
                    return 3;
                }

                ConsoleBridge.WriteLine("目标文件：" + target);
                ConsoleBridge.WriteLine("版本：" + analysis.Version + "，SHA-256：" + analysis.Sha256);
                foreach (var s in analysis.Sites)
                    ConsoleBridge.WriteLine(string.Format("  [{0}] {1}：{2}", s.Id, s.Title, StateText(s)));

                if (check)
                {
                    if (analysis.AllPatched)
                    {
                        ConsoleBridge.WriteLine("状态：已打过补丁");
                        return 0;
                    }
                    ConsoleBridge.WriteLine("状态：未打补丁");
                    return 1;
                }

                if (analysis.AllPatched)
                {
                    ConsoleBridge.WriteLine("状态：已打过补丁，无需重复操作");
                    return 0;
                }
                if (analysis.Locked)
                {
                    ConsoleBridge.ErrorLine("请先退出 PCL（文件正被占用）");
                    return 6;
                }

                var report = Pcl2Patcher.Apply(target, ConsoleBridge.WriteLine);
                ConsoleBridge.WriteLine(report.Message);
                if (!report.Success) return analysis.UsableSites == 0 ? 4 : 5;
                ConsoleBridge.WriteLine("[完成] 现在可以启动 PCL 使用下载功能了。");
                return 0;
            }
            catch (Exception ex)
            {
                ConsoleBridge.ErrorLine("出错：" + ex.Message);
                if (Environment.GetEnvironmentVariable("PCL2FIX_DEBUG") == "1")
                    ConsoleBridge.ErrorLine(ex.ToString());
                return 1;
            }
        }

        private static string StateText(PatchSite s)
        {
            switch (s.State)
            {
                case SiteState.Patched: return "已修补";
                case SiteState.Missing: return "不适用（" + s.Detail + "）";
                default: return "未修补" + (string.IsNullOrEmpty(s.Detail) ? "" : "（" + s.Detail + "）");
            }
        }

        public static string AutoDetect()
        {
            var dirs = new[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Directory.GetCurrentDirectory()
            };
            foreach (var d in dirs)
            {
                try
                {
                    var p = Path.Combine(d, TargetFileName);
                    if (File.Exists(p)) return p;
                    var sub = Path.Combine(d, "PCL", TargetFileName);
                    if (File.Exists(sub)) return sub;
                }
                catch { }
            }

            // 桌面快捷方式兜底：快捷方式指向 Plain Craft Launcher 2.exe 时直接采用
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null && Directory.Exists(desktop))
                {
                    var shell = Activator.CreateInstance(shellType);
                    foreach (var lnk in Directory.GetFiles(desktop, "*.lnk"))
                    {
                        try
                        {
                            var sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                            var targetPath = sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string;
                            if (!string.IsNullOrEmpty(targetPath) &&
                                string.Equals(Path.GetFileName(targetPath), TargetFileName, StringComparison.OrdinalIgnoreCase) &&
                                File.Exists(targetPath))
                                return targetPath;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        public static void PrintUsage()
        {
            ConsoleBridge.WriteLine("PCL2 下载解锁 " + AppInfo.Version);
            ConsoleBridge.WriteLine("解除原版 PCL 2.99.x 在中国大陆地区的 MC 下载 / 安装限制（IL 补丁，可还原）");
            ConsoleBridge.WriteLine();
            ConsoleBridge.WriteLine("用法：PCL2Unlock.exe [选项] [目标文件]");
            ConsoleBridge.WriteLine("  （不带参数）：对自动探测到的 PCL 主程序应用补丁");
            ConsoleBridge.WriteLine("  --check, -c  只检测状态");
            ConsoleBridge.WriteLine("  --revert, -r 从备份还原");
            ConsoleBridge.WriteLine("  --help, -h   显示本帮助");
            ConsoleBridge.WriteLine();
            ConsoleBridge.WriteLine("退出码：0 成功 / 1 失败 / 2 用法错误 / 3 不是 PCL / 4 无可用补丁点 / 5 校验失败 / 6 文件被占用");
        }
    }
}
