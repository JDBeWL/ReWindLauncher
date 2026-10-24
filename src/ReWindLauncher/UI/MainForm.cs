using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReWindLauncher.Core;

namespace ReWindLauncher.UI
{
    public sealed class MainForm : Form
    {
        private const int IdLaunch = 0;
        private const int IdUpdate = 1;
        private const int IdSettings = 2;
        private const int IdCombo = 3;
        private const int IdOpenDir = 4;
        private const int IdClose = 5;

        private Config _config;
        private List<VersionEntry> _versions = new List<VersionEntry>();
        private int _versionIndex = -1;
        private Image _background;
        private Bitmap _backgroundCache;
        private int _hover = -1;
        private int _pressed = -1;
        private string _status = "";
        private JavaProcess _java;
        private DropdownForm _dropdown;
        private bool _scanning;
        private int _scanGeneration;

        public MainForm()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Dpi.S(MainLayout.Width), Dpi.S(MainLayout.Height));
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            Text = "ReWindLauncher";
            BackColor = Theme.Frame;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _config = Config.Load();
            Log.Init(_config.BaseDir);
            Log.DebugEnabled = _config.DebugMode;
            Log.Info("配置路径: " + _config.ConfigPath);
            Log.Info("游戏目录: " + _config.ResolvedGameRoot);

            LoadBackground();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ScanVersionsAsync();
            // 后台预热 Java 缓存，避免首次启动游戏/打开设置时同步扫盘卡住界面
            Task.Run(() => JavaLocator.FindAll(true));
        }

        private void LoadBackground()
        {
            Image loaded = null;
            try
            {
                if (Directory.Exists(_config.SkinDir))
                {
                    var files = Directory.GetFiles(_config.SkinDir)
                        .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (files.Count > 0)
                    {
                        string chosen;
                        if (_config.RandomSkin)
                        {
                            chosen = files[new Random(Guid.NewGuid().GetHashCode()).Next(files.Count)];
                        }
                        else
                        {
                            chosen = files.FirstOrDefault(f =>
                                         Path.GetFileName(f).Equals("bg1.jpg", StringComparison.OrdinalIgnoreCase))
                                     ?? files[0];
                        }
                        using (Image source = Image.FromFile(chosen))
                            loaded = new Bitmap(source);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("加载皮肤失败", ex);
            }

            if (loaded == null) loaded = Theme.LoadEmbedded("bg_default.jpg");
            Image old = _background;
            _background = loaded;
            old?.Dispose();
            RebuildBackgroundCache();
        }

        // 高 DPI 下预缩放，避免每次重绘重做双三次插值
        private void RebuildBackgroundCache()
        {
            _backgroundCache?.Dispose();
            _backgroundCache = null;
            if (_background == null || Dpi.Scale <= 1.001f) return;

            Rectangle bounds = ClientRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            var cache = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics graphics = Graphics.FromImage(cache))
                Theme.DrawStretchImage(graphics, _background, bounds);
            _backgroundCache = cache;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RebuildBackgroundCache();
        }

        // 版本多或磁盘慢时，首屏不能等扫描完
        private async void ScanVersionsAsync()
        {
            int generation = ++_scanGeneration;
            _scanning = true;
            Invalidate(Dpi.S(MainLayout.VersionBox));

            string root = _config.ResolvedGameRoot;
            string selected = _config.SelectedVersion;
            List<VersionEntry> versions;
            try
            {
                versions = await Task.Run(() => VersionStore.Scan(root));
            }
            catch (Exception ex)
            {
                Log.Error("扫描版本失败", ex);
                versions = new List<VersionEntry>();
            }

            if (generation != _scanGeneration || IsDisposed) return;

            _scanning = false;
            _versions = versions;
            _versionIndex = _versions.FindIndex(v => v.Id == selected);
            if (_versionIndex < 0 && _versions.Count > 0) _versionIndex = 0;
            if (_versionIndex >= 0 && _versionIndex < _versions.Count)
                _config.SelectedVersion = _versions[_versionIndex].Id;
            Invalidate(Dpi.S(MainLayout.VersionBox));
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            if (_backgroundCache != null)
            {
                g.DrawImageUnscaled(_backgroundCache, 0, 0);
            }
            else if (_background != null)
            {
                if (Dpi.Scale <= 1.001f) g.DrawImageUnscaled(_background, 0, 0);
                else Theme.DrawStretchImage(g, _background, ClientRectangle);
            }
            else
            {
                using (var back = new SolidBrush(Theme.Frame))
                    g.FillRectangle(back, ClientRectangle);
            }

            Rectangle launchRect = Dpi.S(MainLayout.LaunchButton);
            Rectangle updateRect = Dpi.S(MainLayout.UpdateButton);
            Rectangle settingsRect = Dpi.S(MainLayout.SettingsButton);

            Theme.DrawPlate(g, launchRect, 190, _hover == IdLaunch, _pressed == IdLaunch);
            Theme.DrawPlate(g, updateRect, 200, _hover == IdUpdate, _pressed == IdUpdate);
            Theme.DrawPlate(g, settingsRect, 200, _hover == IdSettings, _pressed == IdSettings);

            const TextFormatFlags center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                           TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            Theme.DrawShadowText(g, "启动游戏", Theme.BigButtonFont, launchRect, Theme.Ink, center);
            Theme.DrawShadowText(g, "更新游戏", Theme.SmallButtonFont, updateRect, Theme.Ink, center);
            Theme.DrawShadowText(g, "设置", Theme.SmallButtonFont, settingsRect, Theme.Ink, center);

            DrawVersionBox(g);
            DrawOpenDirectory(g);
            Theme.DrawCloseGlyph(g, Dpi.S(MainLayout.CloseButton), _hover == IdClose);

            string footer = "ReWindLauncher " + _config.DisplayVersion + " by JDBeWL";
            var footerRect = Dpi.S(MainLayout.Footer);
            TextRenderer.DrawText(g, footer, Theme.FooterFont,
                new Rectangle(footerRect.X + 1, footerRect.Y + 1, footerRect.Width, footerRect.Height),
                Color.FromArgb(130, 0, 0, 0),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, footer, Theme.FooterFont, footerRect, Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            if (!string.IsNullOrEmpty(_status))
            {
                var statusRect = Dpi.S(new Rectangle(300, 424, 412, 18));
                TextRenderer.DrawText(g, _status, Theme.FooterFont,
                    new Rectangle(statusRect.X + 1, statusRect.Y + 1, statusRect.Width, statusRect.Height),
                    Color.FromArgb(130, 0, 0, 0),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, _status, Theme.FooterFont, statusRect, Color.White,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private void DrawVersionBox(Graphics g)
        {
            Rectangle box = Dpi.S(MainLayout.VersionBox);
            Theme.DrawPlate(g, box, 220, _hover == IdCombo, _pressed == IdCombo);
            var inner = new Rectangle(box.X + Dpi.S(8), box.Y, box.Width - Dpi.S(34), box.Height);
            string text;
            if (_versionIndex >= 0 && _versionIndex < _versions.Count) text = _versions[_versionIndex].Id;
            else text = _scanning ? "正在扫描版本…" : "未找到版本";
            Color color = _versionIndex >= 0 ? Theme.Ink : Color.FromArgb(130, 120, 120, 120);
            TextRenderer.DrawText(g, text, Theme.ComboFont, inner, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.EndEllipsis);

            int cx = box.Right - Dpi.S(18);
            int cy = box.Y + box.Height / 2 + Dpi.S(1);
            int dx = Dpi.S(5);
            int dy = Dpi.S(3);
            Point[] triangle = { new Point(cx - dx, cy - dy), new Point(cx + dx, cy - dy), new Point(cx, cy + dy) };
            using (var brush = new SolidBrush(Color.FromArgb(150, 80, 80, 80)))
                g.FillPolygon(brush, triangle);
        }

        private void DrawOpenDirectory(Graphics g)
        {
            var rect = Dpi.S(MainLayout.OpenDirText);
            TextRenderer.DrawText(g, "打开目录", Theme.OpenDirFont,
                new Rectangle(rect.X + 1, rect.Y + 1, rect.Width, rect.Height),
                Color.FromArgb(150, 30, 30, 30),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "打开目录", Theme.OpenDirFont, rect, Color.White,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private int HitTest(Point point)
        {
            if (Dpi.S(MainLayout.CloseButton).Contains(point)) return IdClose;
            if (Dpi.S(MainLayout.LaunchButton).Contains(point)) return IdLaunch;
            if (Dpi.S(MainLayout.UpdateButton).Contains(point)) return IdUpdate;
            if (Dpi.S(MainLayout.SettingsButton).Contains(point)) return IdSettings;
            if (Dpi.S(MainLayout.VersionBox).Contains(point)) return IdCombo;
            if (Dpi.S(MainLayout.OpenDirText).Contains(point)) return IdOpenDir;
            return -1;
        }

        private Rectangle RectFor(int id)
        {
            switch (id)
            {
                case IdLaunch: return Dpi.S(MainLayout.LaunchButton);
                case IdUpdate: return Dpi.S(MainLayout.UpdateButton);
                case IdSettings: return Dpi.S(MainLayout.SettingsButton);
                case IdCombo: return Dpi.S(MainLayout.VersionBox);
                case IdOpenDir: return Dpi.S(MainLayout.OpenDirText);
                case IdClose: return Dpi.S(MainLayout.CloseButton);
                default: return Rectangle.Empty;
            }
        }

        private void InvalidateButton(int id)
        {
            Rectangle rect = RectFor(id);
            if (rect.IsEmpty) return;
            int pad = Dpi.S(2);
            Invalidate(Rectangle.Inflate(rect, pad, pad));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hover = HitTest(e.Location);
            if (hover != _hover)
            {
                int previous = _hover;
                _hover = hover;
                Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
                InvalidateButton(previous);
                InvalidateButton(hover);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1)
            {
                int previous = _hover;
                _hover = -1;
                Cursor = Cursors.Default;
                InvalidateButton(previous);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int hit = HitTest(e.Location);
                if (hit == IdClose)
                {
                    Close();
                    return;
                }
                if (hit >= 0)
                {
                    _pressed = hit;
                    InvalidateButton(hit);
                    return;
                }
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN,
                    (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _pressed >= 0)
            {
                int pressed = _pressed;
                _pressed = -1;
                InvalidateButton(pressed);
                if (HitTest(e.Location) == pressed) Execute(pressed);
                return;
            }
            base.OnMouseUp(e);
        }

        private void Execute(int id)
        {
            switch (id)
            {
                case IdLaunch:
                    LaunchGame();
                    break;
                case IdUpdate:
                    using (var form = new UpdateForm(_config))
                        form.ShowDialog(this);
                    ScanVersionsAsync();
                    break;
                case IdSettings:
                    using (var form = new SettingsForm(_config))
                    {
                        if (form.ShowDialog(this) == DialogResult.OK)
                        {
                            _config = Config.Load();
                            Log.DebugEnabled = _config.DebugMode;
                            LoadBackground();
                            ScanVersionsAsync();
                        }
                    }
                    Invalidate();
                    break;
                case IdCombo:
                    OpenDropdown();
                    break;
                case IdOpenDir:
                    OpenGameDirectory();
                    break;
                case IdClose:
                    Close();
                    break;
            }
        }

        private void OpenDropdown()
        {
            if (_versions.Count == 0)
            {
                MessageBox.Show(this, "没有找到游戏版本，请把版本放入 .minecraft\\versions 或先在更新游戏中下载。",
                    "Minecraft启动器");
                return;
            }
            if (_dropdown != null && !_dropdown.IsDisposed) _dropdown.Close();
            var items = _versions.Select(v => v.Id).ToList();
            _dropdown = new DropdownForm(items, _versionIndex);
            _dropdown.ShowAt(this, Dpi.S(MainLayout.VersionBox));
            _dropdown.FormClosed += (sender, args) =>
            {
                if (_dropdown.SelectedIndex >= 0 && _dropdown.SelectedIndex < _versions.Count)
                {
                    _versionIndex = _dropdown.SelectedIndex;
                    _config.SelectedVersion = _versions[_versionIndex].Id;
                    _config.Save();
                    Invalidate();
                }
            };
        }

        private void OpenGameDirectory()
        {
            string path = _config.ResolvedGameRoot;
            if (_versionIndex >= 0 && _versionIndex < _versions.Count)
            {
                string versionDir = Path.Combine(_config.VersionsDir, _versions[_versionIndex].Id);
                if (Directory.Exists(versionDir)) path = versionDir;
            }
            try
            {
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                Log.Error("打开目录失败", ex);
                MessageBox.Show(this, "打开目录失败: " + ex.Message, "Minecraft启动器");
            }
        }

        private async void LaunchGame()
        {
            if (_java != null && _java.IsRunning)
            {
                MessageBox.Show(this, "游戏正在运行中。", "Minecraft启动器");
                return;
            }
            if (_versionIndex < 0 || _versionIndex >= _versions.Count)
            {
                MessageBox.Show(this,
                    _scanning ? "正在扫描游戏版本，请稍候再试。" : "没有选择游戏,请选择或点击更新按钮更新游戏",
                    "Minecraft启动器");
                return;
            }

            AuthSession auth = null;
            if (_config.UseMicrosoft)
            {
                auth = AuthStore.Load();
                if (auth == null)
                {
                    MessageBox.Show(this,
                        "已开启正版登录，但本机没有保存账号信息。\r\n请到设置里点击「正版登录测试」完成登录。",
                        "Minecraft启动器");
                    return;
                }

                if (auth.IsExpiring)
                {
                    UseWaitCursor = true;
                    _status = "正在验证正版账号…";
                    Invalidate();
                    try
                    {
                        auth = await MicrosoftAuth.RefreshAsync(auth, MicrosoftAuth.ClientIdFor(_config),
                            System.Threading.CancellationToken.None);
                        AuthStore.Save(auth);
                        _config.UserName = auth.Name;
                        Log.Info("正版令牌已刷新: " + auth.Name);
                    }
                    catch (AuthException ex)
                    {
                        UseWaitCursor = false;
                        _status = "";
                        Invalidate();
                        Log.Info("刷新正版登录失败: " + ex.Message);
                        MessageBox.Show(this, ex.Message + "\r\n\r\n请到设置中重新登录。", "Minecraft启动器");
                        return;
                    }
                    catch (Exception ex)
                    {
                        UseWaitCursor = false;
                        _status = "";
                        Invalidate();
                        Log.Error("刷新正版登录异常", ex);
                        MessageBox.Show(this, "验证正版账号失败: " + ex.Message, "Minecraft启动器");
                        return;
                    }
                    UseWaitCursor = false;
                    _status = "";
                    Invalidate();
                }
            }

            VersionEntry entry = _versions[_versionIndex];
            try
            {
                LaunchPlan plan = LaunchBuilder.Build(_config, entry,
                    auth != null ? auth.Name : _config.UserName, auth);
                if (plan.MissingFiles.Count > 0)
                {
                    string list = string.Join("\r\n", plan.MissingFiles.Take(8).Select(Path.GetFileName));
                    if (plan.MissingFiles.Count > 8) list += "\r\n……";
                    MessageBox.Show(this,
                        "缺少 " + plan.MissingFiles.Count + " 个游戏文件，无法启动：\r\n" + list +
                        "\r\n\r\n请先在「更新游戏」中下载完整版本。",
                        "Minecraft启动器");
                    return;
                }

                if (!File.Exists(plan.AssetsIndexPath))
                {
                    DialogResult result = MessageBox.Show(this,
                        "没有检测到此版本游戏的资源文件（包含语言文件和声音文件）\r\n" +
                        "请前往游戏更新按钮内点击更新声音文件按钮进行更新。\r\n\r\n仍然继续启动？",
                        "Minecraft启动器", MessageBoxButtons.OKCancel);
                    if (result != DialogResult.OK) return;
                }

                _status = "游戏运行中";
                Invalidate();

                var process = new JavaProcess();
                _java = process;
                process.OutputLine += line => Log.Debug(line);
                process.Exited += code =>
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _status = "游戏已退出（" + code + "）";
                            Invalidate();
                        }));
                    }
                    catch
                    {
                    }
                };
                process.Start(plan, _config.DebugMode);
                Log.Info("启动版本: " + entry.Id + "，Java: " + plan.JavaPath);
            }
            catch (LaunchException ex)
            {
                MessageBox.Show(this, ex.Message, "Minecraft启动器");
            }
            catch (Exception ex)
            {
                Log.Error("启动失败", ex);
                MessageBox.Show(this, "启动失败: " + ex.Message, "Minecraft启动器");
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_java != null && _java.IsRunning)
            {
                DialogResult result = MessageBox.Show(this,
                    "游戏正在运行，确定退出启动器吗？", "Minecraft启动器",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            _java?.Dispose();
            _backgroundCache?.Dispose();
            _backgroundCache = null;
            _background?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
