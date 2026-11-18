using System;
using System.Drawing;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace TwinName {
    internal sealed class PreviewContent : IDisposable {
        internal Bitmap Image;
        internal string Text;
        internal string Caption;
        internal int Width, Height;

        internal static PreviewContent Load(string path, int limit) {
            if (Photos.Supported(path)) {
                try {
                    PhotoInfo photo = Photos.Read(path, limit);
                    return new PreviewContent { Image = photo.Preview, Width = photo.Width, Height = photo.Height };
                } catch (ArgumentException) { }
                catch (InvalidDataException) { }
                catch (OutOfMemoryException) { }
            }
            if (FilePreviews.IsText(path)) return new PreviewContent { Text = FilePreviews.TextExcerpt(path) };
            Bitmap thumbnail = ShellThumbnail.TryRead(path, limit);
            if (thumbnail != null) return new PreviewContent { Image = thumbnail, Caption = "Miniatura di Windows" };
            Bitmap icon = null;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                try {
                    using (Icon associated = Icon.ExtractAssociatedIcon(path))
                        if (associated != null) icon = associated.ToBitmap();
                } catch (ArgumentException) { }
                catch (SystemException) { }
            }
            return new PreviewContent {
                Image = icon ?? SystemIcons.Application.ToBitmap(),
                Caption = "Anteprima del contenuto non disponibile"
            };
        }

        public void Dispose() { if (Image != null) Image.Dispose(); }
    }

    internal static class ShellThumbnail {
        private static readonly HashSet<string> ThumbnailFormats = new HashSet<string>(new[] {
            ".pdf", ".jpeg", ".jpg", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico",
            ".webp", ".heic", ".heif", ".avif", ".svg", ".psd",
            ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".mpeg", ".mpg", ".mts", ".m2ts",
            ".docx", ".pptx", ".xlsx"
        }, StringComparer.OrdinalIgnoreCase);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize {
            internal int Width;
            internal int Height;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        private interface IShellItemImageFactory {
            [PreserveSig]
            int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint apartment);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        internal static Bitmap TryRead(string path, int limit) {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT ||
                !ThumbnailFormats.Contains(Path.GetExtension(path)) || !File.Exists(path)) return null;
            const uint ThumbnailOnly = 0x8;
            IShellItemImageFactory factory = null;
            IntPtr bitmap = IntPtr.Zero;
            bool initialized = CoInitializeEx(IntPtr.Zero, 0) >= 0;
            try {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) < 0 || factory == null)
                    return null;
                int edge = Math.Min(512, limit);
                if (factory.GetImage(new NativeSize { Width = edge, Height = edge }, ThumbnailOnly, out bitmap) < 0 ||
                    bitmap == IntPtr.Zero) return null;
                return System.Drawing.Image.FromHbitmap(bitmap);
            } catch (COMException) {
                return null;
            } finally {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (factory != null) Marshal.ReleaseComObject(factory);
                if (initialized) CoUninitialize();
            }
        }
    }
}
