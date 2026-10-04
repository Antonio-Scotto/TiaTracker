using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TiaTracker.App.Services;
using TiaTracker.Contracts;
using TiaTracker.Core.Workers;

namespace TiaTracker.App.ViewModels;

/// <summary>Un worker (V21 o V18): dove sta, dove e' stato cercato, esito di "hello".</summary>
public sealed partial class WorkerCheckRow : ObservableObject
{
    public WorkerCheckRow(WorkerLookup lookup)
    {
        Lookup = lookup;
    }

    public WorkerLookup Lookup { get; }

    public int Major => Lookup.TiaMajor;

    public string Title => "Worker TIA V" + Major + "  (" + Lookup.ExeName + ")";

    public bool Found => Lookup.Found;

    public string StatusText => Found ? "Trovato: " + Lookup.Origin : "NON trovato";

    public string? Path => Lookup.Path;

    public string SearchedText => string.Join(Environment.NewLine, Lookup.Searched);

    [ObservableProperty]
    public partial string HelloText { get; set; } = "";

    [ObservableProperty]
    public partial bool? HelloOk { get; set; }

    [ObservableProperty]
    public partial bool Running { get; set; }
}

/// <summary>
/// Finestra Diagnostica: tutto cio' che serve per capire perche' uno snapshot
/// da TIA non parte, in un posto solo, con un report da copiare.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    public DiagnosticsViewModel()
    {
        WorkerDir = AppServices.Settings.WorkerDir;
        ApiV21 = !string.IsNullOrWhiteSpace(AppServices.Settings.TiaPublicApiV21) ? AppServices.Settings.TiaPublicApiV21 : AppServices.Settings.TiaPublicApi;
        ApiV18 = AppServices.Settings.TiaPublicApiV18;
        Refresh();
    }

    public ObservableCollection<WorkerCheckRow> Workers { get; } = new();

    public ObservableCollection<string> Apis { get; } = new();

    public ObservableCollection<string> Instances { get; } = new();

    [ObservableProperty]
    public partial string GroupText { get; set; } = "";

    [ObservableProperty]
    public partial bool? GroupOk { get; set; }

    [ObservableProperty]
    public partial string InstancesText { get; set; } = "";

    [ObservableProperty]
    public partial string? WorkerDir { get; set; }

    [ObservableProperty]
    public partial string? ApiV21 { get; set; }

    [ObservableProperty]
    public partial string? ApiV18 { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial bool AllOk { get; set; }

    public string AppDir => DataPaths.AppDir;

    public string DataRoot => DataPaths.Root + "  (" + DataPaths.Mode + ")";

    public string LogDir => DataPaths.Logs;

    public string WorkDir => DataPaths.Work;

    public string AppVersion => "TiaTracker " + typeof(DiagnosticsViewModel).Assembly.GetName().Version;

    [RelayCommand]
    private void Refresh()
    {
        Workers.Clear();
        foreach (int major in WorkerHealth.NeededMajors().Union(new[] { 18, 21 }).OrderByDescending(m => m))
        {
            Workers.Add(new WorkerCheckRow(WorkerRunner.Locate(major)));
        }

        Apis.Clear();
        List<OpennessApi> apis = TiaInstallations.Find();
        foreach (OpennessApi a in apis)
        {
            Apis.Add("Openness " + a.ApiVersion + " → worker v" + a.WorkerMajor + ":  " + a.Folder + "\\" + a.Dll);
        }

        if (Apis.Count == 0)
        {
            Apis.Add("Nessuna cartella PublicAPI trovata in Programmi\\Siemens\\Automation (TIA Portal con Openness non installato?)");
        }

        GroupOk = WorkerHealth.InOpennessGroup();
        GroupText = GroupOk switch
        {
            true => "L'utente " + Environment.UserName + " e' nel gruppo \"" + WorkerHealth.OpennessGroup + "\".",
            false => "L'utente " + Environment.UserName + " NON risulta nel gruppo \"" + WorkerHealth.OpennessGroup +
                     "\". Aggiungerlo da Gestione computer > Utenti e gruppi locali, poi disconnettersi e riaccedere.",
            _ => "Appartenenza al gruppo \"" + WorkerHealth.OpennessGroup + "\" non determinabile.",
        };

        bool workersOk = Workers.Where(w => w.Major == 21 || WorkerHealth.NeededMajors().Contains(w.Major)).All(w => w.Found);
        AllOk = workersOk && apis.Count > 0 && GroupOk != false;
        Summary = AllOk
            ? "Nessun problema trovato. Il primo snapshot con un worker nuovo fa comparire in TIA la richiesta \"Openness access\": va confermata."
            : "Ci sono problemi: vedere le righe in rosso.";
        if (Application.Current?.MainWindow?.DataContext is MainViewModel main)
        {
            main.CheckWorkers();
        }
    }

    [RelayCommand]
    private async Task Hello(WorkerCheckRow? row)
    {
        if (row == null || row.Running)
        {
            return;
        }

        row.Running = true;
        row.HelloText = "Avvio del worker...";
        row.HelloOk = null;
        try
        {
            WorkerOutcome o = await new WorkerRunner().RunAsync(row.Major, "hello", new Dictionary<string, object?>(), null, CancellationToken.None);
            HelloResult? r = o.Ok ? o.Result<HelloResult>() : null;
            row.HelloOk = r != null;
            row.HelloText = r != null
                ? "OK: " + r.EngineeringAssembly + " da " + r.ApiPath + " (worker " + r.WorkerVersion + ", .NET " + r.Framework + ")"
                : o.Explain();
        }
        catch (Exception ex)
        {
            Log.Error("hello", ex);
            row.HelloOk = false;
            row.HelloText = ex.Message;
        }
        finally
        {
            row.Running = false;
        }
    }

    [RelayCommand]
    private async Task ListInstances()
    {
        InstancesText = "Ricerca delle istanze di TIA Portal...";
        Instances.Clear();
        WorkerOutcome o = await new WorkerRunner().RunAsync(21, "instances", new Dictionary<string, object?>(), null, CancellationToken.None);
        if (!o.Ok)
        {
            InstancesText = o.Explain();
            return;
        }

        List<InstanceInfo> list = o.Result<InstancesResult>()?.Instances ?? new List<InstanceInfo>();
        foreach (InstanceInfo i in list)
        {
            Instances.Add("PID " + i.Pid.ToString(CultureInfo.InvariantCulture) + " · " + i.Mode + " · " + (i.ProjectPath ?? "(nessun progetto aperto)"));
        }

        InstancesText = list.Count == 0 ? "Nessun TIA Portal aperto." : list.Count + " istanze di TIA Portal aperte.";
    }

    [RelayCommand]
    private void Save()
    {
        AppSettings s = AppServices.Settings;
        s.WorkerDir = Blank(WorkerDir);
        s.TiaPublicApiV21 = Blank(ApiV21);
        s.TiaPublicApi = null;
        s.TiaPublicApiV18 = Blank(ApiV18);
        AppServices.SaveSettings();
        Log.Info("Impostazioni worker: WorkerDir=" + s.WorkerDir + ", V21=" + s.TiaPublicApiV21 + ", V18=" + s.TiaPublicApiV18);
        Refresh();
        Notify.Success("Impostazioni salvate", AllOk ? "Nessun problema trovato." : "Restano dei problemi: vedere Diagnostica.");
    }

    [RelayCommand]
    private void BrowseWorkerDir()
    {
        string? folder = PickFolder("Cartella dei worker (quella che contiene v21\\ e v18\\, o l'exe)", WorkerDir);
        if (folder != null)
        {
            WorkerDir = folder;
        }
    }

    [RelayCommand]
    private void BrowseApi(string? which)
    {
        bool v18 = which == "18";
        string? folder = PickFolder(v18 ? "Cartella con Siemens.Engineering.dll (TIA V18)" : "Cartella con Siemens.Engineering.Base.dll (TIA V21)",
            v18 ? ApiV18 : ApiV21);
        if (folder == null)
        {
            return;
        }

        if (v18)
        {
            ApiV18 = folder;
        }
        else
        {
            ApiV21 = folder;
        }
    }

    [RelayCommand]
    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string target = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        if (Directory.Exists(target))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private void CopyReport()
    {
        StringBuilder sb = new();
        sb.AppendLine(AppVersion + " - diagnostica del " + DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
        sb.AppendLine("App: " + AppDir);
        sb.AppendLine("Dati: " + DataRoot);
        sb.AppendLine();
        foreach (WorkerCheckRow w in Workers)
        {
            sb.AppendLine(w.Title + ": " + w.StatusText + (w.Path != null ? " - " + w.Path : ""));
            if (!string.IsNullOrEmpty(w.HelloText))
            {
                sb.AppendLine("  hello: " + w.HelloText);
            }

            if (!w.Found)
            {
                sb.AppendLine("  cercato in:");
                foreach (string p in w.Lookup.Searched)
                {
                    sb.AppendLine("    " + p);
                }
            }
        }

        sb.AppendLine();
        foreach (string a in Apis)
        {
            sb.AppendLine(a);
        }

        sb.AppendLine(GroupText);
        if (!string.IsNullOrEmpty(InstancesText))
        {
            sb.AppendLine(InstancesText);
            foreach (string i in Instances)
            {
                sb.AppendLine("  " + i);
            }
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            Notify.Success("Report diagnostico copiato negli appunti");
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Notify.Warning("Appunti non disponibili", ex.Message);
        }
    }

    private static string? PickFolder(string title, string? start)
    {
        OpenFolderDialog dialog = new() { Title = title };
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start))
        {
            dialog.InitialDirectory = start;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Trim('"');
}
