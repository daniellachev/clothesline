using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clothesline
{
    public static class Tests
    {
        private static readonly List<string> Results = new List<string>();
        private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Results.Add("PASS " + name); }
        private static Clip Text(string text) { var clip = new Clip { Kind = "text", Text = text }; clip.Identify(); return clip; }
        private static string Output(string[] args) { return args.Length > 1 ? args[1] : Path.Combine(Path.GetDirectoryName(Program.Executable), "test-results.txt"); }

        public static int Run(string[] args)
        {
            string folder = Path.Combine(Path.GetTempPath(), "Clothesline-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var history = new History(folder);
                for (int i = 0; i < 14; i++) history.Add(Text("Item " + i));
                Check(history.Data.Items.Count == 10, "Only 10 entries retained after 14 copies");
                Check(history.Data.Items[0].Text == "Item 13" && history.Data.Items[9].Text == "Item 4", "Newest-first ordering and oldest-item eviction");
                history.Add(Text("Item 7"));
                Check(history.Data.Items.Count == 10 && history.Data.Items[0].Text == "Item 7", "Duplicate is moved to front without consuming a slot");
                history.Add(Text("Item 7")); Check(history.Data.Items.Count == 10, "Consecutive duplicate ignored");
                history.TogglePause(); history.Add(Text("Paused copy"));
                Check(history.Data.Items[0].Text == "Item 7", "Pause does not record content");
                history.TogglePause();
                var image = new Clip { Kind = "image", Png = DemoImage() }; image.Identify(); history.Add(image);
                var rich = Text("Здравей, world! 🌿\nSecond line"); rich.Html = "<b>Здравей</b>"; rich.Rtf = @"{\rtf1 Test}"; history.Add(rich);
                var files = new Clip { Kind = "files", Files = new[] { @"C:\Example\notes.txt" } }; files.Identify(); history.Add(files);
                var reload = new History(folder); reload.Load();
                Check(reload.LastError == null && reload.Data.Items.Count == 10, "Encrypted history loads after restart");
                Check(reload.Data.Items[0].Files[0] == files.Files[0] && reload.Data.Items[1].Text == rich.Text && reload.Data.Items[1].Html == rich.Html && reload.Data.Items[1].Rtf == rich.Rtf, "Unicode, rich text and file paths round-trip");
                Check(reload.Data.Items[2].Png.SequenceEqual(image.Png), "Full image bytes survive restart");
                Check(reload.Data.Items[2].Image(0).PixelWidth == 600, "Stored image decodes at original resolution");
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(history.FilePath)).Contains("Item 7"), "Saved history does not contain plain-text clipboard content");
                reload.Remove(reload.Data.Items[0]); Check(reload.Data.Items.Count == 9, "Individual removal");
                reload.Clear(); var empty = new History(folder); empty.Load(); Check(empty.Data.Items.Count == 0, "Clear persists across restart");
                Check(!empty.Data.HomeKeyOff, "Home key is on by default");
                empty.ToggleHomeKey(); var settings = new History(folder); settings.Load();
                Check(settings.LastError == null && settings.Data.HomeKeyOff, "Home key setting persists across restart");
                File.WriteAllBytes(empty.FilePath, new byte[] { 1, 2, 3 }); empty.Load();
                Check(empty.LastError != null && empty.Data.Items.Count == 0 && Directory.GetFiles(folder, "*.unreadable-*").Length == 1, "Corrupt history is preserved and handled");
                File.WriteAllLines(Output(args), Results); return 0;
            }
            catch (Exception ex) { Results.Add("FAIL " + ex); File.WriteAllLines(Output(args), Results); return 1; }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }

        public static int Integration(string[] args)
        {
            string folder = Path.Combine(Path.GetTempPath(), "Clothesline-integration-" + Guid.NewGuid().ToString("N"));
            DataObject backup = null;
            uint lastWritten = 0;
            HwndSource source = null;
            try
            {
                // Materialize existing clipboard formats before testing, without logging contents.
                var original = Clipboard.GetDataObject(); backup = new DataObject();
                if (original != null)
                {
                    foreach (string format in original.GetFormats(false))
                    {
                        object data = original.GetData(format, false);
                        var stream = data as MemoryStream;
                        if (stream != null) data = new MemoryStream(stream.ToArray());
                        if (data != null) backup.SetData(format, data);
                    }
                }
                var history = new History(folder); int notifications = 0;
                source = new HwndSource(new HwndSourceParameters("Clothesline integration listener") { Width = 0, Height = 0, WindowStyle = 0 });
                source.AddHook(delegate(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
                {
                    if (msg == 0x31D) { notifications++; history.Add(ClipboardIO.Read()); }
                    return IntPtr.Zero;
                });
                Check(Native.AddClipboardFormatListener(source.Handle), "Windows clipboard listener registration");
                for (int i = 0; i < 12; i++)
                {
                    Clipboard.SetText("Clothesline integration " + i); lastWritten = Native.GetClipboardSequenceNumber(); Pump();
                }
                Check(notifications >= 12 && history.Data.Items.Count == 10 && history.Data.Items[0].Text.EndsWith("11") && history.Data.Items[9].Text.EndsWith("2"), "Real clipboard updates are captured and capped at 10");
                var dataObject = new DataObject(); dataObject.SetText("Private test"); dataObject.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1 })); Clipboard.SetDataObject(dataObject, true); lastWritten = Native.GetClipboardSequenceNumber(); Pump();
                Check(history.Data.Items[0].Text.EndsWith("11"), "Private clipboard format is excluded");
                dataObject = new DataObject(); dataObject.SetText("History disabled test"); dataObject.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0))); Clipboard.SetDataObject(dataObject, true); lastWritten = Native.GetClipboardSequenceNumber(); Pump();
                Check(history.Data.Items[0].Text.EndsWith("11"), "Windows clipboard-history opt-out is respected");
                var picture = new Clip { Kind = "image", Png = DemoImage() };
                Clipboard.SetImage(picture.Image(0)); lastWritten = Native.GetClipboardSequenceNumber(); Pump();
                Check(history.Data.Items[0].Kind == "image" && history.Data.Items[0].Image(0).PixelWidth == 600, "Actual screenshot-format capture at full resolution");
                var captured = history.Data.Items[0]; ClipboardIO.Write(captured, false); lastWritten = Native.GetClipboardSequenceNumber(); Pump();
                Check(Clipboard.ContainsImage() && Clipboard.GetImage().PixelWidth == 600, "Image restored to real Windows clipboard");
                Check(history.Data.Items[0] == captured, "Recopy does not re-enter history");
                dataObject = new DataObject(); dataObject.SetText("Bold text"); dataObject.SetData(DataFormats.Html, "<b>Bold text</b>"); Clipboard.SetDataObject(dataObject, true); lastWritten = Native.GetClipboardSequenceNumber(); Pump();
                Check(history.Data.Items[0].Html == "<b>Bold text</b>", "Rich-text clipboard content captured");
                ClipboardIO.Write(history.Data.Items[0], true); lastWritten = Native.GetClipboardSequenceNumber(); Pump();
                Check(Clipboard.GetText() == "Bold text" && !Clipboard.ContainsData(DataFormats.Html), "Plain-text copy strips formatting");
                string file = Path.Combine(folder, "fixture.txt"); File.WriteAllText(file, "fixture");
                var fileClip = new Clip { Kind = "files", Files = new[] { file } }; ClipboardIO.Write(fileClip, false); lastWritten = Native.GetClipboardSequenceNumber(); Pump();
                Check(Clipboard.GetFileDropList()[0] == file, "File clipboard round-trip");
                bool registered = Native.RegisterHotKey(source.Handle, 4001, 0x4000, 0x13);
                Check(registered, "Pause shortcut is available"); Native.UnregisterHotKey(source.Handle, 4001);
                registered = Native.RegisterHotKey(source.Handle, 4002, 0x4000, 0x24);
                Check(registered, "Home shortcut is available"); Native.UnregisterHotKey(source.Handle, 4002);
                File.WriteAllLines(Output(args), Results); return 0;
            }
            catch (Exception ex) { Results.Add("FAIL " + ex); File.WriteAllLines(Output(args), Results); return 1; }
            finally
            {
                if (source != null) { Native.RemoveClipboardFormatListener(source.Handle); source.Dispose(); }
                if (backup != null && Native.GetClipboardSequenceNumber() == lastWritten) Clipboard.SetDataObject(backup, true);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private static void Pump()
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }

        public static byte[] DemoImage()
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(212, 224, 217)), null, new Rect(0, 0, 600, 400));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(244, 211, 132)), null, new Point(440, 110), 53, 53);
                var hills = new StreamGeometry();
                using (var geo = hills.Open()) { geo.BeginFigure(new Point(0, 310), true, true); geo.BezierTo(new Point(160, 70), new Point(250, 370), new Point(600, 210), true, false); geo.LineTo(new Point(600, 400), true, false); geo.LineTo(new Point(0, 400), true, false); }
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(100, 137, 119)), null, hills);
                dc.DrawText(new FormattedText("a breath of fresh air", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe Print"), 28, new SolidColorBrush(Color.FromRgb(52, 73, 63)), 1), new Point(38, 30));
            }
            var image = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32); image.Render(visual); return Clip.Encode(image);
        }

        public static List<Clip> DemoItems()
        {
            var items = new List<Clip>();
            items.Add(Text("A thought worth\nkeeping.\n\nLet it catch a little\nfresh air."));
            var image = new Clip { Kind = "image", Png = DemoImage() }; image.Identify(); items.Add(image);
            items.Add(Text("https://animiotics.com"));
            items.Add(Text("A tiny idea:\n\nWhat if your\nclipboard had a\nclothesline?"));
            items.Add(Text("Today\n\nMake something\nuseful.\nMake it feel good."));
            return items;
        }
    }
}
