using System.Drawing.Drawing2D;

namespace CodexUsageWidget;

static class Theme
{
    public static readonly Color Background = Color.FromArgb(10, 20, 30);
    public static readonly Color Text = Color.FromArgb(239, 240, 245);
    public static readonly Color Muted = Color.FromArgb(143, 173, 194);
    public static readonly Color Mint = Color.FromArgb(29, 210, 230);
    public static readonly Color Lilac = Color.FromArgb(69, 188, 241);
    public static Color AvatarColor(string id)
    {
        Color[] colors = { Color.FromArgb(107, 81, 235), Color.FromArgb(14, 158, 103), Color.FromArgb(32, 116, 232), Color.FromArgb(199, 83, 152), Color.FromArgb(24, 156, 174) };
        uint hash = 2166136261; foreach (char c in id) hash = unchecked((hash ^ c) * 16777619);
        return colors[hash % colors.Length];
    }
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) return path;
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90); path.CloseFigure();
        return path;
    }
    public static void Label(Graphics g, string text, Rectangle rect, float size, Color color, bool bold = false, bool right = false)
    {
        using var font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
        TextRenderer.DrawText(g, text, font, rect, color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | (right ? TextFormatFlags.Right : TextFormatFlags.Left));
    }
    public static void Bar(Graphics g, RectangleF rect, double? value, Color accent)
    {
        using var shape = Round(rect, rect.Height / 2);
        using var track = new SolidBrush(Color.FromArgb(37, 55, 71)); g.FillPath(track, shape);
        if (value is not > 0) return;
        var fillRect = new RectangleF(rect.X, rect.Y, Math.Max(1, (float)(rect.Width * value / 100)), rect.Height);
        using var fillShape = Round(fillRect, rect.Height / 2);
        using var fill = new LinearGradientBrush(rect, accent, Color.FromArgb(Math.Min(255, accent.R + 20), Math.Min(255, accent.G + 16), Math.Min(255, accent.B + 12)), 0f);
        g.FillPath(fill, fillShape);
        using var sheen = new Pen(Color.FromArgb(85, 225, 255, 255), 1);
        if (fillRect.Width > 5) g.DrawLine(sheen, fillRect.X + 2, fillRect.Y + 1, fillRect.Right - 2, fillRect.Y + 1);
    }
}

sealed class BufferedPanel : Panel
{
    public BufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
}

sealed class BufferedFlowPanel : FlowLayoutPanel
{
    public BufferedFlowPanel() { DoubleBuffered = true; ResizeRedraw = true; }
}

sealed class SoftButton : Button
{
    bool hovered, pressed;
    public bool Selected { get; set; }
    public SoftButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; BackColor = Color.Transparent; ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 8.25f); Cursor = Cursors.Hand;
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 8);
        var color = Selected ? Color.FromArgb(24, 69, 92) : pressed ? Color.FromArgb(27, 51, 70) : hovered ? Color.FromArgb(39, 67, 89) : Color.FromArgb(28, 46, 64);
        using var fill = new LinearGradientBrush(ClientRectangle, color, Color.FromArgb(Math.Max(0, color.R - 9), Math.Max(0, color.G - 12), Math.Max(0, color.B - 12)), 90f); g.FillPath(fill, path);
        using var edge = new Pen(Selected ? Color.FromArgb(54, 126, 158) : Color.FromArgb(62, 83, 104)); g.DrawPath(edge, path);
        using var highlight = new Pen(Color.FromArgb(32, 211, 234, 255)); g.DrawLine(highlight, 9, 1, Width - 10, 1);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? ForeColor : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues) { using var pen = new Pen(Theme.Muted); g.DrawPath(pen, path); }
    }
}
