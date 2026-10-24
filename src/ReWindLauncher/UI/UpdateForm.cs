using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReWindLauncher.Core;
using ReWindLauncher.Net;
using Newtonsoft.Json.Linq;

namespace ReWindLauncher.UI
{
    public sealed class UpdateForm : Form
    {
        private readonly Config _config;
        private readonly Image _cloud;
        private SkinListView _list;
        private ComboBox _versionCombo;
        private RadioButton _simple;
        private RadioButton _official;
        private RadioButton _forge;
        private RadioButton _forgeOptiFine;
        private Label _status;
        private Button _updateButton;
        private Button _soundsButton;
        private List<RemoteVersion> _remoteVersions = new List<RemoteVersion>();
        private List<RemoteVersion> _allVersions;
        private List<string> _forgeMinecraftVersions = new List<string>();
        private List<ForgeBuild> _forgeBuilds = new List<ForgeBuild>();
        private CancellationTokenSource _cts;
        private bool _busy;
        private bool _suppressCombo;
        private int _reloadGeneration;

        public UpdateForm(Config config)
        {
            _config = config;
            _cloud = Theme.LoadEmbedded("cloud.jpg");

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Dpi.S(UpdateLayout.Width), Dpi.S(UpdateLayout.Height));
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(232, 240, 246);
            Font = Theme.BodyFont;
            Text = "更新游戏";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildControls();
            _official.Checked = true;
        }

        private bool IsForgeMode => _forge.Checked || _forgeOptiFine.Checked;

        private void BuildControls()
        {
            _list = new SkinListView
            {
                Location = Dpi.S(new Point(8, 36)),
                Size = new Size(Dpi.S(690), Dpi.S(320))
            };
            _list.ItemActivated += (sender, args) => UpdateGame(DownloadContent.Game);
            Controls.Add(_list);

            Panel sourceGroup = new Panel
            {
                Location = Dpi.S(new Point(10, 374)),
                Size = new Size(Dpi.S(330), Dpi.S(26)),
                BackColor = Color.Transparent
            };
            Controls.Add(sourceGroup);

            _simple = AddRadio(sourceGroup, "简单整合", 0, 0);
            _official = AddRadio(sourceGroup, "官方原版", 76, 0);
            _forge = AddRadio(sourceGroup, "Forge", 150, 0);
            _forgeOptiFine = AddRadio(sourceGroup, "Forge+OptiFine", 206, 0);

            _versionCombo = new ComboBox
            {
                Location = Dpi.S(new Point(344, 374)),
                Width = Dpi.S(140),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Font = Theme.BodyFont,
                BackColor = Color.White
            };
            _versionCombo.SelectedIndexChanged += VersionComboChanged;
            Controls.Add(_versionCombo);

            _soundsButton = AddButton("更新声音文件", 512, 371, 92, 28);
            _soundsButton.Click += (sender, args) => UpdateGame(DownloadContent.Sounds);
            _updateButton = AddButton("更新游戏", 612, 371, 86, 28);
            _updateButton.Click += (sender, args) => UpdateGame(DownloadContent.Game);

            _status = new Label
            {
                Location = Dpi.S(new Point(10, 405)),
                Size = new Size(Dpi.S(686), Dpi.S(20)),
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(70, 80, 90),
                Font = Theme.BodyFont,
                Text = ""
            };
            Controls.Add(_status);

            _simple.CheckedChanged += (sender, args) => { if (_simple.Checked) _ = ReloadSourceAsync(); };
            _official.CheckedChanged += (sender, args) => { if (_official.Checked) _ = ReloadSourceAsync(); };
            _forge.CheckedChanged += (sender, args) => { if (_forge.Checked) _ = ReloadSourceAsync(); };
            _forgeOptiFine.CheckedChanged += (sender, args) => { if (_forgeOptiFine.Checked) _ = ReloadSourceAsync(); };
        }

        private RadioButton AddRadio(Control parent, string text, int x, int y)
        {
            var radio = new RadioButton
            {
                Text = text,
                Location = Dpi.S(new Point(x, y)),
                AutoSize = true,
                BackColor = Color.Transparent,
                ForeColor = Color.Black,
                Font = Theme.BodyFont
            };
            parent.Controls.Add(radio);
            return radio;
        }

        private Button AddButton(string text, int x, int y, int width, int height)
        {
            var button = new Button
            {
                Text = text,
                Location = Dpi.S(new Point(x, y)),
                Size = new Size(Dpi.S(width), Dpi.S(height)),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(246, 246, 246),
                ForeColor = Color.Black,
                Font = Theme.BodyFont,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(172, 180, 186);
            button.FlatAppearance.BorderSize = 1;
            Controls.Add(button);
            return button;
        }

        private async Task ReloadSourceAsync()
        {
            if (_busy) return;
            int generation = ++_reloadGeneration;

            _list.SetItems(null);
            _remoteVersions.Clear();
            _forgeBuilds.Clear();
            _suppressCombo = true;
            _versionCombo.Items.Clear();
            _suppressCombo = false;

            if (IsForgeMode)
            {
                _status.Text = "正在获取 Forge 版本列表...";
                try
                {
                    List<string> minecraftVersions = await ForgeService
                        .GetMinecraftVersionsAsync(CancellationToken.None);
                    if (IsDisposed || generation != _reloadGeneration || !IsForgeMode) return;

                    _forgeMinecraftVersions = minecraftVersions;
                    _suppressCombo = true;
                    foreach (string id in minecraftVersions) _versionCombo.Items.Add(id);
                    if (_versionCombo.Items.Count > 0)
                    {
                        int index = minecraftVersions.IndexOf(PreferredMinecraftVersion());
                        _versionCombo.SelectedIndex = index >= 0 ? index : 0;
                    }
                    _suppressCombo = false;

                    if (_versionCombo.Items.Count == 0)
                    {
                        _status.Text = "没有可静默安装的 Forge 版本（需要 Minecraft 1.12.2 及以上）";
                        return;
                    }
                    await LoadForgeBuildsAsync(_versionCombo.SelectedItem as string, generation);
                }
                catch (Exception ex)
                {
                    _suppressCombo = false;
                    Log.Error("获取 Forge 列表失败", ex);
                    _status.Text = "获取 Forge 列表失败：" + ex.Message;
                }
                return;
            }

            if (_simple.Checked)
            {
                _status.Text = "本地整合包目录为空（packs\\*.json）";
                return;
            }

            _status.Text = "正在获取列表...";
            try
            {
                var versions = await MojangApi.GetVersionListAsync(false, CancellationToken.None);
                if (IsDisposed || generation != _reloadGeneration) return;
                _remoteVersions = versions;
                var items = versions.Select(version => new SkinListItem(
                    version.Id,
                    version.Type == "release" ? "正式版" : version.Type,
                    version.ReleaseTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    version)).ToList();
                _list.SetItems(items, items.Count > 0 ? 0 : -1);

                _suppressCombo = true;
                foreach (RemoteVersion version in versions) _versionCombo.Items.Add(version.Id);
                if (_versionCombo.Items.Count > 0) _versionCombo.SelectedIndex = 0;
                _suppressCombo = false;

                _status.Text = "共 " + versions.Count + " 个正式版，双击或点「更新游戏」开始下载";
                Log.Info("获取版本列表成功，共 " + versions.Count + " 个");
            }
            catch (Exception ex)
            {
                Log.Error("获取版本列表失败", ex);
                _status.Text = "获取列表失败：" + ex.Message;
            }
        }

        private string PreferredMinecraftVersion()
        {
            string selected = _config.SelectedVersion ?? "";
            int forgeIndex = selected.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase);
            if (forgeIndex > 0) selected = selected.Substring(0, forgeIndex);

            if (_forgeMinecraftVersions.Any(id =>
                    string.Equals(id, selected, StringComparison.OrdinalIgnoreCase)))
                return selected;

            return _forgeMinecraftVersions.FirstOrDefault() ?? "";
        }

        private async void VersionComboChanged(object sender, EventArgs e)
        {
            if (_suppressCombo || _busy) return;
            if (IsForgeMode)
            {
                int generation = ++_reloadGeneration;
                await LoadForgeBuildsAsync(_versionCombo.SelectedItem as string, generation);
                return;
            }
            SyncSelectionFromCombo();
        }

        private async Task LoadForgeBuildsAsync(string minecraft, int generation)
        {
            if (string.IsNullOrEmpty(minecraft)) return;
            _list.SetItems(null);
            _status.Text = "正在获取 Forge " + minecraft + " 版本列表...";
            try
            {
                List<ForgeBuild> builds = await ForgeService.GetBuildsAsync(minecraft, CancellationToken.None);
                if (IsDisposed || generation != _reloadGeneration || !IsForgeMode) return;

                _forgeBuilds = builds;
                var items = builds.Select(build => new SkinListItem(
                    build.Version,
                    "Forge",
                    build.Released > DateTime.MinValue
                        ? build.Released.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : build.Minecraft,
                    build)).ToList();
                _list.SetItems(items, items.Count > 0 ? 0 : -1);
                _status.Text = items.Count > 0
                    ? "Minecraft " + minecraft + " 共 " + items.Count + " 个 Forge 版本，双击或点「更新游戏」安装"
                    : "没有找到 Minecraft " + minecraft + " 的 Forge 安装包";
            }
            catch (Exception ex)
            {
                Log.Error("获取 Forge 构建失败: " + minecraft, ex);
                _status.Text = "获取 Forge 列表失败：" + ex.Message;
            }
        }

        private void SyncSelectionFromCombo()
        {
            if (_suppressCombo || _versionCombo.SelectedIndex < 0) return;
            string id = _versionCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(id)) return;
            int index = _remoteVersions.FindIndex(version => version.Id == id);
            if (index >= 0) _list.SelectIndex(index, true);
        }

        private RemoteVersion GetSelectedRemote()
        {
            if (_list.SelectedTag is RemoteVersion selected) return selected;

            string id = _versionCombo.SelectedItem as string;
            if (!string.IsNullOrEmpty(id))
                return _remoteVersions.FirstOrDefault(v => v.Id == id);
            return null;
        }

        private async Task<RemoteVersion> FindRemoteVersionAsync(string id)
        {
            if (_allVersions == null)
            {
                try
                {
                    _allVersions = await MojangApi.GetVersionListAsync(true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Error("获取完整版本列表失败", ex);
                    return null;
                }
            }
            return _allVersions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private async void UpdateGame(DownloadContent content)
        {
            if (_busy) return;

            if (IsForgeMode)
            {
                if (content == DownloadContent.Sounds)
                {
                    string minecraft = _versionCombo.SelectedItem as string;
                    if (string.IsNullOrEmpty(minecraft))
                    {
                        MessageBox.Show(this,
                            "请在更新声音资源按钮旁的下拉列表框内选择想要更新的游戏版本",
                            "Minecraft启动器");
                        return;
                    }
                    RemoteVersion baseVersion = await FindRemoteVersionAsync(minecraft);
                    if (baseVersion == null)
                    {
                        MessageBox.Show(this, "没有找到 Minecraft " + minecraft + " 的官方版本信息。",
                            "Minecraft启动器");
                        return;
                    }
                    await DownloadRemoteAsync(baseVersion, DownloadContent.Sounds);
                    return;
                }

                await InstallForgeAsync(_forgeOptiFine.Checked);
                return;
            }

            RemoteVersion version = GetSelectedRemote();
            if (version == null)
            {
                MessageBox.Show(this,
                    "请在更新声音资源按钮旁的下拉列表框内选择想要更新的游戏版本",
                    "Minecraft启动器");
                return;
            }

            await DownloadRemoteAsync(version, content);
        }

        private async Task DownloadRemoteAsync(RemoteVersion version, DownloadContent content)
        {
            _busy = true;
            SetBusy(true);
            _cts = new CancellationTokenSource();
            var progress = new Progress<DownloadProgress>(p => UpdateProgress(p, content));
            var downloader = new VersionDownloader(progress, SetStatus);

            try
            {
                await downloader.DownloadAsync(version, _config.ResolvedGameRoot, content, _cts.Token);
                _status.Text = (content == DownloadContent.Sounds ? "声音更新完成: " : "更新完成: ") + version.Id;
                MessageBox.Show(this,
                    "游戏 " + version.Id +
                    (content == DownloadContent.Sounds ? " 的声音文件更新完成。" : " 更新完成。"),
                    "Minecraft启动器");
            }
            catch (OperationCanceledException)
            {
                _status.Text = "已取消";
            }
            catch (Exception ex)
            {
                Log.Error("更新游戏失败: " + version.Id + " (" + content + ")", ex);
                _status.Text = "更新失败：" + ex.Message;
                MessageBox.Show(this, "更新失败：\r\n" + ex.Message, "Minecraft启动器");
            }
            finally
            {
                _busy = false;
                SetBusy(false);
                _soundsButton.Text = "更新声音文件";
                _updateButton.Text = "更新游戏";
                _cts?.Dispose();
                _cts = null;
            }
        }

        private async Task InstallForgeAsync(bool withOptiFine)
        {
            ForgeBuild build = _list.SelectedTag as ForgeBuild ?? _forgeBuilds.FirstOrDefault();
            if (build == null)
            {
                MessageBox.Show(this, "请先在列表中选择要安装的 Forge 版本。", "Minecraft启动器");
                return;
            }

            _busy = true;
            SetBusy(true);
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            try
            {
                string root = _config.ResolvedGameRoot;
                string baseJson = Path.Combine(root, "versions", build.Minecraft,
                    build.Minecraft + ".json");

                if (!File.Exists(baseJson))
                {
                    RemoteVersion baseVersion = await FindRemoteVersionAsync(build.Minecraft);
                    if (baseVersion == null)
                        throw new InvalidOperationException(
                            "没有找到 Minecraft " + build.Minecraft + " 的官方版本信息");

                    _status.Text = "正在下载基础版本 " + build.Minecraft + "...";
                    var progress = new Progress<DownloadProgress>(p => UpdateProgress(p, DownloadContent.Game));
                    var downloader = new VersionDownloader(progress, SetStatus);
                    await downloader.DownloadAsync(baseVersion, root, DownloadContent.Game, token);
                }

                string installer = await ForgeService.DownloadInstallerAsync(build,
                    Path.Combine(_config.BaseDir, "mctemp"), SetStatus, token);
                string javaExe = PickInstallerJava(build.Minecraft, root);

                _updateButton.Text = "安装中...";
                SetStatus("正在安装 Forge " + build.Version + "...");
                await ForgeService.InstallAsync(javaExe, installer, root, SetStatus, token);

                VersionEntry installed = ForgeService.FindInstalled(root, build.Minecraft, build.Version);
                if (installed == null)
                    throw new InvalidOperationException(
                        "Forge 安装器没有生成版本文件，请查看 logs\\launcher.log");

                _config.SelectedVersion = installed.Id;
                _config.Save();
                Log.Info("Forge 安装完成: " + installed.Id);

                string extra = "";
                if (withOptiFine) extra = PromptOptiFine(installed, root);

                _status.Text = "Forge 安装完成: " + installed.Id;
                MessageBox.Show(this,
                    "Forge " + build.Version + " 安装完成。\r\n版本：" + installed.Id + extra,
                    "Minecraft启动器");
            }
            catch (OperationCanceledException)
            {
                _status.Text = "已取消";
            }
            catch (Exception ex)
            {
                Log.Error("安装 Forge 失败", ex);
                _status.Text = "安装失败：" + ex.Message;
                MessageBox.Show(this, "安装 Forge 失败：\r\n" + ex.Message, "Minecraft启动器");
            }
            finally
            {
                _busy = false;
                SetBusy(false);
                _soundsButton.Text = "更新声音文件";
                _updateButton.Text = "更新游戏";
                _cts?.Dispose();
                _cts = null;
            }
        }

        private string PickInstallerJava(string minecraft, string root)
        {
            int required = 8;
            try
            {
                string jsonPath = Path.Combine(root, "versions", minecraft, minecraft + ".json");
                if (File.Exists(jsonPath))
                {
                    JObject version = JObject.Parse(File.ReadAllText(jsonPath));
                    required = (int?)version["javaVersion"]?["majorVersion"] ?? 8;
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取 Java 版本要求失败: " + minecraft, ex);
            }

            string javaw = JavaLocator.PickForMajor(required, out int actual);
            if (string.IsNullOrEmpty(javaw))
                throw new InvalidOperationException(
                    "没有找到 Java " + required + "，请先在设置中指定 javaw.exe 或安装对应版本的 Java");

            Log.Info("Forge 安装器使用 Java: " + javaw + "（需要 " + required + "，实际 " + actual + "）");
            string javaExe = Path.Combine(Path.GetDirectoryName(javaw) ?? "", "java.exe");
            return File.Exists(javaExe) ? javaExe : javaw;
        }

        private string PromptOptiFine(VersionEntry entry, string root)
        {
            string selected;
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择 OptiFine 安装包（可取消，稍后手动放入 mods）";
                dialog.Filter = "OptiFine (*.jar)|*.jar";
                if (dialog.ShowDialog(this) != DialogResult.OK) return "\r\n未选择 OptiFine。";
                selected = dialog.FileName;
            }

            try
            {
                string destination = ForgeService.CopyOptiFine(root, entry, _config.Isolate, selected);
                Log.Info("OptiFine 已复制: " + destination);
                return "\r\nOptiFine 已复制到：" + destination;
            }
            catch (Exception ex)
            {
                Log.Error("复制 OptiFine 失败", ex);
                return "\r\n复制 OptiFine 失败：" + ex.Message;
            }
        }

        private void SetStatus(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => { if (!IsDisposed) _status.Text = text; })); }
                catch { }
            }
            else
            {
                _status.Text = text;
            }
        }

        private void UpdateProgress(DownloadProgress progress, DownloadContent content)
        {
            int percent = progress.BytesTotal > 0
                ? (int)(progress.BytesDone * 100 / progress.BytesTotal)
                : (progress.FilesTotal > 0 ? progress.FilesDone * 100 / progress.FilesTotal : 0);
            percent = Math.Max(0, Math.Min(100, percent));

            string speed = FormatSpeed(progress.BytesPerSecond);
            string size = progress.BytesTotal > 0
                ? FormatSize(progress.BytesDone) + " / " + FormatSize(progress.BytesTotal)
                : "";
            _status.Text = (content == DownloadContent.Sounds ? "正在更新声音: " : "正在更新: ") +
                           progress.FilesDone + "/" + progress.FilesTotal +
                           "  " + percent + "%  " + size + "  " + speed;
            if (content == DownloadContent.Sounds)
                _soundsButton.Text = "更新中 " + percent + "%";
            else
                _updateButton.Text = "更新中 " + percent + "%";
        }

        private static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "";
            return FormatSize((long)bytesPerSecond) + "/s";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GB";
        }

        private void SetBusy(bool busy)
        {
            _simple.Enabled = !busy;
            _official.Enabled = !busy;
            _forge.Enabled = !busy;
            _forgeOptiFine.Enabled = !busy;
            _versionCombo.Enabled = !busy;
            _soundsButton.Enabled = !busy;
            _updateButton.Enabled = !busy;
            _list.Enabled = !busy;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var back = new SolidBrush(Color.FromArgb(247, 250, 252)))
                g.FillRectangle(back, ClientRectangle);

            Theme.DrawStretchImageAlpha(g, _cloud,
                new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 0.22f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int titleHeight = Dpi.S(UpdateLayout.TitleHeight);
            var bar = new Rectangle(0, 0, ClientSize.Width, titleHeight);
            using (var brush = new LinearGradientBrush(bar,
                       Color.FromArgb(228, 240, 250), Color.FromArgb(190, 214, 234), LinearGradientMode.Vertical))
                g.FillRectangle(brush, bar);
            using (var pen = new Pen(Color.FromArgb(150, 160, 190, 210), Math.Max(1f, Dpi.Scale)))
                g.DrawLine(pen, 0, titleHeight - 1, ClientSize.Width, titleHeight - 1);

            Theme.DrawCloseGlyph(g, Dpi.S(UpdateLayout.CloseButton), false);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (Dpi.S(UpdateLayout.CloseButton).Contains(e.Location))
                {
                    Close();
                    return;
                }
                if (e.Y < Dpi.S(UpdateLayout.TitleHeight))
                {
                    NativeMethods.ReleaseCapture();
                    NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN,
                        (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
                    return;
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                DialogResult result = MessageBox.Show(this,
                    "正在下载中，确定要关闭吗？", "更新游戏",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cts?.Cancel();
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _cloud?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
