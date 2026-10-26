using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace WhaleCompanion
{
    public sealed class Preferences
    {
        public int Version = 1;
        public int X = Int32.MinValue, Y = Int32.MinValue;
        public bool Inertia = true, Locked, Compact, Quiet, FollowLatest = true;
        public double Scale = 1;
        public string PinnedPath = "", CodexHome = "";
        public bool ReferencePricing;
        public double InputPerMillion, CachedPerMillion, OutputPerMillion;
        public string Currency = "USD";
    }

    internal static class Program
    {
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string PrefPath = System.IO.Path.Combine(Root, "settings.json");
        public static string StartupWarning = "";
        public static Preferences Prefs;
        public static bool Preview;
        public static void Log(Exception ex)
        {
            try { File.AppendAllText(System.IO.Path.Combine(Root, "errors.log"), DateTime.Now.ToString("s") + " " + ex + Environment.NewLine); }
            catch (IOException) { Debug.WriteLine(ex); }
            catch (UnauthorizedAccessException) { Debug.WriteLine(ex); }
        }
        public static void Save()
        {
            string temporary = PrefPath + ".tmp";
            File.WriteAllText(temporary, Json.Serializer().Serialize(Prefs));
            if (File.Exists(PrefPath)) File.Replace(temporary, PrefPath, null); else File.Move(temporary, PrefPath);
        }
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Contains("--self-test")) return SelfTest.Run();
            if (args.Contains("--probe")) return SelfTest.Probe(args);
            Preview = args.Contains("--preview");
            bool created;
            using (var mutex = new Mutex(true, "Local\\CodexWhaleCompanion_" + Environment.UserName + "_" + Root.GetHashCode().ToString("X") + (Preview ? "_preview" : ""), out created))
            {
                if (!created) { MessageBox.Show("鲸鱼娘已经在运行。可以从系统托盘找回窗口，或按 Ctrl+Alt+W 复位。", "鲸鱼娘"); return 0; }
                try
                {
                    Prefs = new Preferences();
                    if (File.Exists(PrefPath))
                    {
                        try { Prefs = Json.Serializer().Deserialize<Preferences>(File.ReadAllText(PrefPath)) ?? new Preferences(); }
                        catch (ArgumentException) { StartupWarning = "设置格式有误，已使用默认值；原设置文件保留。"; }
                        catch (InvalidOperationException) { StartupWarning = "设置无法读取，已使用默认值。"; }
                    }
                    Prefs.Scale = Glide.Clamp(Prefs.Scale, 0.65, 1.25);
                    string home = Environment.GetEnvironmentVariable("CODEX_HOME");
                    if (String.IsNullOrWhiteSpace(home)) home = DefaultCodexHome();
                    if (!String.IsNullOrWhiteSpace(Prefs.CodexHome)) home = Prefs.CodexHome;
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    { Log(e.Exception); if (!Preview) MessageBox.Show("鲸鱼娘遇到异常，详细信息已写入 errors.log。请关闭后重新打开。", "鲸鱼娘"); app.Shutdown(1); e.Handled = true; };
                    var window = new CompanionWindow(new SessionMonitor(home));
                    app.Run(window);
                    return 0;
                }
                catch (Exception ex) { Log(ex); if (!Preview) MessageBox.Show("启动失败：" + ex.Message, "鲸鱼娘"); return 1; }
            }
        }
        public static string DefaultCodexHome()
        {
            string profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (String.IsNullOrWhiteSpace(profile)) profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(profile, ".codex");
        }
    }

    internal sealed class Particle
    {
        public TextBlock View;
        public double X, Y, Vx, Vy, Life;
    }

    internal sealed class CompanionWindow : Window
    {
        private const int WidgetWidth = 320, WidgetHeight = 176, CompactWidth = 152;
        private const double PetLeft = 182, PetTop = 6, PetSize = 132, CompactOffset = 174;
        private readonly SessionMonitor monitor;
        private readonly Canvas stage = new Canvas { Width = WidgetWidth, Height = WidgetHeight };
        private readonly Canvas viewport = new Canvas { ClipToBounds = true };
        private Viewbox viewbox;
        private readonly Canvas particles = new Canvas { Width = WidgetWidth, Height = WidgetHeight, IsHitTestVisible = false };
        private readonly Dictionary<string, BitmapImage> images = new Dictionary<string, BitmapImage>();
        private readonly Image pet = new Image { Width = PetSize, Height = PetSize, Stretch = Stretch.Uniform, Cursor = Cursors.Hand };
        private readonly ScaleTransform petScale = new ScaleTransform(1, 1);
        private readonly RotateTransform petRotation = new RotateTransform();
        private readonly TranslateTransform petShift = new TranslateTransform();
        private readonly Border card = new Border(), bubble = new Border(), badge = new Border();
        private readonly TextBlock speed = Text("—", 30, "#1D1D1F", FontWeights.SemiBold);
        private readonly TextBlock model = Text("等待 Codex", 10.5, "#6E6E73", FontWeights.Medium);
        private readonly TextBlock total = Text("会话 —", 11, "#515156", FontWeights.Medium);
        private readonly TextBlock cache = Text("缓存 —", 11, "#515156", FontWeights.Medium);
        private readonly TextBlock stateText = Text("等待日志", 9.5, "#6E6E73", FontWeights.Normal);
        private readonly TextBlock sourceText = Text("本机只读 · 最近活动", 10, "#7189AA", FontWeights.Normal);
        private readonly TextBlock speech = Text("本鲸今天也会认真陪你！", 11, "#303035", FontWeights.Normal);
        private readonly TextBlock badgeText = Text("拖动我", 9.5, "#6E6E73", FontWeights.Medium);
        private readonly Spring tilt = new Spring(), squeeze = new Spring(), hop = new Spring();
        private readonly Glide glide = new Glide();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Random random = new Random();
        private readonly List<Particle> floating = new List<Particle>();
        private DispatcherTimer pollTimer;
        private Forms.NotifyIcon tray;
        private IntPtr handle;
        private HwndSource nativeSource;
        private bool pressed, dragging, pendingSave, polling, closing;
        private Native.Point pressCursor, lastCursor;
        private double pressX, pressY, lastDragTime, dragVx, dragVy, lastFrame;
        private double interactionUntil, nextIdle = 10, speechUntil, nextSave, lastClick = -1, swapAt;
        private DateTime lastStatus = DateTime.MinValue;
        private double speechShownAt;
        private int clickChain;
        private string currentPose = "", requestedPose = "", interactionPose = "", previousTurn = "", previousState = "";
        private bool wasActive;
        private bool? previousCompact;
        private double previousScale;
        private readonly Queue<double> speedHistory = new Queue<double>();
        private readonly Polyline chart = new Polyline { Stroke = Brush("#007AFF"), StrokeThickness = 1.4, Opacity = 0.5, IsHitTestVisible = false, StrokeLineJoin = PenLineJoin.Round };

        private static SolidColorBrush Brush(string value) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(value); b.Freeze(); return b; }
        private static TextBlock Text(string text, double size, string color, FontWeight weight)
        { return new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), FontWeight = weight }; }
        private static void Put(Canvas parent, UIElement child, double x, double y) { Canvas.SetLeft(child, x); Canvas.SetTop(child, y); parent.Children.Add(child); }
        public CompanionWindow(SessionMonitor sessionMonitor)
        {
            monitor = sessionMonitor;
            monitor.FollowLatest = Program.Prefs.FollowLatest; monitor.PinnedPath = Program.Prefs.PinnedPath;
            Title = "鲸鱼娘 · Codex 速度挂件";
            Width = (Program.Prefs.Compact ? CompactWidth : WidgetWidth) * Program.Prefs.Scale; Height = WidgetHeight * Program.Prefs.Scale;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
            Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); UseLayoutRounding = true; SnapsToDevicePixels = true;
            viewbox = new Viewbox { Child = stage, Stretch = Stretch.Uniform };
            TextOptions.SetTextRenderingMode(stage, TextRenderingMode.Grayscale);
            TextOptions.SetTextFormattingMode(stage, TextFormattingMode.Display);
            viewport.Children.Add(viewbox); Content = viewport;
            BuildVisuals();
            SourceInitialized += delegate
            {
                handle = new WindowInteropHelper(this).Handle;
                nativeSource = HwndSource.FromHwnd(handle); nativeSource.AddHook(WindowMessage);
                bool resetKey = Native.RegisterHotKey(handle, 1, 0x4003, 0x57); // Ctrl+Alt+W
                bool hideKey = Native.RegisterHotKey(handle, 2, 0x4003, 0x48); // Ctrl+Alt+H
                if (!resetKey || !hideKey) Program.StartupWarning = "部分快捷键已被占用，请使用托盘菜单。";
            };
            Loaded += delegate
            {
                RestorePosition(); if (!Program.Preview) InitTray(); ApplyPreferences();
                SetPose("greet", true); interactionPose = "greet"; interactionUntil = 2.8;
                ShowSpeech(Program.StartupWarning.Length > 0 ? Program.StartupWarning : "本鲸上线！拖动我，或者右键看看。", 6);
                CompositionTarget.Rendering += Frame;
                pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                pollTimer.Tick += async delegate { await Poll(); };
                pollTimer.Start();
                Dispatcher.BeginInvoke(new Action(async delegate
                {
                    await Poll();
                    if (Program.Preview) { await Task.Delay(250); ExportPreview(); Close(); }
                }));
            };
            Closed += delegate
            {
                closing = true; CompositionTarget.Rendering -= Frame;
                if (pollTimer != null) pollTimer.Stop();
                Native.UnregisterHotKey(handle, 1); Native.UnregisterHotKey(handle, 2);
                if (nativeSource != null) nativeSource.RemoveHook(WindowMessage);
                if (tray != null) tray.Dispose();
                if (!Program.Preview) SavePosition();
            };
            MouseLeftButtonDown += PointerDown; MouseMove += PointerMove; MouseLeftButtonUp += PointerUp;
            LostMouseCapture += delegate { if (pressed) FinishDrag(false); };
            MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e) { FinishDrag(false); OpenMenu(); e.Handled = true; };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { FinishDrag(false); glide.Vx = glide.Vy = 0; }
                if (!Program.Prefs.Locked && (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down))
                {
                    int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
                    glide.X += e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                    glide.Y += e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                    ClampToScreen(); MoveNative(); MarkSave(); e.Handled = true;
                }
            };
        }

        private void BuildVisuals()
        {
            card.Width = 228; card.Height = 108; card.CornerRadius = new CornerRadius(22); card.Cursor = Cursors.Hand;
            card.Background = new LinearGradientBrush(Color.FromArgb(249, 255, 255, 255), Color.FromArgb(239, 242, 244, 249), 90);
            card.BorderBrush = new LinearGradientBrush(Color.FromArgb(255, 255, 255, 255), Color.FromArgb(180, 206, 211, 221), 90);
            card.BorderThickness = new Thickness(1);
            card.Effect = new DropShadowEffect { Color = Color.FromRgb(30, 38, 55), BlurRadius = 14, ShadowDepth = 3, Opacity = 0.14 };
            var inner = new Canvas(); card.Child = inner;
            model.MaxWidth = 106; model.TextTrimming = TextTrimming.CharacterEllipsis;
            stateText.MaxWidth = 75; stateText.TextTrimming = TextTrimming.CharacterEllipsis;
            total.MaxWidth = 104; total.TextTrimming = TextTrimming.CharacterEllipsis;
            cache.MaxWidth = 88; cache.TextTrimming = TextTrimming.CharacterEllipsis;
            speed.Width = 91;
            Put(inner, model, 14, 10); Put(inner, stateText, 125, 10);
            Put(inner, speed, 13, 25);
            Put(inner, Text("tok/s", 10.5, "#6E6E73", FontWeights.Normal), 105, 48);
            Put(inner, chart, 151, 44);
            Put(inner, new Line { X2 = 198, Stroke = Brush("#16000000"), StrokeThickness = 1 }, 14, 74);
            Put(inner, total, 14, 84); Put(inner, cache, 125, 84);
            Put(stage, card, 8, 60);
            card.ToolTip = "单击查看详情 · 按住拖动\n速度为分段模型均速，响应结束后更新。\n默认跟随最近活动会话；右键可锁定。";

            var shadow = new Ellipse { Width = 76, Height = 7, Fill = Brush("#697B9A"), Opacity = 0.16, IsHitTestVisible = false,
                Effect = new BlurEffect { Radius = 5 } };
            Put(stage, shadow, PetLeft + 33, PetTop + PetSize - 1);
            var transforms = new TransformGroup(); transforms.Children.Add(petScale); transforms.Children.Add(petRotation); transforms.Children.Add(petShift);
            pet.RenderTransform = transforms; pet.RenderTransformOrigin = new Point(0.5, 0.72);
            RenderOptions.SetBitmapScalingMode(pet, BitmapScalingMode.HighQuality);
            pet.ToolTip = "单击摸头 · 连点庆祝 · 按住拖动 · 右键菜单\nCtrl+Alt+W 复位 · Ctrl+Alt+H 显示/隐藏";
            System.Windows.Automation.AutomationProperties.SetName(pet, "鲸鱼娘，按住拖动，单击摸头，右键菜单");
            Put(stage, pet, PetLeft, PetTop);

            badge.Background = Brush("#F0FFFFFF"); badge.BorderBrush = Brush("#CAD3D8E2"); badge.BorderThickness = new Thickness(0.7);
            badge.CornerRadius = new CornerRadius(11); badge.Padding = new Thickness(9, 3, 9, 4); badge.Child = badgeText; badge.Opacity = 0;
            Put(stage, badge, 241, 145);
            bubble.MaxWidth = 174; bubble.CornerRadius = new CornerRadius(16, 16, 4, 16);
            bubble.Background = Brush("#F7FFFFFF"); bubble.BorderBrush = Brush("#D2D8E2"); bubble.BorderThickness = new Thickness(0.7);
            bubble.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.1, Color = Colors.Black };
            bubble.Padding = new Thickness(11, 7, 11, 8); bubble.Child = speech;
            speech.TextWrapping = TextWrapping.Wrap; speech.MaxWidth = 150; speech.MaxHeight = 34; speech.LineHeight = 16; speech.TextTrimming = TextTrimming.CharacterEllipsis;
            Put(stage, bubble, 8, 5);
            stage.Children.Add(particles);
        }

        private BitmapImage ImageFor(string pose)
        {
            BitmapImage image;
            if (images.TryGetValue(pose, out image)) return image;
            string path = System.IO.Path.Combine(Program.Root, "assets", "dsh-whale-state-" + pose + ".png");
            if (!File.Exists(path)) throw new FileNotFoundException("缺少鲸鱼娘素材：" + pose, path);
            image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); images.Add(pose, image); return image;
        }
        private void SetPose(string pose, bool immediate)
        {
            if (pose == requestedPose) return;
            requestedPose = pose;
            if (immediate || currentPose.Length == 0 || Program.Prefs.Quiet)
            { pet.Source = ImageFor(pose); currentPose = pose; swapAt = 0; }
            else { squeeze.Velocity -= 1.4; swapAt = clock.Elapsed.TotalSeconds + 0.075; }
        }
        private void ShowSpeech(string words, double seconds)
        { speech.Text = words; speechShownAt = clock.Elapsed.TotalSeconds; speechUntil = speechShownAt + seconds; bubble.Opacity = 0; bubble.Visibility = String.IsNullOrWhiteSpace(words) ? Visibility.Hidden : Visibility.Visible; }
        private void Interact(string pose, string words, double seconds)
        {
            interactionPose = pose; interactionUntil = clock.Elapsed.TotalSeconds + seconds; SetPose(pose, false);
            if (!Program.Prefs.Quiet) { hop.Velocity -= 85 * PetSize / 238; Burst(); }
            ShowSpeech(words, seconds);
        }
        private void Burst()
        {
            for (int i = 0; i < 7; i++)
            {
                var view = Text(i % 3 == 0 ? "♥" : "✦", 9 + random.Next(5), i % 2 == 0 ? "#F697BE" : "#78BAF5", FontWeights.Bold);
                particles.Children.Add(view);
                floating.Add(new Particle { View = view, X = PetLeft + PetSize / 2 + random.Next(-24, 24), Y = PetTop + 57 + random.Next(20),
                    Vx = random.Next(-40, 40), Vy = -45 - random.Next(30), Life = 1.1 + random.NextDouble() * 0.35 });
            }
        }
        private void Frame(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds, dt = Math.Min(0.04, Math.Max(0.001, now - lastFrame)); lastFrame = now;
            if (swapAt > 0 && now >= swapAt) { pet.Source = ImageFor(requestedPose); currentPose = requestedPose; swapAt = 0; squeeze.Velocity += 1.3; }
            if (pressed && dragging && now - lastDragTime > 0.08) { dragVx *= Math.Exp(-18 * dt); dragVy *= Math.Exp(-18 * dt); }
            if (!pressed && glide.Moving)
            {
                var bounds = BoundsForPosition();
                if (glide.Step(dt, bounds.Left, bounds.Top, Math.Max(bounds.Left, bounds.Right - PixelWidth()), Math.Max(bounds.Top, bounds.Bottom - PixelHeight())))
                { squeeze.Velocity -= 0.9; tilt.Velocity *= -0.3; }
                MoveNative(); if (!glide.Moving) MarkSave();
            }
            tilt.Step(dragging && !Program.Prefs.Quiet ? Glide.Clamp(dragVx * 0.018, -13, 13) : 0, dt);
            squeeze.Step(dragging && !Program.Prefs.Quiet ? 0.05 : 0, dt); hop.Step(0, dt);
            double breathing = Program.Prefs.Quiet ? 0 : Math.Sin(now * 2.1) * 0.012;
            petScale.ScaleX = 1 - squeeze.Value * 0.48 - breathing * 0.35;
            petScale.ScaleY = 1 + squeeze.Value + breathing;
            petRotation.Angle = tilt.Value + (Program.Prefs.Quiet || dragging ? 0 : Math.Sin(now * 1.15) * 0.8);
            petShift.Y = hop.Value + (Program.Prefs.Quiet ? 0 : Math.Sin(now * 2.1) * 1.2);
            for (int i = floating.Count - 1; i >= 0; i--)
            {
                var p = floating[i]; p.Life -= dt; p.Vy += 90 * dt; p.X += p.Vx * dt; p.Y += p.Vy * dt;
                p.View.Opacity = Glide.Clamp(p.Life, 0, 1); Canvas.SetLeft(p.View, p.X); Canvas.SetTop(p.View, p.Y);
                if (p.Life <= 0) { particles.Children.Remove(p.View); floating.RemoveAt(i); }
            }
            if (bubble.Visibility == Visibility.Visible)
            {
                if (now > speechUntil) bubble.Visibility = Visibility.Hidden;
                else bubble.Opacity = Program.Prefs.Quiet ? 1 : Math.Min(Glide.Clamp((now - speechShownAt) / 0.15, 0, 1), Glide.Clamp((speechUntil - now) / 0.2, 0, 1));
            }
            double hintTarget = pet.IsMouseOver && !dragging && bubble.Visibility != Visibility.Visible ? 1 : 0;
            badge.Opacity += (hintTarget - badge.Opacity) * (1 - Math.Exp(-12 * dt));
            if (now > interactionUntil && !dragging) ChoosePose(now);
            if (pendingSave && now > nextSave && !pressed && !glide.Moving) { pendingSave = false; SavePosition(); }
        }
        private void ChoosePose(double now)
        {
            var m = monitor.Current;
            if (m != null && m.Active) { SetPose(m.State == "工具运行" ? "tool" : "thinking", false); return; }
            if (m != null && m.Failed) { SetPose("failure", false); return; }
            if (now >= nextIdle)
            {
                string[] idle = { "idle-cute", "daily-coffee", "wink", "tail-swing", "daily-stretch", "meme-smug", "daily-eat", "daily-gaming" };
                SetPose(idle[random.Next(idle.Length)], false); nextIdle = now + 9 + random.Next(9);
            }
        }

        private async Task Poll()
        {
            if (polling || closing) return;
            polling = true;
            try
            {
                await Task.Run(new Action(monitor.Poll));
                if (!closing)
                {
                    RefreshMetrics();
                    if (!Program.Preview && (DateTime.UtcNow - lastStatus).TotalSeconds >= 10) WriteStatus();
                }
            }
            catch (Exception ex) { Program.Log(ex); if (!closing) { stateText.Text = "读取异常"; sourceText.Text = "日志读取失败 · 右键查看说明"; } }
            finally { polling = false; }
        }
        private static string ShortNumber(long n) { return n >= 1000000 ? (n / 1000000.0).ToString("0.00") + "M" : n >= 1000 ? (n / 1000.0).ToString("0.0") + "K" : n.ToString(); }
        private void RefreshMetrics()
        {
            var m = monitor.Current;
            if (monitor.Error.Length > 0) { stateText.Text = "日志不可用"; sourceText.Text = monitor.Error; speed.Text = "—"; return; }
            if (m == null) { stateText.Text = "等待 Codex"; sourceText.Text = "尚未找到日志 · 右键可选择 Codex 数据目录"; return; }
            model.Text = m.Model;
            speed.Text = m.Speed.HasValue ? m.Speed.Value.ToString("0") : "—";
            speed.FontSize = speed.Text.Length > 5 ? 24 : 30;
            stateText.Text = (m.Active ? "● " : "") + m.State;
            stateText.Foreground = Brush(m.Failed ? "#C44839" : m.Active ? "#0066CC" : "#6E6E73");
            model.ToolTip = m.Model;
            total.Text = "会话 " + (m.HasUsage ? ShortNumber(m.Total) : "—");
            cache.Text = "缓存 " + (m.CachePercent.HasValue ? m.CachePercent.Value.ToString("0.0") + "%" : "—");
            sourceText.Text = (monitor.FollowLatest ? "最近活动会话" : "已锁定会话") + " · " + SessionLabel(monitor.SelectedPath) +
                (m.LastUsageUtc == DateTime.MinValue ? "" : " · " + m.LastUsageUtc.ToLocalTime().ToString("HH:mm:ss"));
            badgeText.Text = Program.Prefs.Locked ? "已固定" : "拖动我";
            card.ToolTip = "单击查看详情 · 按住拖动\n" + sourceText.Text + "\n" + (m.Speed.HasValue ? "分段均速 " + m.Speed.Value.ToString("0.00") + " tok/s" : "等待完整速度样本") + "\n每段模型响应结束后更新";
            if (m.TurnId != previousTurn) { previousTurn = m.TurnId; speedHistory.Clear(); }
            if (m.Speed.HasValue && m.State != "等待模型")
            {
                double value = m.Speed.Value;
                if (speedHistory.Count == 0 || Math.Abs(speedHistory.Last() - value) > 0.01) { speedHistory.Enqueue(value); while (speedHistory.Count > 24) speedHistory.Dequeue(); }
                double max = Math.Max(1, speedHistory.Max()); var points = new PointCollection(); int i = 0;
                foreach (double v in speedHistory) points.Add(new Point(i++ * 44.0 / Math.Max(1, speedHistory.Count - 1), 12 - v / max * 12));
                chart.Points = points;
            }
            else chart.Points = new PointCollection();
            if (wasActive && !m.Active && m.State != previousState && !Program.Prefs.Quiet)
                Interact(m.Failed ? "failure" : "success", m.Failed ? "遇到一点问题，我陪你看看。" : "完成啦，给本鲸加个鸡腿！", 4);
            wasActive = m.Active; previousState = m.State;
            // Speech never claims a live stream: samples arrive only after completed model items.
        }
        private static string SessionLabel(string path)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            return name.Length > 12 ? "…" + name.Substring(name.Length - 8) : name;
        }

        private void PointerDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || handle == IntPtr.Zero) return;
            pressed = true; dragging = false; Native.GetCursorPos(out pressCursor); lastCursor = pressCursor;
            pressX = glide.X; pressY = glide.Y; glide.Vx = glide.Vy = dragVx = dragVy = 0;
            lastDragTime = clock.Elapsed.TotalSeconds;
            CaptureMouse(); e.Handled = true;
        }
        private void PointerMove(object sender, MouseEventArgs e)
        {
            if (!pressed || Program.Prefs.Locked) return;
            Native.Point cursor; Native.GetCursorPos(out cursor);
            if (!dragging && Math.Abs(cursor.X - pressCursor.X) + Math.Abs(cursor.Y - pressCursor.Y) < 5) return;
            if (!dragging) { dragging = true; SetPose("pick-up", true); squeeze.Velocity += 0.8; }
            double now = clock.Elapsed.TotalSeconds, dt = Math.Max(0.006, now - lastDragTime);
            dragVx = dragVx * 0.35 + (cursor.X - lastCursor.X) / dt * 0.65;
            dragVy = dragVy * 0.35 + (cursor.Y - lastCursor.Y) / dt * 0.65;
            glide.X = pressX + cursor.X - pressCursor.X; glide.Y = pressY + cursor.Y - pressCursor.Y;
            ClampToScreen(); MoveNative(); lastCursor = cursor; lastDragTime = now; e.Handled = true;
        }
        private void PointerUp(object sender, MouseButtonEventArgs e)
        {
            if (!pressed) return;
            bool moved = dragging; Point location = e.GetPosition(pet);
            FinishDrag(true);
            if (!moved)
            {
                if (!Program.Prefs.Compact && card.IsMouseOver) { DetailsDialog(); e.Handled = true; return; }
                double now = clock.Elapsed.TotalSeconds; clickChain = now - lastClick < 0.65 ? clickChain + 1 : 1; lastClick = now;
                if (clickChain >= 3) { Interact("star", "好啦好啦，本鲸最喜欢你！", 4); clickChain = 0; }
                else if (location.Y < pet.ActualHeight * 0.44) Interact("react-head", "摸头可以，再摸要收费一碗饭。", 3.5);
                else if (location.X > pet.ActualWidth * 0.7) Interact("react-tail", "尾巴不可以随便捏啦！", 3.5);
                else Interact("blush", "才、才没有在等你摸摸呢。", 3.5);
            }
            e.Handled = true;
        }
        private void FinishDrag(bool release)
        {
            bool moved = dragging; pressed = dragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (moved)
            {
                if (release && Program.Prefs.Inertia && !Program.Prefs.Locked && !Program.Prefs.Quiet) glide.Release(dragVx, dragVy);
                else glide.Vx = glide.Vy = 0;
                if (!Program.Prefs.Quiet) { squeeze.Velocity -= 1.25; hop.Velocity -= 24 * PetSize / 238; }
                interactionPose = "idle-cute"; interactionUntil = clock.Elapsed.TotalSeconds + 0.65; SetPose("idle-cute", false);
                nextIdle = clock.Elapsed.TotalSeconds + 6; MarkSave();
            }
        }
        private double PixelWidth() { Native.Rect rect; return Native.GetWindowRect(handle, out rect) ? rect.Right - rect.Left : Width; }
        private double PixelHeight() { Native.Rect rect; return Native.GetWindowRect(handle, out rect) ? rect.Bottom - rect.Top : Height; }
        private System.Drawing.Rectangle BoundsForPosition()
        {
            var point = new System.Drawing.Point((int)(glide.X + PixelWidth() / 2), (int)(glide.Y + PixelHeight() / 2));
            return Forms.Screen.FromPoint(point).WorkingArea;
        }
        private void ClampToScreen()
        {
            var r = BoundsForPosition();
            glide.X = Glide.Clamp(glide.X, r.Left, Math.Max(r.Left, r.Right - PixelWidth()));
            glide.Y = Glide.Clamp(glide.Y, r.Top, Math.Max(r.Top, r.Bottom - PixelHeight()));
        }
        private void MoveNative() { if (handle != IntPtr.Zero) Native.SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(glide.X), (int)Math.Round(glide.Y), 0, 0, 0x0015); }
        private void RestorePosition()
        {
            if (Program.Prefs.X == Int32.MinValue) ResetPosition();
            else { glide.X = Program.Prefs.X; glide.Y = Program.Prefs.Y; ClampToScreen(); MoveNative(); }
        }
        private void ResetPosition()
        {
            FinishDrag(false); glide.Vx = glide.Vy = 0;
            var r = Forms.Screen.PrimaryScreen.WorkingArea;
            glide.X = r.Right - PixelWidth() - 24; glide.Y = r.Bottom - PixelHeight() - 24;
            ClampToScreen(); MoveNative(); MarkSave();
        }
        private void MarkSave() { pendingSave = true; nextSave = clock.Elapsed.TotalSeconds + 0.7; }
        private void SavePosition()
        {
            if (Program.Preview) return;
            Program.Prefs.X = (int)Math.Round(glide.X); Program.Prefs.Y = (int)Math.Round(glide.Y);
            Program.Prefs.FollowLatest = monitor.FollowLatest; Program.Prefs.PinnedPath = monitor.PinnedPath;
            try { Program.Save(); }
            catch (IOException ex) { Program.Log(ex); if (!closing) ShowSpeech("位置暂时保存失败，请检查目录是否可写。", 5); }
            catch (UnauthorizedAccessException ex) { Program.Log(ex); if (!closing) ShowSpeech("挂件目录只读，位置无法保存。", 5); }
        }
        private void ApplyPreferences()
        {
            double oldPetX = glide.X;
            bool changingLayout = previousCompact.HasValue && (previousCompact.Value != Program.Prefs.Compact || previousScale != Program.Prefs.Scale);
            if (changingLayout) oldPetX += (previousCompact.Value ? PetLeft - CompactOffset : PetLeft) * PixelWidth() / (previousCompact.Value ? CompactWidth : WidgetWidth);
            card.Visibility = Program.Prefs.Compact ? Visibility.Hidden : Visibility.Visible;
            viewbox.Width = WidgetWidth * Program.Prefs.Scale; viewbox.Height = WidgetHeight * Program.Prefs.Scale;
            Canvas.SetLeft(viewbox, Program.Prefs.Compact ? -CompactOffset * Program.Prefs.Scale : 0);
            Canvas.SetLeft(bubble, Program.Prefs.Compact ? CompactOffset + 6 : 8); Canvas.SetTop(bubble, Program.Prefs.Compact ? 137 : 5);
            bubble.MaxWidth = Program.Prefs.Compact ? 140 : 174; speech.MaxWidth = Program.Prefs.Compact ? 116 : 150;
            speech.MaxHeight = Program.Prefs.Compact ? 28 : 34; speech.LineHeight = Program.Prefs.Compact ? 14 : 16;
            bubble.Padding = Program.Prefs.Compact ? new Thickness(8, 3, 8, 4) : new Thickness(11, 7, 11, 8);
            Width = (Program.Prefs.Compact ? CompactWidth : WidgetWidth) * Program.Prefs.Scale; Height = WidgetHeight * Program.Prefs.Scale;
            UpdateLayout();
            if (changingLayout)
            {
                glide.X = oldPetX - (Program.Prefs.Compact ? PetLeft - CompactOffset : PetLeft) * PixelWidth() / (Program.Prefs.Compact ? CompactWidth : WidgetWidth);
                ClampToScreen(); MoveNative();
            }
            previousCompact = Program.Prefs.Compact; previousScale = Program.Prefs.Scale;
            MarkSave();
        }
        private void InitTray()
        {
            tray = new Forms.NotifyIcon { Text = "鲸鱼娘 · Codex 速度挂件", Visible = true };
            string icon = System.IO.Path.Combine(Program.Root, "whale.ico");
            tray.Icon = File.Exists(icon) ? new System.Drawing.Icon(icon) : System.Drawing.SystemIcons.Information;
            tray.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(delegate { Show(); ResetPosition(); })); };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示 / 隐藏", null, delegate { Dispatcher.BeginInvoke(new Action(ToggleVisible)); });
            menu.Items.Add("找回鲸鱼娘（位置复位）", null, delegate { Dispatcher.BeginInvoke(new Action(delegate { Show(); ResetPosition(); })); });
            menu.Items.Add("退出", null, delegate { Dispatcher.BeginInvoke(new Action(Close)); }); tray.ContextMenuStrip = menu;
        }
        private void ToggleVisible() { if (IsVisible) Hide(); else Show(); }
        private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (msg == 0x0312) { if (wp.ToInt32() == 1) { Show(); ResetPosition(); } else if (wp.ToInt32() == 2) ToggleVisible(); handled = true; }
            if (msg == 0x007E) Dispatcher.BeginInvoke(new Action(delegate { ClampToScreen(); MoveNative(); MarkSave(); }));
            return IntPtr.Zero;
        }

        private MenuItem Item(string title, Action action, bool? check)
        {
            var item = new MenuItem { Header = title, Padding = new Thickness(9, 5, 9, 5), Foreground = Brush("#303035") };
            if (check.HasValue) { item.IsCheckable = true; item.IsChecked = check.Value; }
            item.Click += delegate { action(); }; return item;
        }
        private void OpenMenu()
        {
            var menu = new ContextMenu { FontFamily = FontFamily, FontSize = 12, Background = Brush("#FAFAFC"), BorderBrush = Brush("#DCE0E7"), BorderThickness = new Thickness(1), Padding = new Thickness(5) };
            menu.Items.Add(Item("摸摸头", delegate { Interact("react-head", "今天也辛苦啦，摸摸！", 4); }, null));
            menu.Items.Add(Item("投喂一碗白饭", delegate { Interact("eat", "啊呜……本鲸还能再吃一碗！", 5); }, null));
            menu.Items.Add(Item("夸夸她", delegate { Interact("star", "本鲸当然很厉害啦！", 4); }, null));
            var poses = new MenuItem { Header = "更多表情和动作" };
            string[] names = { "喝咖啡", "伸懒腰", "挥手", "眨眼", "害羞", "小得意", "生气", "睡觉", "摸鱼", "玩游戏", "撒娇", "庆祝" };
            string[] keys = { "daily-coffee", "daily-stretch", "greet", "wink", "blush", "meme-smug", "angry", "sleep", "daily-fishing", "daily-gaming", "meme-heart", "celebrate" };
            for (int i = 0; i < keys.Length; i++) { string key = keys[i], name = names[i]; poses.Items.Add(Item(name, delegate { Interact(key, name + "中的本鲸也很可爱！", 6); }, null)); }
            var gallery = new MenuItem { Header = "全部 89 款表情立绘" };
            foreach (string file in Directory.GetFiles(System.IO.Path.Combine(Program.Root, "assets"), "dsh-whale-*.png").OrderBy(x => x))
            {
                string key = System.IO.Path.GetFileNameWithoutExtension(file).Replace("dsh-whale-state-", "");
                if (key.StartsWith("dsh-whale-")) continue;
                gallery.Items.Add(Item(key, delegate { Interact(key, "", 7); }, null));
            }
            poses.Items.Add(gallery); menu.Items.Add(poses); menu.Items.Add(new Separator());
            menu.Items.Add(Item("拖动后轻微惯性", delegate { Program.Prefs.Inertia = !Program.Prefs.Inertia; glide.Vx = glide.Vy = 0; MarkSave(); }, Program.Prefs.Inertia));
            menu.Items.Add(Item("固定当前位置（禁止拖动）", delegate { Program.Prefs.Locked = !Program.Prefs.Locked; glide.Vx = glide.Vy = 0; MarkSave(); RefreshMetrics(); }, Program.Prefs.Locked));
            menu.Items.Add(Item("安静模式（减少动效）", delegate { Program.Prefs.Quiet = !Program.Prefs.Quiet; MarkSave(); }, Program.Prefs.Quiet));
            menu.Items.Add(Item("只显示鲸鱼娘", delegate { Program.Prefs.Compact = !Program.Prefs.Compact; ApplyPreferences(); }, Program.Prefs.Compact));
            var sizes = new MenuItem { Header = "大小" };
            foreach (double s in new double[] { 0.85, 1, 1.15, 1.25 }) { double value = s; sizes.Items.Add(Item((s * 100).ToString("0") + "%" + (s == 1 ? " · 推荐" : s == 0.85 ? " · 迷你" : ""), delegate { Program.Prefs.Scale = value; ApplyPreferences(); Dispatcher.BeginInvoke(new Action(delegate { ClampToScreen(); MoveNative(); })); }, Program.Prefs.Scale == s)); }
            menu.Items.Add(sizes);
            menu.Items.Add(Item("精确设置位置…", PositionDialog, null));
            menu.Items.Add(Item("位置复位   Ctrl+Alt+W", ResetPosition, null)); menu.Items.Add(new Separator());
            menu.Items.Add(Item("跟随最近活动会话", delegate { monitor.FollowLatest = true; MarkSave(); }, monitor.FollowLatest));
            menu.Items.Add(Item("锁定当前统计会话", delegate { monitor.PinnedPath = monitor.SelectedPath; monitor.FollowLatest = false; MarkSave(); }, !monitor.FollowLatest));
            menu.Items.Add(Item("选择历史会话…", ChooseSession, null));
            menu.Items.Add(Item("选择 Codex 数据目录…", ChooseHome, null));
            menu.Items.Add(Item("统计详情 / 速度口径", DetailsDialog, null));
            menu.Items.Add(Item("参考费用设置…", PricingDialog, null)); menu.Items.Add(new Separator());
            menu.Items.Add(Item("隐藏   Ctrl+Alt+H", Hide, null));
            menu.Items.Add(Item("退出鲸鱼娘", Close, null));
            ContextMenu = menu; menu.IsOpen = true;
        }
        private void ChooseSession()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择只读统计的 Codex 会话日志", Filter = "Codex 会话日志|*.jsonl" };
            if (dialog.ShowDialog(this) == true) { monitor.PinnedPath = dialog.FileName; monitor.FollowLatest = false; MarkSave(); }
        }
        private void ChooseHome()
        {
            using (var dialog = new Forms.FolderBrowserDialog { Description = "选择包含 sessions 文件夹的 Codex 数据目录（通常是 .codex）" })
            {
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
                if (!Directory.Exists(System.IO.Path.Combine(dialog.SelectedPath, "sessions"))) { MessageBox.Show(this, "这个目录没有 sessions 文件夹。", "鲸鱼娘"); return; }
                Program.Prefs.CodexHome = dialog.SelectedPath; SavePosition();
                MessageBox.Show(this, "目录已保存，退出后重新打开鲸鱼娘即可生效。", "鲸鱼娘");
            }
        }
        private Window Dialog(string title, StackPanel content, double width)
        {
            return new Window { Title = title, Content = content, Width = width, SizeToContent = SizeToContent.Height, MaxHeight = 800,
                Owner = this, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize,
                FontFamily = FontFamily, FontSize = 13, Background = Brush("#F5F5F7"), ShowInTaskbar = false };
        }
        private void PositionDialog()
        {
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = "精确放置后会立即停住，并保存位置。" });
            var x = new TextBox { Text = ((int)glide.X).ToString(), Margin = new Thickness(0, 10, 0, 8) };
            var y = new TextBox { Text = ((int)glide.Y).ToString(), Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(new TextBlock { Text = "水平位置 X（屏幕像素）" }); panel.Children.Add(x);
            panel.Children.Add(new TextBlock { Text = "垂直位置 Y（屏幕像素）" }); panel.Children.Add(y);
            var button = new Button { Content = "放到这里", Padding = new Thickness(12, 6, 12, 6) }; panel.Children.Add(button);
            var dialog = Dialog("鲸鱼娘的位置", panel, 340);
            button.Click += delegate
            {
                int px, py;
                if (!Int32.TryParse(x.Text, out px) || !Int32.TryParse(y.Text, out py)) { MessageBox.Show(dialog, "请输入整数坐标。", "鲸鱼娘"); return; }
                glide.Vx = glide.Vy = 0; glide.X = px; glide.Y = py; ClampToScreen(); MoveNative(); SavePosition(); dialog.Close();
            };
            dialog.ShowDialog();
        }
        private string CostText(SessionMetrics m)
        {
            if (!Program.Prefs.ReferencePricing || m == null || !m.HasUsage) return "费用：未设置参考单价；订阅消耗不能当作实际人民币账单。";
            if (!m.HasCache) return "费用：日志缺少缓存分类，无法可靠计算参考等值。";
            double uncached = m.Input - m.Cached;
            double amount = (uncached * Program.Prefs.InputPerMillion + m.Cached * Program.Prefs.CachedPerMillion + m.Output * Program.Prefs.OutputPerMillion) / 1000000;
            double saved = m.Cached * (Program.Prefs.InputPerMillion - Program.Prefs.CachedPerMillion) / 1000000;
            return "参考 API 等值：" + amount.ToString("0.0000") + " " + Program.Prefs.Currency + "\n缓存参考节省：" + saved.ToString("0.0000") + " " + Program.Prefs.Currency + "\n仅按你设置的单价计算，不代表订阅实际扣费。";
        }
        private void DetailsDialog()
        {
            var m = monitor.Current;
            string info = m == null ? "尚无可用会话日志。" :
                "模型：" + m.Model + "\n状态：" + m.State + "\n分段均速：" + (m.Speed.HasValue ? m.Speed.Value.ToString("0.00") + " tok/s" : "等待完整样本") +
                "\n" + sourceText.Text + "\n输入：" + m.Input.ToString("N0") + "\n其中缓存：" + m.Cached.ToString("N0") +
                "\n输出（含推理）：" + m.Output.ToString("N0") + "\n累计：" + m.Total.ToString("N0") +
                "\n本轮输出：" + (m.HasBaseline ? (m.Output - m.TurnOutputBaseline).ToString("N0") : "缺少起点") +
                "\n首 token 等待：" + (m.FirstTokenSeconds.HasValue ? m.FirstTokenSeconds.Value.ToString("0.00") + " 秒" : "尚无记录") +
                "\n无法解析的记录：" + m.MalformedLines + "，超长记录：" + m.OversizedLines;
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = info + "\n\n" + CostText(m), LineHeight = 23, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Margin = new Thickness(0, 16, 0, 12), TextWrapping = TextWrapping.Wrap, Foreground = Brush("#5B6B85"), LineHeight = 21,
                Text = "速度 = 真实输出 tokens / Reasoning 与 AgentMessage 输出时段的并集。排除工具时长；不足 0.5 秒的样本不显示。日志按完整响应写入，所以是分段更新的模型均速，不是逐 token 瞬时速度。\n\n默认统计最近有日志活动的会话，不识别侧栏中打开的空闲聊天。可右键锁定或选择历史会话。\n\n本工具只读本机日志，不读取登录凭据、不上传聊天、不调用付费 API。鲸鱼娘立绘来自 dsh-whale-musume，MIT 授权。" });
            var button = new Button { Content = "打开使用说明", Padding = new Thickness(8) }; panel.Children.Add(button);
            button.Click += delegate { Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Program.Root, "使用说明.md")) { UseShellExecute = true }); };
            Dialog("鲸鱼娘 · 统计详情", panel, 460).ShowDialog();
        }
        private void PricingDialog()
        {
            var panel = new StackPanel { Margin = new Thickness(24) };
            var enabled = new CheckBox { Content = "启用参考 API 等值计算", IsChecked = Program.Prefs.ReferencePricing, Margin = new Thickness(0, 0, 0, 12) }; panel.Children.Add(enabled);
            var fields = new List<TextBox>();
            string[] labels = { "未缓存输入单价 / 百万 tokens", "缓存输入单价 / 百万 tokens", "输出单价 / 百万 tokens" };
            double[] values = { Program.Prefs.InputPerMillion, Program.Prefs.CachedPerMillion, Program.Prefs.OutputPerMillion };
            for (int i = 0; i < 3; i++) { panel.Children.Add(new TextBlock { Text = labels[i] }); var input = new TextBox { Text = values[i].ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 5, 0, 12) }; fields.Add(input); panel.Children.Add(input); }
            var currency = new ComboBox { ItemsSource = new string[] { "USD", "CNY" }, SelectedItem = Program.Prefs.Currency, Margin = new Thickness(0, 0, 0, 14) }; panel.Children.Add(currency);
            panel.Children.Add(new TextBlock { Text = "按所选会话统一单价计算，不是订阅实付账单。混用模型时请分别锁定会话核对。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
            var button = new Button { Content = "保存", Padding = new Thickness(8) }; panel.Children.Add(button);
            var dialog = Dialog("参考费用设置", panel, 380);
            button.Click += delegate
            {
                double a, b, c;
                if (!Double.TryParse(fields[0].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out a) || !Double.TryParse(fields[1].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out b) || !Double.TryParse(fields[2].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out c) || a < 0 || b < 0 || c < 0 || !IsFinite(a + b + c))
                { MessageBox.Show(dialog, "请输入非负单价（小数点使用 .）。", "鲸鱼娘"); return; }
                Program.Prefs.InputPerMillion = a; Program.Prefs.CachedPerMillion = b; Program.Prefs.OutputPerMillion = c;
                Program.Prefs.Currency = Convert.ToString(currency.SelectedItem); Program.Prefs.ReferencePricing = enabled.IsChecked == true; SavePosition(); dialog.Close();
            };
            dialog.ShowDialog();
        }
        private static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private void WriteStatus()
        {
            Native.Rect rect; Native.GetWindowRect(handle, out rect); var m = monitor.Current;
            string json = Json.Serializer().Serialize(new { updated = DateTime.Now.ToString("o"),
                visible = Native.IsWindowVisible(handle), x = rect.Left, y = rect.Top, width = rect.Right - rect.Left, height = rect.Bottom - rect.Top,
                pose = currentPose, source = System.IO.Path.GetFileName(monitor.SelectedPath),
                model = m == null ? null : m.Model, state = m == null ? null : m.State,
                speed = m == null ? null : m.Speed, totalTokens = m == null ? 0 : m.Total, error = monitor.Error });
            try { File.WriteAllText(System.IO.Path.Combine(Program.Root, "运行状态.json"), json); }
            catch (IOException ex) { Program.Log(ex); }
            catch (UnauthorizedAccessException ex) { Program.Log(ex); }
            lastStatus = DateTime.UtcNow;
        }
        private void ExportPreview()
        {
            File.WriteAllText(System.IO.Path.Combine(Program.Root, "ui-check-result.txt"), "RUNNING");
            var checks = new List<string>();
            foreach (string path in Directory.GetFiles(System.IO.Path.Combine(Program.Root, "assets"), "dsh-whale-state-*.png"))
            {
                string pose = System.IO.Path.GetFileNameWithoutExtension(path).Replace("dsh-whale-state-", "");
                var source = ImageFor(pose);
                if (source.PixelWidth != 512 || source.PixelHeight != 512) throw new InvalidOperationException("立绘大小不一致：" + pose);
            }
            checks.Add("PASS loaded " + images.Count + " full-pose images");
            foreach (double value in new double[] { 0.85, 1, 1.15, 1.25 })
            {
                Program.Prefs.Scale = value; Program.Prefs.Compact = false; ApplyPreferences(); UpdateLayout();
                if (Math.Abs(ActualWidth - WidgetWidth * value) > 1 || Math.Abs(ActualHeight - WidgetHeight * value) > 1) throw new InvalidOperationException("缩放尺寸错误");
            }
            checks.Add("PASS compact redesign at 85%, 100%, 115%, 125%");
            Program.Prefs.Scale = 1; Program.Prefs.Compact = true; ApplyPreferences(); UpdateLayout();
            if (Math.Abs(ActualWidth - CompactWidth) > 1) throw new InvalidOperationException("紧凑模式尺寸错误");
            checks.Add("PASS compact window uses actual pet bounds");
            ShowSpeech("投喂成功，本鲸再吃一碗！", 3); bubble.UpdateLayout();
            stage.Measure(new Size(WidgetWidth, WidgetHeight)); stage.Arrange(new Rect(0, 0, WidgetWidth, WidgetHeight));
            viewbox.Measure(new Size(WidgetWidth, WidgetHeight)); viewbox.Arrange(new Rect(-CompactOffset, 0, WidgetWidth, WidgetHeight));
            viewport.Measure(new Size(CompactWidth, WidgetHeight)); viewport.Arrange(new Rect(0, 0, CompactWidth, WidgetHeight)); viewport.UpdateLayout();
            Point compactSpeech = bubble.TransformToAncestor(viewport).Transform(new Point());
            if (compactSpeech.X < 0 || compactSpeech.X + bubble.ActualWidth > ActualWidth + 1 || compactSpeech.Y + bubble.ActualHeight > ActualHeight + 1)
                throw new InvalidOperationException("仅宠物模式的气泡被裁切：x=" + compactSpeech.X + " y=" + compactSpeech.Y + " bubble=" + bubble.ActualWidth + "x" + bubble.ActualHeight + " window=" + ActualWidth + "x" + ActualHeight);
            checks.Add("PASS compact pet speech stays inside the small window");
            var r = BoundsForPosition(); glide.X = r.Left + 32; glide.Y = r.Top + 32; MoveNative();
            Native.Rect positioned; Native.GetWindowRect(handle, out positioned);
            if (positioned.Left != (int)glide.X || positioned.Top != (int)glide.Y) throw new InvalidOperationException("原生位置设置未生效");
            checks.Add("PASS native physical-pixel window placement readback");
            Program.Prefs.Compact = false; ApplyPreferences(); UpdateLayout();
            bubble.Visibility = Visibility.Hidden;
            currentPose = requestedPose = "idle-cute"; pet.Source = ImageFor("idle-cute");
            petScale.ScaleX = petScale.ScaleY = 1; petRotation.Angle = 0; petShift.Y = 0;
            // Public previews must not expose local usage, session labels, or model history.
            model.Text = "Codex"; stateText.Text = "● 工作中"; speed.Text = "42";
            total.Text = "会话 128K"; cache.Text = "缓存 90.0%";
            chart.Points = new PointCollection(new Point[] { new Point(0, 10), new Point(11, 6), new Point(22, 8), new Point(33, 3), new Point(44, 5) });
            stage.Measure(new Size(WidgetWidth, WidgetHeight)); stage.Arrange(new Rect(0, 0, WidgetWidth, WidgetHeight)); stage.UpdateLayout();
            foreach (TextBlock label in new TextBlock[] { model, stateText, speed, total, cache })
            {
                Point point = label.TransformToAncestor(stage).Transform(new Point());
                if (point.X < 0 || point.Y < 0 || point.X + label.ActualWidth > WidgetWidth || point.Y + label.ActualHeight > WidgetHeight)
                    throw new InvalidOperationException("数据标签超出窗口：" + label.Text);
            }
            checks.Add("PASS all metric labels fit the 320x176 window");
            var bitmap = new RenderTargetBitmap(WidgetWidth, WidgetHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(stage);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(System.IO.Path.Combine(Program.Root, "挂件预览.png"))) encoder.Save(stream);
            checks.Add("PASS exported actual WPF UI with synthetic demo metrics (not a benchmark)");
            string framesDir = System.IO.Path.Combine(Program.Root, "preview-frames"); Directory.CreateDirectory(framesDir);
            var demoTilt = new Spring(); var demoSqueeze = new Spring(); var demoHop = new Spring();
            for (int frame = 0; frame < 72; frame++)
            {
                double time = frame / 18.0;
                if (frame == 0) pet.Source = ImageFor("daily-coffee");
                if (frame == 18) { pet.Source = ImageFor("pick-up"); demoSqueeze.Velocity = 1.3; }
                if (frame == 39) { pet.Source = ImageFor("blush"); demoSqueeze.Velocity = -2; demoHop.Velocity = -50 * PetSize / 238; }
                if (frame == 56) { pet.Source = ImageFor("eat"); demoHop.Velocity = -70 * PetSize / 238; }
                demoTilt.Step(frame >= 18 && frame < 39 ? Math.Sin(time * 5) * 10 : 0, 1.0 / 18);
                demoSqueeze.Step(frame >= 18 && frame < 39 ? 0.05 : 0, 1.0 / 18); demoHop.Step(0, 1.0 / 18);
                petRotation.Angle = demoTilt.Value;
                petScale.ScaleX = 1 - demoSqueeze.Value * 0.48 - Math.Sin(time * 3) * 0.006;
                petScale.ScaleY = 1 + demoSqueeze.Value + Math.Sin(time * 3) * 0.014;
                petShift.Y = demoHop.Value;
                var animationFrame = new RenderTargetBitmap(WidgetWidth, WidgetHeight, 96, 96, PixelFormats.Pbgra32); animationFrame.Render(stage);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(animationFrame));
                using (var stream = File.Create(System.IO.Path.Combine(framesDir, frame.ToString("000") + ".png"))) png.Save(stream);
            }
            checks.Add("PASS exported 72 animation frames using production spring physics (illustrative motion, not mouse automation)");
            Program.Prefs.Scale = 0.85; ApplyPreferences(); UpdateLayout();
            var mini = new RenderTargetBitmap((int)Math.Ceiling(Width), (int)Math.Ceiling(Height), 96, 96, PixelFormats.Pbgra32); mini.Render(viewport);
            var miniEncoder = new PngBitmapEncoder(); miniEncoder.Frames.Add(BitmapFrame.Create(mini));
            using (var stream = File.Create(System.IO.Path.Combine(Program.Root, "迷你预览.png"))) miniEncoder.Save(stream);
            checks.Add("PASS mini export at 272x150 logical pixels");
            File.WriteAllLines(System.IO.Path.Combine(Program.Root, "ui-check-result.txt"), checks);
        }
    }
}
