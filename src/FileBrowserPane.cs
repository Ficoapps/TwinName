using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TwinName {
    internal sealed class FileBrowserPane : UserControl {
        private const int PageSize = 125;
        private readonly bool selectable;
        private readonly ListView rows = new ListView();
        private readonly PictureBox picture = new PictureBox();
        private readonly TextBox excerpt = new TextBox();
        private readonly Label fileTitle = new Label();
        private readonly Label note = new Label();
        private readonly Label pager = new Label();
        private readonly Label empty = new Label();
        private readonly Button previous, next, selectAll, deselectAll, rename;
        private readonly HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Action<string> openPreview;
        private CancellationTokenSource previewToken;
        private List<FileEntry> files = new List<FileEntry>();
        private int page;
        private bool rebuilding, editable;
        private string emptyText;

        internal event EventHandler RenameRequested;

        internal FileBrowserPane(bool selectable, Color background, Color ink, Color muted, Color accent,
                Action<string> openPreview) {
            this.selectable = selectable;
            this.openPreview = openPreview;
            Dock = DockStyle.Fill;
            Padding = new Padding(10);
            BackColor = Color.White;
            ForeColor = ink;
            emptyText = "Scegli una cartella per iniziare.";

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            layout.Controls.Add(actions, 0, 0);
            layout.SetColumnSpan(actions, 2);
            if (selectable) {
                selectAll = AddButton(actions, "Seleziona tutti", Color.White, ink);
                selectAll.Click += delegate {
                    foreach (var entry in files) selected.Add(entry.Path);
                    RenderPage();
                };
                deselectAll = AddButton(actions, "Deseleziona tutti", Color.White, ink);
                deselectAll.Click += delegate { selected.Clear(); RenderPage(); };
                rename = AddButton(actions, "Rinomina selezionati…", accent, Color.White);
                rename.Click += delegate {
                    EventHandler handler = RenameRequested;
                    if (handler != null && editable && selected.Count > 0) handler(this, EventArgs.Empty);
                };
            }
            previous = AddButton(actions, "‹", Color.White, ink);
            previous.AccessibleName = "Pagina precedente";
            previous.Click += delegate { page--; RenderPage(); };
            next = AddButton(actions, "›", Color.White, ink);
            next.AccessibleName = "Pagina successiva";
            next.Click += delegate { page++; RenderPage(); };
            pager.AutoSize = true;
            pager.ForeColor = muted;
            pager.Margin = new Padding(4, 11, 0, 0);
            actions.Controls.Add(pager);

            var left = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0) };
            layout.Controls.Add(left, 0, 1);
            rows.Dock = DockStyle.Fill;
            rows.View = View.Details;
            rows.FullRowSelect = true;
            rows.HideSelection = false;
            rows.MultiSelect = false;
            rows.CheckBoxes = selectable;
            rows.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            rows.ShowItemToolTips = true;
            rows.AccessibleName = selectable ? "File con nomi diversi dalla cartella" : "File della cartella";
            rows.Columns.Add("Nome", 255);
            rows.Columns.Add("Dimensione", 98);
            rows.ItemSelectionChanged += delegate(object sender, ListViewItemSelectionChangedEventArgs e) {
                if (!rebuilding && e.IsSelected) LoadPreview((FileEntry)e.Item.Tag);
            };
            rows.ItemCheck += delegate(object sender, ItemCheckEventArgs e) {
                if (rebuilding) return;
                if (!editable) { e.NewValue = e.CurrentValue; return; }
                var entry = (FileEntry)rows.Items[e.Index].Tag;
                if (e.NewValue == CheckState.Checked) selected.Add(entry.Path);
                else selected.Remove(entry.Path);
                RenderActions();
            };
            rows.DoubleClick += delegate { ExpandSelected(); };
            rows.SizeChanged += delegate {
                if (rows.ClientSize.Width > 165) rows.Columns[0].Width = rows.ClientSize.Width - 118;
            };
            left.Controls.Add(rows);

            empty.TextAlign = ContentAlignment.MiddleCenter;
            empty.ForeColor = muted;
            empty.BackColor = background;
            empty.Dock = DockStyle.Fill;
            left.Controls.Add(empty);

            var detail = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1,
                BackColor = background, Padding = new Padding(12) };
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.Controls.Add(detail, 1, 1);
            fileTitle.Text = "Anteprima";
            fileTitle.Dock = DockStyle.Fill;
            fileTitle.AutoEllipsis = true;
            fileTitle.TextAlign = ContentAlignment.MiddleLeft;
            fileTitle.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            detail.Controls.Add(fileTitle, 0, 0);
            var body = new Panel { Dock = DockStyle.Fill };
            detail.Controls.Add(body, 0, 1);
            picture.Dock = DockStyle.Fill;
            picture.SizeMode = PictureBoxSizeMode.Zoom;
            picture.DoubleClick += delegate { ExpandSelected(); };
            picture.AccessibleName = "Anteprima del file selezionato";
            body.Controls.Add(picture);
            excerpt.Dock = DockStyle.Fill;
            excerpt.Multiline = true;
            excerpt.ReadOnly = true;
            excerpt.ScrollBars = ScrollBars.Vertical;
            excerpt.BackColor = Color.White;
            excerpt.Visible = false;
            body.Controls.Add(excerpt);
            note.Dock = DockStyle.Fill;
            note.ForeColor = muted;
            note.UseMnemonic = false;
            note.AutoEllipsis = true;
            note.TextAlign = ContentAlignment.MiddleLeft;
            detail.Controls.Add(note, 0, 2);

            RenderPage();
        }

        private static Button AddButton(Control parent, string text, Color back, Color fore) {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(32, 33),
                FlatStyle = FlatStyle.Flat, ForeColor = fore, BackColor = back,
                UseVisualStyleBackColor = false, Padding = new Padding(6, 0, 6, 0), Margin = new Padding(0, 4, 7, 2) };
            button.FlatAppearance.BorderColor = Color.FromArgb(211, 222, 228);
            parent.Controls.Add(button);
            return button;
        }

        internal void SetFiles(List<FileEntry> items, string whenEmpty) {
            files = items;
            emptyText = whenEmpty;
            selected.IntersectWith(files.Select(f => f.Path));
            page = 0;
            RenderPage();
        }

        internal void EnableRename(bool enabled) {
            editable = enabled;
            RenderActions();
        }

        internal ISet<string> SelectedNames() {
            return new HashSet<string>(selected.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
        }

        private void RenderPage() {
            int pages = Math.Max(1, (files.Count + PageSize - 1) / PageSize);
            page = Math.Max(0, Math.Min(page, pages - 1));
            rebuilding = true;
            rows.BeginUpdate();
            try {
                rows.Items.Clear();
                foreach (var entry in files.Skip(page * PageSize).Take(PageSize)) {
                    var item = new ListViewItem(new[] { Path.GetFileName(entry.Path), DisplaySize(entry.Length) }) {
                        Tag = entry, ToolTipText = entry.Path
                    };
                    rows.Items.Add(item);
                    if (selectable) item.Checked = selected.Contains(entry.Path);
                }
                if (rows.Items.Count > 0) rows.Items[0].Selected = true;
            } finally { rows.EndUpdate(); rebuilding = false; }
            empty.Text = emptyText;
            empty.Visible = files.Count == 0;
            rows.Visible = files.Count > 0;
            previous.Enabled = page > 0;
            next.Enabled = page + 1 < pages;
            RenderActions();
            LoadPreview(rows.Items.Count == 0 ? null : (FileEntry)rows.Items[0].Tag);
        }

        private void RenderActions() {
            int pages = Math.Max(1, (files.Count + PageSize - 1) / PageSize);
            pager.Text = (selectable ? selected.Count + " selezionati · " : "") + files.Count + " file" +
                (pages > 1 ? " · " + (page + 1) + " / " + pages : "");
            if (!selectable) return;
            selectAll.Enabled = editable && selected.Count < files.Count;
            deselectAll.Enabled = editable && selected.Count > 0;
            rename.Enabled = editable && selected.Count > 0;
        }

        private void ExpandSelected() {
            if (rows.SelectedItems.Count != 1 || picture.Image == null) return;
            var entry = (FileEntry)rows.SelectedItems[0].Tag;
            if (Photos.Supported(entry.Path)) openPreview(entry.Path);
        }

        private async void LoadPreview(FileEntry entry) {
            ClearPreview();
            if (entry == null || IsDisposed) return;
            fileTitle.Text = Path.GetFileName(entry.Path).Replace("&", "&&");
            string metadata = DisplaySize(entry.Length) + Environment.NewLine + entry.Path;
            note.Text = metadata;
            var source = new CancellationTokenSource();
            previewToken = source;
            CancellationToken token = source.Token;
            PreviewContent content = null;
            try {
                content = await Task.Run(() => PreviewContent.Load(entry.Path, 1000), token);
                if (token.IsCancellationRequested || IsDisposed) return;
                if (content.Text != null) {
                    excerpt.Text = content.Text;
                    excerpt.Visible = true;
                } else {
                    picture.SizeMode = content.Caption == "Anteprima del contenuto non disponibile"
                        ? PictureBoxSizeMode.CenterImage : PictureBoxSizeMode.Zoom;
                    picture.Image = content.Image;
                    content.Image = null;
                }
                string detail = content.Width > 0 ? content.Width + " × " + content.Height + " px" : content.Caption;
                if (!String.IsNullOrEmpty(detail)) note.Text = detail + " · " + metadata;
            } catch (OperationCanceledException) { }
            catch (Exception) {
                if (!token.IsCancellationRequested && !IsDisposed) note.Text = "Anteprima non leggibile · " + metadata;
            } finally {
                if (content != null) content.Dispose();
            }
        }

        internal void ClearPreview() {
            if (previewToken != null) {
                previewToken.Cancel();
                previewToken.Dispose();
                previewToken = null;
            }
            Image old = picture.Image;
            picture.Image = null;
            if (old != null) old.Dispose();
            excerpt.Visible = false;
            fileTitle.Text = "Anteprima";
            note.Text = "";
        }

        internal static string DisplaySize(long bytes) {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1048576) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1073741824) return (bytes / 1048576.0).ToString("0.#") + " MB";
            return (bytes / 1073741824.0).ToString("0.##") + " GB";
        }

        protected override void Dispose(bool disposing) {
            if (disposing) ClearPreview();
            base.Dispose(disposing);
        }
    }
}
