using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualBasic;
using ReWindLauncher.Core;
using Newtonsoft.Json.Linq;

namespace ReWindLauncher.UI
{
    public sealed class SettingsForm : Form
    {
        private readonly Config _config;
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly Image _cloud;
        private List<VersionEntry> _versions = new List<VersionEntry>();

        private TextBox _nameBox;
        private TextBox _memoryBox;
        private TextBox _javaBox;
        private RadioButton _autoMemory;
        private RadioButton _manualMemory;
        private RadioButton _randomSkin;
        private RadioButton _defaultSkin;
        private RadioButton _isolateOn;
        private RadioButton _isolateOff;
        private CheckBox _useMicrosoft;
        private CheckBox _debug;
        private ComboBox _versionCombo;
        private Label _versionNote;
        private bool _loading;

        public SettingsForm(Config config)
        {
            _config = config;
            _cloud = Theme.LoadEmbedded("cloud.jpg");

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Dpi.S(SettingsLayout.Width), Dpi.S(SettingsLayout.Height));
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(225, 238, 246);
            Font = Theme.BodyFont;
            Text = "游戏设置窗口";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildControls();
            LoadValues();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var back = new LinearGradientBrush(ClientRectangle,
                       Color.FromArgb(165, 213, 231), Color.FromArgb(198, 227, 237),
                       LinearGradientMode.Vertical))
                g.FillRectangle(back, ClientRectangle);

            Theme.DrawStretchImageAlpha(g, _cloud,
                new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 0.38f);

            DrawGroupBackground(g, Dpi.S(SettingsLayout.LeftGroup));
            DrawGroupBackground(g, Dpi.S(SettingsLayout.RightGroup));
        }

        private static void DrawGroupBackground(Graphics g, Rectangle rect)
        {
            using (var fill = new SolidBrush(Color.FromArgb(76, 255, 255, 255)))
                g.FillRectangle(fill, rect);
            using (var pen = new Pen(Color.FromArgb(150, 182, 204, 216), Math.Max(1f, Dpi.Scale)))
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            TextRenderer.DrawText(g, "游戏设置", Theme.BodyBoldFont, Dpi.S(new Point(24, 34)), Color.Black);
            TextRenderer.DrawText(g, "游戏管理", Theme.BodyBoldFont, Dpi.S(new Point(423, 34)), Color.Black);
            Theme.DrawCloseGlyph(g, Dpi.S(SettingsLayout.CloseButton), false);
        }

        private void BuildControls()
        {
            AddLabel("游戏名字", 25, 64);
            _nameBox = AddTextBox(96, 60, 90);

            _useMicrosoft = AddCheck("正版登录", 198, 63);
            _toolTip.SetToolTip(_useMicrosoft, "勾选后使用微软账号登录，需要先完成一次登录");

            AddLabel("最大内存", 25, 96);
            Panel memoryGroup = AddRadioGroup(90, 92, 148, 24);
            _autoMemory = AddRadio(memoryGroup, "自动设置", 2, 0);
            _autoMemory.ForeColor = Theme.AccentBlue;
            _manualMemory = AddRadio(memoryGroup, "手动", 84, 0);
            _memoryBox = AddTextBox(243, 92, 42);
            AddLabel("MB", 292, 96);

            AddLabel("启动器皮肤", 25, 124);
            Panel skinGroup = AddRadioGroup(90, 120, 148, 24);
            _randomSkin = AddRadio(skinGroup, "随机皮肤", 2, 0);
            _defaultSkin = AddRadio(skinGroup, "默认皮肤", 84, 0);

            AddLabel("java路径", 25, 152);
            _javaBox = AddTextBox(84, 148, 236);
            var browse = AddButton("浏览", 328, 146, 50, 26);
            browse.Click += (sender, args) => BrowseJava();

            AddLabel("游戏列表", 423, 64);
            _versionCombo = new ComboBox
            {
                Location = Dpi.S(new Point(502, 60)),
                Width = Dpi.S(168),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Font = Theme.BodyFont,
                BackColor = Color.White
            };
            _versionCombo.SelectedIndexChanged += (sender, args) => RefreshVersionNote();
            Controls.Add(_versionCombo);

            Label dirLabel = AddLabel("游戏目录(Forge mods, 材质包, 存档的目录)", 423, 88);
            dirLabel.AutoSize = false;
            dirLabel.Size = new Size(Dpi.S(250), Dpi.S(18));
            dirLabel.Font = Theme.PixelFont("宋体", 11f);
            Panel isolateGroup = AddRadioGroup(422, 108, 252, 46);
            _isolateOn = AddRadio(isolateGroup, "保存到 .minecraft", 2, 0);
            _isolateOff = AddRadio(isolateGroup, "保存到 .minecraft\\versions\\版本名称\\", 2, 24);
            _isolateOn.Font = Theme.PixelFont("宋体", 11f);
            _isolateOff.Font = Theme.PixelFont("宋体", 11f);
            _versionNote = AddLabel("只在1.6以上的版本有效", 423, 162);
            _versionNote.ForeColor = Color.FromArgb(95, 95, 95);

            var rename = AddButton("重命名", 603, 158, 70, 26);
            rename.Click += (sender, args) => RenameVersion();

            _debug = AddCheck("调试模式", 18, 220);
            var findJava = AddButton("查找java", 90, 217, 88, 26);
            findJava.Click += (sender, args) => FindJava(findJava);
            var testLogin = AddButton("正版登录测试", 193, 217, 90, 26);
            testLogin.Click += (sender, args) => PromptLogin(false);
            var deleteAuth = AddButton("删除正版信息", 300, 217, 90, 26);
            deleteAuth.Click += (sender, args) => DeleteAuth();
            var deleteGame = AddButton("删除选中游戏", 463, 217, 105, 26);
            deleteGame.Click += (sender, args) => DeleteVersion();
            var save = AddButton("保存设置", 600, 217, 74, 26);
            save.Click += (sender, args) => SaveAndClose();

            _autoMemory.CheckedChanged += (sender, args) => UpdateMemoryEnabled();
            _manualMemory.CheckedChanged += (sender, args) => UpdateMemoryEnabled();
            _useMicrosoft.CheckedChanged += (sender, args) => OnMicrosoftChecked();
        }

        private Label AddLabel(string text, int x, int y)
        {
            var label = new Label
            {
                Text = text,
                Location = Dpi.S(new Point(x, y)),
                AutoSize = true,
                BackColor = Color.Transparent,
                ForeColor = Color.Black,
                Font = Theme.BodyFont
            };
            Controls.Add(label);
            return label;
        }

        private TextBox AddTextBox(int x, int y, int width)
        {
            var box = new TextBox
            {
                Location = Dpi.S(new Point(x, y)),
                Width = Dpi.S(width),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                ForeColor = Color.Black,
                Font = Theme.BodyFont
            };
            Controls.Add(box);
            return box;
        }

        private Panel AddRadioGroup(int x, int y, int width, int height)
        {
            var panel = new Panel
            {
                Location = Dpi.S(new Point(x, y)),
                Size = new Size(Dpi.S(width), Dpi.S(height)),
                BackColor = Color.Transparent
            };
            Controls.Add(panel);
            return panel;
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

        private CheckBox AddCheck(string text, int x, int y)
        {
            var check = new CheckBox
            {
                Text = text,
                Location = Dpi.S(new Point(x, y)),
                AutoSize = true,
                BackColor = Color.Transparent,
                ForeColor = Color.Black,
                Font = Theme.BodyFont
            };
            Controls.Add(check);
            return check;
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

        private void LoadValues()
        {
            _loading = true;
            _nameBox.Text = _config.UserName;
            _useMicrosoft.Checked = _config.UseMicrosoft;
            if (_config.UseMicrosoft)
            {
                AuthSession saved = AuthStore.Load();
                if (saved != null) _nameBox.Text = saved.Name;
            }
            _autoMemory.Checked = _config.MemoryAuto;
            _manualMemory.Checked = !_config.MemoryAuto;
            _memoryBox.Text = _config.MemoryMB.ToString();
            _randomSkin.Checked = _config.RandomSkin;
            _defaultSkin.Checked = !_config.RandomSkin;
            _isolateOn.Checked = !_config.Isolate;
            _isolateOff.Checked = _config.Isolate;
            _javaBox.Text = _config.JavaPath;
            _debug.Checked = _config.DebugMode;
            _loading = false;
            UpdateMemoryEnabled();
            ReloadVersions();
        }

        private void UpdateMemoryEnabled()
        {
            _memoryBox.Enabled = _manualMemory.Checked;
        }

        private void ReloadVersions()
        {
            _versions = VersionStore.Scan(_config.ResolvedGameRoot);
            _versionCombo.Items.Clear();
            foreach (VersionEntry entry in _versions) _versionCombo.Items.Add(entry.Id);

            int index = _versions.FindIndex(v => v.Id == _config.SelectedVersion);
            if (index < 0 && _versions.Count > 0) index = 0;
            if (_versions.Count > 0) _versionCombo.SelectedIndex = index;
            RefreshVersionNote();
        }

        private void RefreshVersionNote()
        {
            _versionNote.Text = "只在1.6以上的版本有效";
        }

        private void BrowseJava()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择java路径";
                dialog.Filter = "Java 可执行文件|javaw.exe;java.exe|所有文件|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string path = dialog.FileName;
                if (Path.GetFileName(path).Equals("java.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string javaw = Path.Combine(Path.GetDirectoryName(path) ?? "", "javaw.exe");
                    if (File.Exists(javaw)) path = javaw;
                }
                _javaBox.Text = path;
            }
        }

        private async void FindJava(Button button)
        {
            button.Enabled = false;
            string path;
            try
            {
                path = await Task.Run(() => JavaLocator.Find(""));
            }
            finally
            {
                if (!button.IsDisposed) button.Enabled = true;
            }
            if (IsDisposed) return;

            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show(this, "没有找到java路径,请手动指定javaw.exe", "Minecraft启动器");
                return;
            }
            _javaBox.Text = path;
            MessageBox.Show(this, "找到java路径,已经自动填入", "Minecraft启动器");
        }

        private void OnMicrosoftChecked()
        {
            if (_loading) return;

            if (!_useMicrosoft.Checked)
            {
                // 没有保存按钮，改动即刻落盘，否则关窗口会回退
                _config.UseMicrosoft = false;
                _config.Save();
                return;
            }

            AuthSession session = AuthStore.Load();
            if (session != null)
            {
                _config.UseMicrosoft = true;
                _config.UserName = session.Name;
                _config.Save();
                _loading = true;
                _nameBox.Text = session.Name;
                _loading = false;
                return;
            }

            PromptLogin(true);
        }

        private void PromptLogin(bool uncheckOnCancel)
        {
            using (var form = new LoginForm(_config))
            {
                if (form.ShowDialog(this) == DialogResult.OK && form.Session != null)
                {
                    _loading = true;
                    _nameBox.Text = form.Session.Name;
                    _useMicrosoft.Checked = true;
                    _loading = false;

                    _config.UserName = form.Session.Name;
                    _config.UseMicrosoft = true;
                    _config.Save();

                    MessageBox.Show(this, "正版登录成功，当前账号：" + form.Session.Name,
                        "Minecraft启动器");
                    return;
                }
            }

            if (uncheckOnCancel)
            {
                _loading = true;
                _useMicrosoft.Checked = false;
                _loading = false;
            }
        }

        private void DeleteAuth()
        {
            AuthSession session = AuthStore.Load();
            if (session == null)
            {
                MessageBox.Show(this, "本机没有保存正版登录信息。", "Minecraft启动器");
                return;
            }

            if (MessageBox.Show(this,
                    "确认删除本机保存的正版登录信息吗？\r\n账号：" + session.Name,
                    "Minecraft启动器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            AuthStore.Delete();
            _loading = true;
            _useMicrosoft.Checked = false;
            _loading = false;
            _config.UseMicrosoft = false;
            _config.Save();
            MessageBox.Show(this, "已删除正版登录信息。", "Minecraft启动器");
        }

        private void RenameVersion()
        {
            if (_versionCombo.SelectedIndex < 0 || _versionCombo.SelectedIndex >= _versions.Count)
            {
                MessageBox.Show(this, "没有选择游戏,请选择或点击更新按钮更新游戏", "Minecraft启动器");
                return;
            }

            VersionEntry entry = _versions[_versionCombo.SelectedIndex];
            string oldId = entry.Id;
            string oldDir = Path.Combine(_config.VersionsDir, oldId);
            string input = Interaction.InputBox("输入版本名称", "输入版本名称", oldId);
            string newId = (input ?? "").Trim();
            if (newId.Length == 0 || newId == oldId) return;

            if (newId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newId.Contains("\\") || newId.Contains("/"))
            {
                MessageBox.Show(this, "版本名称包含非法字符", "Minecraft启动器");
                return;
            }

            string newDir = Path.Combine(_config.VersionsDir, newId);
            if (Directory.Exists(newDir))
            {
                MessageBox.Show(this, "已存在同名版本", "Minecraft启动器");
                return;
            }

            try
            {
                Directory.Move(oldDir, newDir);

                string oldJson = Path.Combine(newDir, oldId + ".json");
                string newJson = Path.Combine(newDir, newId + ".json");
                if (File.Exists(oldJson)) File.Move(oldJson, newJson);

                string oldJar = Path.Combine(newDir, oldId + ".jar");
                string newJar = Path.Combine(newDir, newId + ".jar");
                if (File.Exists(oldJar)) File.Move(oldJar, newJar);

                if (File.Exists(newJson))
                {
                    JObject json = JObject.Parse(File.ReadAllText(newJson));
                    json["id"] = newId;
                    File.WriteAllText(newJson, json.ToString(Newtonsoft.Json.Formatting.Indented));
                }

                if (_config.SelectedVersion == oldId)
                {
                    _config.SelectedVersion = newId;
                    _config.Save();
                }
                ReloadVersions();
            }
            catch (Exception ex)
            {
                Log.Error("重命名版本失败", ex);
                MessageBox.Show(this, "重命名失败: " + ex.Message, "Minecraft启动器");
            }
        }

        private void DeleteVersion()
        {
            if (_versionCombo.SelectedIndex < 0 || _versionCombo.SelectedIndex >= _versions.Count)
            {
                MessageBox.Show(this, "没有选择游戏,请选择或点击更新按钮更新游戏", "Minecraft启动器");
                return;
            }
            VersionEntry entry = _versions[_versionCombo.SelectedIndex];
            string dir = Path.Combine(_config.VersionsDir, entry.Id);
            DialogResult result = MessageBox.Show(this,
                "你确认要删除此版本游戏吗？\r\n将会删除 .minecraft\\versions\\" + entry.Id,
                "Minecraft启动器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;

            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                if (_config.SelectedVersion == entry.Id)
                {
                    _config.SelectedVersion = "";
                    _config.Save();
                }
                ReloadVersions();
            }
            catch (Exception ex)
            {
                Log.Error("删除版本失败", ex);
                MessageBox.Show(this, "删除失败: " + ex.Message, "Minecraft启动器");
            }
        }

        private void SaveAndClose()
        {
            string name = _nameBox.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, "没有填写游戏名字", "Minecraft启动器");
                return;
            }

            int memory = _config.MemoryMB;
            if (_manualMemory.Checked && !int.TryParse(_memoryBox.Text.Trim(), out memory))
            {
                MessageBox.Show(this, "最大内存必须是数字", "Minecraft启动器");
                return;
            }
            if (memory < 256) memory = 256;

            if (_useMicrosoft.Checked)
            {
                AuthSession session = AuthStore.Load();
                if (session == null)
                {
                    MessageBox.Show(this, "还没有正版登录信息，请先点击「正版登录测试」完成登录。",
                        "Minecraft启动器");
                    return;
                }
                name = session.Name;
                _loading = true;
                _nameBox.Text = name;
                _loading = false;
            }

            _config.UserName = name;
            _config.UseMicrosoft = _useMicrosoft.Checked;
            _config.MemoryAuto = _autoMemory.Checked;
            _config.MemoryMB = memory;
            _config.JavaPath = _javaBox.Text.Trim();
            _config.RandomSkin = _randomSkin.Checked;
            _config.Isolate = _isolateOff.Checked;
            _config.DebugMode = _debug.Checked;
            _config.SelectedVersion = _versionCombo.SelectedIndex >= 0 &&
                                      _versionCombo.SelectedIndex < _versions.Count
                ? _versions[_versionCombo.SelectedIndex].Id
                : "";
            _config.Save();
            Log.DebugEnabled = _config.DebugMode;

            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (Dpi.S(SettingsLayout.CloseButton).Contains(e.Location))
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                    return;
                }
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN,
                    (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
            }
            base.OnMouseDown(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _cloud?.Dispose();
            _toolTip.Dispose();
            base.OnFormClosed(e);
        }
    }
}
