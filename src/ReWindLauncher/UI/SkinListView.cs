using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ReWindLauncher.UI
{
    public sealed class SkinListItem
    {
        public string Version;
        public string Name;
        public string Summary;
        public object Tag;

        public SkinListItem(string version, string name, string summary, object tag = null)
        {
            Version = version;
            Name = name;
            Summary = summary;
            Tag = tag;
        }
    }

    /// <summary>自绘列表，不画背景，透出父窗口的云纹。</summary>
    public sealed class SkinListView : Control
    {
        private const int HeaderHeightUnits = 26;
        private const int RowHeightUnits = 22;
        private const int ScrollWidthUnits = 12;
        private const int PaddingUnits = 6;
        private const int VersionColumnUnits = 90;
        private const int NameColumnUnits = 190;

        private readonly List<SkinListItem> _items = new List<SkinListItem>();
        private int _selectedIndex = -1;
        private int _hoverIndex = -1;
        private int _scroll;
        private bool _draggingThumb;
        private int _dragStartY;
        private int _dragStartScroll;

        public event EventHandler ItemActivated;

        public SkinListView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor |
                     ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
            BackColor = Color.Transparent;
            Font = Theme.BodyFont;
            TabStop = true;
        }

        public int SelectedIndex
        {
            get => _selectedIndex;
            set => SelectIndex(value, true);
        }

        public object SelectedTag =>
            _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex].Tag : null;

        public int Count => _items.Count;

        public void SetItems(IEnumerable<SkinListItem> items, int selectIndex = -1)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);
            _scroll = 0;
            _selectedIndex = items != null && selectIndex >= 0 && selectIndex < _items.Count ? selectIndex : -1;
            EnsureVisible();
            Invalidate();
        }

        public void SelectIndex(int index, bool ensureVisible)
        {
            if (index < -1 || index >= _items.Count) index = -1;
            if (_selectedIndex == index) return;
            _selectedIndex = index;
            if (ensureVisible) EnsureVisible();
            Invalidate();
        }

        private int HeaderHeight => Dpi.S(HeaderHeightUnits);
        private int RowHeight => Dpi.S(RowHeightUnits);
        private int ScrollBarWidth => Dpi.S(ScrollWidthUnits);
        private int BodyHeight => Math.Max(0, Height - HeaderHeight);
        private int ContentHeight => _items.Count * RowHeight;
        private bool NeedsScrollBar => ContentHeight > BodyHeight;
        private int MaxScroll => Math.Max(0, ContentHeight - BodyHeight);

        private Rectangle RowRect(int index) =>
            new Rectangle(0, HeaderHeight + index * RowHeight - _scroll,
                Width - (NeedsScrollBar ? ScrollBarWidth : 0), RowHeight);

        private Rectangle ScrollTrack =>
            new Rectangle(Width - ScrollBarWidth, HeaderHeight, ScrollBarWidth, BodyHeight);

        private Rectangle ScrollThumb
        {
            get
            {
                if (!NeedsScrollBar || ContentHeight <= 0) return Rectangle.Empty;
                int trackHeight = BodyHeight;
                int thumbHeight = Math.Max(Dpi.S(26), (int)((long)trackHeight * trackHeight / ContentHeight));
                thumbHeight = Math.Min(trackHeight, thumbHeight);
                int span = Math.Max(1, trackHeight - thumbHeight);
                int top = HeaderHeight + (int)((long)span * _scroll / Math.Max(1, MaxScroll));
                return new Rectangle(Width - ScrollBarWidth + Dpi.S(2), top,
                    ScrollBarWidth - Dpi.S(4), thumbHeight);
            }
        }

        private void ClampScroll()
        {
            if (_scroll < 0) _scroll = 0;
            if (_scroll > MaxScroll) _scroll = MaxScroll;
        }

        private void EnsureVisible()
        {
            if (_selectedIndex < 0) return;
            int top = _selectedIndex * RowHeight;
            int bottom = top + RowHeight;
            if (top < _scroll) _scroll = top;
            else if (bottom > _scroll + BodyHeight) _scroll = bottom - BodyHeight;
            ClampScroll();
        }

        private int IndexAt(int y)
        {
            if (y < HeaderHeight) return -1;
            int bodyY = y - HeaderHeight + _scroll;
            int index = bodyY / Math.Max(1, RowHeight);
            return index >= 0 && index < _items.Count ? index : -1;
        }

        private int[] ColumnStarts()
        {
            int pad = Dpi.S(PaddingUnits);
            return new[] { pad, pad + Dpi.S(VersionColumnUnits), pad + Dpi.S(VersionColumnUnits + NameColumnUnits) };
        }

        private Rectangle ColumnRect(int column, Rectangle row)
        {
            int[] starts = ColumnStarts();
            int pad = Dpi.S(PaddingUnits);
            int left = starts[column];
            int right = column == 2 ? row.Right - pad : starts[column + 1] - pad / 2;
            return new Rectangle(left, row.Y, Math.Max(0, right - left), row.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            ClampScroll();

            using (var fill = new SolidBrush(Color.FromArgb(218, 255, 255, 255)))
                g.FillRectangle(fill, ClientRectangle);

            var header = new Rectangle(0, 0, Width, HeaderHeight);
            using (var headerFill = new SolidBrush(Color.FromArgb(238, 246, 248, 250)))
                g.FillRectangle(headerFill, header);

            float borderWidth = Math.Max(1f, Dpi.Scale);
            using (var line = new Pen(Color.FromArgb(165, 176, 192, 202), borderWidth))
            {
                g.DrawLine(line, 0, HeaderHeight - 1, Width, HeaderHeight - 1);
                g.DrawRectangle(line, 0, 0, Width - 1, Height - 1);
                int[] starts = ColumnStarts();
                int pad = Dpi.S(PaddingUnits);
                foreach (int x in new[] { starts[1], starts[2] })
                    g.DrawLine(line, x - pad / 2, Dpi.S(4), x - pad / 2, HeaderHeight - Dpi.S(4));
            }

            string[] headers = { "版本", "名称", "简介" };
            for (int i = 0; i < 3; i++)
            {
                TextRenderer.DrawText(g, headers[i], Font, ColumnRect(i, header),
                    Color.FromArgb(55, 65, 75),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }

            if (_items.Count > 0)
            {
                int bodyTop = HeaderHeight;
                int bodyWidth = Width - (NeedsScrollBar ? ScrollBarWidth : 0);
                g.SetClip(new Rectangle(0, bodyTop, bodyWidth, BodyHeight));
                int first = _scroll / RowHeight;
                int y = bodyTop - (_scroll % RowHeight);

                for (int i = first; i < _items.Count; i++)
                {
                    if (y >= Height) break;
                    var row = new Rectangle(0, y, bodyWidth, RowHeight);
                    SkinListItem item = _items[i];

                    if (i == _selectedIndex)
                    {
                        using (var selected = new SolidBrush(Color.FromArgb(225, 40, 120, 210)))
                            g.FillRectangle(selected, row);
                    }
                    else if (i == _hoverIndex)
                    {
                        using (var hover = new SolidBrush(Color.FromArgb(34, 40, 120, 210)))
                            g.FillRectangle(hover, row);
                    }

                    Color textColor = i == _selectedIndex ? Color.White : Color.FromArgb(32, 32, 32);
                    TextRenderer.DrawText(g, item.Version, Font, ColumnRect(0, row), textColor,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, item.Name, Font, ColumnRect(1, row), textColor,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, item.Summary, Font, ColumnRect(2, row), textColor,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

                    y += RowHeight;
                }
                g.ResetClip();
            }

            if (NeedsScrollBar) DrawScrollBar(g);
            if (Focused)
            {
                using (var focus = new Pen(Color.FromArgb(130, 40, 120, 210), borderWidth))
                    g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
            }
        }

        private void DrawScrollBar(Graphics g)
        {
            Rectangle track = ScrollTrack;
            using (var brush = new SolidBrush(Color.FromArgb(22, 0, 0, 0)))
                g.FillRectangle(brush, track);

            Rectangle thumb = ScrollThumb;
            if (thumb.IsEmpty) return;
            using (var brush = new SolidBrush(Color.FromArgb(120, 128, 138, 148)))
                g.FillRectangle(brush, thumb);
            using (var pen = new Pen(Color.FromArgb(80, 255, 255, 255), Math.Max(1f, Dpi.Scale)))
                g.DrawRectangle(pen, thumb.X, thumb.Y, thumb.Width - 1, thumb.Height - 1);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button != MouseButtons.Left) return;

            if (NeedsScrollBar && ScrollTrack.Contains(e.Location))
            {
                Rectangle thumb = ScrollThumb;
                if (thumb.Contains(e.Location))
                {
                    _draggingThumb = true;
                    _dragStartY = e.Y;
                    _dragStartScroll = _scroll;
                    Capture = true;
                }
                else
                {
                    int page = Math.Max(RowHeight, BodyHeight - RowHeight);
                    ScrollBy(e.Y < thumb.Top ? -page : page);
                }
                return;
            }

            int index = IndexAt(e.Y);
            if (index >= 0) SelectIndex(index, false);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_draggingThumb)
            {
                int trackHeight = BodyHeight;
                Rectangle thumb = ScrollThumb;
                int span = Math.Max(1, trackHeight - thumb.Height);
                int delta = e.Y - _dragStartY;
                _scroll = _dragStartScroll + (int)((long)delta * MaxScroll / span);
                ClampScroll();
                Invalidate();
                return;
            }

            int index = IndexAt(e.Y);
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Invalidate();
            }
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_draggingThumb)
            {
                _draggingThumb = false;
                Capture = false;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                Invalidate();
            }
            Cursor = Cursors.Default;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int index = IndexAt(e.Y);
            if (index >= 0)
            {
                SelectIndex(index, false);
                ItemActivated?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = Math.Max(1, BodyHeight / Math.Max(1, RowHeight));
            int direction = e.Delta > 0 ? -1 : 1;
            ScrollBy(direction * lines * RowHeight);
        }

        private void ScrollBy(int delta)
        {
            if (!NeedsScrollBar) return;
            _scroll += delta;
            ClampScroll();
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Enter:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_items.Count == 0) return;
            int page = Math.Max(1, BodyHeight / Math.Max(1, RowHeight) - 1);
            switch (e.KeyCode)
            {
                case Keys.Up:
                    SelectIndex(Math.Max(0, _selectedIndex - 1), true);
                    e.Handled = true;
                    break;
                case Keys.Down:
                    SelectIndex(Math.Min(_items.Count - 1, _selectedIndex + 1), true);
                    e.Handled = true;
                    break;
                case Keys.PageUp:
                    SelectIndex(Math.Max(0, _selectedIndex - page), true);
                    e.Handled = true;
                    break;
                case Keys.PageDown:
                    SelectIndex(Math.Min(_items.Count - 1, _selectedIndex + page), true);
                    e.Handled = true;
                    break;
                case Keys.Enter:
                    ItemActivated?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }
    }
}
