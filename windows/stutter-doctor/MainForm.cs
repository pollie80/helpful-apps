using System.Diagnostics;
using System.Text;

namespace StutterDoctor;

public sealed class MainForm : Form
{
    private readonly Engine _eng = new();
    private List<Finding> _audit = new();
    private readonly int _startTab;
    private readonly bool _autoStart;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private int _tick;
    private int _shownEvents;

    // header / chrome
    private Label _lblStatus, _lblSession;
    private Button _btnStart, _btnMark, _btnExport, _btnFolder, _btnAdmin, _btnRecheck;
    private CheckBox _chkLowOverhead, _chkOnlyInGame;
    private TabControl _tabs;

    // live tab
    private GraphPanel _graph;
    private readonly Dictionary<string, Tile> _tiles = new();

    // stutters tab
    private ListView _lvEvents;
    private RichTextBox _txtEvent;

    // checkup tab
    private ListView _lvAudit;
    private RichTextBox _txtAudit;

    // cached derived stats, recomputed once a second
    private double _fpsNow, _fps1Low, _worstFrame10s;

    private const int HotkeyMark = 0xB01;
    private const int HotkeyMarkAlt = 0xB02;

    /// <param name="startTab">0 Live, 1 Stutters, 2 Checkup. Lets you launch straight into the
    /// checkup, which is the useful screen before a session has been recorded.</param>
    /// <param name="autoStart">Begin recording as soon as the window opens.</param>
    public MainForm(int startTab = 0, bool autoStart = false)
    {
        _startTab = startTab;
        _autoStart = autoStart;
        Text = "Stutter Doctor — Valorant frame stutter diagnosis";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f);
        MinimumSize = new Size(980, 700);
        Size = new Size(1220, 860);
        StartPosition = FormStartPosition.CenterScreen;

        BuildChrome();
        BuildTabs();

        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        RunAudit();
        if (_startTab > 0 && _startTab < _tabs.TabPages.Count) _tabs.SelectedIndex = _startTab;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Must wait for the handle: BeginInvoke from the constructor throws.
        if (_autoStart) OnStartStop(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ chrome

    private void BuildChrome()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Panel, Padding = new Padding(14, 8, 14, 8) };

        var title = new Label
        {
            Text = "Stutter Doctor",
            Font = new Font("Segoe UI Semibold", 14f),
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(14, 8),
        };
        _lblSession = new Label
        {
            Text = "idle",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Theme.TextDim,
            AutoSize = true,
            Location = new Point(16, 36),
        };
        _lblStatus = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Theme.TextDim,
            AutoSize = false,
            Location = new Point(250, 10),
            Size = new Size(700, 44),
        };
        header.Controls.AddRange(new Control[] { title, _lblSession, _lblStatus });

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Panel, Padding = new Padding(14, 10, 14, 10) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };

        _btnStart = MakeButton("Start session", Theme.Good, 130, OnStartStop);
        _btnMark = MakeButton("Mark stutter  (F8)", Theme.Accent, 150, (_, _) => MarkNow());
        _btnExport = MakeButton("Export report", Theme.PanelAlt, 120, OnExport);
        _btnFolder = MakeButton("Open reports", Theme.PanelAlt, 115, (_, _) => OpenReports());
        _btnAdmin = MakeButton("Restart as Admin", Theme.Warn, 140, (_, _) => RestartAsAdmin());

        _chkLowOverhead = new CheckBox
        {
            Text = "Low-overhead mode",
            ForeColor = Theme.TextDim,
            AutoSize = true,
            Margin = new Padding(16, 9, 0, 0),
        };
        _chkOnlyInGame = new CheckBox
        {
            Text = "Only list stutters while Valorant runs",
            ForeColor = Theme.TextDim,
            AutoSize = true,
            Checked = true,
            Margin = new Padding(12, 9, 0, 0),
        };
        // Rebuild rather than append, so toggling the filter reveals events that were skipped.
        _chkOnlyInGame.CheckedChanged += (_, _) =>
        {
            _shownEvents = 0;
            _lvEvents.Items.Clear();
            AppendNewEvents();
        };

        flow.Controls.AddRange(new Control[] { _btnStart, _btnMark, _btnExport, _btnFolder, _chkLowOverhead, _chkOnlyInGame });
        if (!Native.IsElevated()) flow.Controls.Add(_btnAdmin);
        footer.Controls.Add(flow);

        Controls.Add(footer);
        Controls.Add(header);
    }

    private static Button MakeButton(string text, Color accent, int width, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelAlt,
            ForeColor = accent == Theme.PanelAlt ? Theme.Text : accent,
            Margin = new Padding(0, 2, 8, 2),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Theme.GridStrong;
        b.FlatAppearance.MouseOverBackColor = Theme.Grid;
        b.Click += onClick;
        return b;
    }

    // ------------------------------------------------------------------ tabs

    private void BuildTabs()
    {
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            ItemSize = new Size(130, 30),
            SizeMode = TabSizeMode.Fixed,
            Padding = new Point(0, 0),
        };
        _tabs.DrawItem += DrawTab;

        _tabs.TabPages.Add(BuildLiveTab());
        _tabs.TabPages.Add(BuildEventsTab());
        _tabs.TabPages.Add(BuildAuditTab());
        Controls.Add(_tabs);
        _tabs.BringToFront();
    }

    private void DrawTab(object sender, DrawItemEventArgs e)
    {
        var page = _tabs.TabPages[e.Index];
        bool sel = e.Index == _tabs.SelectedIndex;
        using var bg = new SolidBrush(sel ? Theme.Panel : Theme.Bg);
        e.Graphics.FillRectangle(bg, e.Bounds);
        if (sel)
            using (var accent = new SolidBrush(Theme.Accent))
                e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Bottom - 3, e.Bounds.Width, 3);
        TextRenderer.DrawText(e.Graphics, page.Text, new Font("Segoe UI", 9.5f),
            e.Bounds, sel ? Theme.Text : Theme.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private TabPage BuildLiveTab()
    {
        var page = new TabPage("Live") { BackColor = Theme.Bg, Padding = new Padding(12) };

        _graph = new GraphPanel { Dock = DockStyle.Top, Height = 300 };

        var tiles = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(0, 12, 0, 0),
            AutoScroll = true,
        };
        foreach (var name in new[]
        {
            "Stutters", "Frame rate", "Worst frame (10s)", "Worst stall",
            "CPU", "DPC time", "GPU", "GPU clock",
            "RAM free", "Present mode", "Monitor overhead", "Valorant",
        })
        {
            var t = new Tile(name);
            _tiles[name] = t;
            tiles.Controls.Add(t);
        }

        page.Controls.Add(tiles);
        page.Controls.Add(_graph);
        return page;
    }

    private TabPage BuildEventsTab()
    {
        var page = new TabPage("Stutters") { BackColor = Theme.Bg, Padding = new Padding(12) };
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 320,
            BackColor = Theme.Bg,
        };

        // "Leading suspect" goes last so the auto-fill column is the one that benefits from width.
        _lvEvents = MakeListView(("#", 44), ("Time", 78), ("Kind", 92), ("Duration", 84),
                                 ("Severity", 74), ("Conf", 54), ("Leading suspect", 430));
        _lvEvents.SelectedIndexChanged += (_, _) => ShowEventDetail();

        _txtEvent = MakeDetailBox("Select a stutter above to see the full breakdown.\n\n" +
                                  "Nothing here yet — start a session, play, and press F8 whenever you feel a freeze.");

        split.Panel1.Controls.Add(_lvEvents);
        split.Panel2.Controls.Add(_txtEvent);
        page.Controls.Add(split);
        return page;
    }

    private TabPage BuildAuditTab()
    {
        var page = new TabPage("Checkup") { BackColor = Theme.Bg, Padding = new Padding(12) };

        var bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg };
        _btnRecheck = MakeButton("Re-run checkup", Theme.Accent, 130, (_, _) => RunAudit());
        _btnRecheck.Location = new Point(0, 2);
        bar.Controls.Add(_btnRecheck);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 300,
            BackColor = Theme.Bg,
        };

        _lvAudit = MakeListView(("Severity", 90), ("Area", 112), ("Finding", 820));
        _lvAudit.SelectedIndexChanged += (_, _) => ShowAuditDetail();
        _txtAudit = MakeDetailBox("Select a finding above for the detail and the fix.");

        split.Panel1.Controls.Add(_lvAudit);
        split.Panel2.Controls.Add(_txtAudit);

        page.Controls.Add(split);
        page.Controls.Add(bar);
        return page;
    }

    private static RichTextBox MakeDetailBox(string placeholder) => new()
    {
        Dock = DockStyle.Fill,
        BackColor = Theme.Panel,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.None,
        ReadOnly = true,
        Font = new Font("Segoe UI", 9.5f),
        Text = placeholder,
        DetectUrls = false,
    };

    private ListView MakeListView(params (string Name, int Width)[] cols)
    {
        var lv = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            OwnerDraw = true,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        foreach (var (n, w) in cols) lv.Columns.Add(n, w);

        lv.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(Theme.PanelAlt);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var line = new Pen(Theme.Grid);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, new Font("Segoe UI", 8.5f),
                Rectangle.Inflate(e.Bounds, -6, 0), Theme.TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
        lv.DrawItem += (_, e) =>
        {
            var c = e.Item.Selected ? Theme.Grid : (e.ItemIndex % 2 == 0 ? Theme.Panel : Color.FromArgb(0x1E, 0x22, 0x2A));
            using var bg = new SolidBrush(c);
            e.Graphics.FillRectangle(bg, e.Bounds);
        };
        lv.DrawSubItem += (_, e) =>
        {
            var color = e.SubItem.ForeColor == Color.Empty || e.SubItem.ForeColor == SystemColors.WindowText
                ? Theme.Text : e.SubItem.ForeColor;
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, lv.Font,
                Rectangle.Inflate(e.Bounds, -6, 0), color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
        // Windows paints the header strip past the final column itself, in the system colour, which
        // leaves a white block on a dark form. Stretching the last column removes the gap entirely.
        lv.SizeChanged += (_, _) => AutoFitLastColumn(lv);
        return lv;
    }

    private static void AutoFitLastColumn(ListView lv)
    {
        if (lv.Columns.Count == 0) return;
        int used = 0;
        for (int i = 0; i < lv.Columns.Count - 1; i++) used += lv.Columns[i].Width;
        int avail = lv.ClientSize.Width - used - 2;
        if (avail > 120 && lv.Columns[^1].Width != avail) lv.Columns[^1].Width = avail;
    }

    // ------------------------------------------------------------------ hotkey

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RegisterHotKey(Handle, HotkeyMark, Native.MOD_NOREPEAT, Native.VK_F8);
        Native.RegisterHotKey(Handle, HotkeyMarkAlt,
            Native.MOD_NOREPEAT | Native.MOD_CONTROL | Native.MOD_SHIFT, Native.VK_F8);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.UnregisterHotKey(Handle, HotkeyMark);
        Native.UnregisterHotKey(Handle, HotkeyMarkAlt);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY &&
            (m.WParam.ToInt32() == HotkeyMark || m.WParam.ToInt32() == HotkeyMarkAlt))
        {
            MarkNow();
            return;
        }
        base.WndProc(ref m);
    }

    private void MarkNow()
    {
        if (!_eng.Running)
        {
            _lblStatus.Text = "Start a session first — nothing is being recorded.";
            return;
        }
        _eng.MarkNow();
        _lblStatus.Text = $"Marked at {_eng.Store.Now():F1}s — analysing…";
    }

    // ------------------------------------------------------------------ actions

    private void OnStartStop(object sender, EventArgs e)
    {
        if (_eng.Running)
        {
            _eng.Stop();
            _btnStart.Text = "Start session";
            _btnStart.ForeColor = Theme.Good;
            _chkLowOverhead.Enabled = true;
            _lblStatus.Text = "Session stopped. Export the report, or start another to compare.";
        }
        else
        {
            _eng.LowOverhead = _chkLowOverhead.Checked;
            _chkLowOverhead.Enabled = false;
            _shownEvents = 0;
            _lvEvents.Items.Clear();
            _eng.Start();
            _btnStart.Text = "Stop session";
            _btnStart.ForeColor = Theme.Bad;
            _lblStatus.Text = "Recording. Alt-tab into Valorant and play — press F8 whenever you feel a freeze.";
        }
    }

    private void OnExport(object sender, EventArgs e)
    {
        try
        {
            // Re-audit before writing: the startup snapshot predates the game and everything the
            // user opened alongside it, which is precisely what the report needs to name.
            RunAudit();
            string dir = ReportWriter.Write(_eng, _audit);
            _lblStatus.Text = "Report written to " + dir;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void OpenReports()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "reports");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void RestartAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "Could not elevate: " + ex.Message;
        }
    }

    private void RunAudit()
    {
        try
        {
            _audit = ConfigAudit.Run(_eng.Store);
            _graph.RefreshHz = _eng.Store.RefreshHz;
            _lvAudit.BeginUpdate();
            _lvAudit.Items.Clear();
            foreach (var f in _audit)
            {
                var item = new ListViewItem(f.Severity.ToString().ToUpperInvariant()) { Tag = f };
                item.UseItemStyleForSubItems = false;
                item.SubItems[0].ForeColor = Theme.For(f.Severity);
                item.SubItems.Add(f.Area).ForeColor = Theme.TextDim;
                item.SubItems.Add(f.Title).ForeColor = Theme.Text;
                _lvAudit.Items.Add(item);
            }
            _lvAudit.EndUpdate();
            AutoFitLastColumn(_lvAudit);

            int bad = _audit.Count(x => x.Severity >= Sev.High);
            _lblStatus.Text = bad > 0
                ? $"Checkup: {bad} high-severity finding(s) — see the Checkup tab before you even play."
                : "Checkup: nothing high-severity in your configuration.";
            if (_lvAudit.Items.Count > 0) { _lvAudit.Items[0].Selected = true; ShowAuditDetail(); }
        }
        catch (Exception ex) { _lblStatus.Text = "Checkup failed: " + ex.Message; }
    }

    // ------------------------------------------------------------------ detail panes

    private void ShowAuditDetail()
    {
        if (_lvAudit.SelectedItems.Count == 0) return;
        var f = (Finding)_lvAudit.SelectedItems[0].Tag;

        _txtAudit.Clear();
        Append(_txtAudit, f.Title + "\n", new Font("Segoe UI Semibold", 12f), Theme.For(f.Severity));
        Append(_txtAudit, $"{f.Severity.ToString().ToUpperInvariant()}  ·  {f.Area}\n\n",
            new Font("Segoe UI", 8.5f), Theme.TextDim);
        Append(_txtAudit, f.Detail + "\n\n", new Font("Segoe UI", 9.5f), Theme.Text);
        if (!string.IsNullOrWhiteSpace(f.Fix))
        {
            Append(_txtAudit, "How to fix\n", new Font("Segoe UI Semibold", 10f), Theme.Good);
            Append(_txtAudit, f.Fix + "\n", new Font("Segoe UI", 9.5f), Theme.Text);
        }
        _txtAudit.SelectionStart = 0;
        _txtAudit.ScrollToCaret();
    }

    private void ShowEventDetail()
    {
        if (_lvEvents.SelectedItems.Count == 0) return;
        var ev = (StutterEvent)_lvEvents.SelectedItems[0].Tag;

        _txtEvent.Clear();
        Append(_txtEvent, $"Stutter #{ev.Id} — {ev.DurationMs:F1} ms  ({ev.Severity})\n",
            new Font("Segoe UI Semibold", 12f), Theme.ForSeverity(ev.Severity));
        Append(_txtEvent, $"at {ev.T:F2}s into the session · {ev.Wall:HH:mm:ss.fff} · detected via {ev.Kind}" +
                          $"{(ev.GameRunning ? " · Valorant running" : " · Valorant not running")}\n\n",
            new Font("Segoe UI", 8.5f), Theme.TextDim);

        int rank = 1;
        foreach (var s in ev.Suspects)
        {
            var c = s.Score >= 80 ? Theme.Bad : s.Score >= 60 ? Theme.Warn : Theme.TextDim;
            Append(_txtEvent, $"{rank++}.  {s.Name}   [{s.Score}% confidence]\n",
                new Font("Segoe UI Semibold", 10.5f), c);
            Append(_txtEvent, s.Why + "\n", new Font("Segoe UI", 9.5f), Theme.Text);
            if (!string.IsNullOrWhiteSpace(s.Fix))
                Append(_txtEvent, "Fix: " + s.Fix + "\n", new Font("Segoe UI", 9.5f), Theme.Good);
            Append(_txtEvent, "\n", new Font("Segoe UI", 9.5f), Theme.Text);
        }

        if (ev.Evidence.Count > 0)
        {
            Append(_txtEvent, "Measurements in the window\n", new Font("Segoe UI Semibold", 10f), Theme.Accent);
            foreach (var line in ev.Evidence)
                Append(_txtEvent, "  • " + line + "\n", new Font("Consolas", 9f), Theme.TextDim);
        }
        _txtEvent.SelectionStart = 0;
        _txtEvent.ScrollToCaret();
    }

    private static void Append(RichTextBox box, string text, Font font, Color color)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionFont = font;
        box.SelectionColor = color;
        box.AppendText(text);
    }

    // ------------------------------------------------------------------ tick

    private void OnTick()
    {
        _eng.Tick();
        _tick++;

        var st = _eng.Store;

        // Keep a running census of what is actually up while the user plays, every ~10 s.
        if (_eng.Running && _tick % 40 == 0)
        {
            try
            {
                foreach (var p in Native.SnapshotProcesses())
                {
                    var n = Path.GetFileNameWithoutExtension(p.Name ?? "");
                    if (n.Length > 0) st.ProcessesSeen.Add(n);
                }
            }
            catch { /* a census miss is not worth interrupting the session for */ }
        }
        bool gaming = st.ValorantForeground;

        if (_eng.Running)
        {
            var ts = TimeSpan.FromSeconds(st.SessionSeconds);
            _lblSession.Text = $"recording  {ts:hh\\:mm\\:ss}   ·   {st.FramesSeen:N0} frames   ·   " +
                               $"{st.FrametimeStatus}   ·   {st.GpuStatus}";
            _lblSession.ForeColor = Theme.Good;
        }
        else
        {
            _lblSession.Text = "idle — press Start session";
            _lblSession.ForeColor = Theme.TextDim;
        }

        AppendNewEvents();

        // Once a second: derived frame statistics (cheap, bounded window).
        if (_tick % 4 == 0 && _eng.Running) RecomputeFrameStats();

        // Repaint the graph only when it can actually be seen. While Valorant is fullscreen there
        // is nothing to draw to, and GDI+ work would be pure waste on the same GPU the game needs.
        bool visible = !gaming && WindowState != FormWindowState.Minimized && Visible;
        if (visible && _eng.Running)
        {
            double now = st.Now();
            _graph.RefreshHz = st.RefreshHz;
            _graph.SetData(
                st.Frames.WindowByTime(f => f.T, now - _graph.WindowSeconds, now),
                st.Stalls.WindowByTime(s => s.T, now - _graph.WindowSeconds, now),
                st.Counters.WindowByTime(c => c.T, now - _graph.WindowSeconds, now),
                now,
                st.FrametimeCaptureActive);
        }

        if (visible || _tick % 8 == 0) UpdateTiles();
    }

    private void RecomputeFrameStats()
    {
        var st = _eng.Store;
        double now = st.Now();
        var recent = st.Frames.WindowByTime(f => f.T, now - 10, now);
        if (recent.Length < 30) { _fpsNow = _fps1Low = _worstFrame10s = 0; return; }

        var ms = recent.Select(f => (double)f.FrameMs).OrderBy(x => x).ToArray();
        _fpsNow = 1000.0 / ms.Average();
        _fps1Low = 1000.0 / ms[Math.Clamp((int)(0.99 * (ms.Length - 1)), 0, ms.Length - 1)];
        _worstFrame10s = ms[^1];
    }

    private void UpdateTiles()
    {
        var st = _eng.Store;
        var events = st.EventsSnapshot();
        var shown = _chkOnlyInGame.Checked ? events.Where(e => e.GameRunning).ToArray() : events;

        int freeze = shown.Count(e => e.Severity == "Freeze");
        int major = shown.Count(e => e.Severity == "Major");
        Set("Stutters", shown.Length.ToString(), shown.Length > 0 ? Theme.Warn : Theme.Good,
            $"{freeze} freeze · {major} major");

        Set("Frame rate", _fpsNow > 0 ? $"{_fpsNow:F0} fps" : "—",
            _fpsNow > 0 ? Theme.Text : Theme.TextDim,
            _fps1Low > 0 ? $"1% low {_fps1Low:F0} fps" : "needs frametime capture");

        Set("Worst frame (10s)", _worstFrame10s > 0 ? $"{_worstFrame10s:F1} ms" : "—",
            _worstFrame10s > 33 ? Theme.Bad : _worstFrame10s > 16 ? Theme.Warn : Theme.Text,
            st.RefreshHz > 0 ? $"refresh = {1000.0 / st.RefreshHz:F2} ms" : "");

        double res = _eng.Stall.TimerResolutionMs;
        bool badTimer = !double.IsNaN(res) && res > 2.0;
        Set("Worst stall", _eng.Stall.WorstMs > 0 ? $"{_eng.Stall.WorstMs:F1} ms" : "—",
            badTimer ? Theme.TextDim
                     : _eng.Stall.WorstMs > 30 ? Theme.Bad : _eng.Stall.WorstMs > 10 ? Theme.Warn : Theme.Good,
            badTimer ? $"UNRELIABLE — timer {res:F1} ms"
                     : $"base {_eng.Stall.BaselineMs:F2} · timer {res:F2} ms");

        var c = st.Counters.Tail(1);
        if (c.Length > 0)
        {
            Set("CPU", $"{c[0].CpuPct:F0}%", c[0].CpuPct > 80 ? Theme.Warn : Theme.Text,
                c[0].MaxCoreIdx >= 0 ? $"busiest core {c[0].MaxCorePct:F0}% (#{c[0].MaxCoreIdx})" : "");
            Set("DPC time", $"{c[0].DpcPct:F2}%",
                c[0].DpcPct > 5 ? Theme.Bad : c[0].DpcPct > 2 ? Theme.Warn : Theme.Good,
                $"ISR {c[0].IsrPct:F2}%  ·  {c[0].InterruptsSec / 1000:F0}k int/s");
            Set("RAM free", $"{c[0].AvailMB / 1024:F1} GB",
                c[0].AvailMB < 2048 ? Theme.Bad : c[0].AvailMB < 4096 ? Theme.Warn : Theme.Text,
                $"hard faults {c[0].PagesInSec:F0}/s");
        }

        var g = st.Gpu.Tail(1);
        if (g.Length > 0)
        {
            string thr = GpuSampler.DescribeThrottle(g[0].ThrottleMask);
            Set("GPU", $"{g[0].UtilPct:F0}%", thr != null ? Theme.Bad : Theme.Text,
                thr ?? $"{g[0].PowerW:F0} W · {g[0].TempC:F0} °C");
            Set("GPU clock", $"{g[0].SmClockMhz:F0} MHz", Theme.Text, $"P-state P{g[0].PState}");
        }
        else
        {
            Set("GPU", "—", Theme.TextDim, st.GpuStatus);
            Set("GPU clock", "—", Theme.TextDim, "");
        }

        Set("Present mode", st.FrametimeCaptureActive ? Shorten(_eng.Frames.CurrentPresentMode) : "—",
            st.FrametimeCaptureActive && _eng.Frames.CurrentPresentMode.Contains("Composed") ? Theme.Warn : Theme.Text,
            st.FrametimeCaptureActive ? "" : "needs admin + PresentMon");

        Set("Monitor overhead", $"{st.SelfCpuPct:F2}%",
            st.SelfCpuPct > 2 ? Theme.Warn : Theme.Good,
            _chkLowOverhead.Checked ? "low-overhead mode" : $"peak {st.SelfPeakCpuPct:F2}% of total CPU");

        Set("Valorant",
            st.ValorantForeground ? "in focus" : st.ValorantRunning ? "running" : "not running",
            st.ValorantRunning ? Theme.Good : Theme.TextDim,
            st.ValorantForeground ? "UI paused to stay out of the way" : "");
    }

    private static string Shorten(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || mode == "-") return "—";
        return mode.Replace("Hardware Composed: ", "HW Comp ")
                   .Replace("Hardware: ", "HW ")
                   .Replace("Independent Flip", "Indep Flip");
    }

    private void Set(string tile, string value, Color color, string sub)
    {
        if (_tiles.TryGetValue(tile, out var t)) t.Set(value, color, sub);
    }

    private void AppendNewEvents()
    {
        var events = _eng.Store.EventsSnapshot();
        if (events.Length <= _shownEvents) return;

        _lvEvents.BeginUpdate();
        for (int i = _shownEvents; i < events.Length; i++)
        {
            var e = events[i];
            if (_chkOnlyInGame.Checked && !e.GameRunning && e.Kind != StutterKind.UserMarked) continue;

            var top = e.Suspects.FirstOrDefault();
            var item = new ListViewItem(e.Id.ToString()) { Tag = e, UseItemStyleForSubItems = false };
            item.SubItems[0].ForeColor = Theme.TextDim;
            item.SubItems.Add($"{e.T:F2}s").ForeColor = Theme.TextDim;
            item.SubItems.Add(e.Kind.ToString()).ForeColor = Theme.TextDim;
            item.SubItems.Add($"{e.DurationMs:F1} ms").ForeColor = Theme.Text;
            item.SubItems.Add(e.Severity).ForeColor = Theme.ForSeverity(e.Severity);
            item.SubItems.Add(top != null ? $"{top.Score}%" : "").ForeColor = Theme.TextDim;
            item.SubItems.Add(top?.Name ?? "—").ForeColor =
                (top?.Score ?? 0) >= 70 ? Theme.Bad : (top?.Score ?? 0) >= 50 ? Theme.Warn : Theme.TextDim;
            _lvEvents.Items.Add(item);
        }
        _shownEvents = events.Length;
        _lvEvents.EndUpdate();
        AutoFitLastColumn(_lvEvents);

        if (_lvEvents.Items.Count > 0 && _lvEvents.SelectedItems.Count == 0)
        {
            _lvEvents.Items[^1].Selected = true;
            ShowEventDetail();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _eng.Dispose();
        base.OnFormClosing(e);
    }
}

// -------------------------------------------------------------------------------------------

/// <summary>A single labelled readout on the Live tab.</summary>
public sealed class Tile : Panel
{
    private readonly Label _value, _sub;

    public Tile(string title)
    {
        Size = new Size(188, 82);
        Margin = new Padding(0, 0, 10, 10);
        BackColor = Theme.Panel;
        Padding = new Padding(12, 8, 8, 8);

        var t = new Label
        {
            Text = title.ToUpperInvariant(),
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = Theme.TextDim,
            AutoSize = true,
            Location = new Point(12, 8),
        };
        // 15 pt in a 28 px box clipped descenders ("not runnin_g_"); 14 pt in 32 px clears them.
        _value = new Label
        {
            Text = "—",
            Font = new Font("Segoe UI Semibold", 14f),
            ForeColor = Theme.Text,
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(10, 23),
            Size = new Size(174, 32),
        };
        _sub = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = Theme.TextDim,
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(12, 58),
            Size = new Size(172, 18),
        };
        Controls.AddRange(new Control[] { t, _value, _sub });
    }

    public void Set(string value, Color color, string sub)
    {
        if (_value.Text != value) _value.Text = value;
        if (_value.ForeColor != color) _value.ForeColor = color;
        if (_sub.Text != (sub ?? "")) _sub.Text = sub ?? "";
    }
}
