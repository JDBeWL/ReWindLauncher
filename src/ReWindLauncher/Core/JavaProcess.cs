using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ReWindLauncher.Core
{
    public sealed class JavaProcess : IDisposable
    {
        private Process _process;
        private StreamWriter _writer;
        private readonly object _gate = new object();
        private int _pendingLines;
        private DateTime _lastFlush = DateTime.UtcNow;

        public event Action<string> OutputLine;
        public event Action<int> Exited;

        public bool IsRunning
        {
            get
            {
                try
                {
                    return _process != null && !_process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void Start(LaunchPlan plan, bool debug)
        {
            string baseDir = Path.GetDirectoryName(plan.LogPath) ?? ".";
            string logPath = debug
                ? plan.LogPath
                : Path.Combine(baseDir, "logs", "java.log");

            try
            {
                string dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // 调试模式逐行落盘；正常模式靠 `OnLine` 攒批刷，减少写盘次数
                _writer = new StreamWriter(logPath, debug ? false : true, new UTF8Encoding(false)) { AutoFlush = debug };
            }
            catch
            {
                _writer = null;
            }

            var info = new ProcessStartInfo
            {
                FileName = plan.JavaPath,
                Arguments = plan.Arguments,
                WorkingDirectory = plan.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = plan.JavaMajor >= 18 ? Encoding.UTF8 : Encoding.Default,
                StandardErrorEncoding = plan.JavaMajor >= 18 ? Encoding.UTF8 : Encoding.Default
            };

            _process = new Process { StartInfo = info, EnableRaisingEvents = true };
            _process.OutputDataReceived += (sender, args) => OnLine(args.Data);
            _process.ErrorDataReceived += (sender, args) => OnLine(args.Data);
            _process.Exited += (sender, args) =>
            {
                int code = 0;
                try { code = _process.ExitCode; } catch { }
                OnLine("[启动器] 游戏进程已退出，退出码 " + code);
                lock (_gate) FlushLocked();
                Exited?.Invoke(code);
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            OnLine("[启动器] 已启动: " + plan.JavaPath);
            OnLine("[启动器] 工作目录: " + plan.WorkingDirectory);
        }

        private void OnLine(string line)
        {
            if (line == null) return;
            lock (_gate)
            {
                try
                {
                    if (_writer != null)
                    {
                        _writer.WriteLine(line);
                        _pendingLines++;
                        // 16 行 / 500ms 先到先落盘，进程被杀时最多丢这一批
                        if (_pendingLines >= 16 || (DateTime.UtcNow - _lastFlush).TotalMilliseconds >= 500)
                            FlushLocked();
                    }
                }
                catch
                {
                }
            }
            OutputLine?.Invoke(line);
        }

        private void FlushLocked()
        {
            try { _writer?.Flush(); } catch { }
            _pendingLines = 0;
            _lastFlush = DateTime.UtcNow;
        }

        public void Stop()
        {
            try
            {
                if (IsRunning) _process.Kill();
            }
            catch (Exception ex)
            {
                Log.Error("结束游戏进程失败", ex);
            }
        }

        public void Dispose()
        {
            try
            {
                lock (_gate)
                {
                    _writer?.Dispose();
                    _writer = null;
                }
            }
            catch
            {
            }
            try { _process?.Dispose(); } catch { }
            _process = null;
        }
    }
}
