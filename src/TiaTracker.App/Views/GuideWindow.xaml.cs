using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TiaTracker.App.Services;

namespace TiaTracker.App.Views;

/// <summary>Guida rapida (F1): scritta in tutte e due le lingue, si apre da sola al primo avvio.</summary>
public partial class GuideWindow : Window
{
    private static GuideWindow? _open;

    private static readonly (string It, string En, string[] ItLines, string[] EnLines)[] Sections =
    {
        ("Primi passi", "Getting started",
            new[]
            {
                "**Nuova commessa** (Ctrl+N): codice, nome e la cartella dove stanno le versioni del progetto TIA.",
                "**Scansiona** (F5) rilegge le cartelle e trova le versioni: cartelle con un file .ap21/.ap18, archivi .rar e backup.",
                "A sinistra l'elenco delle commesse e delle versioni; Ctrl+F per filtrare.",
            },
            new[]
            {
                "**New job** (Ctrl+N): code, name and the folder that holds the TIA project versions.",
                "**Scan** (F5) reads the folders again and finds the versions: folders with an .ap21/.ap18 file, .rar archives and backups.",
                "On the left, the list of jobs and versions; Ctrl+F to filter.",
            }),
        ("Stati delle versioni", "Version states",
            new[]
            {
                "Con il tasto destro su una versione: **In lavoro**, **Caricata** su uno o piu' PLC, **Vecchia**, **Scartata**, nuova versione, backup.",
                "La Dashboard della commessa mostra cosa c'e' su ogni PLC, la versione in lavoro e se lo snapshot e' da aggiornare.",
            },
            new[]
            {
                "Right-click a version: **In progress**, **Loaded** on one or more PLCs, **Old**, **Discarded**, new version, backup.",
                "The job Dashboard shows what is on each PLC, the version in progress and whether its snapshot needs updating.",
            }),
        ("Snapshot da TIA", "Snapshots from TIA",
            new[]
            {
                "**Snapshot da TIA** legge blocchi, hardware, rete e tabelle tag in sola lettura, dal TIA aperto o da una copia della cartella.",
                "Serve TIA Portal V21 (o V18) con Openness e l'utente nel gruppo Windows \"Siemens TIA Openness\": **Diagnostica** controlla tutto.",
            },
            new[]
            {
                "**Snapshot from TIA** reads blocks, hardware, network and tag tables read-only, from the open TIA or from a copy of the folder.",
                "It needs TIA Portal V21 (or V18) with Openness and the user in the Windows group \"Siemens TIA Openness\": **Diagnostics** checks everything.",
            }),
        ("Modifiche e confronto", "Changes and comparison",
            new[]
            {
                "Scheda **Modifiche**: cosa e' stato toccato, su quali blocchi e a che punto e' su ogni versione (importata, compilata, salvata).",
                "Dopo ogni snapshot i blocchi cambiati che nessuna modifica dichiara diventano **bozze da convalidare**.",
                "**Confronta** due versioni blocco per blocco; la **Matrice** incrocia modifiche e versioni.",
            },
            new[]
            {
                "**Changes** tab: what was touched, on which blocks and how far it is on each version (imported, compiled, saved).",
                "After every snapshot, changed blocks that no change declares become **drafts to validate**.",
                "**Compare** two versions block by block; the **Matrix** crosses changes and versions.",
            }),
        ("Documenti e pianificazione", "Documents and planning",
            new[]
            {
                "**Documenti**: Lista IP, Lista hardware, Lista IO, registro modifiche e piano in DOCX, PDF e CSV, nella cartella TiaTrackerOut della commessa.",
                "**Rete / IP**: la Lista IP si allinea anche a una scansione PRONETA.",
                "**Pianificazione**: attivita' e milestone su un Gantt, con l'avanzamento nella Dashboard.",
            },
            new[]
            {
                "**Documents**: IP list, Hardware list, IO list, change log and plan as DOCX, PDF and CSV, in the job's TiaTrackerOut folder (documents are in Italian).",
                "**Network / IP**: the IP list can also be aligned with a PRONETA scan.",
                "**Planning**: tasks and milestones on a Gantt chart, with progress on the Dashboard.",
            }),
        ("Lingua e dati", "Language and data",
            new[]
            {
                "La lingua (italiano o inglese) si sceglie in alto a destra e vale dal riavvio.",
                "I dati stanno in %LOCALAPPDATA%\\TiaTracker; nella versione portatile nella cartella data accanto all'exe.",
                "Tasti: Ctrl+N nuova commessa, F5 scansiona, Ctrl+F filtra, F1 questa guida.",
            },
            new[]
            {
                "The language (Italian or English) is chosen at the top right and applies after a restart.",
                "Data is stored in %LOCALAPPDATA%\\TiaTracker; in the portable version in the data folder next to the exe.",
                "Keys: Ctrl+N new job, F5 scan, Ctrl+F filter, F1 this guide.",
            }),
    };

    public GuideWindow()
    {
        InitializeComponent();
        Title = Loc.Pick("Guida rapida", "Quick guide");
        Footer.Text = Loc.Pick("Si riapre con il pulsante Guida o con F1.", "Open it again with the Guide button or F1.");
        Build();
    }

    public static void ShowSingle()
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }

        _open = new GuideWindow { Owner = Application.Current.MainWindow };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    private void Build()
    {
        Body.Children.Add(new TextBlock
        {
            Text = Loc.Pick("TiaTracker in breve", "TiaTracker at a glance"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        Body.Children.Add(new TextBlock
        {
            Text = Loc.Pick(
                "Tiene in ordine le versioni dei progetti TIA Portal di ogni commessa: quale e' sul PLC, su quale si lavora e a che punto sono le modifiche.",
                "Keeps the TIA Portal project versions of every job in order: which one is on the PLC, which one is being worked on and how far each change is."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 0, 0, 14),
        });

        int n = 1;
        foreach ((string it, string en, string[] itLines, string[] enLines) in Sections)
        {
            StackPanel card = new();
            card.Children.Add(new TextBlock
            {
                Text = n++ + ". " + Loc.Pick(it, en),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
            });
            foreach (string line in Loc.IsEnglish ? enLines : itLines)
            {
                card.Children.Add(Bullet(line));
            }

            Body.Children.Add(new Border
            {
                Child = card,
                Background = (System.Windows.Media.Brush)FindResource("CardBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 10),
                Margin = new Thickness(0, 0, 0, 10),
            });
        }
    }

    /// <summary>Una riga con il pallino; **testo** in grassetto.</summary>
    private static TextBlock Bullet(string line)
    {
        TextBlock tb = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5), LineHeight = 20 };
        tb.Inlines.Add(new Run("•  "));
        string[] parts = line.Split("**");
        for (int i = 0; i < parts.Length; i++)
        {
            Run r = new(parts[i]);
            if (i % 2 == 1)
            {
                r.FontWeight = FontWeights.SemiBold;
            }

            tb.Inlines.Add(r);
        }

        return tb;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
