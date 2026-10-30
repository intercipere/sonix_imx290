using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Sonix290
{
    /// <summary>
    /// Setup dialog: the video mode and the log folder. Every other setting is a measured default and sits behind
    /// "Advanced..." (the property list, for tests and bug reports). Built in code, no designer.
    /// </summary>
    sealed class SetupForm : Form
    {
        sealed class Mode
        {
            public string Format, Title, Note;
            public int Width, Height;
            public bool Listed = true;
            public bool Is(string format, int w, int h) =>
                Format.Equals(format, StringComparison.OrdinalIgnoreCase) && Width == w && Height == h;
            public override string ToString() => Listed ? Title : Title + " - not offered by this camera";
        }

        /// <summary>The modes the driver's timing is measured for.</summary>
        static Mode[] KnownModes() => new[]
        {
            new Mode
            {
                Format = "MJPG", Width = 1920, Height = 1080, Title = "MJPEG 1920x1080 (compressed)",
                Note = "Works with every camera of this type. Exposures from 0.1 ms to 7.6 s.",
            },
            new Mode
            {
                Format = "YUY2", Width = 1920, Height = 1080, Title = "YUY2 1920x1080 (uncompressed)",
                Note = "Only cameras with the firmware patch for this mode offer it. No JPEG artefacts; exposures up " +
                       "to two minutes, each frame takes at least 0.54 s.",
            },
            new Mode
            {
                Format = "YUY2", Width = 640, Height = 480, Title = "YUY2 640x480 (uncompressed)",
                Note = "Works with every camera of this type. Lower resolution, no JPEG artefacts.",
            },
        };

        public Settings Result { get; private set; }
        readonly Settings edit;
        readonly ComboBox mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
        readonly Label note = new Label { AutoSize = false, Size = new Size(380, 48) };
        readonly Label status = new Label { AutoSize = false, Size = new Size(380, 34), ForeColor = SystemColors.GrayText };
        readonly NumericUpDown gain = new NumericUpDown { Width = 80, Minimum = 0 };
        readonly Label gainNote = new Label { AutoSize = false, Size = new Size(380, 48), ForeColor = SystemColors.GrayText };
        List<Mode> modes;
        /// <summary>Formats the camera lists; empty when it could not be asked.</summary>
        List<(Guid sub, int width, int height)> listed = new List<(Guid, int, int)>();

        public SetupForm(Settings s, bool connected)
        {
            edit = s;
            Result = edit;
            Text = "Sonix IMX290 camera setup " + typeof(SetupForm).Assembly.GetName().Version.ToString(2);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            string found = LoadModes();
            status.Text = (connected ? "Connected: a new mode applies after reconnecting. " : "") + found;
            mode.SelectedIndexChanged += (o, e) => note.Text = (mode.SelectedItem as Mode)?.Note ?? "";
            SelectCurrent();
            ShowGain();

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            var logs = new Button { Text = "Open log folder", AutoSize = true };
            var advanced = new Button { Text = "Advanced...", AutoSize = true };
            logs.Click += (o, e) =>
            {
                System.IO.Directory.CreateDirectory(Log.Folder);
                Process.Start("explorer.exe", Log.Folder);
            };
            advanced.Click += (o, e) =>
            {
                Apply();
                using (var f = new AdvancedForm(edit)) f.ShowDialog(this);
                SelectCurrent();
                ShowGain();
            };
            ok.Click += (o, e) => Apply();

            var left = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            left.Controls.AddRange(new Control[] { logs, advanced });
            var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            right.Controls.AddRange(new Control[] { ok, cancel });
            var buttons = new TableLayoutPanel { ColumnCount = 2, Size = new Size(380, 32), Margin = new Padding(0, 12, 0, 0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons.Controls.Add(left, 0, 0);
            buttons.Controls.Add(right, 1, 0);

            var page = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(12),
            };
            page.Controls.Add(new Label { Text = "Video mode", AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
            page.Controls.AddRange(new Control[] { mode, note, status });
            page.Controls.Add(new Label { Text = "Gain at connect", AutoSize = true, Margin = new Padding(0, 8, 0, 4) });
            page.Controls.AddRange(new Control[] { gain, gainNote, buttons });
            Controls.Add(page);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        /// <summary>Fills the list with the known modes this camera offers; all of them when it cannot be asked.</summary>
        string LoadModes()
        {
            modes = KnownModes().ToList();
            try { listed = UvcDevice.ListModes(edit.DeviceMatch); }
            catch (Exception e) { Log.Write("setup: listing the camera's modes failed: " + e.Message); }
            if (listed.Count == 0) return "Camera not found: all modes are shown.";
            modes.RemoveAll(m => !Offered(m));
            return "The list shows the modes this camera offers.";
        }

        /// <summary>Selects the mode in the settings; one set by hand (Advanced) is shown as an extra entry.</summary>
        void SelectCurrent()
        {
            var cur = modes.FirstOrDefault(m => m.Is(edit.Format, edit.Width, edit.Height));
            if (cur == null)
            {
                cur = KnownModes().FirstOrDefault(m => m.Is(edit.Format, edit.Width, edit.Height)) ?? new Mode
                {
                    Format = edit.Format, Width = edit.Width, Height = edit.Height,
                    Title = $"{edit.Format} {edit.Width}x{edit.Height}",
                    Note = "Set under Advanced. The exposure timing is only measured for the other modes.",
                };
                cur.Listed = listed.Count == 0 || Offered(cur);
                modes.Add(cur);
            }
            mode.Items.Clear();
            mode.Items.AddRange(modes.ToArray());
            mode.SelectedItem = cur;
        }

        bool Offered(Mode m) => listed.Any(l => m.Is(MediaGuids.Name(l.sub), l.width, l.height));

        /// <summary>Gain field from the settings (range as the driver will offer it).</summary>
        void ShowGain()
        {
            gain.Maximum = Math.Max(0, Math.Min(edit.SensorGainMax, SensorTiming.GainLimit + Math.Max(0, edit.HcgFromGain)));
            gain.Value = Math.Max(gain.Minimum, Math.Min(gain.Maximum, edit.ConnectGain));
            gain.Enabled = gainNote.Enabled = edit.SensorGain;
            // HCG adds ~6 dB from HcgFromGain on, where the register restarts at 0
            double Db(int g) => 0.3 * g + (edit.HcgFromGain >= 0 && g >= edit.HcgFromGain ? 6 - 0.3 * edit.HcgFromGain : 0);
            gainNote.Text = $"Sensor gain in 0.3 dB steps (0 = {Db(0):F0} dB, {gain.Maximum} = {Db((int)gain.Maximum):F0} dB). " +
                            "PHD2 always uses this value; N.I.N.A. and SharpCap set their own gain. For faint guide stars 40-90.";
        }

        void Apply()
        {
            edit.ConnectGain = (int)gain.Value;
            if (!(mode.SelectedItem is Mode m)) return;
            edit.Format = m.Format;
            edit.Width = m.Width;
            edit.Height = m.Height;
        }

        /// <summary>All settings as a property list, for tests and bug reports.</summary>
        sealed class AdvancedForm : Form
        {
            public AdvancedForm(Settings edit)
            {
                Text = "Sonix IMX290: advanced settings";
                Font = SystemFonts.MessageBoxFont;
                Size = new Size(460, 620);
                StartPosition = FormStartPosition.CenterParent;
                MinimizeBox = false;
                var info = new Label
                {
                    Dock = DockStyle.Top, Height = 40, Padding = new Padding(6),
                    Text = "Defaults are the values measured for this camera. Change them only for testing.",
                };
                var grid = new PropertyGrid
                {
                    Dock = DockStyle.Fill, SelectedObject = edit, ToolbarVisible = false, HelpVisible = true,
                    PropertySort = PropertySort.Alphabetical,
                };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
                var close = new Button { Text = "Close", DialogResult = DialogResult.OK };
                var reset = new Button { Text = "Defaults", AutoSize = true };
                reset.Click += (o, e) =>
                {
                    var d = new Settings();
                    foreach (var p in typeof(Settings).GetProperties())
                        p.SetValue(edit, p.GetValue(d));
                    grid.Refresh();
                };
                buttons.Controls.AddRange(new Control[] { close, reset });
                AcceptButton = CancelButton = close;
                Controls.Add(grid);
                Controls.Add(info);
                Controls.Add(buttons);
            }
        }
    }
}
