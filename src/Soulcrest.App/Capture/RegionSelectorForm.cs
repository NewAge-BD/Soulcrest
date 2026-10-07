using Soulcrest.App.Services;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Soulcrest.App.Capture;

/// <summary>
/// Full-screen "drag a rectangle" selection over a frozen screenshot of all monitors
/// (technique as in the Map Overlay region selector, written anew for WinForms).
/// </summary>
public sealed class RegionSelectorForm : Form
{
    private readonly Bitmap _screenshot;
    private readonly Rectangle _virtual;
    private readonly string _hint;
    private Point? _start;
    private Rectangle _selection;
    private bool _activated;
    private bool _finished;

    private RegionSelectorForm(Bitmap screenshot, Rectangle virtualScreen, string hint)
    {
        _screenshot = screenshot;
        _virtual = virtualScreen;
        _hint = hint;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = virtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
    }

    public Rectangle? Result { get; private set; }

    public static Rectangle? Select(string hint)
    {
        var virtualScreen = SystemInformation.VirtualScreen;
        using var screenshot = ScreenCapture.Capture(virtualScreen);
        using var form = new RegionSelectorForm(screenshot, virtualScreen, hint);
        form.ShowDialog();
        return form.Result;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        Focus();
        if (!ContainsFocus) CancelSelection();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _activated = true;
        // The previously focused game may have confined the cursor. Only release it
        // while our selection window owns focus; never fight another app for input.
        System.Windows.Forms.Cursor.Clip = Rectangle.Empty;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (_activated && !_finished && !IsDisposed && !Disposing)
            CancelSelection();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            CancelSelection();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void CancelSelection()
    {
        if (_finished) return;
        _finished = true;
        Result = null;
        _start = null;
        Capture = false;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Capture = false;
        base.OnFormClosed(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _start = e.Location;
            _selection = Rectangle.Empty;
            Capture = true;
        }
        else if (e.Button == MouseButtons.Right)
            CancelSelection();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_start is not { } start)
            return;
        _selection = Rectangle.FromLTRB(Math.Min(start.X, e.X), Math.Min(start.Y, e.Y), Math.Max(start.X, e.X), Math.Max(start.Y, e.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_start is null || e.Button != MouseButtons.Left)
            return;
        _start = null;
        Capture = false;
        if (_selection.Width >= 20 && _selection.Height >= 20)
        {
            _finished = true;
            Result = new Rectangle(_selection.X + _virtual.X, _selection.Y + _virtual.Y, _selection.Width, _selection.Height);
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImageUnscaled(_screenshot, 0, 0);
        using (var dim = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
        using (var region = new Region(ClientRectangle))
        {
            region.Exclude(_selection);
            g.FillRegion(dim, region);
        }
        if (_selection.Width > 0)
        {
            using var pen = new Pen(Color.FromArgb(255, 94, 234, 212), 2) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, _selection);
            g.DrawString($"{_selection.Width} × {_selection.Height}", Font, Brushes.White, _selection.X + 4, _selection.Bottom + 4);
        }
        using var font = new Font("Segoe UI", 13, FontStyle.Bold);
        var text = UiText.T(_hint) + "   ·   " + UiText.T("Esc / Rechtsklick = Abbrechen");
        var size = g.MeasureString(text, font);
        var box = new RectangleF((Width - size.Width) / 2 - 12, 24, size.Width + 24, size.Height + 12);
        using (var back = new SolidBrush(Color.FromArgb(220, 20, 24, 32)))
            g.FillRectangle(back, box);
        g.DrawString(text, font, Brushes.White, box.X + 12, box.Y + 6);
    }
}
