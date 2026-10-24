using System;
using System.IO;
using System.Text;

namespace ReWindLauncher.Core
{
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static bool DebugEnabled { get; set; }
        public static string CurrentPath => _path;

        public static void Init(string baseDir)
        {
            try
            {
                string dir = Path.Combine(baseDir, "logs");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "launcher.log");
                RotateIfNeeded();
                Write("===== ReWindLauncher 启动 =====");
            }
            catch
            {
                _path = null;
            }
        }

        public static void Info(string message) => Write(message);

        public static void Debug(string message)
        {
            if (DebugEnabled) Write("[debug] " + message);
        }

        public static void Error(string message, Exception ex = null)
        {
            Write("[error] " + message + (ex == null ? "" : "\r\n" + ex));
        }

        private static void Write(string message)
        {
            if (_path == null) return;
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(_path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch
            {
                // 写日志失败不抛，日志不能影响主流程
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                if (!File.Exists(_path)) return;
                if (new FileInfo(_path).Length < 2 * 1024 * 1024) return;
                string old = _path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(_path, old);
            }
            catch
            {
            }
        }
    }
}
