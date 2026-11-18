# TwinName

<img src="src/Ficoapps.png" width="120" alt="Ficoapps">

[Italiano](#italiano) · [English](#english) · [Download](https://github.com/Ficoapps/TwinName/releases/latest)

## Italiano

TwinName è un’app portatile per Windows 10 e 11, sviluppata da Ficoapps. Trova duplicati e foto simili, mostra le anteprime e rinomina più file usando il nome della cartella. L’app lavora in locale.

1. **Scarica e avvia.** Scarica lo ZIP per Windows dalla [pagina delle release](https://github.com/Ficoapps/TwinName/releases), estrailo e apri `TwinName.exe`.
2. **Cerca duplicati.** Scegli una cartella e premi **Cerca duplicati**. TwinName seleziona le copie identiche e lascia una copia per gruppo. Tiene quella con la data di modifica più vecchia. A parità di data, tiene il primo percorso in ordine alfabetico. Le foto simili si selezionano a mano.
3. **Controlla le anteprime.** Nella scheda **Tutti i file** puoi vedere le anteprime anche dei file unici. TwinName mostra immagini e brevi estratti di testo. Per PDF, video e altri formati usa le miniature disponibili in Windows.
4. **Rimuovi le copie scelte.** Rivedi le spunte nella scheda **Duplicati** e conferma con **Elimina selezionati** per spostare i file nel Cestino di Windows.
5. **Rinomina i file.** Nella scheda **Da rinominare** spunta i file che vuoi modificare. Controlla i nuovi nomi e conferma la rinomina. Le estensioni vengono conservate e puoi annullare l’ultima operazione.

## English

TwinName is a portable app for Windows 10 and 11 by Ficoapps. It finds duplicate files and similar photos, previews files, and renames selected files after their folder. It runs locally.

1. **Download and run.** Get the Windows ZIP from the [Releases page](https://github.com/Ficoapps/TwinName/releases), extract it, and open `TwinName.exe`.
2. **Find duplicates.** Choose a folder and click **Cerca duplicati**. TwinName selects exact duplicates and leaves one copy in each group. It keeps the file with the earliest modification date, using alphabetical path order for ties. Select similar photos manually.
3. **Preview files.** Open **Tutti i file** to preview files even when they have no duplicates. TwinName displays images and short text excerpts. For PDFs, videos, and other formats, it uses thumbnails provided by Windows when available.
4. **Remove selected copies.** Review the selection in **Duplicati** and confirm with **Elimina selezionati** to move the files to the Windows Recycle Bin.
5. **Rename files.** In **Da rinominare**, tick the files you want to rename. Review the new names before confirming. The app preserves file extensions and can undo the last rename operation.

## Compilazione / Build

Da Windows, con il .NET SDK. / On Windows, with the .NET SDK.

```powershell
dotnet build src/TwinName.csproj -c Release
```

## Crediti / Credits

Progetto e logo di Ficoapps. / Project and logo by Ficoapps.  
© 2026 [Ficoapps](https://github.com/Ficoapps).
