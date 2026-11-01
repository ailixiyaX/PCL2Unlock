using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PCL2Unlock
{
    /// <summary>
    /// WPF 程序是窗口子系统，双击运行时没有控制台。
    /// 这里在需要输出时接管父控制台，或者新建一个；输出被重定向时直接写标准输出。
    /// </summary>
    public static class ConsoleBridge
    {
        private const int STD_OUTPUT_HANDLE = -11;
        private const int FILE_TYPE_CHAR = 2;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int GetFileType(IntPtr hFile);

        private static bool _ready;
        private static TextWriter _out;
        private static TextWriter _err;

        public static void Ensure()
        {
            if (_ready) return;
            _ready = true;
            try
            {
                if (Console.IsOutputRedirected)
                {
                    _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                    _err = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
                    return;
                }
                bool hasConsole = GetFileType(GetStdHandle(STD_OUTPUT_HANDLE)) == FILE_TYPE_CHAR;
                if (!hasConsole)
                {
                    if (!AttachConsole(-1)) AllocConsole();
                }
                try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
                _out = Console.Out;
                _err = Console.Error;
            }
            catch
            {
                _out = Console.Out;
                _err = Console.Error;
            }
        }

        public static void Write(string text)
        {
            Ensure();
            try { _out.Write(text); } catch { }
        }

        public static void WriteLine(string text = "")
        {
            Ensure();
            try { _out.WriteLine(text); } catch { }
        }

        public static void ErrorLine(string text)
        {
            Ensure();
            try { _err.WriteLine(text); } catch { }
        }
    }
}
