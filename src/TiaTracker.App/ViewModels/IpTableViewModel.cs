using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Win32;
using TiaTracker.App.Services;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Output;

namespace TiaTracker.App.ViewModels;

public sealed partial class IpRow : ObservableObject
{
    public IpRow(IpDevice d)
    {
        _device = d;
        Source = IpSources.Normalize(d.Source);
        TiaMissing = d.TiaMissing;
        Network = d.Network;
        Ip = d.Ip;
        Subnet = d.Subnet;
        Gateway = d.Gateway;
        Name = d.Name;
        Kind = d.Kind;
        ProfinetName = d.ProfinetName;
        Location = d.Location;
        Mac = d.Mac;
        Notes = d.Notes;
    }

    [ObservableProperty] public partial string? Network { get; set; }
    [ObservableProperty] public partial string? Ip { get; set; }
    [ObservableProperty] public partial string? Subnet { get; set; }
    [ObservableProperty] public partial string? Gateway { get; set; }
    [ObservableProperty] public partial string? Name { get; set; }
    [ObservableProperty] public partial string? Kind { get; set; }
    [ObservableProperty] public partial string? ProfinetName { get; set; }
    [ObservableProperty] public partial string? Location { get; set; }
    [ObservableProperty] public partial string? Mac { get; set; }
    [ObservableProperty] public partial string? Notes { get; set; }

    /// <summary>"IP non valido", "IP duplicato" o null.</summary>
    [ObservableProperty]
    public partial string? Problem { get; set; }

    private readonly IpDevice _device;

    /// <summary>
    /// manuale, tia (rete, IP, maschera, gateway, dispositivo, tipo e nome PROFINET li
    /// aggiorna TIA) o proneta (trovata in rete, non nel progetto).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFromTia), nameof(SourceText), nameof(SourceTip))]
    public partial string Source { get; set; } = IpSources.Manual;

    public bool IsFromTia => Source == IpSources.Tia;

    /// <summary>Era in TIA e all'ultimo export non c'era piu'.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceText), nameof(SourceTip))]
    public partial bool TiaMissing { get; set; }

    public string SourceText => Source == IpSources.Proneta ? "PRONETA" : !IsFromTia ? "" : TiaMissing ? "TIA ✕" : "TIA";

    public string? SourceTip => Source == IpSources.Proneta
        ? "Trovata in rete con PRONETA: al prossimo \"Aggiorna da TIA\" diventa la riga del progetto se ha lo stesso IP"
        : !IsFromTia ? "Riga scritta a mano"
        : TiaMissing ? "Non c'e' piu' nell'ultimo export da TIA: verificare e togliere la riga se non serve"
        : "Letta da TIA" + (_device.TiaSeenUtc.HasValue ? " il " + _device.TiaSeenUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : "") +
          ": i campi di rete si cambiano in TIA (o si stacca la riga)";

    /// <summary>Da TIA a manuale: da qui in poi l'allineamento non la tocca piu'.</summary>
    public void Detach()
    {
        Source = IpSources.Manual;
        TiaMissing = false;
        _device.TiaKey = null;
    }

    public IpDevice ToDevice() => new()
    {
        Network = Network, Ip = Ip?.Trim(), Subnet = Subnet, Gateway = Gateway, Name = Name, Kind = Kind,
        ProfinetName = ProfinetName, Location = Location, Mac = Mac, Notes = Notes,
        Source = Source,
        TiaKey = IsFromTia ? _device.TiaKey : null,
        TiaVersionId = IsFromTia ? _device.TiaVersionId : null,
        TiaSeenUtc = IsFromTia ? _device.TiaSeenUtc : null,
        TiaMissing = IsFromTia && TiaMissing,
    };
}

/// <summary>
/// Layout IP della commessa: griglia modificabile, salvata da sola dopo ogni
/// modifica. Con Google si scambia dagli appunti: la copia incollata in
/// Documenti diventa una tabella, quella di Fogli si incolla qui.
/// </summary>
public sealed partial class IpTableViewModel : ObservableObject
{
    private readonly CommessaNode _node;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _save;
    private bool _loading;

    public IpTableViewModel(CommessaNode node, MainViewModel main)
    {
        _node = node;
        _main = main;
        // Allineamento con TIA fatto altrove: prima si salva cio' che e' in sospeso, poi si ricarica.
        WeakReferenceMessenger.Default.Register<IpTableViewModel, IpTableFlushRequest>(this, (vm, m) =>
        {
            if (m.CommessaId == vm._node.Commessa.Id && vm._save.IsEnabled)
            {
                vm.Save();
            }
        });
        WeakReferenceMessenger.Default.Register<IpTableViewModel, IpTableChanged>(this, (vm, m) =>
        {
            if (m.CommessaId == vm._node.Commessa.Id)
            {
                vm.Load();
            }
        });
        _save = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            Save();
        };
        Rows.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (IpRow r in e.NewItems)
                {
                    r.PropertyChanged += OnRowChanged;
                }
            }

            Touch();
        };
        Load();
    }

    public ObservableCollection<IpRow> Rows { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial IpRow? Selected { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    public void Load()
    {
        _loading = true;
        Rows.Clear();
        foreach (IpDevice d in AppServices.Commesse.IpDevices(_node.Commessa.Id))
        {
            Rows.Add(new IpRow(d));
        }

        _loading = false;
        Validate();
        int tia = Rows.Count(r => r.IsFromTia), missing = Rows.Count(r => r.TiaMissing);
        Status = Rows.Count + " dispositivi" + (tia > 0 ? ", " + tia + " letti da TIA" : "") + (missing > 0 ? ", " + missing + " non piu' in TIA" : "");
        OnPropertyChanged(nameof(HasMissing));
    }

    public bool HasMissing => Rows.Any(r => r.TiaMissing);

    /// <summary>Le colonne che per le righe di TIA si cambiano solo in TIA.</summary>
    public static readonly HashSet<string> TiaOwned = new(StringComparer.Ordinal)
    {
        nameof(IpRow.Ip), nameof(IpRow.Name), nameof(IpRow.Kind), nameof(IpRow.Network), nameof(IpRow.Subnet),
        nameof(IpRow.Gateway), nameof(IpRow.ProfinetName),
    };

    [RelayCommand]
    private void DetachFromTia()
    {
        if (Selected is not { IsFromTia: true } row)
        {
            Status = "Selezionare una riga letta da TIA.";
            return;
        }

        row.Detach();
        Touch();
        Status = "Riga staccata da TIA: ora si modifica a mano e l'allineamento non la tocca.";
    }

    [RelayCommand]
    private void RemoveMissing()
    {
        List<IpRow> missing = Rows.Where(r => r.TiaMissing).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        if (MessageBox.Show("Togliere le " + missing.Count + " righe che non ci sono piu' in TIA?", "Lista IP",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        foreach (IpRow r in missing)
        {
            Rows.Remove(r);
        }
    }

    /// <summary>Hardware e rete letti adesso da TIA (versione di riferimento), poi Lista IP allineata.</summary>
    [RelayCommand]
    private async Task RefreshFromTia()
    {
        ProjectVersion? v = HardwareService.Reference(_node.Commessa);
        VersionNode? node = v == null ? null : _node.Versions.FirstOrDefault(x => x.Version.Id == v.Id);
        if (node == null)
        {
            Notify.Warning("Aggiorna da TIA", "La commessa non ha una versione di riferimento (In lavoro, caricata o piu' recente).");
            return;
        }

        if (_save.IsEnabled)
        {
            Save();
        }

        await new VersionActions(_main, node).ReadHardware(forceIpSync: true);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IpRow.Problem))
        {
            Touch();
        }
    }

    private void Touch()
    {
        if (_loading)
        {
            return;
        }

        Validate();
        Status = "Modifiche da salvare...";
        _save.Stop();
        _save.Start();
    }

    private void Validate()
    {
        Dictionary<string, int> count = Rows.Where(r => !string.IsNullOrWhiteSpace(r.Ip))
            .GroupBy(r => r.Ip!.Trim()).ToDictionary(g => g.Key, g => g.Count());
        foreach (IpRow r in Rows)
        {
            r.Problem = string.IsNullOrWhiteSpace(r.Ip) ? null
                : !IpTableFormat.IsValidIp(r.Ip) ? "IP non valido"
                : count[r.Ip!.Trim()] > 1 ? "IP duplicato"
                : null;
        }
    }

    /// <summary>Salvataggio (anche automatico); aggiorna Rete\layout_ip in TiaTrackerOut.</summary>
    [RelayCommand]
    public void Save()
    {
        _save.Stop();
        AppServices.Commesse.SaveIpDevices(_node.Commessa.Id, Rows.Select(r => r.ToDevice()).ToList());
        OutputService.Refresh(_node.Commessa.Id);
        Status = Rows.Count + " dispositivi · salvato alle " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void Add()
    {
        // Proposta: rete e subnet della riga selezionata, IP successivo.
        IpRow? from = Selected ?? Rows.LastOrDefault();
        IpRow row = new(new IpDevice { Network = from?.Network, Subnet = from?.Subnet, Gateway = from?.Gateway, Ip = NextIp(from?.Ip) });
        int at = Selected != null ? Rows.IndexOf(Selected) + 1 : Rows.Count;
        Rows.Insert(at, row);
        Selected = row;
    }

    private string? NextIp(string? ip)
    {
        if (!IpTableFormat.IsValidIp(ip))
        {
            return null;
        }

        string[] p = ip!.Trim().Split('.');
        int last = int.Parse(p[3], CultureInfo.InvariantCulture);
        HashSet<string> used = Rows.Select(r => r.Ip?.Trim() ?? "").ToHashSet();
        for (int n = last + 1; n < 255; n++)
        {
            string candidate = p[0] + "." + p[1] + "." + p[2] + "." + n.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private bool CanRemove() => Selected != null;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (Selected != null)
        {
            int at = Rows.IndexOf(Selected);
            Rows.Remove(Selected);
            Selected = Rows.Count == 0 ? null : Rows[Math.Min(at, Rows.Count - 1)];
        }
    }

    [RelayCommand]
    private void SortByIp()
    {
        List<IpRow> sorted = Rows.OrderBy(r => r.Network ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => IpTableFormat.SortKey(r.Ip), StringComparer.Ordinal).ToList();
        Rows.Clear();
        foreach (IpRow r in sorted)
        {
            Rows.Add(r);
        }
    }

    /// <summary>
    /// Negli appunti sia HTML sia testo con tabulazioni: Google Documenti
    /// incolla una tabella, Google Fogli ed Excel una cella per colonna.
    /// </summary>
    [RelayCommand]
    private void CopyForGoogle()
    {
        List<IpDevice> devices = Rows.Select(r => r.ToDevice()).ToList();
        DataObject data = new();
        data.SetData(DataFormats.Html, ClipboardHtml(IpTableFormat.ToHtmlTable(devices, "Layout IP - " + _node.Commessa.Display)));
        data.SetData(DataFormats.UnicodeText, IpTableFormat.ToTsv(devices));
        Clipboard.SetDataObject(data, copy: true);
        Status = devices.Count + " righe copiate: incollare in Google Documenti (tabella) o Google Fogli.";
    }

    /// <summary>Righe copiate da Google Fogli o Excel (con o senza intestazione).</summary>
    [RelayCommand]
    private void PasteFromClipboard()
    {
        if (!Clipboard.ContainsText())
        {
            Notify.Info("Incolla", "Negli appunti non c'e' testo. Copiare le righe da Google Fogli (con l'intestazione, se c'e').");
            return;
        }

        AddParsed(IpTableFormat.Parse(Clipboard.GetText()), "incollate");
    }

    [RelayCommand]
    private void ImportCsv()
    {
        OpenFileDialog dialog = new() { Filter = "CSV o testo|*.csv;*.tsv;*.txt|Tutti i file|*.*", Title = "Importa layout IP" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string text = File.ReadAllText(dialog.FileName);
        if (Core.Network.PronetaFile.LooksLikeProneta(text))
        {
            ImportProneta(dialog.FileName, text);
            return;
        }

        AddParsed(IpTableFormat.Parse(text), "importate da " + Path.GetFileName(dialog.FileName));
    }

    /// <summary>
    /// Dispositivi trovati in rete da PRONETA (CSV della topologia online o XML della
    /// topologia): MAC e campi vuoti sulle righe abbinate, nuovi dispositivi in fondo.
    /// </summary>
    [RelayCommand]
    private void ImportFromProneta()
    {
        OpenFileDialog dialog = new()
        {
            Filter = "PRONETA (CSV o XML)|*.csv;*.xml|Tutti i file|*.*",
            Title = "Importa da PRONETA (topologia online)",
        };
        if (dialog.ShowDialog() == true)
        {
            ImportProneta(dialog.FileName, File.ReadAllText(dialog.FileName));
        }
    }

    public void ImportProneta(string file, string text)
    {
        List<Core.Network.PronetaDevice> devices;
        try
        {
            devices = Core.Network.PronetaFile.Parse(text);
        }
        catch (System.Xml.XmlException ex)
        {
            Notify.Error("File PRONETA illeggibile", ex.Message);
            return;
        }

        if (devices.Count == 0)
        {
            Notify.Warning("Nessun dispositivo", Path.GetFileName(file) + " non sembra un export di PRONETA (topologia online in CSV o XML).");
            return;
        }

        List<IpDevice> current = Rows.Select(r => r.ToDevice()).ToList();
        List<Core.Network.PronetaMatch> plan = Core.Network.PronetaImport.Plan(current, devices);
        Views.PronetaImportWindow dialog = new(Path.GetFileName(file), plan) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        List<IpDevice> result = Core.Network.PronetaImport.Apply(current, plan);
        _loading = true;
        Rows.Clear();
        foreach (IpDevice d in result)
        {
            Rows.Add(new IpRow(d));
        }

        _loading = false;
        Touch();
        int matched = plan.Count(p => p.Apply && p.Kind == Core.Network.PronetaMatchKind.Matched);
        int added = plan.Count(p => p.Apply && p.Kind == Core.Network.PronetaMatchKind.New);
        int differ = plan.Count(p => p.IpDiffers);
        Status = "PRONETA: " + matched + " righe completate (MAC), " + added + " dispositivi aggiunti" + (differ > 0 ? ", " + differ + " con IP diverso dalla tabella" : "");
        Notify.Success("Importato da PRONETA", Status);
    }

    [RelayCommand]
    private void ExportCsv()
    {
        SaveFileDialog dialog = new()
        {
            Filter = "CSV (punto e virgola)|*.csv",
            FileName = "layout_ip_" + _node.Commessa.Code + ".csv",
            InitialDirectory = OutputService.Layout(_node.Commessa.Id)?.Network is string dir && Directory.Exists(dir) ? dir : null,
        };
        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, IpTableFormat.ToCsv(Rows.Select(r => r.ToDevice())), new UTF8Encoding(true));
            Status = "Esportato in " + dialog.FileName;
        }
    }

    private void AddParsed(List<IpDevice> devices, string what)
    {
        if (devices.Count == 0)
        {
            Notify.Info("Layout IP", "Nessuna riga riconosciuta.");
            return;
        }

        bool replace = false;
        if (Rows.Count > 0)
        {
            MessageBoxResult r = MessageBox.Show(
                devices.Count + " righe " + what + ".\n\nSi = aggiungere in fondo\nNo = sostituire la tabella attuale",
                "Layout IP", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel)
            {
                return;
            }

            replace = r == MessageBoxResult.No;
        }

        if (replace)
        {
            Rows.Clear();
        }

        foreach (IpDevice d in devices)
        {
            Rows.Add(new IpRow(d));
        }

        Status = devices.Count + " righe " + what;
    }

    /// <summary>Formato CF_HTML richiesto dagli appunti di Windows.</summary>
    private static string ClipboardHtml(string fragment)
    {
        const string Header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string Pre = "<html><head><meta charset=\"utf-8\"></head><body><!--StartFragment-->";
        const string Post = "<!--EndFragment--></body></html>";
        int headerLength = string.Format(CultureInfo.InvariantCulture, Header, 0, 0, 0, 0).Length;
        int startHtml = headerLength;
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(Pre);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(Post);
        return string.Format(CultureInfo.InvariantCulture, Header, startHtml, endHtml, startFragment, endFragment) + Pre + fragment + Post;
    }
}
