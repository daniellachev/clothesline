using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using IOPath = System.IO.Path;

namespace Clothesline
{
    public static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    }

    public static class Program
    {
        public static readonly string DataFolder = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clothesline");
        public static readonly string StartupLink = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Clothesline.lnk");
        public static string Executable { get { return System.Reflection.Assembly.GetExecutingAssembly().Location; } }
        private static Mutex single;

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Contains("--self-test")) return Tests.Run(args);
            if (args.Contains("--integration-test")) return Tests.Integration(args);
            if (args.Contains("--diagnostics"))
            {
                var store = new History(DataFolder); store.Load();
                File.WriteAllText(IOPath.Combine(DataFolder, "diagnostics.txt"), "Items=" + store.Data.Items.Count + "\r\nPaused=" + store.Data.Paused + "\r\n" + store.LastError);
                return 0;
            }
            bool created;
            bool demo = args.Contains("--demo");
            single = new Mutex(true, demo ? "Local\\Clothesline.Demo" : "Local\\Clothesline.Desktop", out created);
            if (!created)
            {
                var window = Native.FindWindow(null, demo ? "Clothesline Demo" : "Clothesline");
                if (window != IntPtr.Zero) Native.PostMessage(window, 0x8001, IntPtr.Zero, IntPtr.Zero);
                return 0;
            }
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                {
                    Directory.CreateDirectory(DataFolder);
                    File.AppendAllText(IOPath.Combine(DataFolder, "errors.log"), DateTime.Now.ToString("s") + " " + e.Exception.GetType().Name + "\r\n" + e.Exception.StackTrace + "\r\n");
                    e.Handled = true;
                };
                var history = new History(DataFolder);
                if (!demo) history.Load(); else history.Data.Items = Tests.DemoItems();
                if (demo && args.Contains("--ten"))
                    while (history.Data.Items.Count < 10) history.Data.Items.Add(Tests.DemoItems()[history.Data.Items.Count % 5]);
                var clothesline = new LineWindow(history, demo, args.Contains("--background"));
                app.MainWindow = clothesline;
                clothesline.Show();
                if (demo && args.Contains("--preview"))
                    clothesline.Dispatcher.BeginInvoke(new Action(delegate { clothesline.SavePreview(IOPath.Combine(IOPath.GetDirectoryName(Program.Executable), "preview.png")); }), DispatcherPriority.ApplicationIdle);
                app.Run();
                return 0;
            }
            finally { single.ReleaseMutex(); single.Dispose(); }
        }
    }

    public sealed class LineWindow : Window
    {
        private readonly History history;
        private readonly bool demo;
        private readonly bool startHidden;
        private readonly Canvas canvas = new Canvas();
        private readonly List<RotateTransform> swings = new List<RotateTransform>();
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly HashSet<string> pendingScreenshots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer captureTimer = new DispatcherTimer();
        private readonly DispatcherTimer hideTimer = new DispatcherTimer();
        private HwndSource source;
        private IntPtr handle;
        private Forms.NotifyIcon tray;
        private TextBlock status;
        private int attempts;
        private uint sequence;
        private bool closing;
        private bool hiddenAnimation;
        private DateTime lastToggle = DateTime.MinValue;
        private double cardWidth;
        private string bindingStatus = "";
        private int toggleCount;
        private bool pauseKey, homeKey, backupKey;

        public LineWindow(History history, bool demo, bool startHidden)
        {
            this.history = history; this.demo = demo; this.startHidden = startHidden;
            Title = demo ? "Clothesline Demo" : "Clothesline";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize; ShowInTaskbar = demo; Topmost = true; ShowActivated = false;
            Width = SystemParameters.PrimaryScreenWidth; Height = 350;
            Content = canvas;
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            SourceInitialized += OnSourceInitialized;
            Loaded += delegate
            {
                Position(); RenderLine();
                if (startHidden) Hide(); else AnimateIn();
                if (history.LastError != null) Notify(history.LastError);
            };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!closing) { e.Cancel = true; HideLine(); } };
            history.Changed += delegate { if (IsVisible) RenderLine(); UpdateTray(); };
            captureTimer.Interval = TimeSpan.FromMilliseconds(85);
            captureTimer.Tick += CaptureTick;
            hideTimer.Interval = TimeSpan.FromMilliseconds(550);
            hideTimer.Tick += delegate { hideTimer.Stop(); HideLine(); };
            PreviewKeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) HideLine(); };
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            handle = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(handle); source.AddHook(Messages);
            if (demo) return;
            if (!Native.AddClipboardFormatListener(handle)) throw new InvalidOperationException("Clipboard listener could not start.");
            pauseKey = Native.RegisterHotKey(handle, 4101, 0x4000, 0x13);
            backupKey = Native.RegisterHotKey(handle, 4103, 0x4007, 0x43);
            SetupTray(); WatchScreenshots();
            Directory.CreateDirectory(history.Folder);
            ApplyHomeKey();
            if (!homeKey && !pauseKey && !backupKey) Notify("Another app is using all of Clothesline's shortcuts. Use the tray icon to show the line.");
        }

        // Home is optional because a global Home hotkey takes the key away from every text editor.
        private void ApplyHomeKey()
        {
            Native.UnregisterHotKey(handle, 4102);
            homeKey = !history.Data.HomeKeyOff && Native.RegisterHotKey(handle, 4102, 0x4000, 0x24);
            bindingStatus = "Pause=" + pauseKey + "; Home=" + homeKey + "; CtrlAltShiftC=" + backupKey;
            File.WriteAllText(IOPath.Combine(history.Folder, "runtime.txt"), bindingStatus + "\r\nClipboard listener=True\r\n");
            if (!history.Data.HomeKeyOff && !homeKey) Notify("Another app is using the Home key. " + ShowKey + " still shows the line.");
            UpdateTray();
            if (IsVisible) RenderLine();
        }

        private string ShowKey { get { return demo || homeKey ? "Home" : pauseKey ? "Pause" : "Ctrl+Alt+Shift+C"; } }

        private IntPtr Messages(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x031D && !demo && !history.Data.Paused)
            {
                sequence = Native.GetClipboardSequenceNumber(); attempts = 0;
                // Try immediately so quick successive copies are less likely to be coalesced.
                CaptureTick(null, EventArgs.Empty);
            }
            if (msg == 0x0312 || msg == 0x8001)
            {
                handled = true;
                if ((DateTime.Now - lastToggle).TotalMilliseconds > 260)
                {
                    lastToggle = DateTime.Now; toggleCount++; Toggle();
                    if (!demo) File.WriteAllText(IOPath.Combine(history.Folder, "runtime.txt"), bindingStatus + "\r\nClipboard listener=True\r\nToggles=" + toggleCount + "\r\nVisible=" + IsVisible + "\r\n");
                }
            }
            if (msg == 0x8002) { handled = true; Exit(); }
            if (msg == 0x007E || msg == 0x02E0) Dispatcher.BeginInvoke(new Action(delegate { Position(); if (IsVisible) RenderLine(); }));
            if (msg == 0x0021) { handled = true; return new IntPtr(3); } // Keep the user's paste target focused.
            return IntPtr.Zero;
        }

        private void CaptureTick(object sender, EventArgs args)
        {
            captureTimer.Stop();
            if (history.Data.Paused) return;
            try
            {
                uint before = Native.GetClipboardSequenceNumber();
                Clip clip = ClipboardIO.Read();
                if (Native.GetClipboardSequenceNumber() != before) { captureTimer.Start(); return; }
                history.Add(clip);
                if (ClipboardIO.LastNotice != null) Notify(ClipboardIO.LastNotice);
                if (history.LastError != null) Notify(history.LastError);
            }
            catch (Exception ex)
            {
                if (Native.GetClipboardSequenceNumber() != sequence) { sequence = Native.GetClipboardSequenceNumber(); attempts = 0; }
                if (++attempts < 12) captureTimer.Start();
                else Notify("Could not read that copy. Try copying it again. (" + ex.GetType().Name + ")");
            }
        }

        private void WatchScreenshots()
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            folders.Add(IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"));
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
            {
                string saved = key == null ? null : key.GetValue("{B7BEDE81-DF94-4682-A7D8-57A52620B86F}") as string;
                if (!String.IsNullOrEmpty(saved)) folders.Add(Environment.ExpandEnvironmentVariables(saved));
            }
            foreach (var folder in folders.Where(Directory.Exists))
            {
                try
                {
                    var watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
                    watcher.Created += delegate(object s, FileSystemEventArgs e) { QueueScreenshot(e.FullPath); };
                    watcher.Renamed += delegate(object s, RenamedEventArgs e) { QueueScreenshot(e.FullPath); };
                    watchers.Add(watcher);
                }
                catch { }
            }
        }

        private void QueueScreenshot(string file)
        {
            string ext = IOPath.GetExtension(file).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (history.Data.Paused || !pendingScreenshots.Add(file)) return;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                int tries = 0;
                timer.Tick += delegate
                {
                    if (history.Data.Paused) { timer.Stop(); pendingScreenshots.Remove(file); return; }
                    try
                    {
                        if (new FileInfo(file).Length > 32 * 1024 * 1024) throw new InvalidOperationException();
                        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            if ((long)image.PixelWidth * image.PixelHeight > 60000000) throw new InvalidOperationException();
                            var clip = new Clip { Kind = "image", Png = Clothesline.Clip.Encode(image) }; clip.Identify(); history.Add(clip);
                        }
                        timer.Stop(); pendingScreenshots.Remove(file);
                    }
                    catch { if (++tries >= 8) { timer.Stop(); pendingScreenshots.Remove(file); } }
                };
                timer.Start();
            }));
        }

        private void Position()
        {
            var screen = Forms.Screen.FromHandle(Native.GetForegroundWindow());
            var scale = source == null ? Matrix.Identity : source.CompositionTarget.TransformFromDevice;
            var origin = scale.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
            Left = origin.X; Top = origin.Y;
            Width = screen.WorkingArea.Width * scale.M11;
            Height = Math.Min(350, screen.WorkingArea.Height * scale.M22);
        }

        public void Toggle() { if (IsVisible && !hiddenAnimation) HideLine(); else ShowLine(); }
        private void ShowLine()
        {
            hideTimer.Stop(); hiddenAnimation = false; BeginAnimation(OpacityProperty, null); Opacity = 1;
            Position(); RenderLine(); Show(); AnimateIn();
        }

        private void HideLine()
        {
            hideTimer.Stop();
            if (!IsVisible || hiddenAnimation) return;
            hiddenAnimation = true;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            fade.Completed += delegate { if (hiddenAnimation) { Hide(); hiddenAnimation = false; BeginAnimation(OpacityProperty, null); Opacity = 1; } };
            BeginAnimation(OpacityProperty, fade);
        }

        private void AnimateIn()
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            for (int i = 0; i < swings.Count; i++)
            {
                double rest = RestAngle(i);
                var motion = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1250 + i * 35) };
                motion.KeyFrames.Add(new EasingDoubleKeyFrame(rest + (i % 2 == 0 ? -8 : 8), KeyTime.FromPercent(0)));
                motion.KeyFrames.Add(new EasingDoubleKeyFrame(rest - (i % 2 == 0 ? -3 : 3), KeyTime.FromPercent(.3), new SineEase { EasingMode = EasingMode.EaseInOut }));
                motion.KeyFrames.Add(new EasingDoubleKeyFrame(rest + (i % 2 == 0 ? -1 : 1), KeyTime.FromPercent(.65), new SineEase { EasingMode = EasingMode.EaseInOut }));
                motion.KeyFrames.Add(new EasingDoubleKeyFrame(rest, KeyTime.FromPercent(1), new SineEase { EasingMode = EasingMode.EaseOut }));
                swings[i].BeginAnimation(RotateTransform.AngleProperty, motion);
            }
        }

        private static SolidColorBrush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }
        private static double RestAngle(int index) { return new double[] { -3, 2.5, -1.8, 3.5, -2.3, 1.4, -3.2, 2.2, -1.3, 3 }[index % 10]; }
        private static double RopeY(double ratio) { return 69 + 23 * 4 * ratio * (1 - ratio); }

        private void RenderLine()
        {
            canvas.Children.Clear(); swings.Clear();
            double width = Math.Max(480, Width);
            // A real transparent overlay: the desktop remains visible between the notes.
            var ropeGeometry = new StreamGeometry();
            using (var dc = ropeGeometry.Open())
            {
                dc.BeginFigure(new Point(14, 69), false, false);
                dc.BezierTo(new Point(width / 3, 100), new Point(width * 2 / 3, 100), new Point(width - 14, 69), true, false);
            }
            ropeGeometry.Freeze();
            var ropeShadow = new System.Windows.Shapes.Path { Data = ropeGeometry, Stroke = Brush("#80000000"), StrokeThickness = 5, IsHitTestVisible = false, RenderTransform = new TranslateTransform(0, 2) };
            var rope = new System.Windows.Shapes.Path { Data = ropeGeometry, Stroke = Brush("#CDAE80"), StrokeThickness = 3, IsHitTestVisible = false };
            var thread = new System.Windows.Shapes.Path { Data = ropeGeometry, Stroke = Brush("#F5E4C5"), StrokeThickness = .7, StrokeDashArray = new DoubleCollection { 3, 3 }, IsHitTestVisible = false };
            canvas.Children.Add(ropeShadow); canvas.Children.Add(rope); canvas.Children.Add(thread);
            AddEnd(14); AddEnd(width - 18);

            int count = history.Data.Items.Count;
            double step = Math.Min(197, (width - 88) / Math.Max(5, count));
            cardWidth = Math.Min(170, step - 23);
            double start = (width - step * Math.Max(1, count)) / 2 + step / 2;
            if (count == 0)
            {
                var empty = new Clip { Kind = "text", Text = "A little room\nfor your next idea.\n\nCopy some text or take a screenshot. It will hang here.", Created = DateTime.Now };
                AddCard(empty, 0, width / 2, 218, true);
            }
            else for (int i = 0; i < count; i++) AddCard(history.Data.Items[i], i, start + i * step, cardWidth, false);

            var header = new Border
            {
                Background = Brush("#F2222928"), BorderBrush = Brush("#5364615A"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(21), Padding = new Thickness(14, 6, 8, 6),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = .24 }
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Brush(history.Data.Paused ? "#E6AD68" : "#AED4A3"), Margin = new Thickness(0, 0, 9, 0) });
            row.Children.Add(new TextBlock { Text = "clothesline", Foreground = Brush("#F8F5EB"), FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = "  " + count + " / 10", Foreground = Brush("#A9B6AD"), FontSize = 12, Margin = new Thickness(5, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            var home = HeaderButton((ShowKey.Length > 5 ? "Hide" : ShowKey) + "  ↓", "Hide the line", delegate { HideLine(); });
            row.Children.Add(home);
            var menu = HeaderButton("•••", "Pause, startup and other options", null);
            menu.Click += delegate { var options = BuildMenu(); menu.ContextMenu = options; options.PlacementTarget = menu; options.IsOpen = true; };
            row.Children.Add(menu); header.Child = row;
            header.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
            Canvas.SetLeft(header, (width - header.DesiredSize.Width) / 2); Canvas.SetTop(header, 10); canvas.Children.Add(header);
            status = new TextBlock
            {
                Text = history.Data.Paused ? "Capture paused" : count == 0 ? "Your last 10 copies, hung out to dry." : "Click to copy  ·  Right-click for more  ·  " + ShowKey + " to tuck away",
                Foreground = Brush("#F2EADC"), FontSize = 12, TextAlignment = TextAlignment.Center,
                Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Opacity = .9 }, IsHitTestVisible = false
            };
            status.Width = width; Canvas.SetTop(status, 309); canvas.Children.Add(status);
        }

        private void AddEnd(double x)
        {
            var end = new Ellipse { Width = 7, Height = 7, Fill = Brush("#E2CDA9"), Stroke = Brush("#79654B"), StrokeThickness = 1, IsHitTestVisible = false };
            Canvas.SetLeft(end, x - 3); Canvas.SetTop(end, 65); canvas.Children.Add(end);
        }

        private Button HeaderButton(string text, string tooltip, Action action)
        {
            var button = new Button { Content = text, ToolTip = tooltip, Foreground = Brush("#EBEFE6"), Background = Brush("#3B4640"), BorderThickness = new Thickness(0), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(3, 0, 0, 0), FontSize = 11, Cursor = Cursors.Hand, Focusable = false };
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12)); border.SetValue(Border.BackgroundProperty, button.Background);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(ContentPresenter.MarginProperty, button.Padding); border.AppendChild(presenter); template.VisualTree = border; button.Template = template;
            if (action != null) button.Click += delegate { action(); };
            return button;
        }

        private void AddCard(Clip clip, int index, double center, double width, bool empty)
        {
            string[] paper = { "#FFF5D7", "#E1EBD9", "#F4E5DE", "#E0E9EE", "#EFE2C7" };
            var hanger = new Canvas { Width = width, Height = 208, RenderTransformOrigin = new Point(.5, .025) };
            var rotation = new RotateTransform(RestAngle(index));
            var scale = new ScaleTransform(1, 1); var lift = new TranslateTransform();
            var transforms = new TransformGroup(); transforms.Children.Add(rotation); transforms.Children.Add(scale); transforms.Children.Add(lift); hanger.RenderTransform = transforms;
            swings.Add(rotation);
            double height = clip.Kind == "image" ? 184 : 177 + (index % 3) * 6;
            var sheet = new Border
            {
                Width = width, Height = height, Background = Brush(clip.Kind == "image" ? "#FFFEF8" : paper[index % paper.Length]),
                CornerRadius = new CornerRadius(2, 2, 5, 2), BorderBrush = Brush("#30FFFFFF"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 6, Direction = 270, Opacity = .29 },
                Cursor = empty ? Cursors.Arrow : Cursors.Hand
            };
            var content = new Grid { Margin = new Thickness(clip.Kind == "image" ? 7 : 13, 17, clip.Kind == "image" ? 7 : 13, 10) };
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(23) });
            var label = new TextBlock { Text = empty ? "FRESH AIR FOR YOUR CLIPBOARD" : clip.Label, FontSize = empty ? 9 : 8.5, FontWeight = FontWeights.SemiBold, Foreground = Brush("#78776B"), TextTrimming = TextTrimming.CharacterEllipsis };
            content.Children.Add(label);
            if (clip.Kind == "image")
            {
                var image = new Image { Source = clip.Image(400), Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
                var mat = new Border { Background = Brush("#EAEDE7"), Child = image, ClipToBounds = true };
                Grid.SetRow(mat, 1); content.Children.Add(mat);
            }
            else
            {
                var note = new TextBlock { Text = clip.Summary.Length > 900 ? clip.Summary.Substring(0, 900) : clip.Summary, FontFamily = new FontFamily(empty ? "Segoe UI" : "Segoe Print"), FontSize = empty ? 15 : width < 130 ? 10.5 : 12.5, LineHeight = empty ? 22 : 21, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush("#333E37"), ClipToBounds = true };
                Grid.SetRow(note, 1); content.Children.Add(note);
            }
            var footer = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
            footer.Children.Add(new TextBlock { Text = empty ? "Make yourself at home." : clip.Created.ToString("HH:mm"), Foreground = Brush("#8A8A7D"), FontSize = 10 });
            if (!empty) footer.Children.Add(new TextBlock { Text = index == 0 ? "latest ↑" : (index + 1).ToString("00"), Foreground = Brush("#8A8A7D"), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Right });
            Grid.SetRow(footer, 2); content.Children.Add(footer); sheet.Child = content;
            Canvas.SetTop(sheet, 7); hanger.Children.Add(sheet);

            var peg = new Border { Width = 13, Height = 34, CornerRadius = new CornerRadius(2), Background = new LinearGradientBrush(Color.FromRgb(224, 188, 131), Color.FromRgb(172, 129, 72), 0), BorderBrush = Brush("#C99761"), BorderThickness = new Thickness(1), Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 2, Opacity = .3 }, IsHitTestVisible = false };
            var pegLines = new Canvas();
            var groove = new Rectangle { Width = 1, Height = 25, Fill = Brush("#A47649") }; Canvas.SetLeft(groove, 5); Canvas.SetTop(groove, 3); pegLines.Children.Add(groove);
            var spring = new Rectangle { Width = 11, Height = 3, Fill = Brush("#8E8876") }; Canvas.SetTop(spring, 16); pegLines.Children.Add(spring); peg.Child = pegLines;
            Canvas.SetLeft(peg, width / 2 - 6.5); Canvas.SetTop(peg, -11); hanger.Children.Add(peg);
            Canvas.SetLeft(hanger, center - width / 2); Canvas.SetTop(hanger, RopeY(center / Width) - 5); canvas.Children.Add(hanger);
            if (empty) return;
            System.Windows.Automation.AutomationProperties.SetName(sheet, "Copy " + clip.Label.ToLowerInvariant() + " " + (index + 1));
            sheet.ToolTip = "Click to copy. Right-click to preview or remove.";
            sheet.MouseEnter += delegate
            {
                Panel.SetZIndex(hanger, 20);
                rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.045, TimeSpan.FromMilliseconds(160)));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.045, TimeSpan.FromMilliseconds(160)));
                lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, TimeSpan.FromMilliseconds(160)));
            };
            sheet.MouseLeave += delegate
            {
                Panel.SetZIndex(hanger, 0);
                rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(RestAngle(index), TimeSpan.FromMilliseconds(220)));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
                lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
            };
            sheet.MouseLeftButtonUp += delegate { if (!demo) Copy(clip, false); };
            var menu = new ContextMenu();
            Menu(menu, "Copy", delegate { Copy(clip, false); });
            if (clip.Kind == "text") Menu(menu, "Copy as plain text", delegate { Copy(clip, true); });
            Menu(menu, "Preview", delegate { Preview(clip); });
            menu.Items.Add(new Separator());
            Menu(menu, "Take off the line", delegate { history.Remove(clip); });
            sheet.ContextMenu = menu;
        }

        private static void Menu(ContextMenu menu, string label, Action action)
        {
            var item = new MenuItem { Header = label }; item.Click += delegate { action(); }; menu.Items.Add(item);
        }

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            Menu(menu, history.Data.Paused ? "Resume remembering" : "Pause remembering", delegate { history.TogglePause(); });
            var startup = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = File.Exists(Program.StartupLink) };
            startup.Click += delegate { SetStartup(startup.IsChecked); }; menu.Items.Add(startup);
            if (!demo)
            {
                var homeItem = new MenuItem { Header = "Use the Home key", IsCheckable = true, IsChecked = !history.Data.HomeKeyOff };
                homeItem.Click += delegate { history.ToggleHomeKey(); ApplyHomeKey(); }; menu.Items.Add(homeItem);
            }
            menu.Items.Add(new Separator());
            Menu(menu, "Clear the line…", delegate
            {
                if (MessageBox.Show("Remove all saved items from Clothesline?", "Clear the line", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) history.Clear();
            });
            Menu(menu, "How it works", Help);
            Menu(menu, "Quit Clothesline", Exit);
            return menu;
        }

        private void Copy(Clip clip, bool plain)
        {
            hideTimer.Stop();
            int retries = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            Action attempt = delegate
            {
                try
                {
                    ClipboardIO.Write(clip, plain); timer.Stop();
                    if (status != null) status.Text = "Copied. Ready to paste with Ctrl+V.";
                    hideTimer.Start();
                }
                catch (IOException ex) { timer.Stop(); Notify(ex.Message); }
                catch { if (++retries < 10) timer.Start(); else { timer.Stop(); Notify("The clipboard is busy. Click the item again."); } }
            };
            timer.Tick += delegate { attempt(); }; attempt();
        }

        private void Preview(Clip clip)
        {
            var preview = new Window { Title = "Clothesline · " + clip.Label, Width = Math.Min(950, SystemParameters.WorkArea.Width - 80), Height = Math.Min(650, SystemParameters.WorkArea.Height - 80), WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Brush("#FAF7EF"), Topmost = true, FontFamily = FontFamily };
            var dock = new DockPanel { Margin = new Thickness(22) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
            var copy = new Button { Content = "Copy to clipboard", Padding = new Thickness(15, 8, 15, 8) };
            copy.Click += delegate { Copy(clip, false); preview.Close(); }; actions.Children.Add(copy);
            actions.Children.Add(new TextBlock { Text = "   " + clip.Created.ToString("dd MMM, HH:mm"), Foreground = Brush("#78776B"), VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(actions, Dock.Top); dock.Children.Add(actions);
            if (clip.Kind == "image") dock.Children.Add(new ScrollViewer { Content = new Image { Source = clip.Image(0), Stretch = Stretch.Uniform } });
            else dock.Children.Add(new TextBox { Text = clip.Kind == "files" ? String.Join("\r\n", clip.Files) : clip.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 16, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(5) });
            preview.Content = dock; preview.PreviewKeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) preview.Close(); };
            preview.Show(); preview.Activate();
        }

        private void SetupTray()
        {
            tray = new Forms.NotifyIcon { Text = "Clothesline · Home to show", Icon = CreateIcon(), Visible = true };
            tray.MouseClick += delegate(object s, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(new Action(Toggle)); };
            var menu = new Forms.ContextMenuStrip();
            menu.Opening += delegate
            {
                menu.Items.Clear();
                menu.Items.Add("Show / hide clothesline", null, delegate { Toggle(); });
                menu.Items.Add(history.Data.Paused ? "Resume remembering" : "Pause remembering", null, delegate { history.TogglePause(); });
                var start = new Forms.ToolStripMenuItem("Start with Windows") { Checked = File.Exists(Program.StartupLink) };
                start.Click += delegate { SetStartup(!start.Checked); }; menu.Items.Add(start);
                var homeKeyItem = new Forms.ToolStripMenuItem("Use the Home key") { Checked = !history.Data.HomeKeyOff };
                homeKeyItem.Click += delegate { history.ToggleHomeKey(); ApplyHomeKey(); }; menu.Items.Add(homeKeyItem);
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("How it works", null, delegate { Help(); });
                menu.Items.Add("Quit", null, delegate { Exit(); });
            };
            tray.ContextMenuStrip = menu;
        }

        private static System.Drawing.Icon CreateIcon()
        {
            using (var bitmap = new System.Drawing.Bitmap(32, 32))
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(223, 193, 143), 2)) g.DrawBezier(pen, 0, 7, 10, 14, 22, 14, 32, 7);
                using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(249, 240, 206))) g.FillRectangle(brush, 8, 12, 17, 17);
                using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(184, 138, 79))) g.FillRectangle(brush, 14, 6, 4, 12);
                IntPtr raw = bitmap.GetHicon();
                var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(raw).Clone(); Native.DestroyIcon(raw); return icon;
            }
        }

        private void UpdateTray() { if (tray != null) tray.Text = "Clothesline · " + (history.Data.Paused ? "paused" : history.Data.Items.Count + " / 10 items") + " · " + ShowKey; }
        public void SavePreview(string file)
        {
            foreach (var swing in swings) swing.BeginAnimation(RotateTransform.AngleProperty, null);
            canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
        }
        private void Notify(string message)
        {
            if (status != null && IsVisible) status.Text = message;
            if (tray != null) { tray.BalloonTipTitle = "Clothesline"; tray.BalloonTipText = message; tray.ShowBalloonTip(4000); }
        }

        private void Help()
        {
            MessageBox.Show("Press Home to show or hide your clothesline.\n\nCopy text, links, files or images as usual. Screenshots from Win+Shift+S are remembered once they reach the clipboard. New images saved in your Windows Screenshots folder are also watched.\n\nClick a hanging item to copy it back, then paste with Ctrl+V. Right-click to preview or remove it. The latest 10 distinct items are saved locally, encrypted for your Windows account.\n\nPause and Ctrl+Alt+Shift+C also work. If you need Home in your editor, turn off \"Use the Home key\" in the ••• or tray menu.\n\nFile entries remember paths, not backup copies. Unsupported app-specific objects are not stored. Apps that mark clipboard content as private are respected.\n\nUse the tray icon or ••• for pause, startup and quit.", "About Clothesline", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SetStartup(bool enabled)
        {
            try
            {
                if (!enabled) { if (File.Exists(Program.StartupLink)) File.Delete(Program.StartupLink); return; }
                Type type = Type.GetTypeFromProgID("WScript.Shell"); object shell = Activator.CreateInstance(type);
                object shortcut = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { Program.StartupLink });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Program.Executable });
                shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "--background" });
                shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { IOPath.GetDirectoryName(Program.Executable) });
                shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
                Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell);
            }
            catch { Notify("Could not change startup. Use the Start menu shortcut to launch Clothesline."); }
        }

        private void Exit()
        {
            closing = true; captureTimer.Stop(); hideTimer.Stop();
            foreach (var watcher in watchers) watcher.Dispose();
            if (!demo)
            {
                Native.RemoveClipboardFormatListener(handle);
                for (int id = 4101; id <= 4103; id++) Native.UnregisterHotKey(handle, id);
            }
            if (source != null) source.RemoveHook(Messages);
            if (tray != null) { var icon = tray.Icon; tray.Visible = false; tray.Dispose(); icon.Dispose(); }
            Application.Current.Shutdown();
        }
    }
}
