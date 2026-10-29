using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Sonix290
{
    /// <summary>Plain property list for the driver settings; built in code, no designer.</summary>
    sealed class SetupForm : Form
    {
        public Settings Result { get; private set; }
        readonly Settings edit;

        public SetupForm(Settings s, bool connected)
        {
            edit = s;
            Text = "Sonix IMX290 camera setup";
            Size = new Size(460, 620);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;

            var info = new Label
            {
                Dock = DockStyle.Top, Height = 54, Padding = new Padding(6),
                Text = (connected ? "Connected: changes apply after reconnecting.\n" : "") +
                       "Defaults are the values measured for this camera. Change them only for testing.",
            };
            var grid = new PropertyGrid
            {
                Dock = DockStyle.Fill, SelectedObject = edit, ToolbarVisible = false, HelpVisible = true,
                PropertySort = PropertySort.Alphabetical,
            };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            var logs = new Button { Text = "Open log folder", AutoSize = true };
            var reset = new Button { Text = "Defaults", AutoSize = true };
            logs.Click += (o, e) =>
            {
                System.IO.Directory.CreateDirectory(Log.Folder);
                Process.Start("explorer.exe", Log.Folder);
            };
            reset.Click += (o, e) =>
            {
                var d = new Settings();
                foreach (var p in typeof(Settings).GetProperties())
                    p.SetValue(edit, p.GetValue(d));
                grid.Refresh();
            };
            buttons.Controls.AddRange(new Control[] { cancel, ok, logs, reset });
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(grid);
            Controls.Add(info);
            Controls.Add(buttons);
            Result = edit;
            var version = typeof(SetupForm).Assembly.GetName().Version;
            Text += " " + version.ToString(2);
        }
    }
}
