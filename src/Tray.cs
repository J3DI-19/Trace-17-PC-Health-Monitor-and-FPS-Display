using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Trace17 {
    static class Program {
        public const string Name = "Trace17";
        public static readonly string Home = AppDomain.CurrentDomain.BaseDirectory;
        [STAThread]
        static int Main(string[] args) {
            if (!string.Equals(Path.GetPathRoot(Home), @"C:\", StringComparison.OrdinalIgnoreCase)) return 2;
            string mode = args.Length == 0 ? "" : args[0];
            if (mode == "--self-test" || mode == "--verify-live") return Verification.Run(mode);
            if (mode != "" && mode != "--show" && mode != "--simulate") return 2;
            bool simulation = mode == "--simulate";
            using (var mutex = new Mutex(false, simulation ? @"Local\Trace17Simulation" : @"Local\Trace17Monitor")) {
                try {
                    if (!mutex.WaitOne(0)) return 0;
                } catch (AbandonedMutexException) { }
                try {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var app = new MonitorApp(simulation)) {
                        if (mode != "") app.ShowPanel();
                        Application.Run(app);
                    }
                    return 0;
                } finally { mutex.ReleaseMutex(); }
            }
        }
    }

    sealed class Journal {
        public readonly string FilePath;
        public string Failure { get; private set; }
        public Journal(bool simulation) {
            FilePath = Path.Combine(Program.Home, "logs", simulation ? "simulation.log" : "trace17.log");
        }
        public void Add(string message) { Add(new[] { message }); }
        public void Add(IEnumerable<string> messages) {
            string stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            string[] lines = messages.Select(message => stamp + "  " + message.Replace('\r', ' ').Replace('\n', ' ')).ToArray();
            if (lines.Length == 0) return;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= 2 * 1024 * 1024) {
                    string backup = FilePath + ".previous";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(FilePath, backup);
                }
                File.AppendAllLines(FilePath, lines);
                Failure = null;
            } catch (Exception error) {
                Failure = "Log write failed (" + error.GetType().Name + ").";
            }
        }
    }

    sealed class MonitorApp : ApplicationContext {
        readonly bool simulation;
        readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        readonly Journal log;
        readonly SystemLog source;
        readonly PciDeviceResolver pciResolver = new PciDeviceResolver();
        readonly TelemetryCollector telemetry;
        readonly Dictionary<string, Burst> bursts = new Dictionary<string, Burst>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> deviceCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
        readonly TrayIcon tray;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem fpsMenuItem;
        readonly ToolStripMenuItem muteMenuItem;
        readonly string muteFile = Path.Combine(Program.Home, "sound-muted.flag");
        PanelWindow panel;
        FpsOverlayForm fpsOverlay;
        TelemetrySnapshot latestTelemetry = new TelemetrySnapshot();
        Task<Reading> pending;
        Task<TelemetrySnapshot> pendingTelemetry;
        long count;
        string pcIeStatus = "Connecting to System log…";
        string latestEvent = "No WHEA 17 events this session.";
        string collectionIssue;
        bool coverageGap;
        bool catchupAlert;
        DateTimeOffset lastSuccess, nextPoll, nextTelemetryPoll;
        DateTimeOffset simulationStart;
        int simulationStep;
        bool muted;
        bool fpsEnabled;

        public MonitorApp(bool simulation) {
            this.simulation = simulation;
            log = new Journal(simulation);
            muted = !simulation && File.Exists(muteFile);
            if (!simulation) {
                telemetry = new TelemetryCollector();
                Task.Run(() => pciResolver.EnsureLoaded());
            }
            tray = new TrayIcon(ShowPanel, ShowMenu, simulation
                ? (Action)(() => log.Add("Windows reported that the test notification was shown."))
                : null);
            menu.Items.Add("Open Trace17", null, (s, e) => ShowPanel());
            fpsMenuItem = new ToolStripMenuItem("FPS Overlay");
            fpsMenuItem.Click += (s, e) => ToggleFps();
            menu.Items.Add(fpsMenuItem);
            muteMenuItem = new ToolStripMenuItem("Mute sounds");
            muteMenuItem.Click += (s, e) => ToggleMute();
            menu.Items.Add(muteMenuItem);
            menu.Items.Add("View logs", null, (s, e) => ViewLogs());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitThread());
            timer.Tick += (s, e) => Tick();
            simulationStart = DateTimeOffset.UtcNow;
            if (!simulation) source = new SystemLog();
            log.Add(simulation ? "SIMULATION session. No Windows event records will be written."
                : "Trace17 started. Monitoring all WHEA 17 PCIe/storage devices and live system telemetry.");
            SyncMenuStates();
            Tick();
            timer.Start();
        }

        void Tick() {
            var now = DateTimeOffset.UtcNow;
            if (simulation) {
                Simulate(now);
            } else {
                if (pending != null && pending.IsCompleted) {
                    Apply(pending.Result, now);
                    pending = null;
                }
                if (now >= nextPoll && pending == null) {
                    pending = Task.Run(() => source.Poll());
                    nextPoll = now.AddSeconds(3);
                }
                var checkedAt = lastSuccess == default(DateTimeOffset) ? started : lastSuccess;
                if (now - checkedAt > TimeSpan.FromSeconds(7))
                    CollectionFailure("No successful System log read for more than 7 seconds.");

                if (pendingTelemetry != null && pendingTelemetry.IsCompleted) {
                    if (!pendingTelemetry.IsFaulted && pendingTelemetry.Result != null)
                        latestTelemetry = pendingTelemetry.Result;
                    pendingTelemetry = null;
                }
                bool needTelemetry = fpsEnabled || (panel != null && !panel.IsDisposed && panel.Visible);
                if (needTelemetry && now >= nextTelemetryPoll && pendingTelemetry == null) {
                    telemetry.FpsOverlayEnabled = fpsEnabled;
                    pendingTelemetry = Task.Run(() => telemetry.Sample());
                    nextTelemetryPoll = now.AddMilliseconds(fpsEnabled ? 500 : 1000);
                }
            }
            UpdatePanel();
            UpdateOverlay();
        }

        void Apply(Reading result, DateTimeOffset now) {
            if (result.Failure != null) {
                CollectionFailure(result.Failure);
                return;
            }
            if (collectionIssue != null) log.Add("System log collection resumed.");
            if (result.Initialized) log.Add("System log connected. Historical warnings excluded.");
            collectionIssue = null;
            lastSuccess = now;
            if (result.Gap != null) {
                coverageGap = true;
                log.Add("COVERAGE GAP  " + result.Gap);
                Warn("System log coverage gap", result.Gap, true);
            }
            pcIeStatus = coverageGap ? "All PCIe & drives · earlier coverage gap" : "Monitoring all PCIe & storage devices";
            if (result.Events.Count == 0) return;

            var entries = new List<string>();
            var touchedDevices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            WheaEvent worstEvent = result.Events[result.Events.Count - 1];
            string worstLabel = simulation ? worstEvent.FriendlyDevice : pciResolver.Resolve(worstEvent);
            int worstSeverity = 0;
            int worstBurstCount = 0;

            foreach (var item in result.Events) {
                count++;
                string label = simulation ? item.FriendlyDevice : pciResolver.Resolve(item);
                long devTotal;
                deviceCounts.TryGetValue(label, out devTotal);
                deviceCounts[label] = devTotal + 1;

                Burst deviceBurst;
                if (!bursts.TryGetValue(item.DeviceKey, out deviceBurst)) {
                    deviceBurst = new Burst();
                    bursts[item.DeviceKey] = deviceBurst;
                }
                int sev = deviceBurst.Add(item.Time, now);
                if (sev >= worstSeverity) {
                    worstSeverity = sev;
                    worstEvent = item;
                    worstLabel = label;
                    worstBurstCount = deviceBurst.Count;
                }

                int prevBatch;
                touchedDevices.TryGetValue(label, out prevBatch);
                touchedDevices[label] = prevBatch + 1;

                entries.Add((simulation ? "SIMULATED " : "") + "WHEA 17  #" + count + "  [" + label + "]  record "
                    + item.Id + "  corr=" + (item.CorrectableStatus.Length > 0 ? item.CorrectableStatus : "n/a")
                    + "  uncorr=" + (item.UncorrectableStatus.Length > 0 ? item.UncorrectableStatus : "n/a")
                    + "  event " + FormatTime(item.Time));
            }

            if (worstSeverity == 2) {
                Warn("WHEA 17 burst · " + worstLabel,
                    worstBurstCount + " in 60s · " + count + " total this session.\nLatest " + FormatTime(worstEvent.Time), true);
                entries.Add("ESCALATED  " + worstBurstCount + " WHEA 17 errors in 60s on [" + worstLabel + "].");
            } else if (worstSeverity == 1 || catchupAlert) {
                Warn("WHEA 17 warning · " + worstLabel,
                    result.Events.Count + " newly collected · " + count + " total this session.\nLatest " + FormatTime(worstEvent.Time), false);
            }
            latestEvent = (worstSeverity == 2 ? "Burst · " : "WHEA 17 · ") + worstLabel + " · " + FormatTime(worstEvent.Time);
            catchupAlert = false;
            log.Add(entries);
        }

        void CollectionFailure(string message) {
            pcIeStatus = "Collection interrupted · retrying";
            if (collectionIssue != null) return;
            collectionIssue = message;
            catchupAlert = true;
            log.Add("COLLECTION INTERRUPTED  " + message);
            Warn("Trace17 monitoring interrupted", "System log collection is unavailable. Retrying automatically.", true);
        }

        static string FormatTime(DateTimeOffset value) { return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"); }

        void Warn(string title, string message, bool critical) {
            latestEvent = title + " · " + FormatTime(DateTimeOffset.Now);
            bool submitted = tray.ShowNotification(simulation ? "[Test] " + title : title, message, critical);
            bool sound = !muted && TrayIcon.AlertSound();
            if (simulation || !submitted || (!muted && !sound))
                log.Add("Notification shell accepted=" + submitted + "  sound " + (muted ? "muted" : "accepted=" + sound) + "  " + title);
        }

        void Simulate(DateTimeOffset now) {
            double age = (now - simulationStart).TotalSeconds;
            if (simulationStep == 0) {
                pcIeStatus = "Simulation · waiting for sample";
                simulationStep++;
            } else if (simulationStep == 1 && age >= 2) {
                var reading = new Reading();
                var ev = Verification.Sample(now, 1);
                ev.FriendlyDevice = "D: PCIe Root (0:6:1)";
                reading.Events.Add(ev);
                Apply(reading, now);
                simulationStep++;
            } else if (simulationStep == 2 && age >= 9) {
                var reading = new Reading();
                for (int i = 2; i <= 10; i++) {
                    var ev = Verification.Sample(now, i);
                    ev.FriendlyDevice = "C: NVMe (1:0:0)";
                    ev.DeviceKey = "1:0:0/15B7:5045";
                    reading.Events.Add(ev);
                }
                Apply(reading, now);
                simulationStep++;
            }
            if (simulationStep >= 2) pcIeStatus = "Simulation · all-device sample events";
            latestTelemetry = BuildSimulatedTelemetry(age);
        }

        TelemetrySnapshot BuildSimulatedTelemetry(double age) {
            double wave = Math.Sin(age * 0.8);
            var snap = new TelemetrySnapshot {
                CpuName = "Core Ultra 7 270K Plus",
                CpuUsagePercent = 24.0 + wave * 8.0,
                CpuTempC = 54.0 + wave * 3.0,
                CpuTempSource = "Simulation",
                GpuName = "Radeon RX 9070 XT",
                GpuUsagePercent = 68.0 + wave * 12.0,
                GpuTempC = 61.0 + wave * 2.5,
                RamUsagePercent = 47.0,
                RamUsedGb = 22.5,
                RamTotalGb = 48.0,
                ActiveGameName = fpsEnabled ? "SimulatedGame" : null,
                Fps = fpsEnabled ? (double?)(142.0 + wave * 6.0) : null,
                FrameTimeMs = fpsEnabled ? (double?)(7.0 - wave * 0.3) : null,
                PresentMonConnected = true
            };
            snap.Drives.Add(new DriveTelemetry { Name = "C:", TempC = 38, WarningTempC = 90, ActivePercent = 3.0, UsedSpacePercent = 44.0, FreeGb = 520, TotalGb = 931 });
            snap.Drives.Add(new DriveTelemetry { Name = "D:", TempC = 35, WarningTempC = 90, ActivePercent = 1.0, UsedSpacePercent = 31.0, FreeGb = 640, TotalGb = 931 });
            return snap;
        }

        string BuildDeviceSummary() {
            if (count == 0) return "0 errors across all PCIe & storage devices";
            var parts = deviceCounts.OrderByDescending(k => k.Value).Take(3)
                .Select(k => k.Key + " (" + k.Value + ")").ToArray();
            return count + " error" + (count == 1 ? "" : "s") + ": " + string.Join(" · ", parts);
        }

        public void ShowPanel() {
            if (panel == null || panel.IsDisposed) panel = new PanelWindow(simulation, ViewLogs, ToggleMute, ToggleFps);
            if (!simulation && pendingTelemetry == null) {
                telemetry.FpsOverlayEnabled = fpsEnabled;
                pendingTelemetry = Task.Run(() => telemetry.Sample());
                nextTelemetryPoll = DateTimeOffset.UtcNow.AddMilliseconds(500);
            }
            panel.Show();
            UpdatePanel();
            panel.Activate();
        }

        void UpdatePanel() {
            if (panel == null || panel.IsDisposed || !panel.Visible) return;
            panel.RefreshValues(pcIeStatus, BuildDeviceSummary(), log.Failure ?? latestEvent,
                count, coverageGap || collectionIssue != null, muted, fpsEnabled, latestTelemetry);
        }

        void UpdateOverlay() {
            if (!fpsEnabled) {
                if (fpsOverlay != null && !fpsOverlay.IsDisposed && fpsOverlay.Visible)
                    fpsOverlay.Hide();
                return;
            }
            if (fpsOverlay == null || fpsOverlay.IsDisposed)
                fpsOverlay = new FpsOverlayForm();
            fpsOverlay.RefreshOverlay(latestTelemetry, simulation);
            if (!fpsOverlay.Visible) fpsOverlay.Show();
        }

        void ShowMenu() {
            SyncMenuStates();
            tray.Foreground();
            menu.Show(Cursor.Position);
        }

        void SyncMenuStates() {
            fpsMenuItem.Checked = fpsEnabled;
            muteMenuItem.Checked = muted;
        }

        void ToggleFps() {
            fpsEnabled = !fpsEnabled;
            if (!simulation && telemetry != null) {
                telemetry.FpsOverlayEnabled = fpsEnabled;
                if (!fpsEnabled) telemetry.Sample();
            }
            SyncMenuStates();
            UpdateOverlay();
            UpdatePanel();
        }

        void ToggleMute() {
            try {
                if (!simulation) {
                    if (muted) File.Delete(muteFile);
                    else File.WriteAllText(muteFile, "Muted");
                }
                muted = !muted;
            } catch (Exception error) {
                latestEvent = "Sound setting could not be saved (" + error.GetType().Name + ").";
            }
            SyncMenuStates();
            UpdatePanel();
        }

        void ViewLogs() {
            try {
                if (!File.Exists(log.FilePath)) throw new IOException("No log file yet.");
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + log.FilePath + "\"") {
                    UseShellExecute = false
                });
            } catch (Exception error) {
                latestEvent = "Could not open log (" + error.GetType().Name + ").";
                UpdatePanel();
            }
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                timer.Dispose();
                if (fpsOverlay != null) fpsOverlay.Dispose();
                if (panel != null) panel.Dispose();
                if (telemetry != null) telemetry.Dispose();
                menu.Dispose();
                tray.Dispose();
                log.Add(simulation ? "SIMULATION ended." : "Trace17 session ended.");
            }
            base.Dispose(disposing);
        }
    }

    static class DrawHelper {
        public static GraphicsPath RoundedRect(Rectangle bounds, int radius) {
            int d = radius * 2;
            var path = new GraphicsPath();
            if (radius <= 0) {
                path.AddRectangle(bounds);
                return path;
            }
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    sealed class RoundedCard : Panel {
        readonly int radius;
        Color borderColor = Color.FromArgb(42, 52, 68);
        Color fillColor = Color.FromArgb(24, 30, 39);

        public RoundedCard(int radius = 10) {
            this.radius = radius;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(17, 21, 28);
        }

        public void SetColors(Color fill, Color border) {
            if (fill == fillColor && border == borderColor) return;
            fillColor = fill;
            borderColor = border;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = DrawHelper.RoundedRect(rect, radius))
            using (var brush = new SolidBrush(fillColor))
            using (var pen = new Pen(borderColor)) {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
            base.OnPaint(e);
        }
    }

    sealed class MetricBar : Control {
        double percent;
        Color fillColor = Color.FromArgb(79, 227, 247);

        public MetricBar() {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Size = new Size(148, 7);
            BackColor = Color.FromArgb(24, 30, 39);
        }

        public void SetValue(double value, Color color) {
            double clamped = Math.Max(0.0, Math.Min(100.0, value));
            if (Math.Abs(clamped - percent) < 0.1 && color == fillColor) return;
            percent = clamped;
            fillColor = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var trackRect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var trackPath = DrawHelper.RoundedRect(trackRect, Height / 2))
            using (var trackBrush = new SolidBrush(Color.FromArgb(37, 45, 58)))
                g.FillPath(trackBrush, trackPath);

            int w = (int)Math.Round((Width - 1) * (percent / 100.0));
            if (w >= 4) {
                var fillRect = new Rectangle(0, 0, w, Height - 1);
                using (var fillPath = DrawHelper.RoundedRect(fillRect, Height / 2))
                using (var grad = new LinearGradientBrush(fillRect,
                    Color.FromArgb(Math.Max(0, fillColor.R - 25), Math.Max(0, fillColor.G - 20), Math.Max(0, fillColor.B - 10)),
                    fillColor, LinearGradientMode.Horizontal)) {
                    g.FillPath(grad, fillPath);
                }
            } else if (w > 0) {
                using (var fill = new SolidBrush(fillColor))
                    g.FillRectangle(fill, 0, 1, w, Height - 2);
            }
        }
    }

    sealed class PillButton : Control {
        readonly Action onClick;
        bool hovered;
        bool pressed;
        Color bg = Color.FromArgb(28, 36, 48);
        Color hoverBg = Color.FromArgb(38, 50, 66);
        Color border = Color.FromArgb(56, 72, 94);
        Color fg = Color.FromArgb(224, 234, 244);

        public PillButton(string text, Action onClick) {
            this.onClick = onClick;
            Text = text;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", 8.75f);
            BackColor = Color.FromArgb(17, 21, 28);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        public void SetTheme(Color background, Color hoverBackground, Color borderColor, Color textColor) {
            if (bg == background && hoverBg == hoverBackground && border == borderColor && fg == textColor) return;
            bg = background;
            hoverBg = hoverBackground;
            border = borderColor;
            fg = textColor;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) {
            bool fire = pressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
            pressed = false;
            Invalidate();
            if (fire && onClick != null) onClick();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = pressed ? Color.FromArgb(Math.Max(0, bg.R - 6), Math.Max(0, bg.G - 6), Math.Max(0, bg.B - 6))
                : (hovered ? hoverBg : bg);
            using (var path = DrawHelper.RoundedRect(rect, 8))
            using (var b = new SolidBrush(fill))
            using (var p = new Pen(hovered ? Color.FromArgb(Math.Min(255, border.R + 30), Math.Min(255, border.G + 30), Math.Min(255, border.B + 30)) : border))
            using (var tb = new SolidBrush(fg)) {
                g.FillPath(b, path);
                g.DrawPath(p, path);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(Text, Font, tb, new RectangleF(0, 0, Width, Height), sf);
            }
        }
    }

    sealed class BrandHeader : Control {
        readonly bool simulation;
        public BrandHeader(bool simulation) {
            this.simulation = simulation;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Color.FromArgb(17, 21, 28);
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var traceFont = new Font("Segoe UI Semibold", 13.5f))
            using (var numFont = new Font("Segoe UI Bold", 13.5f))
            using (var simFont = new Font("Segoe UI Semibold", 8.5f))
            using (var lightBrush = new SolidBrush(Color.FromArgb(232, 238, 246)))
            using (var cyanBrush = new SolidBrush(Color.FromArgb(79, 227, 247)))
            using (var mutedBrush = new SolidBrush(Color.FromArgb(145, 160, 180))) {
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                Size traceSize = TextRenderer.MeasureText(g, "TRACE", traceFont, Size.Empty, flags);
                Size numSize = TextRenderer.MeasureText(g, "17", numFont, Size.Empty, flags);
                TextRenderer.DrawText(g, "TRACE", traceFont, new Point(0, 1), Color.FromArgb(232, 238, 246), flags);
                TextRenderer.DrawText(g, "17", numFont, new Point(traceSize.Width + 1, 1), Color.FromArgb(79, 227, 247), flags);
                if (simulation) {
                    TextRenderer.DrawText(g, "SIMULATION", simFont,
                        new Point(traceSize.Width + numSize.Width + 10, 6), Color.FromArgb(245, 188, 78), flags);
                }
            }
        }
    }

    sealed class PanelWindow : Form {
        static readonly Color Obsidian = Color.FromArgb(17, 21, 28);
        static readonly Color CardFill = Color.FromArgb(24, 30, 39);
        static readonly Color CardRim = Color.FromArgb(42, 52, 68);
        static readonly Color Cyan = Color.FromArgb(79, 227, 247);
        static readonly Color Emerald = Color.FromArgb(88, 224, 158);
        static readonly Color Amber = Color.FromArgb(245, 188, 78);
        static readonly Color Coral = Color.FromArgb(242, 102, 102);
        static readonly Color Light = Color.FromArgb(232, 238, 246);
        static readonly Color SubText = Color.FromArgb(145, 160, 180);

        sealed class DriveRow {
            public Label Title;
            public MetricBar Bar;
            public Label Stats;
        }

        readonly Label statusPill;
        readonly Label cpuValue, gpuTitle, ramValue;
        readonly Label gpuValue;
        readonly MetricBar cpuBar, gpuBar, ramBar;
        readonly RoundedCard drivesCard, wheaCard;
        readonly List<DriveRow> driveRows = new List<DriveRow>();
        readonly Label wheaHeader, wheaStatusLabel, wheaCountLabel, wheaLatestLabel;
        readonly PillButton fpsButton, logsButton, muteButton;
        readonly ToolTip tips = new ToolTip();

        public PanelWindow(bool simulation, Action openLog, Action toggleMute, Action toggleFps) {
            Text = Program.Name + (simulation ? " — Simulation" : "");
            Font = new Font("Segoe UI", 9);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Obsidian;
            ForeColor = Light;
            ClientSize = new Size(492, 468);

            var brand = new BrandHeader(simulation) { Location = new Point(20, 13), Size = new Size(260, 26) };
            Controls.Add(brand);

            statusPill = new Label {
                Text = "● LIVE",
                Location = new Point(290, 17),
                Size = new Size(182, 20),
                ForeColor = Cyan,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI Semibold", 8.5f)
            };
            Controls.Add(statusPill);

            AddSectionHeader("SYSTEM TELEMETRY", 48);
            var sysCard = new RoundedCard(10) { Location = new Point(20, 68), Size = new Size(452, 134) };
            Controls.Add(sysCard);

            // CPU
            AddCardLabel(sysCard, "CPU", 16, 14, 122, Light, true);
            cpuBar = AddCardBar(sysCard, 146, 21, 134);
            cpuValue = AddCardLabel(sysCard, "—", 288, 14, 148, Light, true, ContentAlignment.MiddleRight);

            // GPU
            gpuTitle = AddCardLabel(sysCard, "GPU", 16, 51, 122, Light, true);
            gpuBar = AddCardBar(sysCard, 146, 58, 134);
            gpuValue = AddCardLabel(sysCard, "—", 288, 51, 148, Light, true, ContentAlignment.MiddleRight);

            // RAM
            AddCardLabel(sysCard, "RAM", 16, 94, 122, Light, true);
            ramBar = AddCardBar(sysCard, 146, 101, 134);
            ramValue = AddCardLabel(sysCard, "—", 288, 94, 148, Light, true, ContentAlignment.MiddleRight);

            AddSectionHeader("STORAGE · NVMe & SSDs", 214);
            drivesCard = new RoundedCard(10) { Location = new Point(20, 234), Size = new Size(452, 82) };
            Controls.Add(drivesCard);

            wheaHeader = AddSectionHeader("WHEA EVENT 17 · ALL PCIe & DRIVES", 328);
            wheaCard = new RoundedCard(10) { Location = new Point(20, 348), Size = new Size(452, 76) };
            Controls.Add(wheaCard);
            wheaStatusLabel = AddCardLabel(wheaCard, "", 16, 10, 420, Light, true);
            wheaCountLabel = AddCardLabel(wheaCard, "", 16, 30, 420, SubText, false);
            wheaLatestLabel = AddCardLabel(wheaCard, "", 16, 49, 420, Light, false);

            fpsButton = new PillButton("FPS Overlay: Off", toggleFps) { Location = new Point(20, 438), Size = new Size(164, 34) };
            logsButton = new PillButton("View logs", openLog) { Location = new Point(196, 438), Size = new Size(132, 34) };
            muteButton = new PillButton("Mute sounds", toggleMute) { Location = new Point(340, 438), Size = new Size(132, 34) };
            Controls.Add(fpsButton);
            Controls.Add(logsButton);
            Controls.Add(muteButton);

            FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        }

        Label AddSectionHeader(string text, int top) {
            var lbl = new Label {
                Text = text,
                Location = new Point(22, top),
                Size = new Size(450, 16),
                ForeColor = SubText,
                Font = new Font("Segoe UI Semibold", 7.75f)
            };
            Controls.Add(lbl);
            return lbl;
        }

        static Label AddCardLabel(Control parent, string text, int left, int top, int width, Color color, bool bold,
            ContentAlignment align = ContentAlignment.MiddleLeft) {
            var lbl = new Label {
                Text = text,
                Location = new Point(left, top),
                Size = new Size(width, 20),
                ForeColor = color,
                BackColor = CardFill,
                TextAlign = align,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 9f, bold ? FontStyle.Bold : FontStyle.Regular)
            };
            parent.Controls.Add(lbl);
            return lbl;
        }

        static MetricBar AddCardBar(Control parent, int left, int top, int width) {
            var bar = new MetricBar { Location = new Point(left, top), Size = new Size(width, 7) };
            parent.Controls.Add(bar);
            return bar;
        }

        void EnsureDriveRows(int count) {
            int rowCount = Math.Max(1, count);
            if (driveRows.Count != rowCount) {
                drivesCard.Controls.Clear();
                driveRows.Clear();
                for (int i = 0; i < rowCount; i++) {
                    int y = 12 + i * 34;
                    var row = new DriveRow {
                        Title = AddCardLabel(drivesCard, "—", 16, y, 122, Light, true),
                        Bar = AddCardBar(drivesCard, 146, y + 7, 134),
                        Stats = AddCardLabel(drivesCard, "—", 288, y, 148, Light, true, ContentAlignment.MiddleRight)
                    };
                    driveRows.Add(row);
                }
                int drivesH = 18 + rowCount * 34;
                drivesCard.Size = new Size(452, drivesH);
                int wheaTop = drivesCard.Bottom + 12;
                wheaHeader.Location = new Point(22, wheaTop);
                wheaCard.Location = new Point(20, wheaTop + 20);
                int btnTop = wheaCard.Bottom + 14;
                fpsButton.Location = new Point(20, btnTop);
                logsButton.Location = new Point(196, btnTop);
                muteButton.Location = new Point(340, btnTop);
                ClientSize = new Size(492, btnTop + 48);
            }
        }

        static Color TempColor(double tempC, double warnC) {
            if (warnC > 0 && tempC >= warnC) return Coral;
            if (tempC >= 85) return Coral;
            if (tempC >= 70) return Amber;
            return Emerald;
        }

        static Color UsageColor(double pct) {
            if (pct >= 90) return Coral;
            if (pct >= 75) return Amber;
            return Cyan;
        }

        public void RefreshValues(string wheaStatus, string wheaSummary, string latest, long total,
            bool wheaIssue, bool muted, bool fpsEnabled, TelemetrySnapshot snap) {
            if (snap == null) snap = new TelemetrySnapshot();

            statusPill.Text = fpsEnabled ? "● FPS HUD ACTIVE" : (snap.PresentMonConnected ? "● TELEMETRY LIVE" : "● SENSOR LIMITED");
            statusPill.ForeColor = fpsEnabled ? Emerald : (snap.PresentMonConnected ? Cyan : Amber);

            // CPU
            double cpuPct = snap.CpuUsagePercent ?? 0.0;
            cpuBar.SetValue(cpuPct, snap.CpuTempC.HasValue ? TempColor(snap.CpuTempC.Value, 85) : UsageColor(cpuPct));
            string cpuTempText = snap.CpuTempC.HasValue ? string.Format(CultureInfo.InvariantCulture, "{0:F0}°C", snap.CpuTempC.Value) : "N/A*";
            cpuValue.Text = snap.CpuUsagePercent.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "{0:F0}%  ·  {1}", cpuPct, cpuTempText)
                : cpuTempText;
            cpuValue.ForeColor = snap.CpuTempC.HasValue ? TempColor(snap.CpuTempC.Value, 85) : Light;
            tips.SetToolTip(cpuValue, snap.CpuTempC.HasValue
                ? "CPU temp source: " + (snap.CpuTempSource ?? "Sensor")
                : "CPU temp on Intel Core Ultra requires HWiNFO64 (Shared Memory or Gadget) or LibreHardwareMonitor running.");

            // GPU
            double gpuPct = snap.GpuUsagePercent ?? 0.0;
            gpuBar.SetValue(gpuPct, snap.GpuTempC.HasValue ? TempColor(snap.GpuTempC.Value, 85) : UsageColor(gpuPct));
            string shortGpu = (snap.GpuName ?? "GPU").Replace("Radeon ", "");
            gpuTitle.Text = shortGpu == "GPU" ? "GPU" : "GPU · " + shortGpu;
            string gpuTempText = snap.GpuTempC.HasValue ? string.Format(CultureInfo.InvariantCulture, "{0:F0}°C", snap.GpuTempC.Value) : "N/A";
            string gpuUseText = snap.GpuUsagePercent.HasValue ? string.Format(CultureInfo.InvariantCulture, "{0:F0}%", gpuPct) : "N/A";
            gpuValue.Text = gpuUseText + "  ·  " + gpuTempText;
            gpuValue.ForeColor = snap.GpuTempC.HasValue ? TempColor(snap.GpuTempC.Value, 85) : Light;
            tips.SetToolTip(gpuTitle, snap.GpuName);

            // RAM
            double ramPct = snap.RamUsagePercent ?? 0.0;
            ramBar.SetValue(ramPct, UsageColor(ramPct));
            ramValue.Text = snap.RamTotalGb > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0:F0}%  ·  {1:F1}/{2:F0} GB", ramPct, snap.RamUsedGb, snap.RamTotalGb)
                : "N/A";
            ramValue.ForeColor = UsageColor(ramPct);

            // SSDs
            EnsureDriveRows(snap.Drives.Count);
            if (snap.Drives.Count == 0) {
                driveRows[0].Title.Text = "Disks";
                driveRows[0].Bar.SetValue(0, SubText);
                driveRows[0].Stats.Text = "Scanning…";
            } else {
                for (int i = 0; i < snap.Drives.Count; i++) {
                    var dr = snap.Drives[i];
                    var row = driveRows[i];
                    double act = dr.ActivePercent ?? 0.0;
                    double warn = dr.WarningTempC ?? 75.0;
                    row.Title.Text = dr.UsedSpacePercent.HasValue
                        ? string.Format(CultureInfo.InvariantCulture, "{0}  ({1:F0}% used)", dr.Name, dr.UsedSpacePercent.Value)
                        : dr.Name;
                    row.Bar.SetValue(act, dr.TempC.HasValue ? TempColor(dr.TempC.Value, warn) : UsageColor(act));
                    string actStr = dr.ActivePercent.HasValue ? string.Format(CultureInfo.InvariantCulture, "{0:F0}% act", act) : "—";
                    string tempStr = dr.TempC.HasValue ? string.Format(CultureInfo.InvariantCulture, "{0:F0}°C", dr.TempC.Value) : "N/A";
                    row.Stats.Text = actStr + "  ·  " + tempStr;
                    row.Stats.ForeColor = dr.TempC.HasValue ? TempColor(dr.TempC.Value, warn) : Light;
                    tips.SetToolTip(row.Title, string.Format(CultureInfo.InvariantCulture, "{0} {1:F0} GB free of {2:F0} GB", dr.Name, dr.FreeGb, dr.TotalGb));
                }
            }

            // WHEA 17 section
            wheaStatusLabel.Text = wheaStatus;
            wheaStatusLabel.ForeColor = wheaIssue ? Amber : (total > 0 ? Coral : Emerald);
            wheaCountLabel.Text = wheaSummary;
            wheaLatestLabel.Text = latest;
            wheaCard.SetColors(CardFill, total > 0 ? Color.FromArgb(120, 62, 66) : (wheaIssue ? Color.FromArgb(120, 96, 48) : CardRim));

            // Buttons
            fpsButton.Text = fpsEnabled ? "FPS Overlay: On" : "FPS Overlay: Off";
            if (fpsEnabled)
                fpsButton.SetTheme(Color.FromArgb(22, 64, 58), Color.FromArgb(28, 82, 74), Emerald, Emerald);
            else
                fpsButton.SetTheme(Color.FromArgb(26, 44, 56), Color.FromArgb(34, 58, 74), Cyan, Cyan);

            logsButton.SetTheme(Color.FromArgb(28, 36, 48), Color.FromArgb(38, 50, 66), CardRim, Light);
            muteButton.Text = muted ? "Unmute sounds" : "Mute sounds";
            if (muted)
                muteButton.SetTheme(Color.FromArgb(52, 42, 28), Color.FromArgb(68, 54, 34), Amber, Amber);
            else
                muteButton.SetTheme(Color.FromArgb(28, 36, 48), Color.FromArgb(38, 50, 66), CardRim, Light);
        }
    }

    sealed class FpsOverlayForm : Form {
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOPMOST = 0x00000008;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        TelemetrySnapshot current = new TelemetrySnapshot();
        bool simulation;

        public FpsOverlayForm() {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(14, 18, 24);
            Opacity = 0.90;
            Size = new Size(356, 58);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams {
            get {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void RefreshOverlay(TelemetrySnapshot snap, bool isSimulation) {
            current = snap ?? new TelemetrySnapshot();
            simulation = isSimulation;
            IntPtr fg = TelemetryCollector.GetForegroundWindowHandle();
            Screen target = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.PrimaryScreen;
            Point desired = new Point(target.Bounds.Left + 16, target.Bounds.Top + 16);
            if (Location != desired) Location = desired;
            if (IsHandleCreated) SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var pen = new Pen(Color.FromArgb(52, 74, 96)))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using (var accent = new SolidBrush(current.Fps.HasValue ? Color.FromArgb(88, 224, 158) : Color.FromArgb(79, 227, 247)))
                g.FillRectangle(accent, 0, 0, 3, Height);

            string fpsText = current.Fps.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "{0:F0} FPS", current.Fps.Value)
                : "— FPS";
            string frameText = current.FrameTimeMs.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "{0:F1} ms", current.FrameTimeMs.Value)
                : "";
            string procText = string.IsNullOrEmpty(current.ActiveGameName)
                ? "Waiting for active game"
                : (simulation ? "[SIM] " + current.ActiveGameName : current.ActiveGameName);

            using (var fpsFont = new Font("Segoe UI Semibold", 13.5f))
            using (var smallFont = new Font("Segoe UI", 8.5f))
            using (var cyanBrush = new SolidBrush(Color.FromArgb(79, 227, 247)))
            using (var greenBrush = new SolidBrush(Color.FromArgb(88, 224, 158)))
            using (var lightBrush = new SolidBrush(Color.FromArgb(232, 238, 246)))
            using (var mutedBrush = new SolidBrush(Color.FromArgb(148, 164, 184))) {
                g.DrawString(fpsText, fpsFont, current.Fps.HasValue ? greenBrush : cyanBrush, 11, 5);
                if (!string.IsNullOrEmpty(frameText))
                    g.DrawString(frameText, smallFont, lightBrush, 108, 11);
                var procRect = new RectangleF(168, 11, Width - 178, 18);
                var fmt = new StringFormat { Alignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                g.DrawString(procText, smallFont, mutedBrush, procRect, fmt);

                string cpu = current.CpuUsagePercent.HasValue ? string.Format(CultureInfo.InvariantCulture, "CPU {0:F0}%", current.CpuUsagePercent.Value) : "CPU —";
                if (current.CpuTempC.HasValue) cpu += string.Format(CultureInfo.InvariantCulture, " {0:F0}°", current.CpuTempC.Value);
                string gpu = "GPU " + (current.GpuTempC.HasValue ? string.Format(CultureInfo.InvariantCulture, "{0:F0}°", current.GpuTempC.Value) : "—");
                if (current.GpuUsagePercent.HasValue) gpu += string.Format(CultureInfo.InvariantCulture, " {0:F0}%", current.GpuUsagePercent.Value);
                string ram = current.RamUsagePercent.HasValue ? string.Format(CultureInfo.InvariantCulture, "RAM {0:F0}%", current.RamUsagePercent.Value) : "RAM —";

                string ssd = "";
                for (int i = 0; i < Math.Min(2, current.Drives.Count); i++) {
                    var dr = current.Drives[i];
                    if (dr.TempC.HasValue)
                        ssd += (ssd.Length > 0 ? " " : "") + dr.Name + string.Format(CultureInfo.InvariantCulture, "{0:F0}°", dr.TempC.Value);
                }

                string line2 = cpu + "  ·  " + gpu + "  ·  " + ram + (ssd.Length > 0 ? "  ·  " + ssd : "");
                g.DrawString(line2, smallFont, lightBrush, new RectangleF(11, 33, Width - 20, 18),
                    new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            }
        }
    }

    sealed class TrayIcon : NativeWindow, IDisposable {
        const int Callback = 0x8001;
        readonly int taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        readonly Action show, menu, notificationShown;
        readonly IntPtr icon;
        bool added;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NotifyData {
            public int Size;
            public IntPtr Window;
            public uint Id, Flags, Callback;
            public IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
            public uint State, StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
            public uint Timeout;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
            public uint InfoFlags;
            public Guid Guid;
            public IntPtr BalloonIcon;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint operation, ref NotifyData data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string message);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern bool PlaySound(string alias, IntPtr module, uint flags);

        public TrayIcon(Action show, Action menu, Action notificationShown) {
            this.show = show;
            this.menu = menu;
            this.notificationShown = notificationShown;
            using (var image = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(image))
            using (var bg = new SolidBrush(Color.FromArgb(18, 23, 30)))
            using (var border = new Pen(Color.FromArgb(79, 227, 247), 2f))
            using (var txtBrush = new SolidBrush(Color.FromArgb(79, 227, 247)))
            using (var font = new Font("Segoe UI Bold", 13.5f, GraphicsUnit.Pixel)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                using (var badge = DrawHelper.RoundedRect(new Rectangle(1, 1, 29, 29), 6)) {
                    g.FillPath(bg, badge);
                    g.DrawPath(border, badge);
                }
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("17", font, txtBrush, new RectangleF(0, 1, 32, 31), sf);
                icon = image.GetHicon();
            }
            CreateHandle(new CreateParams { Caption = Program.Name });
            Register();
        }
        NotifyData Data(uint flags) {
            return new NotifyData { Size = Marshal.SizeOf(typeof(NotifyData)), Window = Handle, Id = 1,
                Flags = flags, Icon = icon, Tip = Program.Name, Callback = Callback, Info = "", Title = "" };
        }
        void Register() {
            var data = Data(7);
            added = Shell_NotifyIcon(0, ref data);
            if (added) {
                data.Timeout = 3;
                Shell_NotifyIcon(4, ref data);
            }
        }
        public bool ShowNotification(string title, string message, bool critical) {
            if (!added) Register();
            var data = Data(0x10);
            data.Title = title.Length > 63 ? title.Substring(0, 63) : title;
            data.Info = message.Length > 255 ? message.Substring(0, 255) : message;
            data.InfoFlags = 0x10u | (critical ? 3u : 2u);
            return added && Shell_NotifyIcon(1, ref data);
        }
        public static bool AlertSound() { return PlaySound("SystemExclamation", IntPtr.Zero, 0x10003); }
        public void Foreground() { SetForegroundWindow(Handle); }
        protected override void WndProc(ref Message message) {
            if (message.Msg == taskbarCreated) Register();
            if (message.Msg == Callback) {
                int kind = message.LParam.ToInt32();
                if (kind == 0x202 || kind == 0x400 || kind == 0x401 || kind == 0x405) show();
                if (kind == 0x205 || kind == 0x7B) menu();
                if (kind == 0x402 && notificationShown != null) notificationShown();
            }
            base.WndProc(ref message);
        }
        public void Dispose() {
            var data = Data(0);
            if (added) Shell_NotifyIcon(2, ref data);
            DestroyHandle();
            DestroyIcon(icon);
        }
    }
}
