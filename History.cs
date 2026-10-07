using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clothesline
{
    [DataContract]
    public sealed class Clip
    {
        [DataMember] public string Id;
        [DataMember] public string Kind;
        [DataMember] public string Text;
        [DataMember] public string Html;
        [DataMember] public string Rtf;
        [DataMember] public byte[] Png;
        [DataMember] public string[] Files;
        [DataMember] public DateTime Created;
        [DataMember] public string Hash;
        public BitmapSource Thumbnail;

        public void Identify()
        {
            Id = Guid.NewGuid().ToString("N");
            Created = DateTime.Now;
            byte[] bytes = Png ?? Encoding.UTF8.GetBytes(Kind == "files" ? String.Join("\n", Files) : Text ?? "");
            using (var sha = SHA256.Create()) Hash = Kind + ":" + Convert.ToBase64String(sha.ComputeHash(bytes));
        }

        public string Label
        {
            get
            {
                if (Kind == "image") return "SCREENSHOT / IMAGE";
                if (Kind == "files") return Files.Length == 1 ? "FILE" : Files.Length + " FILES";
                Uri uri;
                if (Uri.TryCreate((Text ?? "").Trim(), UriKind.Absolute, out uri) && (uri.Scheme == "https" || uri.Scheme == "http")) return "LINK";
                return "TEXT";
            }
        }

        public string Summary
        {
            get
            {
                if (Kind == "files") return String.Join("\n", Files.Select(Path.GetFileName));
                return Text ?? "";
            }
        }

        public BitmapSource Image(int decodeWidth)
        {
            if (Png == null) return null;
            if (decodeWidth > 0 && Thumbnail != null) return Thumbnail;
            using (var stream = new MemoryStream(Png))
            {
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
                image.StreamSource = stream; image.EndInit(); image.Freeze();
                if (decodeWidth > 0) Thumbnail = image;
                return image;
            }
        }

        public static byte[] Encode(BitmapSource image)
        {
            var normalized = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            normalized.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(normalized));
            using (var stream = new MemoryStream()) { encoder.Save(stream); return stream.ToArray(); }
        }
    }

    [DataContract]
    public sealed class HistoryData
    {
        [DataMember] public List<Clip> Items = new List<Clip>();
        [DataMember] public bool Paused;
        // Stored as "off" so history files saved before this setting existed keep Home enabled.
        [DataMember] public bool HomeKeyOff;
    }

    public sealed class History
    {
        public const int Limit = 10;
        public readonly string Folder;
        public readonly string FilePath;
        public HistoryData Data = new HistoryData();
        public string LastError;
        public event Action Changed;
        public History(string folder) { Folder = folder; FilePath = Path.Combine(folder, "history.bin"); }

        public void Load()
        {
            if (!File.Exists(FilePath)) return;
            try
            {
                byte[] decrypted = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
                using (var stream = new MemoryStream(decrypted))
                    Data = (HistoryData)new DataContractSerializer(typeof(HistoryData)).ReadObject(stream);
                Data.Items = (Data.Items ?? new List<Clip>()).Where(c => c != null && c.Hash != null &&
                    ((c.Kind == "text" && c.Text != null) || (c.Kind == "image" && c.Png != null) ||
                    (c.Kind == "files" && c.Files != null))).Take(Limit).ToList();
            }
            catch (Exception ex)
            {
                LastError = "Saved history could not be opened. " + ex.GetType().Name;
                Data = new HistoryData();
                // Preserve an unreadable file rather than silently overwriting it.
                try { File.Move(FilePath, FilePath + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
            }
        }

        public void Add(Clip clip)
        {
            if (clip == null || Data.Paused) return;
            if (clip.Hash == null) clip.Identify();
            if (Data.Items.Count > 0 && Data.Items[0].Hash == clip.Hash) return;
            Data.Items.RemoveAll(c => c.Hash == clip.Hash);
            Data.Items.Insert(0, clip);
            if (Data.Items.Count > Limit) Data.Items.RemoveRange(Limit, Data.Items.Count - Limit);
            Save(); Notify();
        }

        public void Remove(Clip clip) { Data.Items.Remove(clip); Save(); Notify(); }
        public void Clear() { Data.Items.Clear(); Save(); Notify(); }
        public void TogglePause() { Data.Paused = !Data.Paused; Save(); Notify(); }
        public void ToggleHomeKey() { Data.HomeKeyOff = !Data.HomeKeyOff; Save(); Notify(); }
        private void Notify() { if (Changed != null) Changed(); }

        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                byte[] raw;
                using (var stream = new MemoryStream())
                {
                    new DataContractSerializer(typeof(HistoryData)).WriteObject(stream, Data);
                    raw = stream.ToArray();
                }
                byte[] encrypted = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
                string staging = FilePath + ".tmp";
                File.WriteAllBytes(staging, encrypted);
                if (File.Exists(FilePath)) File.Replace(staging, FilePath, null);
                else File.Move(staging, FilePath);
                LastError = null;
                return true;
            }
            catch (Exception ex) { LastError = "History could not be saved. " + ex.GetType().Name; return false; }
        }
    }

    public static class ClipboardIO
    {
        public const string OwnFormat = "Clothesline.OwnCopy.v1";
        public static string LastNotice;
        public static Clip Read()
        {
            LastNotice = null;
            var data = Clipboard.GetDataObject();
            if (data == null || data.GetDataPresent(OwnFormat, false)) return null;
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false) ||
                data.GetDataPresent("ExcludeClipboardContentFromHistory", false) ||
                data.GetDataPresent("application/x-keepassxc", false)) return null;
            if (data.GetDataPresent("CanIncludeInClipboardHistory", false))
            {
                object value = data.GetData("CanIncludeInClipboardHistory", false);
                var memory = value as MemoryStream;
                var bytes = value as byte[];
                if (memory != null) bytes = memory.ToArray();
                if ((bytes != null && bytes.Length >= 4 && BitConverter.ToInt32(bytes, 0) == 0) ||
                    (value is int && (int)value == 0)) return null;
            }
            Clip clip;
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return null;
                clip = new Clip { Kind = "files", Files = files };
            }
            else if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image == null) return null;
                if ((long)image.PixelWidth * image.PixelHeight > 60000000) { LastNotice = "This image is too large to hang (60 megapixel limit)."; return null; }
                byte[] png = Clip.Encode(image);
                if (png.Length > 32 * 1024 * 1024) { LastNotice = "This image is too large to keep (32 MB limit)."; return null; }
                clip = new Clip { Kind = "image", Png = png };
            }
            else if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                string value = data.GetData(DataFormats.UnicodeText) as string;
                if (String.IsNullOrEmpty(value)) return null;
                if (value.Length > 2000000) { LastNotice = "This text is too long to keep (2 million character limit)."; return null; }
                clip = new Clip { Kind = "text", Text = value };
                if (data.GetDataPresent(DataFormats.Html, false)) clip.Html = data.GetData(DataFormats.Html, false) as string;
                if (data.GetDataPresent(DataFormats.Rtf, false)) clip.Rtf = data.GetData(DataFormats.Rtf, false) as string;
                if (clip.Html != null && clip.Html.Length > 4000000) clip.Html = null;
                if (clip.Rtf != null && clip.Rtf.Length > 4000000) clip.Rtf = null;
            }
            else return null;
            clip.Identify(); return clip;
        }

        public static void Write(Clip clip, bool plain)
        {
            var data = new DataObject();
            data.SetData(OwnFormat, "1");
            if (clip.Kind == "image")
            {
                data.SetImage(clip.Image(0));
                data.SetData("PNG", new MemoryStream(clip.Png));
            }
            else if (clip.Kind == "files")
            {
                if (clip.Files.Any(f => !File.Exists(f) && !Directory.Exists(f)))
                    throw new IOException("One of these files has been moved or deleted.");
                var files = new StringCollection(); files.AddRange(clip.Files); data.SetFileDropList(files);
                // Historical file entries always copy; never repeat a destructive cut/move.
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
            }
            else
            {
                data.SetText(clip.Text, TextDataFormat.UnicodeText);
                if (!plain && clip.Html != null) data.SetData(DataFormats.Html, clip.Html);
                if (!plain && clip.Rtf != null) data.SetData(DataFormats.Rtf, clip.Rtf);
            }
            Clipboard.SetDataObject(data, true);
        }
    }
}
