using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("TwinName")]
[assembly: AssemblyDescription("Ricerca duplicati e rinomina di file in locale")]
[assembly: AssemblyProduct("TwinName")]
[assembly: AssemblyCompany("Ficoapps")]
[assembly: AssemblyCopyright("© 2026 Ficoapps")]
[assembly: AssemblyVersion("1.1.2.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.5", FrameworkDisplayName = ".NET Framework 4.5")]
[assembly: AssemblyFileVersion("1.1.2.0")]

namespace TwinName {
    internal static class Program {
        [STAThread]
        private static void Main() {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // The original profile and mutex preserve rename history across the rebrand.
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CartellaChiara");
            using (var gate = new Mutex(false, @"Local\CartellaChiara-Registro-v1")) {
                bool acquired;
                try { acquired = gate.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) {
                    MessageBox.Show("TwinName è già aperto.", "TwinName");
                    return;
                }
                try { Application.Run(new MainWindow(data)); }
                finally { gate.ReleaseMutex(); }
            }
        }
    }

    internal sealed class MainWindow : Form {
        private const string GitHubUrl = "https://github.com/Ficoapps";
        private const string Credit = "© 2026 Ficoapps";
        private static readonly Color Background = Color.FromArgb(240, 244, 247);
        private static readonly Color Ink = Color.FromArgb(22, 39, 50);
        private static readonly Color Muted = Color.FromArgb(72, 91, 104);
        private static readonly Color Accent = Color.FromArgb(12, 103, 94);
        private static readonly Color Navy = Color.FromArgb(20, 42, 53);
        private static readonly Color Line = Color.FromArgb(219, 227, 232);
        private const int PageSize = 12;
        private const string EmptyFolder = "Scegli la cartella da analizzare";

        private readonly Renamer renamer;
        private readonly TabControl tabs = new TabControl();
        private readonly TabPage allPage = new TabPage("Tutti i file");
        private readonly TabPage duplicatePage = new TabPage("Duplicati");
        private readonly TabPage renamePage = new TabPage("Da rinominare");
        private readonly FileBrowserPane allFiles, renameFiles;
        private List<string> inventoryIssues = new List<string>();
        private CancellationTokenSource inventoryCancellation;
        private readonly TextBox folderBox = new TextBox();
        private readonly CheckBox subfolders = new CheckBox();
        private readonly ComboBox similarity = new ComboBox();
        private readonly ComboBox groupFilter = new ComboBox();
        private readonly ListBox groupList = new ListBox();
        private readonly FlowLayoutPanel cards = new FlowLayoutPanel();
        private readonly Label resultTitle = new Label();
        private readonly Label status = new Label();
        private readonly Label pageStatus = new Label();
        private readonly Label duplicateSelectionStatus = new Label();
        private readonly Dictionary<string, FileEntry> duplicateSelection =
            new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly ProgressBar progressBar = new ProgressBar();
        private readonly Button choose, scan, cancel, rename, undo, showIssues, previous, next, recycleSelected, selectExact, clearSelected;
        private readonly SemaphoreSlim previewSlots = new SemaphoreSlim(3, 3);
        private CancellationTokenSource scanCancellation, pageCancellation;
        private string folder;
        private ScanResult results;
        private int page;
        private bool busy, listing, renaming, recycling, hasPending, hasUndo, closing;

        internal MainWindow(string data) {
            renamer = new Renamer(data);
            Text = "TwinName";
            Font = new Font("Segoe UI", 10F);
            ForeColor = Ink;
            BackColor = Background;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(850, 590);
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(1190, area.Width), Math.Min(810, area.Height));
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 67));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            Controls.Add(layout);

            var header = new Panel { Dock = DockStyle.Fill, BackColor = Navy, Margin = Padding.Empty };
            var heading = LabelFor("TwinName", 22, FontStyle.Bold, Color.White);
            heading.Location = new Point(24, 9);
            header.Controls.Add(heading);
            var description = LabelFor("Trova i duplicati e rinomina i file di una cartella", 9, FontStyle.Regular, Color.FromArgb(195, 218, 225));
            description.Location = new Point(26, 45);
            header.Controls.Add(description);
            var brand = new Panel { Dock = DockStyle.Right, Width = 266, BackColor = Navy };
            var brandMark = new PictureBox { Location = new Point(194, 4), Size = new Size(59, 59),
                SizeMode = PictureBoxSizeMode.Zoom, Image = ReadLogo(), AccessibleName = "Logo Ficoapps" };
            brandMark.Disposed += delegate { if (brandMark.Image != null) brandMark.Image.Dispose(); };
            brand.Controls.Add(brandMark);
            var credit = LabelFor(Credit, 10, FontStyle.Bold, Color.White);
            credit.Location = new Point(15, 12);
            brand.Controls.Add(credit);
            var github = MakeLink("Ficoapps su GitHub", Color.FromArgb(138, 231, 207));
            github.Location = new Point(14, 34);
            brand.Controls.Add(github);
            header.Controls.Add(brand);
            layout.Controls.Add(header, 0, 0);

            var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(20, 9, 20, 7),
                RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.Controls.Add(toolbar, 0, 1);

            var pickerRow = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 3, Margin = Padding.Empty };
            pickerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pickerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pickerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.Controls.Add(pickerRow, 0, 0);
            choose = MakeButton("Scegli cartella…", false);
            choose.Click += async delegate { await PickFolder(); };
            pickerRow.Controls.Add(choose, 0, 0);
            folderBox.ReadOnly = true;
            folderBox.TabStop = false;
            folderBox.BackColor = Background;
            folderBox.ForeColor = Muted;
            folderBox.Text = EmptyFolder;
            folderBox.AccessibleName = "Cartella selezionata";
            folderBox.Dock = DockStyle.Fill;
            folderBox.Margin = new Padding(3, 8, 9, 0);
            pickerRow.Controls.Add(folderBox, 1, 0);
            scan = MakeButton("Cerca duplicati", true);
            scan.Click += async delegate { await Search(); };
            var scanActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            scanActions.Controls.Add(scan);
            pickerRow.Controls.Add(scanActions, 2, 0);
            AcceptButton = scan;

            var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            toolbar.Controls.Add(options, 0, 1);
            subfolders.Text = "Includi sottocartelle";
            subfolders.AccessibleDescription = "Vale per la ricerca dei duplicati.";
            subfolders.AutoSize = true;
            subfolders.CheckedChanged += async delegate { await ReloadFolderViews(); };
            subfolders.Margin = new Padding(0, 11, 11, 0);
            options.Controls.Add(subfolders);
            similarity.DropDownStyle = ComboBoxStyle.DropDownList;
            similarity.Items.AddRange(new object[] { "Solo file identici", "Foto simili · stretta", "Foto simili · normale", "Foto simili · ampia" });
            similarity.SelectedIndex = 2;
            similarity.AccessibleName = "Sensibilità della ricerca";
            similarity.Width = 160;
            similarity.Margin = new Padding(0, 8, 11, 0);
            options.Controls.Add(similarity);
            rename = MakeButton("Vai a Da rinominare", false);
            rename.Click += delegate { tabs.SelectedTab = renamePage; };
            options.Controls.Add(rename);
            undo = MakeButton("Annulla ultima rinomina", false);
            undo.Click += async delegate { await UndoRename(); };
            options.Controls.Add(undo);
            cancel = MakeButton("Ferma ricerca", false);
            cancel.Visible = false;
            cancel.Click += delegate {
                if (scanCancellation != null) scanCancellation.Cancel();
                cancel.Enabled = false;
                SetStatus("Interruzione della ricerca…");
            };
            scanActions.Controls.Add(cancel);

            var work = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(20, 13, 20, 5) };
            work.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 218));
            work.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(18, 6);
            tabs.Margin = new Padding(19, 9, 19, 3);
            tabs.AccessibleName = "File, duplicati e rinomina";
            tabs.TabPages.AddRange(new[] { allPage, duplicatePage, renamePage });
            allFiles = new FileBrowserPane(false, Background, Ink, Muted, Accent, Enlarge);
            renameFiles = new FileBrowserPane(true, Background, Ink, Muted, Accent, Enlarge);
            renameFiles.RenameRequested += async delegate { await PreviewRename(); };
            allPage.Controls.Add(allFiles);
            duplicatePage.Controls.Add(work);
            renamePage.Controls.Add(renameFiles);
            layout.Controls.Add(tabs, 0, 2);
            var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Color.White,
                Padding = new Padding(11), Margin = new Padding(0, 0, 11, 0) };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            work.Controls.Add(sidebar, 0, 0);
            sidebar.Controls.Add(LabelFor("Gruppi trovati", 11, FontStyle.Bold, Ink), 0, 0);
            groupFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            groupFilter.Items.AddRange(new object[] { "Tutti i risultati", "File identici", "Foto simili" });
            groupFilter.SelectedIndex = 0;
            groupFilter.AccessibleName = "Filtra i gruppi trovati";
            groupFilter.Dock = DockStyle.Fill;
            groupFilter.SelectedIndexChanged += delegate { RefreshGroups(); };
            sidebar.Controls.Add(groupFilter, 0, 1);
            groupList.Dock = DockStyle.Fill;
            groupList.DrawMode = DrawMode.OwnerDrawFixed;
            groupList.ItemHeight = Math.Max(53, Font.Height * 2 + 13);
            groupList.BorderStyle = BorderStyle.None;
            groupList.IntegralHeight = false;
            groupList.AccessibleName = "Gruppi di file duplicati";
            groupList.DrawItem += DrawGroup;
            groupList.SelectedIndexChanged += delegate { page = 0; ShowCurrentGroup(); };
            sidebar.Controls.Add(groupList, 0, 2);

            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty,
                BackColor = Color.White, Padding = new Padding(12) };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
            work.Controls.Add(content, 1, 0);
            resultTitle.Font = new Font(Font, FontStyle.Bold);
            resultTitle.Dock = DockStyle.Fill;
            resultTitle.AutoEllipsis = true;
            resultTitle.TextAlign = ContentAlignment.MiddleLeft;
            content.Controls.Add(resultTitle, 0, 0);

            var duplicateTools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            recycleSelected = MakeButton("Elimina selezionati…", false);
            recycleSelected.AccessibleDescription = "Sposta i file selezionati nel Cestino di Windows.";
            recycleSelected.Click += async delegate { await RemoveDuplicates(); };
            duplicateTools.Controls.Add(recycleSelected);
            selectExact = MakeButton("Seleziona identici", false);
            selectExact.AccessibleDescription = "Seleziona le copie identiche e conserva una copia per gruppo.";
            selectExact.Click += delegate {
                if (!busy && !listing && results != null) {
                    SelectExactDuplicates();
                    ShowCurrentGroup();
                }
            };
            duplicateTools.Controls.Add(selectExact);
            clearSelected = MakeButton("Deseleziona tutti", false);
            clearSelected.Click += delegate {
                duplicateSelection.Clear();
                foreach (var box in cards.Controls.OfType<Panel>().SelectMany(panel => panel.Controls.OfType<CheckBox>()))
                    box.Checked = false;
                UpdateDuplicateActions();
            };
            duplicateTools.Controls.Add(clearSelected);
            duplicateSelectionStatus.AutoSize = true;
            duplicateSelectionStatus.ForeColor = Muted;
            duplicateSelectionStatus.Margin = new Padding(3, 10, 0, 0);
            duplicateTools.Controls.Add(duplicateSelectionStatus);
            content.Controls.Add(duplicateTools, 0, 1);

            cards.Dock = DockStyle.Fill;
            cards.AutoScroll = true;
            cards.BackColor = Background;
            cards.Padding = new Padding(9);
            cards.SizeChanged += delegate { ResizeEmptyLabel(); };
            content.Controls.Add(cards, 0, 2);

            var paging = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            previous = MakeButton("‹ Indietro", false);
            previous.Click += delegate { page--; ShowCurrentGroup(); };
            next = MakeButton("Avanti ›", false);
            next.Click += delegate { page++; ShowCurrentGroup(); };
            paging.Controls.Add(previous);
            pageStatus.AutoSize = true;
            pageStatus.ForeColor = Muted;
            pageStatus.Margin = new Padding(3, 10, 10, 0);
            paging.Controls.Add(pageStatus);
            paging.Controls.Add(next);
            content.Controls.Add(paging, 0, 3);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty,
                Padding = new Padding(20, 0, 20, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status.Dock = DockStyle.Fill;
            status.AutoEllipsis = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.ForeColor = Muted;
            footer.Controls.Add(status, 0, 0);
            progressBar.Style = ProgressBarStyle.Marquee;
            progressBar.Width = 95;
            progressBar.Height = 11;
            progressBar.Margin = new Padding(6, 12, 5, 0);
            progressBar.Visible = false;
            footer.Controls.Add(progressBar, 1, 0);
            showIssues = MakeButton("Dettagli lettura", false);
            showIssues.Visible = false;
            showIssues.Click += delegate { ShowIssueList(); };
            footer.Controls.Add(showIssues, 2, 0);
            var credits = MakeButton("Crediti", false);
            credits.Click += delegate { ShowCredits(); };
            footer.Controls.Add(credits, 3, 0);
            layout.Controls.Add(footer, 0, 3);

            SetStatus("Pronto");
            RefreshGroups();
            Shown += delegate { RefreshJournal(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (renaming || recycling) {
                    e.Cancel = true;
                    SetStatus("Attendi il completamento dell’operazione prima di chiudere.");
                    return;
                }
                closing = true;
                if (scanCancellation != null) scanCancellation.Cancel();
                if (inventoryCancellation != null) inventoryCancellation.Cancel();
                ClearCards();
                allFiles.ClearPreview();
                renameFiles.ClearPreview();
            };
        }

        private static Label LabelFor(string value, float size, FontStyle style, Color color) {
            return new Label { Text = value, AutoSize = true, ForeColor = color, Font = new Font("Segoe UI", size, style) };
        }

        private Button MakeButton(string text, bool primary) {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 32),
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, Cursor = Cursors.Hand,
                BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink,
                Padding = new Padding(7, 0, 7, 0), Margin = new Padding(0, 4, 8, 2) };
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Line;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(7, 82, 75) : Background;
            return button;
        }

        private void SetStatus(string value) { if (!closing) status.Text = value; }

        private void UpdateButtons() {
            bool available = !busy && !listing;
            choose.Enabled = available;
            subfolders.Enabled = available;
            similarity.Enabled = available;
            rename.Enabled = folder != null;
            renameFiles.EnableRename(available && folder != null && !hasPending);
            scan.Visible = scanCancellation == null;
            scan.Enabled = available && folder != null;
            undo.Enabled = available && (hasPending || hasUndo);
            undo.Text = hasPending ? "Riprendi il ripristino" : "Annulla ultima rinomina";
            cancel.Visible = scanCancellation != null;
            cancel.Enabled = scanCancellation != null && !scanCancellation.IsCancellationRequested;
            progressBar.Visible = busy || listing;
            UpdateDuplicateActions();
        }

        private void RefreshJournal() {
            try {
                RenameState state = renamer.Read();
                hasUndo = state.Last != null;
                hasPending = state.Pending != null;
                if (hasPending)
                    SetStatus("Rinomina interrotta. Usa «Riprendi il ripristino» per recuperare i nomi.");
            } catch (Exception e) {
                hasUndo = false;
                hasPending = true;
                SetStatus("Registro delle rinomine illeggibile: " + e.Message);
            }
            UpdateButtons();
        }

        private async Task PickFolder() {
            using (var picker = new FolderBrowserDialog {
                Description = "Scegli la cartella da analizzare o i cui file vuoi rinominare",
                ShowNewFolderButton = false, SelectedPath = folder ?? ""
            }) {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                if (!StringComparer.OrdinalIgnoreCase.Equals(folder, picker.SelectedPath)) {
                    folder = picker.SelectedPath;
                    folderBox.Text = folder;
                    results = null;
                    duplicateSelection.Clear();
                    RefreshGroups();
                    allFiles.SetFiles(new List<FileEntry>(), "Lettura della cartella…");
                    renameFiles.SetFiles(new List<FileEntry>(), "Lettura della cartella…");
                }
                tabs.SelectedTab = allPage;
                SetStatus("Lettura dell’elenco dei file…");
                await ReloadFolderViews();
            }
        }

        private async Task ReloadFolderViews() {
            if (folder == null || listing) return;
            string target = folder;
            bool recursive = subfolders.Checked;
            listing = true;
            var source = new CancellationTokenSource();
            inventoryCancellation = source;
            UpdateButtons();
            try {
                var loaded = await Task.Run(() => {
                    var issues = new List<string>();
                    var list = Scanner.ReadFiles(target, recursive, issues, source.Token);
                    var names = new List<FileEntry>();
                    bool root = new DirectoryInfo(target).Parent == null;
                    if (!root) {
                        try {
                            foreach (var item in Renamer.Plan(target).Items) {
                                names.Add(new FileEntry { Path = Path.Combine(target, item.Original),
                                    Length = item.Length, Changed = item.Changed });
                            }
                        } catch (IOException error) { issues.Add(error.Message); }
                        catch (UnauthorizedAccessException error) { issues.Add(error.Message); }
                    }
                    return Tuple.Create(list, names, issues, root);
                }, source.Token);
                if (closing) return;
                allFiles.SetFiles(loaded.Item1, "Nessun file trovato.");
                renameFiles.SetFiles(loaded.Item2, loaded.Item4 ? "Scegli una cartella all’interno dell’unità."
                    : (loaded.Item3.Count == 0 ? "Tutti i file visibili hanno già il nome della cartella."
                    : "Nessun file disponibile per la rinomina. Controlla i dettagli di lettura."));
                inventoryIssues = loaded.Item3;
                UpdateIssueButton();
                SetStatus(loaded.Item1.Count + " file · " + loaded.Item2.Count + " da rinominare" +
                    (inventoryIssues.Count == 0 ? "" : " · " + inventoryIssues.Count + " avvisi di lettura"));
            } catch (OperationCanceledException) { }
            catch (Exception e) { if (!closing) ReportError("Lettura della cartella interrotta", e); }
            finally {
                listing = false;
                inventoryCancellation = null;
                source.Dispose();
                if (!closing) UpdateButtons();
            }
        }

        private async Task Search() {
            if (busy || listing || folder == null) return;
            tabs.SelectedTab = duplicatePage;
            busy = true;
            string root = folder;
            bool recursive = subfolders.Checked;
            int? threshold = similarity.SelectedIndex > 0 ? new int?((new[] { 8, 14, 22 })[similarity.SelectedIndex - 1]) : null;
            var cancellation = new CancellationTokenSource();
            scanCancellation = cancellation;
            UpdateButtons();
            var progress = new Progress<string>(SetStatus);
            try {
                results = await Task.Run(() => Scanner.Run(root, recursive, threshold, progress, cancellation.Token));
                if (closing) return;
                SelectExactDuplicates();
                allFiles.SetFiles(results.Files, "Nessun file trovato.");
                groupFilter.SelectedIndex = 0;
                RefreshGroups();
                SetStatus(results.Count + " file trovati · " + results.Groups.Count + " gruppi" +
                    (duplicateSelection.Count == 0 ? "" : " · " + duplicateSelection.Count + " selezionati automaticamente") +
                    (results.Issues.Count == 0 ? "" : " · " + results.Issues.Count + " avvisi di lettura"));
            } catch (OperationCanceledException) { SetStatus("Ricerca interrotta."); }
            catch (Exception e) { if (!closing) ReportError("Ricerca interrotta", e); }
            finally {
                scanCancellation = null;
                cancellation.Dispose();
                busy = false;
                if (!closing) UpdateButtons();
            }
        }

        private void RefreshGroups() {
            groupList.BeginUpdate();
            try {
                groupList.Items.Clear();
                if (results != null) {
                    foreach (var group in results.Groups) {
                        if (groupFilter.SelectedIndex == 0 || (groupFilter.SelectedIndex == 1) == group.Exact)
                            groupList.Items.Add(group);
                    }
                }
                if (groupList.Items.Count > 0) groupList.SelectedIndex = 0;
            } finally { groupList.EndUpdate(); }
            if (groupList.SelectedIndex < 0) ShowCurrentGroup();
            UpdateIssueButton();
            UpdateDuplicateActions();
        }

        private void DrawGroup(object sender, DrawItemEventArgs e) {
            if (e.Index < 0 || e.Index >= groupList.Items.Count) return;
            var group = (DuplicateGroup)groupList.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? Accent : (e.Index % 2 == 0 ? Background : Color.White)))
                e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, (e.Index + 1) + ".  " + (group.Exact ? "File identici" : "Foto simili"),
                Font, new Point(e.Bounds.Left + 9, e.Bounds.Top + 6), selected ? Color.White : Ink);
            TextRenderer.DrawText(e.Graphics, group.Files.Count + " file", Font,
                new Point(e.Bounds.Left + 9, e.Bounds.Top + Font.Height + 9), selected ? Color.White : Muted);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }

        private void ClearCards() {
            if (pageCancellation != null) {
                pageCancellation.Cancel();
                pageCancellation.Dispose();
                pageCancellation = null;
            }
            while (cards.Controls.Count > 0) {
                Control control = cards.Controls[0];
                foreach (PictureBox preview in control.Controls.OfType<PictureBox>()) {
                    if (preview.Image != null) preview.Image.Dispose();
                }
                cards.Controls.RemoveAt(0);
                control.Dispose();
            }
        }

        private void ShowCurrentGroup() {
            if (closing) return;
            var group = groupList.SelectedItem as DuplicateGroup;
            cards.SuspendLayout();
            try {
                ClearCards();
                previous.Visible = next.Visible = pageStatus.Visible = false;
                if (group == null) {
                    resultTitle.Text = "Anteprime";
                    string message = results == null
                        ? (folder == null ? "Scegli una cartella per iniziare." : "Premi «Cerca duplicati» per iniziare.")
                        : "Nessun gruppo corrispondente. Apri «Tutti i file» per vedere le anteprime.";
                    var empty = LabelFor(message, 11, FontStyle.Regular, Muted);
                    empty.AutoSize = false;
                    empty.Name = "empty";
                    empty.Height = 54;
                    empty.TextAlign = ContentAlignment.MiddleCenter;
                    cards.Controls.Add(empty);
                    ResizeEmptyLabel();
                    return;
                }
                int totalPages = (group.Files.Count + PageSize - 1) / PageSize;
                page = Math.Min(totalPages - 1, Math.Max(page, 0));
                resultTitle.Text = (group.Exact ? "File identici" : "Foto simili da verificare") + " · " + group.Files.Count + " file";
                previous.Visible = next.Visible = pageStatus.Visible = totalPages > 1;
                previous.Enabled = page > 0;
                next.Enabled = page + 1 < totalPages;
                pageStatus.Text = (page + 1) + " / " + totalPages;
                pageCancellation = new CancellationTokenSource();
                foreach (FileEntry file in group.Files.Skip(page * PageSize).Take(PageSize))
                    AddCard(file, pageCancellation.Token);
                cards.AutoScrollPosition = Point.Empty;
            } finally { cards.ResumeLayout(true); }
        }

        private void ResizeEmptyLabel() {
            var label = cards.Controls["empty"];
            if (label != null) label.Width = Math.Max(100, cards.ClientSize.Width - 25);
        }

        private void AddCard(FileEntry file, CancellationToken token) {
            var card = new Panel { Width = 229, Height = 331, BackColor = Color.White, Margin = new Padding(0, 0, 9, 9) };
            var preview = new PictureBox { Bounds = new Rectangle(8, 8, 213, 145), BackColor = Background,
                SizeMode = PictureBoxSizeMode.Zoom, Visible = false };
            var placeholder = new Label { Bounds = preview.Bounds, BackColor = Background, ForeColor = Muted,
                TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Text = "Caricamento anteprima…" };
            var name = new Label { Bounds = new Rectangle(9, 159, 211, 24), Text = Path.GetFileName(file.Path),
                Font = new Font(Font, FontStyle.Bold), AutoEllipsis = true };
            var path = new TextBox { Bounds = new Rectangle(9, 184, 211, 55), Text = file.Path,
                Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, BackColor = Background,
                AccessibleName = "Percorso completo di " + Path.GetFileName(file.Path) };
            path.KeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.KeyData == (Keys.Control | Keys.A)) {
                    path.SelectAll();
                    e.SuppressKeyPress = true;
                }
            };
            var meta = new Label { Bounds = new Rectangle(9, 245, 211, 20), Text = FileBrowserPane.DisplaySize(file.Length), AutoEllipsis = true, ForeColor = Muted };
            var open = MakeButton("Mostra nella cartella", false);
            open.MinimumSize = new Size(0, 25);
            open.Location = new Point(8, 300);
            var select = new CheckBox { Text = "Seleziona", AutoSize = true, Location = new Point(10, 270),
                Checked = duplicateSelection.ContainsKey(file.Path),
                AccessibleName = "Seleziona " + Path.GetFileName(file.Path) };
            select.CheckedChanged += delegate {
                if (select.Checked) duplicateSelection[file.Path] = file;
                else duplicateSelection.Remove(file.Path);
                UpdateDuplicateActions();
            };
            open.Click += delegate {
                try {
                    string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    Process.Start(new ProcessStartInfo(explorer, "/select,\"" + file.Path + "\"") { UseShellExecute = true });
                } catch (Exception e) { ReportError("Apertura di Esplora file", e); }
            };
            card.Controls.AddRange(new Control[] { preview, placeholder, name, path, meta, select, open });
            cards.Controls.Add(card);
            var tip = new ToolTip();
            tip.SetToolTip(name, file.Path);
            tip.SetToolTip(meta, file.Path);
            card.Disposed += delegate { tip.Dispose(); };
            preview.Cursor = Cursors.Hand;
            preview.AccessibleName = "Ingrandisci " + Path.GetFileName(file.Path);
            preview.DoubleClick += delegate { if (Photos.Supported(file.Path)) Enlarge(file.Path); };
            LoadPreview(file, preview, placeholder, meta, token);
        }

        private async void LoadPreview(FileEntry file, PictureBox preview, Label placeholder, Label meta, CancellationToken token) {
            bool acquired = false;
            PreviewContent content = null;
            try {
                await previewSlots.WaitAsync(token);
                acquired = true;
                content = await Task.Run(() => PreviewContent.Load(file.Path, 500), token);
                if (token.IsCancellationRequested || preview.IsDisposed) return;
                if (content.Text != null) {
                    placeholder.TextAlign = ContentAlignment.TopLeft;
                    placeholder.Padding = new Padding(8, 5, 6, 4);
                    placeholder.Text = content.Text;
                    return;
                }
                if (content.Width > 0)
                    meta.Text = content.Width + " × " + content.Height + " px · " + FileBrowserPane.DisplaySize(file.Length);
                else if (!String.IsNullOrEmpty(content.Caption))
                    meta.Text = content.Caption + " · " + FileBrowserPane.DisplaySize(file.Length);
                if (content.Caption == "Anteprima del contenuto non disponibile")
                    preview.SizeMode = PictureBoxSizeMode.CenterImage;
                preview.Image = content.Image;
                content.Image = null;
                preview.Visible = true;
                placeholder.Visible = false;
            } catch (OperationCanceledException) { }
            catch (Exception) {
                if (!token.IsCancellationRequested && !placeholder.IsDisposed) placeholder.Text = "Anteprima non leggibile";
            } finally {
                if (content != null) content.Dispose();
                if (acquired) previewSlots.Release();
            }
        }

        private void Enlarge(string path) {
            try {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                PhotoInfo photo = Photos.Read(path, Math.Max(area.Width, area.Height));
                using (photo.Preview)
                using (var dialog = new Form { Text = Path.GetFileName(path), StartPosition = FormStartPosition.CenterParent,
                        Size = new Size(Math.Min(1050, area.Width), Math.Min(760, area.Height)),
                        ShowInTaskbar = false, KeyPreview = true, BackColor = Navy })
                using (var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = photo.Preview }) {
                    dialog.Controls.Add(picture);
                    dialog.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) dialog.Close(); };
                    dialog.ShowDialog(this);
                }
            } catch (Exception e) { ReportError("Apertura dell’anteprima", e); }
        }

        private void SelectExactDuplicates() {
            duplicateSelection.Clear();
            foreach (FileEntry file in results.RecommendedDuplicates())
                duplicateSelection[file.Path] = file;
            UpdateDuplicateActions();
        }

        private void UpdateDuplicateActions() {
            bool available = !busy && !listing;
            recycleSelected.Enabled = available && duplicateSelection.Count > 0;
            clearSelected.Enabled = recycleSelected.Enabled;
            selectExact.Enabled = available && results != null && results.Groups.Any(group => group.Exact);
            duplicateSelectionStatus.Text = duplicateSelection.Count + " selezionati";
        }

        private async Task RemoveDuplicates() {
            if (busy || listing || results == null || duplicateSelection.Count == 0) return;
            var selected = duplicateSelection.Values.ToList();
            int emptyGroups = results.Groups.Count(g => g.Files.All(f => duplicateSelection.ContainsKey(f.Path)));
            string prompt = "Spostare nel Cestino " + selected.Count + " file?";
            if (emptyGroups > 0) prompt += Environment.NewLine + Environment.NewLine +
                "Hai selezionato tutte le copie in " + emptyGroups + (emptyGroups == 1 ? " gruppo." : " gruppi.") +
                " Vuoi procedere?";
            if (MessageBox.Show(this, prompt, "Elimina duplicati selezionati", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            busy = recycling = true;
            UpdateButtons();
            RecycleResult outcome = null;
            try {
                ClearCards();
                allFiles.ClearPreview();
                renameFiles.ClearPreview();
                var progress = new Progress<string>(SetStatus);
                outcome = await RecycleBin.MoveAsync(selected, Handle, progress, results.Groups);
                foreach (string path in outcome.Moved) duplicateSelection.Remove(path);
                if (outcome.Moved.Count > 0) {
                    var moved = new HashSet<string>(outcome.Moved, StringComparer.OrdinalIgnoreCase);
                    results.Files.RemoveAll(f => moved.Contains(f.Path));
                    results.Count = results.Files.Count;
                    foreach (var group in results.Groups) group.Files.RemoveAll(f => moved.Contains(f.Path));
                    results.Groups.RemoveAll(g => g.Files.Count < 2);
                    results.Issues.AddRange(outcome.Errors);
                    RefreshGroups();
                } else {
                    results.Issues.AddRange(outcome.Errors);
                    ShowCurrentGroup();
                    UpdateIssueButton();
                }
                var visiblePaths = new HashSet<string>(results.Groups.SelectMany(g => g.Files).Select(f => f.Path),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string path in duplicateSelection.Keys.Where(p => !visiblePaths.Contains(p)).ToList())
                    duplicateSelection.Remove(path);
                UpdateDuplicateActions();
                await ReloadFolderViews();
                SetStatus(outcome.Moved.Count + " file nel Cestino" +
                    (outcome.Errors.Count == 0 ? "" : " · " + outcome.Errors.Count + " file non spostati"));
                if (outcome.Errors.Count > 0)
                    MessageBox.Show(this, outcome.Moved.Count + " file nel Cestino. " + outcome.Errors.Count +
                        " non spostati." + Environment.NewLine + Environment.NewLine + outcome.Errors[0],
                        "Eliminazione dei duplicati", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            } catch (Exception error) {
                ShowCurrentGroup();
                ReportError("Eliminazione interrotta", error);
            } finally {
                recycling = busy = false;
                UpdateButtons();
            }
        }

        private async Task PreviewRename() {
            if (busy || listing || folder == null || hasPending) return;
            ISet<string> selected = renameFiles.SelectedNames();
            if (selected.Count == 0) return;
            busy = true;
            renaming = true;
            UpdateButtons();
            string target = folder;
            try {
                SetStatus("Preparazione dei nuovi nomi…");
                RenameBatch plan = await Task.Run(() => Renamer.Plan(target, selected));
                if (plan.Items.Count != selected.Count) {
                    await ReloadFolderViews();
                    MessageBox.Show(this, "La cartella è cambiata. Controlla l’elenco aggiornato e ripeti la selezione.", Text);
                    return;
                }
                if (!ConfirmPlan(plan)) {
                    SetStatus("Rinomina annullata.");
                    return;
                }
                ClearCards();
                allFiles.ClearPreview();
                renameFiles.ClearPreview();
                SetStatus("Rinomina in corso…");
                await Task.Run(() => renamer.Apply(plan, false));
                results = null;
                duplicateSelection.Clear();
                RefreshGroups();
                await ReloadFolderViews();
                SetStatus(plan.Items.Count + " file rinominati. Puoi ripristinare i nomi con «Annulla ultima rinomina».");
            } catch (Exception e) {
                ShowCurrentGroup();
                ReportError("Rinomina interrotta", e);
            } finally {
                renaming = busy = false;
                RefreshJournal();
            }
        }

        private bool ConfirmPlan(RenameBatch plan) {
            using (var dialog = MakeDialog("Anteprima rinomina", 850, 510)) {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(15) };
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
                dialog.Controls.Add(layout);
                var intro = new Label { Text = plan.Items.Count + " file da rinominare · " + plan.Folder +
                    Environment.NewLine + "Il pulsante modifica i nomi dei file direttamente in questa cartella.",
                    Dock = DockStyle.Fill, AutoEllipsis = true };
                layout.Controls.Add(intro, 0, 0);
                var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
                    UseCompatibleStateImageBehavior = false, VirtualMode = true, VirtualListSize = plan.Items.Count };
                list.Columns.Add("Nome attuale", 365);
                list.Columns.Add("Nuovo nome", 365);
                list.RetrieveVirtualItem += delegate(object sender, RetrieveVirtualItemEventArgs e) {
                    RenameItem item = plan.Items[e.ItemIndex];
                    e.Item = new ListViewItem(new[] { item.Original, item.Renamed });
                };
                layout.Controls.Add(list, 0, 1);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                var apply = MakeButton("Applica rinomina", true);
                apply.DialogResult = DialogResult.OK;
                var cancelDialog = MakeButton("Indietro", false);
                cancelDialog.DialogResult = DialogResult.Cancel;
                buttons.Controls.Add(apply);
                buttons.Controls.Add(cancelDialog);
                layout.Controls.Add(buttons, 0, 2);
                dialog.CancelButton = cancelDialog;
                dialog.ActiveControl = cancelDialog;
                return dialog.ShowDialog(this) == DialogResult.OK;
            }
        }

        private async Task UndoRename() {
            if (busy || listing) return;
            bool recover = hasPending;
            try {
                RenameState state = renamer.Read();
                RenameBatch batch = recover ? state.Pending : state.Last;
                if (batch == null) return;
                string action = recover ? "Ripristinare i nomi precedenti all’operazione interrotta?" : "Ripristinare i nomi originali?";
                if (MessageBox.Show(this, action + Environment.NewLine + Environment.NewLine + batch.Folder,
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                busy = renaming = true;
                UpdateButtons();
                ClearCards();
                allFiles.ClearPreview();
                renameFiles.ClearPreview();
                SetStatus("Ripristino dei nomi…");
                await Task.Run(() => { if (recover) renamer.Recover(); else renamer.Apply(null, true); });
                results = null;
                duplicateSelection.Clear();
                RefreshGroups();
                await ReloadFolderViews();
                SetStatus("Ripristino completato.");
            } catch (Exception e) {
                ShowCurrentGroup();
                ReportError("Ripristino interrotto", e);
            } finally {
                renaming = busy = false;
                RefreshJournal();
            }
        }

        private Form MakeDialog(string title, int width, int height) {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            return new Form { Text = title, StartPosition = FormStartPosition.CenterParent,
                Size = new Size(Math.Min(width, area.Width), Math.Min(height, area.Height)),
                MinimizeBox = false, ShowInTaskbar = false, Font = Font, BackColor = Background };
        }

        private List<string> ReadIssues() {
            return (results == null ? inventoryIssues : inventoryIssues.Concat(results.Issues).Distinct()).ToList();
        }

        private void UpdateIssueButton() { showIssues.Visible = ReadIssues().Count > 0; }

        private void ShowIssueList() {
            var issues = ReadIssues();
            if (issues.Count == 0) return;
            using (var dialog = MakeDialog("Dettagli di lettura", 760, 430)) {
                var details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both,
                    WordWrap = false, ReadOnly = true, Text = String.Join(Environment.NewLine, issues) };
                dialog.Controls.Add(details);
                dialog.ShowDialog(this);
            }
        }

        private static Image ReadLogo() {
            using (var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("TwinName.Ficoapps.png"))
            using (var source = Image.FromStream(stream)) return new Bitmap(source);
        }

        private LinkLabel MakeLink(string text, Color color) {
            var link = new LinkLabel { Text = text, AutoSize = true, LinkColor = color, ActiveLinkColor = color,
                VisitedLinkColor = color, LinkBehavior = LinkBehavior.HoverUnderline };
            link.LinkClicked += delegate {
                try { Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true }); }
                catch (Exception error) { ReportError("Apertura di GitHub", error); }
            };
            return link;
        }

        private void ShowCredits() {
            using (var dialog = MakeDialog("Crediti di TwinName", 440, 386))
            using (Image logo = ReadLogo()) {
                var picture = new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom,
                    Location = new Point(117, 12), Size = new Size(184, 184), AccessibleName = "Logo Ficoapps" };
                dialog.Controls.Add(picture);
                var title = LabelFor("TwinName 1.1.1", 17, FontStyle.Bold, Ink);
                title.Location = new Point(50, 208);
                dialog.Controls.Add(title);
                var owner = LabelFor("Progetto e marchio Ficoapps" + Environment.NewLine + Credit, 11, FontStyle.Regular, Muted);
                owner.Location = new Point(51, 247);
                dialog.Controls.Add(owner);
                var link = MakeLink("Ficoapps su GitHub", Accent);
                link.Location = new Point(50, 309);
                dialog.Controls.Add(link);
                dialog.ShowDialog(this);
            }
        }

        private void ReportError(string title, Exception e) {
            SetStatus(title + ".");
            MessageBox.Show(this, e.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
