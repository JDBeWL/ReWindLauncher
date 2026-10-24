using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Windows.Forms;
using ReWindLauncher.Core;
using ReWindLauncher.UI;

namespace ReWindLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 64;
            ServicePointManager.Expect100Continue = false;

            Dpi.Init();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (sender, args) =>
            {
                Log.Error("未处理的界面异常", args.Exception);
                MessageBox.Show(args.Exception.Message, "Minecraft启动器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                Log.Error("未处理的异常", args.ExceptionObject as Exception);

            Application.Run(new MainForm());
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            if (name != "Newtonsoft.Json") return null;

            using (Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Newtonsoft.Json.deflate"))
            {
                if (stream == null) return null;
                using (var deflate = new DeflateStream(stream, CompressionMode.Decompress))
                using (var buffer = new MemoryStream())
                {
                    deflate.CopyTo(buffer);
                    return Assembly.Load(buffer.ToArray());
                }
            }
        }
    }
}
