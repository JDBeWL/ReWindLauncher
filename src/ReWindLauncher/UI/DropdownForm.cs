using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ReWindLauncher.UI
{
    internal sealed class DropdownForm : Form
    {
        private readonly ListBox _list;

        public int SelectedIndex { get; private set; } = -1;

        public DropdownForm(IList<string> items, int selectedIndex)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.White;
            DoubleBuffered = true;
            Width = Dpi.S(181);
            Height = Dpi.S(Math.Min(242, Math.Max(26, items.Count * 22 + 4)));

            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = Dpi.S(22),
                IntegralHeight = false,
                Font = Theme.ComboFont,
                BackColor = Color.White,
                ForeColor = Theme.Ink
            };
            foreach (string item in items) _list.Items.Add(item);
            if (selectedIndex >= 0 && selectedIndex < _list.Items.Count)
                _list.SelectedIndex = selectedIndex;

            _list.DrawItem += (sender, args) =>
            {
                if (args.Index < 0) return;
                bool selected = (args.State & DrawItemState.Selected) == DrawItemState.Selected;
                using (var back = new SolidBrush(selected ? Color.FromArgb(230, 240, 250) : Color.White))
                    args.Graphics.FillRectangle(back, args.Bounds);
                string text = _list.Items[args.Index].ToString();
                var rect = new Rectangle(args.Bounds.X + Dpi.S(6), args.Bounds.Y,
                    args.Bounds.Width - Dpi.S(10), args.Bounds.Height);
                TextRenderer.DrawText(args.Graphics, text, _list.Font, rect, Theme.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.EndEllipsis);
            };

            _list.Click += (sender, args) =>
            {
                if (_list.SelectedIndex >= 0)
                {
                    SelectedIndex = _list.SelectedIndex;
                    Close();
                }
            };
            _list.KeyDown += (sender, args) =>
            {
                if (args.KeyCode == Keys.Enter && _list.SelectedIndex >= 0)
                {
                    SelectedIndex = _list.SelectedIndex;
                    Close();
                }
                else if (args.KeyCode == Keys.Escape)
                {
                    Close();
                }
            };

            Controls.Add(_list);
            Deactivate += (sender, args) => Close();
        }

        public void ShowAt(Form owner, Rectangle anchor)
        {
            Point location = owner.PointToScreen(new Point(anchor.Left, anchor.Bottom));
            Location = location;
            Show(owner);
            _list.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Color.FromArgb(150, 160, 170), Math.Max(1f, Dpi.Scale)))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
