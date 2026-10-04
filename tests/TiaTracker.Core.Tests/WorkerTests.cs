using TiaTracker.Contracts;
using TiaTracker.Core.Workers;

namespace TiaTracker.Core.Tests;

public class WorkerLocatorTests
{
    private const string Repo = @"C:\Lavori\TiaTracker";

    private static readonly DateTime Old = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2026, 10, 4, 14, 0, 0, DateTimeKind.Utc);

    private static Func<string, DateTime?> Disk(params (string Path, DateTime Time)[] files)
    {
        Dictionary<string, DateTime> map = files.ToDictionary(f => f.Path, f => f.Time, StringComparer.OrdinalIgnoreCase);
        return p => map.TryGetValue(p, out DateTime t) ? t : null;
    }

    [Fact]
    public void DallOutputDelPublishArrivaAllaRadiceDelRepository()
    {
        // Il caso della segnalazione: l'app in bin\Release\net10.0-windows\win-x64 e il worker
        // solo nella build Debug. Con la vecchia risalita a 6 livelli non si trovava.
        string app = Repo + @"\src\TiaTracker.App\bin\Release\net10.0-windows\win-x64\";
        string worker = Repo + @"\src\TiaTracker.Worker.V21\bin\Debug\tiatracker-worker-v21.exe";

        WorkerLookup r = WorkerLocator.Find(21, app, null, Disk((worker, New)));

        Assert.True(r.Found);
        Assert.Equal(worker, r.Path);
        Assert.Equal(WorkerLocator.OriginNearby, r.Origin);
    }

    [Fact]
    public void PrimaLaCartellaWorkerAccantoAllApp()
    {
        string app = @"C:\Programmi\TiaTracker\";
        string local = @"C:\Programmi\TiaTracker\worker\v21\tiatracker-worker-v21.exe";

        WorkerLookup r = WorkerLocator.Find(21, app, null, Disk((local, Old)));

        Assert.Equal(local, r.Path);
        Assert.Equal(WorkerLocator.OriginApp, r.Origin);
    }

    [Fact]
    public void LaCartellaDelleImpostazioniHaLaPrecedenza()
    {
        string app = @"C:\Programmi\TiaTracker\";
        string local = @"C:\Programmi\TiaTracker\worker\v21\tiatracker-worker-v21.exe";
        string custom = @"D:\Worker\v21\tiatracker-worker-v21.exe";

        WorkerLookup r = WorkerLocator.Find(21, app, @"D:\Worker", Disk((local, New), (custom, Old)));

        Assert.Equal(custom, r.Path);
        Assert.Equal(WorkerLocator.OriginSettings, r.Origin);
    }

    [Fact]
    public void AlloStessoLivelloVinceIlPiuRecente()
    {
        string app = Repo + @"\src\TiaTracker.App\bin\Debug\net10.0-windows\";
        string release = Repo + @"\src\TiaTracker.Worker.V21\bin\Release\tiatracker-worker-v21.exe";
        string debug = Repo + @"\src\TiaTracker.Worker.V21\bin\Debug\tiatracker-worker-v21.exe";

        WorkerLookup r = WorkerLocator.Find(21, app, null, Disk((release, Old), (debug, New)));

        Assert.Equal(debug, r.Path);
    }

    [Fact]
    public void TrovaIlPacchettoDist()
    {
        string app = Repo + @"\src\TiaTracker.App\bin\Release\net10.0-windows\win-x64\";
        string dist = Repo + @"\dist\TiaTracker\worker\v21\tiatracker-worker-v21.exe";

        WorkerLookup r = WorkerLocator.Find(21, app, null, Disk((dist, Old)));

        Assert.Equal(dist, r.Path);
    }

    [Fact]
    public void NonTrovatoRestituisceTuttiIPercorsiGuardati()
    {
        string app = Repo + @"\src\TiaTracker.App\bin\Release\net10.0-windows\win-x64\";

        WorkerLookup r = WorkerLocator.Find(21, app, @"D:\Worker", Disk());

        Assert.False(r.Found);
        Assert.Null(r.Origin);
        Assert.Contains(@"D:\Worker\v21\tiatracker-worker-v21.exe", r.Searched);
        Assert.Contains(app + @"worker\v21\tiatracker-worker-v21.exe", r.Searched);
        Assert.Contains(@"C:\Lavori\TiaTracker\src\TiaTracker.Worker.V21\bin\Debug\tiatracker-worker-v21.exe", r.Searched);
        Assert.Contains(@"C:\worker\v21\tiatracker-worker-v21.exe", r.Searched);
        Assert.Equal(r.Searched.Count, r.Searched.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(15, "v18")]
    [InlineData(18, "v18")]
    [InlineData(19, "v21")]
    [InlineData(21, "v21")]
    public void WorkerPerVersioneDiTia(int major, string folder)
    {
        Assert.Equal(folder, WorkerLocator.FolderName(major));
        Assert.Equal("tiatracker-worker-" + folder + ".exe", WorkerLocator.ExeName(major));
    }
}

public class TiaInstallationsTests
{
    [Fact]
    public void TrovaLeCartellePublicApiComeIlWorker()
    {
        using FakeTree t = new();
        t.File(@"Siemens\Automation\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.Base.dll", "x");
        t.File(@"Siemens\Automation\Portal V18\PublicAPI\V18\Siemens.Engineering.dll", "x");
        t.File(@"Siemens\Automation\Portal V18\PublicAPI\V15.1\Siemens.Engineering.dll", "x");
        t.File(@"Siemens\Automation\Portal V18\PublicAPI\V18.AddIn\Siemens.Engineering.AddIn.dll", "x");
        t.File(@"Siemens\Automation\Portal V18\PublicAPI\V16\leggimi.txt", "x");

        List<OpennessApi> apis = TiaInstallations.Find(new[] { t.Root });

        Assert.Equal(new[] { "V21", "V18", "V15.1" }, apis.Select(a => a.ApiVersion));
        Assert.Equal(21, apis[0].WorkerMajor);
        Assert.EndsWith(@"V21\net48", apis[0].Folder);
        Assert.Equal(18, apis[1].WorkerMajor);
        Assert.Equal("V18", TiaInstallations.BestFor(apis, 18)!.ApiVersion);
        Assert.Equal("V21", TiaInstallations.BestFor(apis, 21)!.ApiVersion);
    }

    [Fact]
    public void NienteSiemensNienteApi()
    {
        using FakeTree t = new();
        Assert.Empty(TiaInstallations.Find(new[] { t.Root, Path.Combine(t.Root, "non-esiste") }));
    }
}

public class WorkerErrorTextTests
{
    [Fact]
    public void WorkerMancanteNonSembraPiuUnProblemaDiDll()
    {
        string text = WorkerErrorText.Explain(ExitCodes.WorkerNotFound, null, "tiatracker-worker-v21.exe non trovato", 21);

        Assert.Contains("worker", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"worker\v21", text);
        Assert.Contains("Diagnostica", text);
        Assert.DoesNotContain("DLL", text);
    }

    [Fact]
    public void AccessoNegatoSpiegaIlGruppo()
    {
        string text = WorkerErrorText.Explain(ExitCodes.AccessDenied, "access-denied", "Access denied", 21);
        Assert.Contains("Siemens TIA Openness", text);
        Assert.Contains("(Access denied)", text);
    }

    [Fact]
    public void PromptOpennessScaduto()
    {
        string text = WorkerErrorText.Explain(ExitCodes.Timeout, "access-prompt-timeout", null, 21);
        Assert.Contains("Openness access", text);
    }

    [Fact]
    public void DllOpennessCitaLaVersione()
    {
        string text = WorkerErrorText.Explain(ExitCodes.OpennessNotFound, "openness-not-found", null, 18);
        Assert.Contains("TIA V18", text);
        Assert.Contains("Diagnostica", text);
    }

    [Fact]
    public void PercorsoTroppoLungoPerTia()
    {
        string text = WorkerErrorText.Explain(ExitCodes.OpenFailed, "open-failed",
            "The specified path' C:\\x\\AUT999999_Prova_V0.71-V21' (210 character) is too long. A maximum of 143 characters is allowed", 21);
        Assert.StartsWith("TIA Portal apre solo progetti in percorsi di al massimo 143 caratteri", text);

        string other = WorkerErrorText.Explain(ExitCodes.OpenFailed, "open-failed", "Licenza mancante", 21);
        Assert.StartsWith("TIA Portal non e' riuscito ad aprire il progetto", other);
    }

    [Fact]
    public void CodiceSconosciutoRiportaIlDettaglio()
    {
        string text = WorkerErrorText.Explain(ExitCodes.Unexpected, "unexpected", "NullReferenceException.", 21);
        Assert.Equal("Errore imprevisto. (NullReferenceException)", text);
    }
}
