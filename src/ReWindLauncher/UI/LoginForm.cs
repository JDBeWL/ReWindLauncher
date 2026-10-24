using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using ReWindLauncher.Core;

namespace ReWindLauncher.UI
{
    /// <summary>默认走浏览器回环，本地端口被占用时改用设备代码。</summary>
    public sealed class LoginForm : Form
    {
        private const int WidthUnits = 480;
        private const int HeightUnits = 292;
        private static readonly Rectangle ContentRect = new Rectangle(18, 58, 444, 160);
        private static readonly Rectangle CloseButton = new Rectangle(452, 2, 22, 22);

        private readonly string _clientId;
        private readonly Image _cloud;
        private readonly Font _codeFont;

        private Label _message;
        private Label _status;
        private Label _code;
        private Button _browserButton;
        private Button _deviceButton;
        private Button _copyButton;
        private Button _cancelButton;

        private CancellationTokenSource _cts;
        private bool _busy;
        private bool _closing;

        public AuthSession Session { get; private set; }

        public LoginForm(Config config)
        {
            _clientId = MicrosoftAuth.ClientIdFor(config);
            _cloud = Theme.LoadEmbedded("cloud.jpg");
            _codeFont = Theme.PixelFont("微软雅黑", 20f, FontStyle.Bold);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Dpi.S(WidthUnits), Dpi.S(HeightUnits));
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(225, 238, 246);
            Font = Theme.BodyFont;
            Text = "微软账号登录";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildControls();
            Shown += (sender, args) => BeginBrowserLogin();
        }

        private void BuildControls()
        {
            _message = new Label
            {
                Text = "登录后将以正版身份进入游戏，服务器会校验账号、皮肤与披风。",
                Location = Dpi.S(new Point(34, 70)),
                Size = new Size(Dpi.S(412), Dpi.S(36)),
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(60, 60, 60),
                Font = Theme.BodyFont
            };
            Controls.Add(_message);

            _status = new Label
            {
                Text = "正在打开浏览器…",
                Location = Dpi.S(new Point(34, 108)),
                Size = new Size(Dpi.S(412), Dpi.S(62)),
                BackColor = Color.Transparent,
                ForeColor = Theme.AccentBlue,
                Font = Theme.BodyFont
            };
            Controls.Add(_status);

            _code = new Label
            {
                Text = "",
                Location = Dpi.S(new Point(34, 176)),
                Size = new Size(Dpi.S(210), Dpi.S(34)),
                BackColor = Color.Transparent,
                ForeColor = Theme.AccentBlue,
                Font = _codeFont
            };
            Controls.Add(_code);

            _copyButton = AddButton("复制代码", 250, 178, 70, 26);
            _copyButton.Visible = false;
            _copyButton.Click += (sender, args) =>
            {
                if (TryCopy(_code.Text)) SetStatus("代码已复制到剪贴板。", false);
            };

            _browserButton = AddButton("浏览器登录", 24, 224, 108, 30);
            _browserButton.Click += (sender, args) => BeginBrowserLogin();

            _deviceButton = AddButton("设备代码登录", 142, 224, 120, 30);
            _deviceButton.Click += (sender, args) => BeginDeviceLogin(false);

            _cancelButton = AddButton("取消", 376, 224, 80, 30);
            _cancelButton.Click += (sender, args) =>
            {
                if (_busy)
                {
                    SetStatus("正在取消…", false);
                    _cts?.Cancel();
                }
                else
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
            };
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

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var back = new LinearGradientBrush(ClientRectangle,
                       Color.FromArgb(165, 213, 231), Color.FromArgb(198, 227, 237),
                       LinearGradientMode.Vertical))
                g.FillRectangle(back, ClientRectangle);

            Theme.DrawStretchImageAlpha(g, _cloud,
                new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 0.38f);

            Rectangle rect = Dpi.S(ContentRect);
            using (var fill = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
                g.FillRectangle(fill, rect);
            using (var pen = new Pen(Color.FromArgb(150, 182, 204, 216), Math.Max(1f, Dpi.Scale)))
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            TextRenderer.DrawText(g, "微软账号登录", Theme.BodyBoldFont, Dpi.S(new Point(24, 32)), Color.Black);
            Theme.DrawCloseGlyph(g, Dpi.S(CloseButton), false);
        }

        private async void BeginBrowserLogin()
        {
            if (_busy || _closing) return;

            if (!MicrosoftAuth.IsCallbackPortAvailable())
            {
                Log.Info("本地回调端口被占用，改用设备代码登录");
                BeginDeviceLogin(true);
                return;
            }

            _busy = true;
            _cts = new CancellationTokenSource();
            UpdateButtons();
            ShowCode("");

            string url = MicrosoftAuth.BuildAuthorizeUrl(_clientId, out string verifier, out string state);
            try
            {
                AuthSession session = await MicrosoftAuth.LoginWithBrowserAsync(_clientId, url, verifier, state,
                    text => SetStatus(text, false), _cts.Token);
                Finish(session);
            }
            catch (AuthException ex) when (ex.PortBusy)
            {
                EndBusy();
                if (_closing) return;
                BeginDeviceLogin(true);
            }
            catch (AuthException ex)
            {
                HandleFailure(ex);
            }
            catch (OperationCanceledException)
            {
                HandleCanceled();
            }
            catch (Exception ex)
            {
                EndBusy();
                if (_closing) return;
                Log.Error("微软登录异常", ex);
                SetStatus("登录失败: " + ex.Message, true);
            }
        }

        private async void BeginDeviceLogin(bool portFallback)
        {
            if (_busy || _closing) return;

            _busy = true;
            _cts = new CancellationTokenSource();
            UpdateButtons();
            ShowCode("");
            SetStatus(portFallback ? "端口被占用，改用设备代码登录…" : "正在获取设备代码…", false);

            try
            {
                DeviceCodeInfo info = await MicrosoftAuth.StartDeviceCodeAsync(_clientId, _cts.Token);
                if (_closing) return;

                ShowCode(info.UserCode);
                bool copied = TryCopy(info.UserCode);
                SetStatus("代码 " + info.UserCode + (copied ? " 已复制。" : "。") +
                          "请在 " + info.VerificationUri + " 输入代码并完成登录。", false);
                OpenVerificationPage(info.VerificationUri);

                AuthSession session = await MicrosoftAuth.LoginWithDeviceCodeAsync(_clientId, info,
                    text => SetStatus(text, false), _cts.Token);
                Finish(session);
            }
            catch (AuthException ex)
            {
                HandleFailure(ex);
            }
            catch (OperationCanceledException)
            {
                HandleCanceled();
            }
            catch (Exception ex)
            {
                EndBusy();
                if (_closing) return;
                Log.Error("设备代码登录异常", ex);
                SetStatus("登录失败: " + ex.Message, true);
            }
        }

        private void Finish(AuthSession session)
        {
            EndBusy();
            if (_closing) return;

            Session = session;
            if (!AuthStore.Save(session)) Log.Error("正版登录信息保存失败");
            Log.Info("正版登录成功: " + session.Name + " / " + session.Uuid);
            DialogResult = DialogResult.OK;
            Close();
        }

        private void HandleFailure(AuthException ex)
        {
            EndBusy();
            if (_closing) return;
            if (ex.Canceled)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            Log.Info("微软登录失败: " + ex.Message);
            SetStatus(ex.Message, true);
        }

        private void HandleCanceled()
        {
            EndBusy();
            if (_closing) return;
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void EndBusy()
        {
            _busy = false;
            if (_cts != null)
            {
                _cts.Dispose();
                _cts = null;
            }
            if (!_closing && !IsDisposed) UpdateButtons();
        }

        private void UpdateButtons()
        {
            _browserButton.Enabled = !_busy;
            _deviceButton.Enabled = !_busy;
            _cancelButton.Text = _busy ? "取消" : "关闭";
        }

        private void SetStatus(string text, bool error)
        {
            if (_closing || IsDisposed) return;
            _status.ForeColor = error ? Color.FromArgb(178, 34, 34) : Theme.AccentBlue;
            _status.Text = text;
        }

        private void ShowCode(string code)
        {
            if (_closing || IsDisposed) return;
            _code.Text = code;
            _copyButton.Visible = code.Length > 0;
        }

        private static bool TryCopy(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void OpenVerificationPage(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (Dpi.S(CloseButton).Contains(e.Location))
                {
                    Close();
                    return;
                }
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN,
                    (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
            }
            base.OnMouseDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;
            try { _cts?.Cancel(); } catch { }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _cloud?.Dispose();
            _codeFont?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
