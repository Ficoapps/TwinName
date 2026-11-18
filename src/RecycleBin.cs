using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TwinName {
    internal sealed class RecycleResult {
        internal readonly List<string> Moved = new List<string>();
        internal readonly List<string> Errors = new List<string>();
    }

    internal static class RecycleBin {
        private const uint OperationFlags = 0x0004 | 0x0010 | 0x0040 | 0x0400 | 0x00080000;
        private static readonly Guid OperationClass = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09");
        private static readonly Guid ShellItemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");

        [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation {
            [PreserveSig] int Advise(IntPtr sink, out uint cookie);
            [PreserveSig] int Unadvise(uint cookie);
            [PreserveSig] int SetOperationFlags(uint flags);
            [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
            [PreserveSig] int SetProgressDialog(IntPtr dialog);
            [PreserveSig] int SetProperties(IntPtr properties);
            [PreserveSig] int SetOwnerWindow(IntPtr window);
            [PreserveSig] int ApplyPropertiesToItem(IntPtr item);
            [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
            [PreserveSig] int RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int MoveItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            [PreserveSig] int MoveItems(IntPtr items, IntPtr destination);
            [PreserveSig] int CopyItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            [PreserveSig] int CopyItems(IntPtr items, IntPtr destination);
            [PreserveSig] int DeleteItem(IntPtr item, IntPtr sink);
            [PreserveSig] int DeleteItems(IntPtr items);
            [PreserveSig] int NewItem(IntPtr destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
                [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
            [PreserveSig] int PerformOperations();
            [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr binding, ref Guid iid, out IntPtr item);

        internal static Task<RecycleResult> MoveAsync(IList<FileEntry> files, IntPtr owner, IProgress<string> progress,
                IEnumerable<DuplicateGroup> groups = null) {
            var task = new TaskCompletionSource<RecycleResult>();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) {
                task.SetException(new PlatformNotSupportedException("Il Cestino è disponibile su Windows."));
                return task.Task;
            }
            var retained = RetainedCopies(files, groups ?? Enumerable.Empty<DuplicateGroup>());
            var thread = new Thread(() => {
                try {
                    var result = new RecycleResult();
                    int count = 0;
                    foreach (var entry in files) {
                        if (++count == 1 || count % 8 == 0)
                            progress.Report("Spostamento nel Cestino… " + count + " / " + files.Count);
                        try {
                            Validate(entry);
                            List<FileEntry> copies;
                            if (retained.TryGetValue(entry.Path, out copies) && !copies.Any(MatchesScan))
                                throw new IOException("Le copie da conservare sono cambiate o mancano. Ripeti la scansione.");
                            MoveToRecycleBin(entry.Path, owner);
                            result.Moved.Add(entry.Path);
                        } catch (Exception error) {
                            if (!(error is IOException) && !(error is UnauthorizedAccessException) &&
                                !(error is SystemException)) throw;
                            result.Errors.Add(entry.Path + " — " + error.Message);
                        }
                    }
                    task.SetResult(result);
                } catch (Exception error) { task.SetException(error); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return task.Task;
        }

        internal static Dictionary<string, List<FileEntry>> RetainedCopies(IEnumerable<FileEntry> selectedFiles,
                IEnumerable<DuplicateGroup> groups) {
            var selected = new HashSet<string>(selectedFiles.Select(file => file.Path), StringComparer.OrdinalIgnoreCase);
            var retained = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (DuplicateGroup group in groups.Where(group => group.Exact)) {
                var keep = group.Files.Where(file => !selected.Contains(file.Path)).ToList();
                if (keep.Count == 0) continue;
                foreach (FileEntry file in group.Files.Where(file => selected.Contains(file.Path)))
                    retained.Add(file.Path, keep);
            }
            return retained;
        }

        internal static void Validate(FileEntry entry) {
            if (!MatchesScan(entry))
                throw new IOException("Il file è stato spostato o è cambiato dopo l’ultima scansione.");
        }

        internal static bool MatchesScan(FileEntry entry) {
            var current = new FileInfo(entry.Path);
            return current.Exists && (current.Attributes & FileAttributes.ReparsePoint) == 0 &&
                current.Length == entry.Length && current.LastWriteTimeUtc.Ticks == entry.Changed;
        }

        private static void MoveToRecycleBin(string path, IntPtr owner) {
            IFileOperation operation = null;
            IntPtr item = IntPtr.Zero;
            try {
                Guid iid = ShellItemId;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
                var type = Type.GetTypeFromCLSID(OperationClass, true);
                operation = (IFileOperation)Activator.CreateInstance(type);
                Marshal.ThrowExceptionForHR(operation.SetOwnerWindow(owner));
                Marshal.ThrowExceptionForHR(operation.SetOperationFlags(OperationFlags));
                Marshal.ThrowExceptionForHR(operation.DeleteItem(item, IntPtr.Zero));
                int completed = operation.PerformOperations();
                bool aborted;
                int outcome = operation.GetAnyOperationsAborted(out aborted);
                Marshal.ThrowExceptionForHR(completed);
                Marshal.ThrowExceptionForHR(outcome);
                if (aborted || File.Exists(path)) throw new IOException("Windows non ha spostato il file nel Cestino.");
            } finally {
                if (operation != null) Marshal.ReleaseComObject(operation);
                if (item != IntPtr.Zero) Marshal.Release(item);
            }
        }
    }
}
