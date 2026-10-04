using System.Text;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Output;

namespace TiaTracker.App.Services;

/// <summary>
/// Scrive la cartella TiaTrackerOut della commessa. Sempre "al meglio": un
/// errore di scrittura (cartella di rete giu', permessi) finisce nel log e
/// nella barra di stato, mai in un'eccezione che blocca il lavoro.
/// </summary>
public static class OutputService
{
    private static readonly UTF8Encoding Utf8Bom = new(true);

    public static OutputLayout? Layout(long commessaId)
    {
        Commessa? c = AppServices.Commesse.Get(commessaId);
        return c == null ? null : OutputLayout.For(c, AppServices.Commesse.ScanRoots(commessaId));
    }

    /// <summary>Registro modifiche, layout IP e dati della commessa. Restituisce la cartella o null.</summary>
    public static string? Refresh(long commessaId)
    {
        try
        {
            Commessa? c = AppServices.Commesse.Get(commessaId);
            OutputLayout? layout = Layout(commessaId);
            if (c == null || layout == null)
            {
                return null;
            }

            layout.EnsureCreated();
            List<ProjectVersion> versions = AppServices.Versions.ByCommessa(commessaId);
            List<Change> changes = AppServices.Changes.ByCommessa(commessaId);
            List<IpDevice> ip = AppServices.Commesse.IpDevices(commessaId);
            List<VersionLoad> loads = AppServices.Versions.CurrentLoads(commessaId);

            Write(Path.Combine(layout.Changes, "registro_modifiche.md"), Reports.ChangeRegisterMarkdown(c, versions, changes, loads));
            Write(Path.Combine(layout.Changes, "registro_modifiche.csv"), Reports.ChangeRegisterCsv(versions, changes));
            Write(Path.Combine(layout.Network, "layout_ip.md"), "# Layout IP - " + c.Display + "\r\n\r\n" + IpTableFormat.ToMarkdown(ip));
            Write(Path.Combine(layout.Network, "layout_ip.csv"), IpTableFormat.ToCsv(ip));
            Write(Path.Combine(layout.Data, "commessa.json"), Reports.CommessaJson(c, versions, changes, ip, loads));
            return layout.Root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("TiaTrackerOut non aggiornata: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Copia l'export grezzo dello snapshot (XML, SCL, manifest) in Snapshot\versione\data_sorgente_plc.
    /// Niente DB: la cartella arriva gia' calcolata, cosi' si puo' chiamare da un thread di lavoro.
    /// </summary>
    public static string? SaveSnapshotFiles(OutputLayout layout, ProjectVersion version, Snapshot s, string exportDir)
    {
        try
        {
            if (!Directory.Exists(exportDir))
            {
                return null;
            }

            layout.EnsureCreated();
            string target = layout.SnapshotDir(version.Label, s.CreatedUtc, s.Source, s.PlcName);
            CopyTree(new DirectoryInfo(exportDir), new DirectoryInfo(target));
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Export dello snapshot non copiato in TiaTrackerOut: " + ex.Message);
            return null;
        }
    }

    public static string? SaveCompareReport(long commessaId, string baseLabel, string targetLabel, string? plc, string markdown)
    {
        try
        {
            OutputLayout? layout = Layout(commessaId);
            if (layout == null)
            {
                return null;
            }

            layout.EnsureCreated();
            string file = layout.CompareFile(baseLabel, targetLabel, plc, DateTime.UtcNow);
            Write(file, markdown);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Report di confronto non scritto: " + ex.Message);
            return null;
        }
    }

    private static void Write(string path, string text) => File.WriteAllText(path, text, Utf8Bom);

    private static void CopyTree(DirectoryInfo source, DirectoryInfo target)
    {
        target.Create();
        foreach (FileInfo f in source.GetFiles())
        {
            f.CopyTo(Path.Combine(target.FullName, f.Name), overwrite: true);
        }

        foreach (DirectoryInfo d in source.GetDirectories())
        {
            CopyTree(d, new DirectoryInfo(Path.Combine(target.FullName, d.Name)));
        }
    }
}
