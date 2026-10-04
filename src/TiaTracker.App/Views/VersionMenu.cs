using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TiaTracker.App.Converters;
using TiaTracker.App.Services;
using TiaTracker.App.ViewModels;
using TiaTracker.Core.Domain;

namespace TiaTracker.App.Views;

/// <summary>
/// Menu del tasto destro su una versione (albero, intestazione "Stato", striscia
/// della dashboard): in cima gli stati con il loro colore, sotto le azioni.
/// Costruito al momento, cosi' abilitazioni e spunte sono sempre aggiornate.
/// </summary>
public static class VersionMenu
{
    public static ContextMenu Build(MainViewModel main, VersionNode node)
    {
        VersionActions a = new(main, node);
        ProjectVersion v = node.Version;
        bool busy = JobRunner.IsRunning;
        ContextMenu menu = new();

        menu.Items.Add(new MenuItem
        {
            Header = v.Label + "  ·  " + (node.Status.ChipText.Length > 0 ? node.Status.ChipText : "nessuno stato"),
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
        });

        foreach (VersionState s in VersionStates.All)
        {
            string text = VersionStates.Italian(s) + s switch
            {
                VersionState.Caricata => "...",
                VersionState.InCollaudo => " (FAT/SAT)...",
                _ => "",
            };
            MenuItem item = Item(text, Dot(VersionStates.ToneOf(s)), () => _ = a.SetState(s));
            item.ToolTip = VersionStates.Description(s);
            if (v.State == s)
            {
                item.IsChecked = true;
                item.FontWeight = FontWeights.SemiBold;
            }

            menu.Items.Add(item);
        }

        MenuItem none = Item("Nessuno stato", Glyph("Icon.Close"), () => _ = a.SetState(VersionState.None));
        none.ToolTip = VersionStates.Description(VersionState.None);
        none.IsEnabled = v.State != VersionState.None;
        menu.Items.Add(none);

        if (node.Status.OnPlc)
        {
            MenuItem off = Item("Togli dal PLC...", Glyph("Icon.Upload"), a.EndLoads);
            off.ToolTip = "Sul PLC: " + node.Status.LoadsText;
            menu.Items.Add(off);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Apri in TIA", Glyph("Icon.Play"), a.OpenInTia, a.HasProject && !a.IsOpenInTia));
        menu.Items.Add(Item("Snapshot da TIA aperto", Glyph("Icon.Camera"), () => _ = a.SnapshotFromOpen(), a.IsOpenInTia && !busy,
            "Export dal TIA che ha aperto questa versione"));
        menu.Items.Add(Item("Snapshot dalla cartella", Glyph("Icon.Camera"), () => _ = a.SnapshotFromFolder(), a.CanSnapshotFromFolder && !busy,
            "Copia della cartella aperta da TIA senza interfaccia (1-2 minuti)"));
        menu.Items.Add(Item("Leggi hardware e rete da TIA", Glyph("Icon.Hardware"), () => _ = a.ReadHardware(), (a.IsOpenInTia || a.CanSnapshotFromFolder) && !busy,
            "Dispositivi, moduli, IP e tabelle tag per Lista IP, Hardware e IO (senza export dei blocchi)"));
        menu.Items.Add(Item("Genera documenti", Glyph("Icon.Document"), () => _ = a.GenerateDocuments(), true,
            "Lista IP, Lista hardware, Lista IO e registro in DOCX, PDF e CSV (TiaTrackerOut\\Elenchi)"));
        menu.Items.Add(Item("Confronta con la caricata", Glyph("Icon.Compare"), a.CompareWithLoaded,
            node.Parent.CurrentLoads.Any(l => l.VersionId != v.Id)));
        menu.Items.Add(Item("Nuova versione da questa...", Glyph("Icon.Add"), () => _ = a.NewVersionFrom(), a.HasProject && !busy));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Apri cartella", Glyph("Icon.Folder"), () => a.OpenFolder(), a.FolderFull != null || a.RarFull != null || a.BackupFull != null));
        menu.Items.Add(Item("Copia percorso", Glyph("Icon.Copy"), a.CopyPath, a.FolderFull != null || a.RarFull != null || a.BackupFull != null));
        menu.Items.Add(Item("Backup .rar su rete", Glyph("Icon.Archive"), () => _ = a.BackupRar(), v.HasFolder && !busy));

        menu.Items.Add(new Separator());
        MenuItem delete = Item("Elimina...", Glyph("Icon.Delete"), a.DeleteVersion, v.HasFolder);
        delete.Foreground = ToneBrushConverter.Lookup(Tone.Danger, "Fg");
        menu.Items.Add(delete);
        return menu;
    }

    /// <summary>Menu piccolo sul nodo della commessa.</summary>
    public static ContextMenu BuildCommessa(MainViewModel main, CommessaNode node)
    {
        ContextMenu menu = new();
        menu.Items.Add(new MenuItem { Header = node.Title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        menu.Items.Add(Item("Aggiungi modifica...", Glyph("Icon.Add"), () => main.ShowCommessa(node)?.NewChangeCommand.Execute(null), true,
            "Nuova modifica della commessa: testo, blocchi toccati e versioni su cui va"));
        menu.Items.Add(Item("Modifiche della commessa", Glyph("Icon.Changes"), () => main.ShowCommessa(node)?.ShowChange(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Scansiona", Glyph("Icon.Scan"), () => main.ScanCommand.Execute(null), main.ScanCommand.CanExecute(null)));
        menu.Items.Add(Item("Importa storico...", Glyph("Icon.Import"), () => main.ImportHistoryCommand.Execute(null)));
        menu.Items.Add(Item("Genera documenti", Glyph("Icon.Document"),
            () => _ = DocumentService.ExportAsync(node.Commessa.Id, null, DocumentService.Kinds.ToArray(), Export.DocExporter.All.ToArray()),
            true, "Documenti della versione di riferimento in TiaTrackerOut\\Elenchi"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Apri TiaTrackerOut", Glyph("Icon.Folder"), () =>
        {
            string? root = OutputService.Refresh(node.Commessa.Id);
            if (root != null && Directory.Exists(root))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + root + "\"") { UseShellExecute = true });
            }
        }));
        string? versions = node.Roots.FirstOrDefault()?.Path;
        menu.Items.Add(Item("Apri cartella delle versioni", Glyph("Icon.Folder"), () =>
        {
            if (versions != null && Directory.Exists(versions))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + versions + "\"") { UseShellExecute = true });
            }
        }, versions != null));
        return menu;
    }

    private static MenuItem Item(string header, object? icon, Action click, bool enabled = true, string? tip = null)
    {
        MenuItem item = new() { Header = header, Icon = icon, IsEnabled = enabled };
        if (tip != null)
        {
            item.ToolTip = tip;
        }

        item.Click += (_, _) => click();
        return item;
    }

    private static Ellipse Dot(Tone tone) => new()
    {
        Width = 10,
        Height = 10,
        Fill = ToneBrushConverter.Lookup(tone, "Solid"),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static TextBlock Glyph(string key)
    {
        TextBlock t = new()
        {
            Text = Application.Current.TryFindResource(key) as string ?? "",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        return t;
    }
}
