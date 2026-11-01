using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace PCL2Unlock.Core
{
    public enum SiteState
    {
        NotPatched = 0,
        Patched = 1,
        Missing = 2
    }

    public sealed class PatchSite
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public SiteState State { get; set; }
    }

    public sealed class PatchAnalysis
    {
        public string Path { get; set; }
        public bool IsSupported { get; set; }
        public string Error { get; set; }
        public string AssemblyName { get; set; }
        public string Version { get; set; }
        public long FileSize { get; set; }
        public string Sha256 { get; set; }
        public bool Locked { get; set; }
        public List<PatchSite> Sites { get; } = new List<PatchSite>();

        public int UsableSites => Sites.Count(s => s.State != SiteState.Missing);
        public int PatchedSites => Sites.Count(s => s.State == SiteState.Patched);
        public bool AllPatched => UsableSites > 0 && PatchedSites == UsableSites;
        public bool AnyPatched => PatchedSites > 0;
        public bool Compatible => IsSupported && UsableSites > 0;
    }

    public sealed class PatchReport
    {
        public bool Changed { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public PatchAnalysis Analysis { get; set; }
    }

    /// <summary>
    /// 针对原版 PCL（Meloong-Git/PCL 2.99.x）的 IL 补丁引擎。
    /// 原版的限制是 <c>ModSecret.RestrictMcDownload</c> 这个编译期常量（True），
    /// 所以运行时不存在可以翻转的开关字段，只能把被改写/删除的方法体逐处恢复。
    /// </summary>
    public static class Pcl2Patcher
    {
        public const string BackupSuffix = ".pcl2fix.bak";
        public const string TargetAssemblyName = "Plain Craft Launcher 2";
        private const string RestrictMessage = "不再提供 MC 下载";

        public static string BackupPath(string path) => path + BackupSuffix;

        private sealed class Site
        {
            public string Id;
            public string Title;
            public Func<ModuleDefMD, bool, string, (SiteState, string)> Run;
        }

        private static readonly List<Site> Sites = new List<Site>
        {
            new Site { Id = "A", Title = "下载页 · 恢复「安装」入口", Run = (m, apply, d) => PatchA(m, apply) },
            new Site { Id = "B", Title = "下载页 · 恢复安装列表加载器", Run = (m, apply, d) => PatchB(m, apply) },
            new Site { Id = "C", Title = "整合包 · 恢复「导入整合包」", Run = (m, apply, d) => PatchC(m, apply) },
            new Site { Id = "D", Title = "整合包 · 移除安装拦截异常", Run = (m, apply, d) => PatchD(m, apply) },
            new Site { Id = "E", Title = "安装页 · 恢复 Minecraft 卡片", Run = (m, apply, d) => PatchE(m, apply) },
            new Site { Id = "F", Title = "资源页 · 恢复整合包搜索按钮", Run = (m, apply, d) => PatchF(m, apply) },
            new Site { Id = "J", Title = "版本选择页 · 恢复「导入整合包」项", Run = (m, apply, d) => PatchJ(m, apply) }
        };

        public static PatchAnalysis Analyze(string path)
        {
            var a = new PatchAnalysis { Path = path };
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    a.Error = "文件不存在：" + path;
                    return a;
                }
                a.FileSize = fi.Length;
                a.Sha256 = Sha256(path);
                a.Locked = IsLocked(path);
            }
            catch (Exception ex)
            {
                a.Error = ex.Message;
                return a;
            }

            try
            {
                using (var mod = ModuleDefMD.Load(path))
                {
                    a.AssemblyName = mod.Assembly?.Name?.String;
                    a.Version = mod.Assembly?.Version?.ToString();
                    a.IsSupported = string.Equals(a.AssemblyName, TargetAssemblyName, StringComparison.OrdinalIgnoreCase)
                                    || (mod.Assembly != null && mod.Assembly.Name.String.StartsWith(TargetAssemblyName, StringComparison.OrdinalIgnoreCase));
                    if (!a.IsSupported)
                    {
                        a.Error = "这不是原版 PCL 主程序（程序集：" + (a.AssemblyName ?? "无") + "）";
                        return a;
                    }
                    foreach (var s in Sites)
                    {
                        var site = new PatchSite { Id = s.Id, Title = s.Title };
                        try
                        {
                            var (state, detail) = s.Run(mod, false, null);
                            site.State = state;
                            site.Detail = detail;
                        }
                        catch (Exception ex)
                        {
                            site.State = SiteState.Missing;
                            site.Detail = ex.Message;
                        }
                        a.Sites.Add(site);
                    }
                }
            }
            catch (Exception ex)
            {
                a.Error = "无法读取程序集：" + ex.Message;
            }
            return a;
        }

        public static PatchReport Apply(string path, Action<string> log)
        {
            var report = new PatchReport();
            var before = Analyze(path);
            if (!before.Compatible)
            {
                report.Analysis = before;
                report.Message = before.Error ?? "此文件不可用";
                return report;
            }
            if (before.AllPatched)
            {
                report.Analysis = before;
                report.Success = true;
                report.Message = "已经打过补丁，无需重复操作";
                return report;
            }
            if (before.Locked)
            {
                report.Analysis = before;
                report.Message = "文件正被占用，请先退出 PCL";
                return report;
            }

            string backup = BackupPath(path);
            string tmp = path + ".pcl2fix.tmp";
            try
            {
                ModuleDefMD mod = ModuleDefMD.Load(path);
                var states = new List<(Site, SiteState, string)>();
                try
                {
                    foreach (var s in Sites)
                    {
                        SiteState st;
                        string detail;
                        try
                        {
                            (st, detail) = s.Run(mod, true, null);
                        }
                        catch (Exception ex)
                        {
                            st = SiteState.Missing;
                            detail = ex.Message;
                        }
                        states.Add((s, st, detail));
                        log?.Invoke(string.Format("[{0}] {1}：{2}", s.Id, s.Title, DetailText(st, detail)));
                    }

                    if (!states.Any(x => x.Item2 == SiteState.Patched))
                    {
                        report.Success = false;
                        report.Message = "没有可修补的位置";
                        return report;
                    }

                    if (File.Exists(tmp)) File.Delete(tmp);
                    var opts = new ModuleWriterOptions(mod)
                    {
                        Logger = DummyLogger.NoThrowInstance,
                        WritePdb = false
                    };
                    mod.Write(tmp, opts);
                }
                finally
                {
                    mod.Dispose();
                }

                if (!File.Exists(backup))
                {
                    File.Copy(path, backup, true);
                    log?.Invoke("已备份原始文件：" + backup);
                }

                Replace(tmp, path);
                log?.Invoke("已写入补丁：" + path);
                report.Changed = true;
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                report.Success = false;
                report.Message = "写入失败：" + ex.Message;
                report.Analysis = Analyze(path);
                return report;
            }

            var after = Analyze(path);
            report.Analysis = after;
            if (after.PatchedSites == 0)
            {
                report.Success = false;
                report.Message = "补丁复核失败，已回滚";
                try { Revert(path, null); } catch { }
                return report;
            }
            report.Success = true;
            report.Message = "补丁完成，共 " + after.PatchedSites + " 处";
            return report;
        }

        public static PatchReport Revert(string path, Action<string> log)
        {
            var report = new PatchReport();
            string backup = BackupPath(path);
            if (!File.Exists(backup))
            {
                report.Message = "没有找到备份文件：" + backup;
                report.Analysis = Analyze(path);
                return report;
            }
            try
            {
                if (IsLocked(path))
                {
                    report.Message = "文件正被占用，请先退出 PCL";
                    report.Analysis = Analyze(path);
                    return report;
                }
                Replace(backup, path);
                log?.Invoke("已从备份还原：" + backup);
                report.Changed = true;
                report.Success = true;
                report.Message = "已还原原始文件";
            }
            catch (Exception ex)
            {
                report.Success = false;
                report.Message = "还原失败：" + ex.Message;
            }
            report.Analysis = Analyze(path);
            return report;
        }

        private static string DetailText(SiteState s, string detail)
        {
            switch (s)
            {
                case SiteState.Patched: return string.IsNullOrEmpty(detail) ? "已修补" : detail;
                case SiteState.Missing: return "不适用（" + detail + "）";
                default: return "未修补";
            }
        }

        private static void Replace(string src, string dst)
        {
            try
            {
                File.Replace(src, dst, null, true);
            }
            catch (Exception)
            {
                File.Copy(src, dst, true);
                TryDelete(src);
            }
        }

        private static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        private static string Sha256(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }

        public static bool IsLocked(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        // ---------------- 通用查找助手 ----------------

        private static TypeDef FindType(ModuleDef md, string fullName)
        {
            return md.GetTypes().FirstOrDefault(t => t.FullName == fullName);
        }

        private static MethodDef FindMethod(ModuleDef md, string typeFull, string name, int paramCount = -1)
        {
            var t = FindType(md, typeFull);
            if (t == null) return null;
            // 注意：MethodDef.Parameters 对实例方法含隐藏的 this，必须用 MethodSig.Params 计真实参数个数
            return t.Methods.FirstOrDefault(m => m.Name == name && (paramCount < 0 || m.MethodSig.Params.Count == paramCount));
        }

        private static bool IsLdloc(Instruction i)
        {
            var c = i.OpCode.Code;
            return c == Code.Ldloc || c == Code.Ldloc_S || c == Code.Ldloc_0 || c == Code.Ldloc_1
                   || c == Code.Ldloc_2 || c == Code.Ldloc_3;
        }

        private static IMethod FindMethodRef(ModuleDef md, Func<IMethod, bool> pred)
        {
            foreach (var t in md.GetTypes())
            {
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var i in m.Body.Instructions)
                    {
                        if (i.Operand is IMethod im && pred(im)) return im;
                    }
                }
            }
            return null;
        }

        private static IMethod FindMethodSpec(ModuleDef md, Func<MethodSpec, bool> pred)
        {
            foreach (var t in md.GetTypes())
            {
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var i in m.Body.Instructions)
                    {
                        if (i.Operand is MethodSpec ms && pred(ms)) return ms;
                    }
                }
            }
            return null;
        }

        private static Instruction Call(TypeDef type, string name, int paramCount = -1)
        {
            var m = type.Methods.FirstOrDefault(x => x.Name == name && (paramCount < 0 || x.MethodSig.Params.Count == paramCount));
            if (m == null) throw new InvalidOperationException("找不到方法 " + type.FullName + "::" + name);
            return Instruction.Create(OpCodes.Call, m);
        }

        private static Instruction CallVirt(TypeDef type, string name)
        {
            var m = type.Methods.FirstOrDefault(x => x.Name == name);
            if (m == null) throw new InvalidOperationException("找不到方法 " + type.FullName + "::" + name);
            return Instruction.Create(OpCodes.Callvirt, m);
        }

        private static int IndexOfString(MethodDef m, string needle)
        {
            var ins = m.Body.Instructions;
            for (int i = 0; i < ins.Count; i++)
                if (ins[i].OpCode == OpCodes.Ldstr && ins[i].Operand is string s && s.Contains(needle))
                    return i;
            return -1;
        }

        private static void RebuildBody(MethodDef m, IList<Instruction> codes, IList<Local> locals = null, int maxStack = 8, bool initLocals = true)
        {
            var body = new CilBody { InitLocals = initLocals, MaxStack = (ushort)maxStack };
            if (locals != null) foreach (var l in locals) body.Variables.Add(l);
            foreach (var c in codes) body.Instructions.Add(c);
            body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            m.Body = body;
        }

        // ---------------- A：PageDownloadLeft 构造函数 ----------------

        private static (SiteState, string) PatchA(ModuleDefMD md, bool apply)
        {
            var ctor = FindMethod(md, "PCL.PageDownloadLeft", ".ctor", 0);
            if (ctor == null || !ctor.HasBody) return (SiteState.Missing, "找不到 PageDownloadLeft 构造函数");
            var ins = ctor.Body.Instructions;
            int idx = -1;
            for (int i = 0; i < ins.Count; i++)
                if (ins[i].OpCode == OpCodes.Call && ins[i].Operand is IMethod im && im.Name == "InitializeComponent") { idx = i; break; }
            if (idx < 0) return (SiteState.Missing, "构造函数结构异常（缺少 InitializeComponent）");
            if (ins.Count == idx + 2 && ins[idx + 1].OpCode == OpCodes.Ret) return (SiteState.Patched, null);
            if (!apply) return (SiteState.NotPatched, "构造函数末尾有 " + (ins.Count - idx - 1) + " 条隐藏入口的指令");
            if (ctor.Body.ExceptionHandlers.Count > 0) return (SiteState.Missing, "构造函数含异常处理，跳过");

            while (ins.Count > idx + 1) ins.RemoveAt(ins.Count - 1);
            ins.Add(Instruction.Create(OpCodes.Ret));
            return (SiteState.Patched, "已移除隐藏「安装」入口的代码");
        }

        // ---------------- B：PageDownloadInstall.LoaderInit ----------------

        private static (SiteState, string) PatchB(ModuleDefMD md, bool apply)
        {
            var m = FindMethod(md, "PCL.PageDownloadInstall", "LoaderInit", 0);
            if (m == null || !m.HasBody) return (SiteState.Missing, "找不到 PageDownloadInstall.LoaderInit");
            var mod = FindType(md, "PCL.ModDownload");
            var field = mod?.Fields.FirstOrDefault(f => f.Name == "DlClientListLoader");
            if (field == null) return (SiteState.Missing, "找不到 ModDownload.DlClientListLoader");

            var ins = m.Body.Instructions;
            int i = IndexOfString(m, "关闭下载");
            if (i < 0)
            {
                bool hasField = ins.Any(x => x.OpCode == OpCodes.Ldsfld && (x.Operand as IField)?.Name == "DlClientListLoader");
                return hasField ? (SiteState.Patched, null) : (SiteState.Missing, "找不到「关闭下载」加载器");
            }
            int j = -1;
            for (int k = i; k < ins.Count; k++)
            {
                if (ins[k].OpCode == OpCodes.Newobj && ins[k].Operand is IMethod ctor
                    && ctor.Name == ".ctor" && ctor.DeclaringType.Name.Contains("LoaderTask"))
                { j = k; break; }
            }
            if (j < 0) return (SiteState.Missing, "找不到替代的 LoaderTask 构造");
            if (!apply) return (SiteState.NotPatched, "安装列表被替换为「关闭下载」占位");

            for (int k = j; k >= i; k--) ins.RemoveAt(k);
            ins.Insert(i, Instruction.Create(OpCodes.Ldsfld, field));
            return (SiteState.Patched, "已恢复 Minecraft 安装列表加载器");
        }

        // ---------------- C：ModModpack.ModpackInstall() ----------------

        private static (SiteState, string) PatchC(ModuleDefMD md, bool apply)
        {
            var m = FindMethod(md, "PCL.ModModpack", "ModpackInstall", 0);
            if (m == null || !m.HasBody) return (SiteState.Missing, "找不到 ModModpack.ModpackInstall()");
            var type = m.DeclaringType;
            if (type.Methods.Any(x => x.Name == "PclfixImportModpack")) return (SiteState.Patched, null);
            if (!apply) return (SiteState.NotPatched, "「导入整合包」被替换为提示");

            var three = FindMethod(md, "PCL.ModModpack", "ModpackInstall", 3);
            var baseType = FindType(md, "PCL.ModBase");
            if (three == null || baseType == null) return (SiteState.Missing, "缺少 ModpackInstall(String,String,String) 或 ModBase");
            var runInThread = baseType.Methods.FirstOrDefault(x => x.Name == "RunInThread");
            if (runInThread == null) return (SiteState.Missing, "找不到 ModBase.RunInThread");

            var selectFile = FindMethodRef(md, im => im.Name == "SelectFile" && im.DeclaringType.FullName.Contains("MeloongCore.Wpf.Dialogs"));
            if (selectFile == null) return (SiteState.Missing, "找不到 Dialogs.SelectFile");
            var firstOrDefault = FindMethodSpec(md, ms => ms.Method.Name == "FirstOrDefault" && ms.Method.DeclaringType.FullName == "System.Linq.Enumerable");
            if (firstOrDefault == null) return (SiteState.Missing, "找不到 Enumerable.FirstOrDefault");
            var actionCtor = FindMethodRef(md, im => im.Name == ".ctor" && im.DeclaringType.FullName == "System.Action");
            if (actionCtor == null) return (SiteState.Missing, "找不到 Action 构造函数");
            var isNullOrEmpty = FindMethodRef(md, im => im.Name == "IsNullOrEmpty" && im.DeclaringType.FullName == "System.String")
                                ?? md.CorLibTypes.GetTypeRef("System", "String").ResolveThrow().Methods.First(x => x.Name == "IsNullOrEmpty");

            var nullableSig = selectFile.MethodSig.Params[3];

            // 1) 新增私有静态方法 PclfixImportModpack()
            var helperSig = MethodSig.CreateStatic(md.CorLibTypes.Void);
            var helper = new MethodDefUser("PclfixImportModpack", helperSig,
                MethodImplAttributes.IL,
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig)
            {
                Attributes = MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig
            };

            var vNull = new Local(nullableSig);
            var vFile = new Local(md.CorLibTypes.String);
            var codes = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldstr, "选择整合包压缩文件"),
                Instruction.Create(OpCodes.Ldc_I4_0),
                Instruction.Create(OpCodes.Ldnull),
                Instruction.Create(OpCodes.Ldloca, vNull),
                Instruction.Create(OpCodes.Initobj, nullableSig.ToTypeDefOrRef()),
                Instruction.Create(OpCodes.Call, selectFile),
                Instruction.Create(OpCodes.Call, firstOrDefault),
                Instruction.Create(OpCodes.Stloc, vFile),
                Instruction.Create(OpCodes.Ldloc, vFile),
                Instruction.Create(OpCodes.Call, isNullOrEmpty),
                null, // brtrue -> ret
                Instruction.Create(OpCodes.Ldloc, vFile),
                Instruction.Create(OpCodes.Ldnull),
                Instruction.Create(OpCodes.Ldnull),
                Instruction.Create(OpCodes.Call, three),
                Instruction.Create(OpCodes.Pop)
            };
            var retInstr = Instruction.Create(OpCodes.Ret);
            codes[10] = Instruction.Create(OpCodes.Brtrue, retInstr);
            var body = new CilBody { InitLocals = true, MaxStack = 4 };
            body.Variables.Add(vNull);
            body.Variables.Add(vFile);
            foreach (var c in codes) body.Instructions.Add(c);
            body.Instructions.Add(retInstr);
            helper.Body = body;
            type.Methods.Add(helper);

            // 2) 重建 ModpackInstall()：RunInThread(AddressOf PclfixImportModpack)
            var newCodes = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldnull),
                Instruction.Create(OpCodes.Ldftn, helper),
                Instruction.Create(OpCodes.Newobj, actionCtor),
                Instruction.Create(OpCodes.Call, runInThread)
            };
            RebuildBody(m, newCodes, null, 3);
            return (SiteState.Patched, "已恢复文件选择 + 后台安装");
        }

        // ---------------- D：ModModpack.ModpackInstall(String,String,String) ----------------

        private static (SiteState, string) PatchD(ModuleDefMD md, bool apply)
        {
            var m = FindMethod(md, "PCL.ModModpack", "ModpackInstall", 3);
            if (m == null || !m.HasBody) return (SiteState.Missing, "找不到 ModpackInstall(String,String,String)");
            var ins = m.Body.Instructions;

            int i = IndexOfString(m, RestrictMessage);
            if (i < 0)
            {
                return (SiteState.Patched, null);
            }
            if (i + 2 >= ins.Count) return (SiteState.Missing, "拦截指令结构异常");
            if (ins[i + 1].OpCode != OpCodes.Newobj || ins[i + 2].OpCode != OpCodes.Throw)
                return (SiteState.Missing, "拦截指令结构异常");
            if (!apply) return (SiteState.NotPatched, "安装前会抛出「不再提供 MC 下载」异常");

            var ldnull = Instruction.Create(OpCodes.Ldnull);
            var ret = Instruction.Create(OpCodes.Ret);
            ins[i].OpCode = OpCodes.Leave;
            ins[i].Operand = ldnull;
            ins.RemoveAt(i + 2);
            ins.RemoveAt(i + 1);
            ins.Add(ldnull);
            ins.Add(ret);
            m.Body.MaxStack = (ushort)Math.Max((int)m.Body.MaxStack, 2);
            return (SiteState.Patched, "已移除安装拦截");
        }

        // ---------------- E：PageDownloadModify.LoadInstanceFinished ----------------

        private static (SiteState, string) PatchE(ModuleDefMD md, bool apply)
        {
            var m = FindMethod(md, "PCL.PageDownloadModify", "LoadInstanceFinished");
            if (m == null || !m.HasBody) return (SiteState.Missing, "找不到 PageDownloadModify.LoadInstanceFinished");
            var ins = m.Body.Instructions;
            for (int i = 0; i + 2 < ins.Count; i++)
            {
                if (!(ins[i].Operand is IMethod getter) || getter.Name != "get_CardMinecraft") continue;
                if (!(ins[i + 2].Operand is IMethod setter) || setter.Name != "set_Visibility") continue;
                // 已修补：可见性已是 Visible(0)
                if (ins[i + 1].OpCode == OpCodes.Ldc_I4_0) return (SiteState.Patched, null);
                if (ins[i + 1].OpCode != OpCodes.Ldc_I4_2 && ins[i + 1].OpCode != OpCodes.Ldc_I4) continue;
                if (!apply) return (SiteState.NotPatched, "Minecraft 卡片被隐藏");
                ins[i + 1].OpCode = OpCodes.Ldc_I4_0;
                ins[i + 1].Operand = null;
                return (SiteState.Patched, "已显示 Minecraft 卡片");
            }
            return (SiteState.Missing, "找不到 CardMinecraft 的可见性设置");
        }

        // ---------------- F：PageResource.set_PageType ----------------

        private static (SiteState, string) PatchF(ModuleDefMD md, bool apply)
        {
            var m = FindMethod(md, "PCL.PageResource", "set_PageType");
            if (m == null || !m.HasBody) return (SiteState.Missing, "找不到 PageResource.set_PageType");
            var ins = m.Body.Instructions;
            int btn = -1;
            for (int i = 0; i < ins.Count; i++)
                if (ins[i].OpCode == OpCodes.Callvirt && ins[i].Operand is IMethod im && im.Name == "get_BtnSearchInstallModPack") { btn = i; break; }
            if (btn < 0) return (SiteState.Missing, "找不到整合包搜索按钮属性");

            // 已修补判定：get_BtnSearchInstallModPack 之后 10 条内出现 Ceq + Brfalse（插入的判断序列）
            for (int k = btn + 1; k < Math.Min(ins.Count - 1, btn + 10); k++)
                if (ins[k].OpCode == OpCodes.Ceq && ins[k + 1].OpCode == OpCodes.Brfalse)
                    return (SiteState.Patched, null);

            int setVis = -1;
            for (int k = btn + 1; k < ins.Count; k++)
                if (ins[k].OpCode == OpCodes.Callvirt && ins[k].Operand is IMethod im2 && im2.Name == "set_Visibility") { setVis = k; break; }
            if (setVis < 0) return (SiteState.Missing, "找不到可见性设置");
            if (!apply) return (SiteState.NotPatched, "整合包搜索按钮被固定隐藏");

            int start = btn + 1;
            int count = setVis - start; // 要替换的区间 [start, setVis)
            var collapsed = Instruction.Create(OpCodes.Ldc_I4_2);
            var done = ins[setVis];
            var newSeq = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Ldc_I4_2),
                Instruction.Create(OpCodes.Ceq),
                Instruction.Create(OpCodes.Brfalse, collapsed),
                Instruction.Create(OpCodes.Ldc_I4_0),
                Instruction.Create(OpCodes.Br, done),
                collapsed
            };
            for (int k = 0; k < count; k++) ins.RemoveAt(start);
            for (int k = 0; k < newSeq.Count; k++) ins.Insert(start + k, newSeq[k]);
            m.Body.MaxStack = (ushort)Math.Max((int)m.Body.MaxStack, 3);
            return (SiteState.Patched, "已恢复「整合包」时的搜索按钮显示");
        }

        // ---------------- J：PageSelectLeft.McFolderListUI ----------------

        private static (SiteState, string) PatchJ(ModuleDefMD md, bool apply)
        {
            var m = FindMethod(md, "PCL.PageSelectLeft", "McFolderListUI");
            if (m == null || !m.HasBody) return (SiteState.Missing, "找不到 PageSelectLeft.McFolderListUI");
            var ins = m.Body.Instructions;
            int title = -1;
            for (int i = 0; i < ins.Count; i++)
                if (ins[i].OpCode == OpCodes.Ldstr && (ins[i].Operand as string) == "导入整合包") { title = i; break; }
            if (title < 0) return (SiteState.Missing, "找不到「导入整合包」列表项");

            int click = -1;
            for (int k = title; k < Math.Min(ins.Count, title + 60); k++)
                if (ins[k].OpCode == OpCodes.Callvirt && ins[k].Operand is IMethod im && im.Name == "add_Click") { click = k; break; }
            if (click < 0) return (SiteState.Missing, "找不到导入整合包项的点击绑定");

            // 已经插入过？必须匹配完整 6 条插入序列（Ldsfld FrmSelectLeft → get_PanList → get_Children → Ldloc → Add → Pop），
            // 否则原版 add_Click 后面紧跟的 spacer（Ldsfld FrmSelectLeft/get_PanList/get_Children/Newobj...）会被误判。
            if (click + 6 < ins.Count
                && ins[click + 1].OpCode == OpCodes.Ldsfld && (ins[click + 1].Operand as IField)?.Name == "FrmSelectLeft"
                && ins[click + 2].OpCode == OpCodes.Callvirt && (ins[click + 2].Operand as IMethod)?.Name == "get_PanList"
                && ins[click + 3].OpCode == OpCodes.Callvirt && (ins[click + 3].Operand as IMethod)?.Name == "get_Children"
                && IsLdloc(ins[click + 4])
                && ins[click + 5].OpCode == OpCodes.Callvirt && (ins[click + 5].Operand as IMethod)?.Name == "Add"
                && ins[click + 6].OpCode == OpCodes.Pop)
                return (SiteState.Patched, null);
            if (!apply) return (SiteState.NotPatched, "「导入整合包」项没有加入列表");

            IField frm = null; IMethod getPan = null; IMethod getChildren = null; IMethod add = null;
            for (int k = click + 1; k < ins.Count && k < click + 40; k++)
            {
                if (frm == null && ins[k].OpCode == OpCodes.Ldsfld && (ins[k].Operand as IField)?.Name == "FrmSelectLeft") frm = (IField)ins[k].Operand;
                if (getPan == null && ins[k].OpCode == OpCodes.Callvirt && (ins[k].Operand as IMethod)?.Name == "get_PanList") getPan = (IMethod)ins[k].Operand;
                if (getChildren == null && ins[k].OpCode == OpCodes.Callvirt && (ins[k].Operand as IMethod)?.Name == "get_Children") getChildren = (IMethod)ins[k].Operand;
                if (add == null && ins[k].OpCode == OpCodes.Callvirt && (ins[k].Operand as IMethod)?.Name == "Add") add = (IMethod)ins[k].Operand;
            }
            if (frm == null || getPan == null || getChildren == null || add == null)
                return (SiteState.Missing, "找不到列表容器指令");

            var item = m.Body.Variables.FirstOrDefault(v => v.Type.FullName == "PCL.MyListItem");
            if (item == null) return (SiteState.Missing, "找不到列表项局部变量");

            var seq = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldsfld, frm),
                Instruction.Create(OpCodes.Callvirt, getPan),
                Instruction.Create(OpCodes.Callvirt, getChildren),
                Instruction.Create(OpCodes.Ldloc, item),
                Instruction.Create(OpCodes.Callvirt, add),
                Instruction.Create(OpCodes.Pop)
            };
            for (int k = 0; k < seq.Count; k++) ins.Insert(click + 1 + k, seq[k]);
            m.Body.MaxStack = (ushort)Math.Max((int)m.Body.MaxStack, 6);
            return (SiteState.Patched, "已把「导入整合包」加入列表");
        }
    }
}
