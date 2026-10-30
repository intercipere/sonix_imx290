// The window: one fixed path (back up -> check -> write), no free-form options. All work is done by the same
// commands as the command line (Program.Execute); this file only sequences them and guards the buttons.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sonix290;

sealed class MainForm : Form
{
    readonly Label backupInfo = new Label { AutoSize = true, MaximumSize = new Size(640, 0) };
    readonly Button btnBackup = new Button { Text = "1. Back up the camera's flash...", AutoSize = true };
    readonly Button btnOpen = new Button { Text = "Use an existing backup...", AutoSize = true };
    readonly RadioButton rbPatch = new RadioButton { Text = "Patch the camera (" + Patch.PatchTitle + ")", AutoSize = true, Checked = true };
    readonly RadioButton rbRestore = new RadioButton { Text = "Restore the original firmware from the backup", AutoSize = true };
    readonly CheckBox chkUnknown = new CheckBox { Text = "A write failed and the camera's state is unknown (recovery: skip the check of the sector)", AutoSize = true, Visible = false };
    readonly Button btnWrite = new Button { AutoSize = true };
    readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Maximum = 1000 };
    readonly Button btnCancel = new Button { Text = "Cancel", AutoSize = true, Enabled = false };
    readonly Label status = new Label { AutoSize = true, Text = "Ready." };
    readonly Label slowPort = new Label { AutoSize = true, Dock = DockStyle.Top, Visible = false, BackColor = Color.Gold, ForeColor = Color.Black };
    readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), WordWrap = true };

    string backupPath;
    byte[] backup;
    Patch.State backupState = Patch.State.Unknown;
    byte[] liveFlash;        // the camera's flash from a backup in this session, camera not unplugged since
    bool polling;
    bool busy;
    bool waiting, stopWait;  // after a write: waiting for the replug
    bool checkingPort;       // the port check when the window opens

    public MainForm()
    {
        Text = "Sonix camera flash tool " + typeof(MainForm).Assembly.GetName().Version.ToString(3) + " (experimental) - " + Patch.PatchTitle;
        ClientSize = new Size(700, 640);
        MinimumSize = new Size(560, 560);
        Font = new Font("Segoe UI", 9f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }   // the icon compiled into the exe

        var warn = new Label
        {
            Text = "Flashing changes the camera's firmware. A mistake can leave the camera unusable (recoverable only with an external " +
                   "SPI programmer). Use this only on a camera you can afford to lose, with only this one Sonix camera connected and no " +
                   "other program using a camera. Never unplug the camera or close this window while it writes. No warranty.",
            ForeColor = Color.White, BackColor = Color.Firebrick, Padding = new Padding(8), AutoSize = true, MaximumSize = new Size(676, 0),
            Dock = DockStyle.Top,
        };

        // shown only while the camera is on a slow port (checked when the window opens and by every command)
        slowPort.Padding = new Padding(8);
        slowPort.MaximumSize = new Size(676, 0);

        var gBackup = new GroupBox { Text = "Backup of the camera's flash (required)", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8) };
        var fBackup = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, MaximumSize = new Size(660, 0) };
        fBackup.Controls.AddRange(new Control[] { backupInfo, btnBackup, btnOpen });
        backupInfo.Text = "No backup yet. Click 1 to read the camera (about 90 seconds) and save the file; keep it safe.";
        backupInfo.Margin = new Padding(0, 0, 0, 6);
        fBackup.SetFlowBreak(backupInfo, true);
        gBackup.Controls.Add(fBackup);

        var gAct = new GroupBox { Text = "Action", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8) };
        var fAct = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        fAct.Controls.AddRange(new Control[] { rbPatch, rbRestore, chkUnknown, btnWrite });
        gAct.Controls.Add(fAct);

        var bar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 6, 0, 6) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(progress, 0, 0);
        bar.Controls.Add(btnCancel, 1, 0);
        bar.Controls.Add(status, 0, 1);
        bar.SetColumnSpan(status, 2);

        var btnSave = new Button { Text = "Save log...", AutoSize = true };
        var btnFolder = new Button { Text = "Open log folder", AutoSize = true };
        var foot = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        foot.Controls.AddRange(new Control[] { btnSave, btnFolder });

        // docking order: last added is laid out first
        Controls.Add(log);
        Controls.Add(foot);
        Controls.Add(bar);
        Controls.Add(gAct);
        Controls.Add(gBackup);
        Controls.Add(slowPort);
        Controls.Add(warn);
        Padding = new Padding(12);

        btnBackup.Click += (s, e) => DoBackup();
        btnOpen.Click += (s, e) => DoOpen();
        btnWrite.Click += (s, e) => DoWrite();
        btnCancel.Click += (s, e) => DoCancel();
        rbPatch.CheckedChanged += (s, e) => ModeChanged();
        rbRestore.CheckedChanged += (s, e) => ModeChanged();
        btnSave.Click += (s, e) => SaveLog();
        btnFolder.Click += (s, e) => { try { Process.Start("explorer.exe", Log.Folder); } catch { } };
        FormClosing += OnClosing;

        Log.ToConsole = false;
        Log.Enabled = true;
        Log.Open("sxflash");
        Program.NoteHook = AddLog;
        Program.StatusHook = t => OnUi(() => { if (busy) status.Text = t; });
        Program.PortHook = ms => OnUi(() => ShowPort(ms));
        Shown += async (s, e) =>
        {
            if (await Task.Run(() => CountCameras()) != 1) return;
            checkingPort = true;
            UpdateButtons();
            try { await Task.Run(() => Program.MeasurePort()); }
            catch (Exception ex) { AddLog("port check failed: " + ex.Message); }
            finally { checkingPort = false; UpdateButtons(); }
        };
        // the bar moves with the flash reads; between them it runs as a marquee, so a long step does not look frozen
        Program.ProgressHook = (n, total) => OnUi(() =>
        {
            if (!busy) return;
            if (n >= total) { progress.Style = ProgressBarStyle.Marquee; return; }
            progress.Style = ProgressBarStyle.Continuous;
            progress.Value = (int)(1000L * n / Math.Max(1, total));
        });
        AddLog("log file: " + Log.FilePath);
        AddLog("this tool: " + Patch.PatchTitle);
        // a backup only stands for the camera's flash while that camera stays connected
        var poll = new Timer { Interval = 1000 };
        poll.Tick += async (s, e) =>
        {
            if (liveFlash == null || busy || waiting || polling) return;
            polling = true;
            int n = await Task.Run(() => CountCameras());
            polling = false;
            if (n != 1 && liveFlash != null)
            {
                liveFlash = null;
                AddLog("the camera was unplugged or another one connected: the next write reads the flash again");
            }
        };
        poll.Start();
        ModeChanged();
    }

    // ---------- helpers ----------

    void OnUi(Action a)
    {
        if (IsDisposed) return;
        if (!IsHandleCreated) { a(); return; }   // only during construction, on the UI thread
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    void AddLog(string line) => OnUi(() => log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line.Replace("\n", "\r\n") + "\r\n"));

    bool PatchMode => rbPatch.Checked;

    void ShowPort(double readMs)
    {
        slowPort.Visible = Program.IsSlow(readMs);
        if (slowPort.Visible) slowPort.Text = Program.SlowPortText(readMs);
    }

    void ModeChanged()
    {
        chkUnknown.Visible = !PatchMode;
        if (PatchMode) chkUnknown.Checked = false;
        btnWrite.Text = PatchMode ? "2. Patch the camera..." : "2. Restore the original firmware...";
        UpdateButtons();
    }

    void UpdateButtons()
    {
        bool have = backup != null;
        bool usable = have && backupState != Patch.State.Unknown;   // any known state: the original is rebuilt from it
        bool idle = !busy && !waiting && !checkingPort;
        btnBackup.Enabled = btnOpen.Enabled = rbPatch.Enabled = rbRestore.Enabled = chkUnknown.Enabled = idle;
        btnWrite.Enabled = idle && usable;
        btnCancel.Enabled = busy || waiting;
    }

    void SetBackup(string path, byte[] data)
    {
        backupPath = path;
        backup = data;
        backupState = Patch.Identify(data, out var fw, out int version);
        switch (backupState)
        {
            case Patch.State.Original:
                backupInfo.Text = $"Backup: {path}\r\nKnown firmware: {fw.Name}, original. It can be patched, and this file restores it.";
                break;
            case Patch.State.Patched:
                backupInfo.Text = $"Backup: {path}\r\nKnown firmware: {fw.Name}, already with patch v{version} (the newest). " +
                                  "Restore can bring back the original firmware with this file.";
                break;
            case Patch.State.OlderPatch:
                backupInfo.Text = $"Backup: {path}\r\nKnown firmware: {fw.Name}, with the older patch v{version}. " +
                                  $"'Patch' updates it to v{Patch.Version} in one write; Restore brings back the original firmware.";
                break;
            default:
                backupInfo.Text = $"Backup: {path}\r\nUNSUPPORTED: not a firmware this patch is known for. Nothing can be written. " +
                                  "Please send this file and the log to the project, so the firmware can be added.";
                AddLog($"UNSUPPORTED firmware: backup sha256 {Patch.Sha(data)} is not in the list of known firmwares");
                MessageBox.Show(this, "This flash content is not a firmware this patch is known for, so nothing can be written to this camera." +
                                      "\r\n\r\n" +
                                      "Nothing was changed. Please send the backup file and the log (Save log...) to the project, so the firmware can be checked and added.",
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
        }
        UpdateButtons();
    }

    // ---------- commands ----------

    void DoBackup()
    {
        string name = "SonixFlashBackup_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bin";
        using (var dlg = new SaveFileDialog
        {
            Title = "Save the backup of the camera's flash",
            FileName = name,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Filter = "Flash backup (*.bin)|*.bin",
            OverwritePrompt = false,
        })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (File.Exists(dlg.FileName)) { MessageBox.Show(this, "That file exists. A backup is never overwritten; choose a new name.", Text); return; }
            string path = dlg.FileName;
            Exec("backup", path, write: false, "Backing up the flash (two passes, about 90 seconds)...", rc =>
            {
                if (rc == 0 && File.Exists(path))
                {
                    var data = File.ReadAllBytes(path);
                    liveFlash = data;
                    SetBackup(path, data);
                }
            });
        }
    }

    void DoOpen()
    {
        using (var dlg = new OpenFileDialog { Title = "Open a backup of the camera's flash", Filter = "Flash backup (*.bin)|*.bin|All files|*.*" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var data = File.ReadAllBytes(dlg.FileName);
            if (data.Length != Patch.ImageLen) { MessageBox.Show(this, "A backup must be exactly 128 KB (131072 bytes).", Text); return; }
            SetBackup(dlg.FileName, data);
            AddLog($"opened {dlg.FileName}: sha256 {Patch.Sha(data)}");
        }
    }

    /// <summary>One command: the checks (nothing written), the last-chance question, then the write.</summary>
    void DoWrite()
    {
        string cmd = PatchMode ? "patch" : "restore";
        bool patch = PatchMode;
        Program.KnownFlash = liveFlash;
        Program.ConfirmHook = what =>
        {
            bool ok = false;
            Invoke((Action)(() =>
            {
                ok = MessageBox.Show(this, $"All checks passed. Last chance: {what}?\r\n\r\nThe write takes {Program.WriteDuration}. " +
                                           "Do not unplug the camera, close this window or put the PC to sleep until it says it is done.",
                                     Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
                if (ok) status.Text = $"Writing ({Program.WriteDuration}). Do not unplug the camera or close this window.";
            }));
            return ok;
        };
        string checking = liveFlash != null ? "Checking the camera (nothing is written yet, about 30 seconds)..."
                                             : "Checking the camera (reads the flash, about 1.5 minutes; nothing is written yet)...";
        Exec(cmd, backupPath, write: true, checking, rc =>
        {
            Program.KnownFlash = null;
            Program.ConfirmHook = null;
            if (rc == 3) return;   // not confirmed or cancelled: nothing written, the camera's flash unchanged
            if (rc == 4) { status.Text = "The camera already has this firmware: nothing was written."; return; }
            if (rc == 0)
            {
                liveFlash = null;
                MessageBox.Show(this, "The write succeeded and the whole flash was read back and verified.\r\n\r\n" +
                                      "The camera reads its firmware only when it powers up: unplug it now and plug it in again. " +
                                      "This program waits for it and then checks which video modes it offers.", Text);
                WaitForReplug(expectHd: patch);
            }
            else if (!Program.WriteStarted)
            {
                status.Text = "A check did not pass: nothing was written, the camera is unchanged. See the log.";
            }
            else
            {
                liveFlash = null;
                status.Text = "The write FAILED. Do NOT unplug the camera. See the log.";
                MessageBox.Show(this, "The write failed.\r\n\r\nDo NOT unplug the camera and do not close this program.\r\n" +
                                      "Read the log. To retry, choose 'Restore', tick the recovery box and write again.", Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
    }

    void DoCancel()
    {
        if (waiting) { stopWait = true; return; }
        if (!Program.CanCancel) { MessageBox.Show(this, "The write has started and cannot be cancelled.", Text); return; }
        Program.CancelRequested = true;
        status.Text = "Cancelling...";
    }

    /// <summary>Runs one command on a worker thread with the window locked; after it, onDone(rc) runs on the UI thread.</summary>
    void Exec(string cmd, string path, bool write, string message, Action<int> onDone)
    {
        Program.Args.Clear();
        Program.Args.Add(cmd);
        Program.Args.Add(path);
        Program.Opt.Clear();
        if (write) Program.Opt["write"] = "1";
        if (!PatchMode && chkUnknown.Checked) Program.Opt["current"] = "any";
        Program.CancelRequested = false;
        Program.CanCancel = true;
        busy = true;
        Program.WriteStarted = false;
        status.Text = message;
        progress.Style = ProgressBarStyle.Marquee;
        progress.Value = 0;
        UpdateButtons();
        AddLog($"--- {cmd}{(write ? " --write" : "")} ---");
        Task.Run(() => Program.Execute(cmd)).ContinueWith(t =>
        {
            int rc = t.IsFaulted ? 1 : t.Result;
            OnUi(() =>
            {
                busy = false;
                status.Text = rc == 0 ? "Done." : rc == 3 ? "Cancelled; nothing was written." : "Stopped: see the log.";
                progress.Style = ProgressBarStyle.Continuous;
                progress.Value = 0;
                try { onDone(rc); } catch (Exception e) { AddLog("error: " + e.Message); }
                UpdateButtons();
            });
        });
    }

    void OnClosing(object sender, FormClosingEventArgs e)
    {
        if (!busy) return;
        if (!Program.CanCancel)
        {
            MessageBox.Show(this, "The camera is being written. Closing now could leave it unusable. Wait until it says it is done.", Text);
            e.Cancel = true;
            return;
        }
        if (MessageBox.Show(this, "An operation is running. Cancel it and close? (Nothing has been written yet.)", Text, MessageBoxButtons.YesNo) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        Program.CancelRequested = true;
    }

    // ---------- after a write: wait for the replug, then check what the camera offers ----------

    /// <summary>
    /// The new descriptors take effect only at power-up. Waits until the camera is gone and back, then reads its
    /// formats: after a patch it must list YUY2 1920x1080, after a restore it must not.
    /// </summary>
    async void WaitForReplug(bool expectHd)
    {
        waiting = true;
        stopWait = false;
        UpdateButtons();
        try
        {
            status.Text = "Unplug the camera now, then plug it in again. Waiting for it...";
            AddLog("waiting for the camera to be unplugged and plugged in again");
            while (await Task.Run(() => CountCameras()) > 0) { if (stopWait) return; await Task.Delay(500); }
            AddLog("camera unplugged");
            status.Text = "Camera unplugged. Plug it in again...";
            while (await Task.Run(() => CountCameras()) == 0) { if (stopWait) return; await Task.Delay(500); }
            AddLog("camera plugged in; reading its video modes");
            status.Text = "Camera found. Reading its video modes...";
            List<string> formats = null;
            for (int i = 0; i < 20 && formats == null && !stopWait; i++)
            {
                await Task.Delay(500);
                formats = await Task.Run(() => ReadFormats());
            }
            if (stopWait) return;
            if (formats == null)
            {
                string t = "The camera is connected but did not report its video modes. Unplug it and plug it in once more, " +
                           "then check it in your camera program.";
                AddLog(t);
                status.Text = t;
                return;
            }
            foreach (var f in formats) AddLog("  " + f);
            bool hd = formats.Any(f => f.Contains("YUY2 1920x1080"));
            string text = hd == expectHd
                ? (hd ? "Done: the camera now offers YUY2 1920x1080, the patch is active."
                      : "Done: the camera offers its original video modes again (no YUY2 1920x1080), the original firmware is active.")
                : (hd ? "The camera still offers YUY2 1920x1080, so the original firmware is not active. Save the log and send it to the project."
                      : "The camera does not offer YUY2 1920x1080, so the patch is not active. Save the log and send it to the project.");
            if (hd == expectHd) text += " You can close this program now.";
            AddLog(text);
            status.Text = text;
            MessageBox.Show(this, text, Text, MessageBoxButtons.OK, hd == expectHd ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            if (stopWait)
            {
                AddLog("stopped waiting for the replug");
                status.Text = "Stopped waiting. Unplug the camera and plug it in again before using it.";
            }
            waiting = false;
            UpdateButtons();
        }
    }

    static int CountCameras() =>
        UvcDevice.List().Count(e => e.Path.IndexOf("vid_0c45", StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>The camera's formats (graph built, stream never run); null while it is not ready yet.</summary>
    static List<string> ReadFormats()
    {
        try
        {
            using (var com = new ComThread("formats"))
                return com.Run(() =>
                {
                    var dev = UvcDevice.Open("vid_0c45", 1920, 1080, MediaGuids.Mjpg, 0, new FrameBuffer());
                    try { return dev.DescribeFormats(); }
                    finally { dev.Dispose(); }
                });
        }
        catch { return null; }
    }

    void SaveLog()
    {
        using (var dlg = new SaveFileDialog { Title = "Save the log", FileName = "sxflash-log.txt", Filter = "Text (*.txt)|*.txt" })
            if (dlg.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dlg.FileName, log.Text, Encoding.UTF8);
    }
}
