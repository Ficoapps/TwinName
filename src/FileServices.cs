using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Text;
using System.Xml.Serialization;

namespace TwinName {
    internal sealed class PhotoInfo {
        internal Bitmap Preview;
        internal int Width;
        internal int Height;
    }

    internal static class Photos {
        private static readonly HashSet<string> Formats = new HashSet<string>(
            new[] { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico" },
            StringComparer.OrdinalIgnoreCase);

        internal static bool Supported(string path) { return Formats.Contains(Path.GetExtension(path)); }

        internal static PhotoInfo Read(string path, int limit) {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var image = Image.FromStream(stream, true, false)) {
                if ((long)image.Width * image.Height > 70000000)
                    throw new InvalidDataException("Immagine oltre il limite di 70 megapixel.");
                ApplyOrientation(image);
                double scale = Math.Min(1.0, (double)limit / Math.Max(image.Width, image.Height));
                return new PhotoInfo {
                    Width = image.Width, Height = image.Height,
                    Preview = Draw(image, Math.Max(1, (int)Math.Round(image.Width * scale)),
                        Math.Max(1, (int)Math.Round(image.Height * scale)))
                };
            }
        }

        private static void ApplyOrientation(Image image) {
            if (Array.IndexOf(image.PropertyIdList, 0x0112) < 0) return;
            var value = image.GetPropertyItem(0x0112).Value;
            if (value.Length == 0) return;
            var orientation = new[] { RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipNone,
                RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate180FlipNone, RotateFlipType.Rotate180FlipX,
                RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate90FlipNone,
                RotateFlipType.Rotate270FlipX, RotateFlipType.Rotate270FlipNone };
            if (value[0] >= 2 && value[0] <= 8) image.RotateFlip(orientation[value[0]]);
        }

        internal static Bitmap Draw(Image image, int width, int height) {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            try {
                using (var graphics = Graphics.FromImage(bitmap))
                using (var attributes = new ImageAttributes()) {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(image, new Rectangle(0, 0, width, height),
                        0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
                }
                return bitmap;
            } catch { bitmap.Dispose(); throw; }
        }
    }

    internal static class FilePreviews {
        private static readonly HashSet<string> TextFormats = new HashSet<string>(
            new[] { ".txt", ".md", ".log", ".csv", ".ini", ".json", ".xml", ".srt", ".yaml", ".yml" },
            StringComparer.OrdinalIgnoreCase);

        internal static bool IsText(string path) { return TextFormats.Contains(Path.GetExtension(path)); }

        internal static string TextExcerpt(string path) {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true)) {
                var text = new char[650];
                int length = reader.ReadBlock(text, 0, text.Length);
                return length == 0 ? "File vuoto" : new string(text, 0, length).Replace("\0", " ") +
                    (reader.Peek() >= 0 ? "…" : "");
            }
        }
    }

    internal sealed class FileEntry {
        internal string Path;
        internal long Length;
        internal long Changed;
    }

    internal sealed class DuplicateGroup {
        internal bool Exact;
        internal List<FileEntry> Files = new List<FileEntry>();
    }

    internal sealed class ScanResult {
        internal int Count;
        internal List<FileEntry> Files;
        internal int SimilarityUnavailable;
        internal List<DuplicateGroup> Groups = new List<DuplicateGroup>();
        internal List<string> Issues = new List<string>();

        internal IEnumerable<FileEntry> RecommendedDuplicates() {
            return Groups.Where(group => group.Exact).SelectMany(group =>
                group.Files.OrderBy(file => file.Changed)
                    .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(file => file.Path, StringComparer.Ordinal)
                    .Skip(1));
        }
    }

    internal sealed class PhotoSignature {
        internal ulong Horizontal, Vertical;
        internal double Ratio, Red, Green, Blue;

        internal static PhotoSignature From(PhotoInfo photo) {
            var signature = new PhotoSignature { Ratio = (double)photo.Width / photo.Height };
            using (var grid = Photos.Draw(photo.Preview, 9, 9)) {
                var brightness = new int[9, 9];
                for (int y = 0; y < 9; y++) {
                    for (int x = 0; x < 9; x++) {
                        Color pixel = grid.GetPixel(x, y);
                        int r = (pixel.R * pixel.A + 255 * (255 - pixel.A)) / 255;
                        int g = (pixel.G * pixel.A + 255 * (255 - pixel.A)) / 255;
                        int b = (pixel.B * pixel.A + 255 * (255 - pixel.A)) / 255;
                        signature.Red += r / 81.0;
                        signature.Green += g / 81.0;
                        signature.Blue += b / 81.0;
                        brightness[x, y] = 299 * r + 587 * g + 114 * b;
                    }
                }
                for (int y = 0; y < 8; y++) {
                    for (int x = 0; x < 8; x++) {
                        signature.Horizontal = (signature.Horizontal << 1) |
                            (brightness[x, y] > brightness[x + 1, y] ? 1UL : 0UL);
                        signature.Vertical = (signature.Vertical << 1) |
                            (brightness[x, y] > brightness[x, y + 1] ? 1UL : 0UL);
                    }
                }
            }
            return signature;
        }

        private static int BitCount(ulong value) {
            unchecked {
                value -= (value >> 1) & 0x5555555555555555UL;
                value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
                value = (value + (value >> 4)) & 0x0f0f0f0f0f0f0f0fUL;
                return (int)((value * 0x0101010101010101UL) >> 56);
            }
        }

        internal int Distance(PhotoSignature other) {
            return BitCount(Horizontal ^ other.Horizontal) + BitCount(Vertical ^ other.Vertical);
        }

        internal bool Compatible(PhotoSignature other, int threshold) {
            double limit = threshold == 8 ? 16 : threshold == 14 ? 26 : 39;
            double ratioTolerance = threshold == 8 ? 1.08 : threshold == 14 ? 1.15 : 1.24;
            return Math.Max(Ratio, other.Ratio) / Math.Min(Ratio, other.Ratio) <= ratioTolerance &&
                Math.Max(Math.Abs(Red - other.Red),
                    Math.Max(Math.Abs(Green - other.Green), Math.Abs(Blue - other.Blue))) <= limit;
        }
    }

    internal sealed class SignatureIndex {
        private readonly PhotoSignature signature;
        private readonly List<DuplicateGroup> groups = new List<DuplicateGroup>();
        private readonly List<PhotoSignature> representatives = new List<PhotoSignature>();
        private readonly Dictionary<int, SignatureIndex> children = new Dictionary<int, SignatureIndex>();

        internal SignatureIndex(PhotoSignature value, DuplicateGroup group) {
            signature = value;
            groups.Add(group);
            representatives.Add(value);
        }

        internal void Add(PhotoSignature value, DuplicateGroup group) {
            int distance = signature.Distance(value);
            if (distance == 0) {
                groups.Add(group);
                representatives.Add(value);
                return;
            }
            SignatureIndex child;
            if (children.TryGetValue(distance, out child)) child.Add(value, group);
            else children.Add(distance, new SignatureIndex(value, group));
        }

        internal void Find(PhotoSignature value, int tolerance, ref int best, ref DuplicateGroup match) {
            int distance = signature.Distance(value);
            if (distance < best) {
                for (int index = 0; index < representatives.Count; index++) {
                    if (!value.Compatible(representatives[index], tolerance)) continue;
                    best = distance;
                    match = groups[index];
                    break;
                }
            }
            foreach (var child in children) {
                if (Math.Abs(child.Key - distance) < best)
                    child.Value.Find(value, tolerance, ref best, ref match);
            }
        }
    }

    internal static class Scanner {
        internal static ScanResult Run(string root, bool recursive, int? similarity,
                IProgress<string> progress, CancellationToken token) {
            var result = new ScanResult();
            progress.Report("Lettura della cartella…");
            var files = ReadFiles(root, recursive, result.Issues, token);
            result.Files = files;
            result.Count = files.Count;
            var candidates = files.GroupBy(f => f.Length).Where(g => g.Count() > 1)
                .SelectMany(g => g).ToList();
            var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var checksums = new Dictionary<string, DuplicateGroup>(StringComparer.Ordinal);
            int checkedCount = 0;
            var buffer = new byte[262144];
            foreach (FileEntry file in candidates) {
                token.ThrowIfCancellationRequested();
                if (++checkedCount == 1 || checkedCount % 16 == 0)
                    progress.Report("Confronto dei file identici… " + checkedCount + " / " + candidates.Count);
                try {
                    string key = file.Length + ":" + Checksum(file, buffer, token);
                    DuplicateGroup group;
                    if (!checksums.TryGetValue(key, out group)) {
                        group = new DuplicateGroup { Exact = true };
                        checksums.Add(key, group);
                    }
                    group.Files.Add(file);
                } catch (Exception e) {
                    if (!IsFileError(e)) throw;
                    result.Issues.Add(file.Path + " — " + e.Message);
                }
            }
            foreach (var group in checksums.Values.Where(g => g.Files.Count > 1).OrderBy(g => g.Files[0].Path)) {
                result.Groups.Add(group);
                FileEntry representative = group.Files.FirstOrDefault(f => Photos.Supported(f.Path));
                foreach (FileEntry file in group.Files) {
                    if (file != representative) duplicates.Add(file.Path);
                }
            }
            if (similarity.HasValue) {
                var images = files.Where(f => Photos.Supported(f.Path) && !duplicates.Contains(f.Path)).ToList();
                FindSimilar(images, similarity.Value, result, progress, token);
            }
            token.ThrowIfCancellationRequested();
            return result;
        }

        internal static List<FileEntry> ReadFiles(string root, bool recursive, List<string> issues, CancellationToken token) {
            var files = new List<FileEntry>();
            var folders = new Stack<string>();
            folders.Push(root);
            while (folders.Count > 0) {
                token.ThrowIfCancellationRequested();
                string folder = folders.Pop();
                try {
                    foreach (string path in Directory.EnumerateFileSystemEntries(folder)) {
                        token.ThrowIfCancellationRequested();
                        try {
                            var attributes = File.GetAttributes(path);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if ((attributes & FileAttributes.Directory) != 0) {
                                if (recursive) folders.Push(path);
                                continue;
                            }
                            var info = new FileInfo(path);
                            files.Add(new FileEntry { Path = path, Length = info.Length,
                                Changed = info.LastWriteTimeUtc.Ticks });
                        } catch (Exception e) {
                            if (!IsFileError(e)) throw;
                            issues.Add(path + " — " + e.Message);
                        }
                    }
                } catch (Exception e) {
                    if (!IsFileError(e)) throw;
                    issues.Add(folder + " — " + e.Message);
                }
            }

            files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
            return files;
        }

        private static void FindSimilar(List<FileEntry> files, int tolerance, ScanResult result,
                IProgress<string> progress, CancellationToken token) {
            SignatureIndex index = null;
            var groups = new List<DuplicateGroup>();
            int count = 0;
            foreach (FileEntry file in files) {
                token.ThrowIfCancellationRequested();
                if (++count == 1 || count % 8 == 0)
                    progress.Report("Confronto delle immagini… " + count + " / " + files.Count);
                try {
                    PhotoInfo photo = Photos.Read(file.Path, 192);
                    PhotoSignature signature;
                    using (photo.Preview) signature = PhotoSignature.From(photo);
                    DuplicateGroup match = null;
                    int best = tolerance + 1;
                    if (index != null) index.Find(signature, tolerance, ref best, ref match);
                    if (match == null) {
                        match = new DuplicateGroup();
                        groups.Add(match);
                        if (index == null) index = new SignatureIndex(signature, match);
                        else index.Add(signature, match);
                    }
                    match.Files.Add(file);
                } catch (Exception e) {
                    if (!IsFileError(e) && !(e is ArgumentException) && !(e is OutOfMemoryException)) throw;
                    result.SimilarityUnavailable++;
                    result.Issues.Add(file.Path + " — confronto immagine: " + e.Message);
                }
            }
            result.Groups.AddRange(groups.Where(g => g.Files.Count > 1));
        }

        private static string Checksum(FileEntry file, byte[] buffer, CancellationToken token) {
            using (var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create()) {
                if (stream.Length != file.Length || File.GetLastWriteTimeUtc(file.Path).Ticks != file.Changed)
                    throw new IOException("Il file è cambiato durante la scansione.");
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) {
                    token.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return Convert.ToBase64String(sha.Hash);
            }
        }

        private static bool IsFileError(Exception e) {
            return e is IOException || e is UnauthorizedAccessException ||
                e is System.Security.SecurityException;
        }
    }

    public sealed class RenameItem {
        public string Original;
        public string Renamed;
        public long Length;
        public long Changed;
        public long Created;
    }

    public sealed class RenameBatch {
        public string Folder;
        public List<RenameItem> Items = new List<RenameItem>();
    }

    public sealed class RenameState {
        public RenameBatch Last;
        public RenameBatch Pending;
        public bool PendingUndo;
    }

    internal sealed class Renamer {
        private readonly string journal;
        private readonly XmlSerializer serializer = new XmlSerializer(typeof(RenameState));

        internal Renamer(string dataDirectory) {
            journal = Path.Combine(dataDirectory, "registro-rinomine.xml");
        }

        internal static RenameBatch Plan(string folder, ISet<string> selection = null) {
            var directory = new DirectoryInfo(folder);
            if (directory.Parent == null) throw new IOException("Scegli una cartella all’interno dell’unità.");
            var entries = directory.GetFileSystemInfos();
            var occupied = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            var files = entries.OfType<FileInfo>().Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
            var batch = new RenameBatch { Folder = directory.FullName };
            int digits = Math.Max(3, files.Count.ToString(CultureInfo.InvariantCulture).Length);
            var existingNumbers = new HashSet<int>();
            var alreadyRenamed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo file in files) {
                string extension = Extension(file.Name);
                string stem = file.Name.Substring(0, file.Name.Length - extension.Length);
                if (StringComparer.OrdinalIgnoreCase.Equals(stem, directory.Name)) {
                    alreadyRenamed.Add(file.Name);
                    continue;
                }
                int separator = stem.LastIndexOf('_');
                if (separator < 1) continue;
                string suffix = stem.Substring(separator + 1);
                int number;
                if (suffix.Length < 3 || !Int32.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
                    number <= 0 || !StringComparer.OrdinalIgnoreCase.Equals(file.Name, MakeName(directory.Name, extension, suffix))) continue;
                existingNumbers.Add(number);
                alreadyRenamed.Add(file.Name);
            }
            int sequence = 0;
            foreach (FileInfo file in files) {
                if (alreadyRenamed.Contains(file.Name) || (selection != null && !selection.Contains(file.Name))) continue;
                string extension = Extension(file.Name);
                string newName;
                do {
                    string number = (++sequence).ToString("D" + digits, CultureInfo.InvariantCulture);
                    newName = MakeName(directory.Name, extension, number);
                } while (existingNumbers.Contains(sequence) || occupied.Contains(newName));
                occupied.Add(newName);
                batch.Items.Add(new RenameItem { Original = file.Name, Renamed = newName,
                    Length = file.Length, Changed = file.LastWriteTimeUtc.Ticks,
                    Created = file.CreationTimeUtc.Ticks });
            }
            return batch;
        }

        private static string Extension(string name) {
            string extension = Path.GetExtension(name);
            return extension == name ? "" : extension;
        }

        private static string MakeName(string folder, string extension, string number) {
            int stemSize = 254 - number.Length - extension.Length;
            if (stemSize < 1) throw new IOException("Estensione troppo lunga: " + extension);
            if (folder.Length > stemSize) {
                if (char.IsHighSurrogate(folder[stemSize - 1])) stemSize--;
                if (stemSize < 1) throw new IOException("Nome cartella troppo lungo: " + folder);
                folder = folder.Substring(0, stemSize);
            }
            return folder + "_" + number + extension;
        }

        internal RenameState Read() {
            if (!File.Exists(journal)) return new RenameState();
            using (var stream = File.OpenRead(journal))
                return (RenameState)serializer.Deserialize(stream);
        }

        internal void Apply(RenameBatch batch, bool undo) {
            var state = Read();
            if (state.Pending != null) throw new IOException("Ripristina prima l’operazione interrotta.");
            if (undo) {
                if (state.Last == null) throw new IOException("Nessuna rinomina da annullare.");
                batch = state.Last;
            }
            if (batch == null || batch.Items.Count == 0) return;
            foreach (var item in batch.Items) Validate(batch, item, undo);
            Save(new RenameState { Last = state.Last, Pending = batch, PendingUndo = undo });
            try {
                foreach (var item in batch.Items) Move(batch, item, undo);
                Save(new RenameState { Last = undo ? null : batch });
            } catch (Exception failure) {
                try {
                    RollBack(batch, undo);
                    Save(state);
                } catch (Exception recovery) {
                    throw new IOException(failure.Message + Environment.NewLine +
                        "Ripristino da completare: " + recovery.Message, failure);
                }
                throw;
            }
        }

        internal void Recover() {
            var state = Read();
            if (state.Pending == null) return;
            RollBack(state.Pending, state.PendingUndo);
            Save(new RenameState { Last = state.Last });
        }

        private static void RollBack(RenameBatch batch, bool wasUndo) {
            var failed = new List<string>();
            foreach (var item in batch.Items.AsEnumerable().Reverse()) {
                try {
                    string before = Member(batch, wasUndo ? item.Renamed : item.Original);
                    string after = Member(batch, wasUndo ? item.Original : item.Renamed);
                    if (Matches(before, item) && !Matches(after, item)) continue;
                    Move(batch, item, !wasUndo);
                } catch (Exception e) {
                    if (!(e is IOException) && !(e is UnauthorizedAccessException)) throw;
                    failed.Add(e.Message);
                }
            }
            if (failed.Count > 0)
                throw new IOException(failed[0] + (failed.Count == 1 ? "" : " (e altri " + (failed.Count - 1) + ")"));
        }

        private static void Validate(RenameBatch batch, RenameItem item, bool undo) {
            string source = Member(batch, undo ? item.Renamed : item.Original);
            string destination = Member(batch, undo ? item.Original : item.Renamed);
            if (!Matches(source, item))
                throw new IOException("Il file è mancante o è cambiato: " + source);
            if (Exists(destination))
                throw new IOException("Il nome di destinazione è già in uso: " + destination);
        }

        private static void Move(RenameBatch batch, RenameItem item, bool undo) {
            Validate(batch, item, undo);
            File.Move(Member(batch, undo ? item.Renamed : item.Original),
                Member(batch, undo ? item.Original : item.Renamed));
        }

        private static bool Exists(string path) { return File.Exists(path) || Directory.Exists(path); }

        private static bool Matches(string path, RenameItem item) {
            var info = new FileInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
                info.Length == item.Length && info.LastWriteTimeUtc.Ticks == item.Changed &&
                info.CreationTimeUtc.Ticks == item.Created;
        }

        private static string Member(RenameBatch batch, string name) {
            if (String.IsNullOrWhiteSpace(name) || name == "." || name == ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("Il registro contiene un nome di file non valido.");
            return Path.Combine(batch.Folder, name);
        }

        private void Save(RenameState state) {
            string directory = Path.GetDirectoryName(journal);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) {
                    serializer.Serialize(stream, state);
                    stream.Flush(true);
                }
                if (File.Exists(journal)) File.Replace(temporary, journal, null);
                else File.Move(temporary, journal);
            } finally {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
