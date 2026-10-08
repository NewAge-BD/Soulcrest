using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Edits only Soulcrest's scan result; applying it to the profile remains a separate action.</summary>
internal sealed class ScanValueEditorForm : Form
{
    internal ScanValueEditorForm(ScanMark mark, Func<string, bool> save)
    {
        Text = UiText.T("Erkannten Wert korrigieren");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(390, 225);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var name = new Label { Text = mark.PetName, AutoEllipsis = true, Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) };
        var hint = new Label { Text = UiText.T("Wert eingeben: 0–4/5, 0–24/25, 0–74/75 oder MAX."), AutoSize = true, MaximumSize = new Size(350, 0) };
        var input = new TextBox { Text = mark.Value == "?" ? "" : mark.Value, Dock = DockStyle.Fill, AccessibleName = UiText.T("Erkannter Wert") };
        var error = new Label { ForeColor = Color.Firebrick, AutoSize = true, MaximumSize = new Size(350, 0) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var cancel = new Button { Text = UiText.T("Abbrechen"), DialogResult = DialogResult.Cancel, AutoSize = true };
        var accept = new Button { Text = UiText.T("Speichern"), AutoSize = true };
        accept.Click += (_, _) =>
        {
            if (PetScanService.ParseCorrection(input.Text) is null)
            {
                error.Text = UiText.T("Bitte einen gültigen Bruch oder MAX eingeben.");
                input.Focus();
                return;
            }
            if (!save(input.Text))
            {
                error.Text = UiText.T("Diese Scan-Karte ist nicht mehr vorhanden.");
                return;
            }
            DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(accept);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(name);
        layout.Controls.Add(hint);
        layout.Controls.Add(input);
        layout.Controls.Add(error);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = accept;
        CancelButton = cancel;
        Shown += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
