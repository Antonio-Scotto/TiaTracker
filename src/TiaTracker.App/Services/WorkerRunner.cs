using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TiaTracker.Contracts;
using TiaTracker.Core.Workers;

namespace TiaTracker.App.Services;

/// <summary>Una riga del protocollo letta dall'app.</summary>
public sealed record WorkerMessage(
    string Type,
    string? Code,
    string? Text,
    string? Phase,
    int? Done,
    int? Total,
    string? Item,
    string? State,
    int? ExitCode,
    JsonElement? Data);

public sealed class WorkerOutcome
{
    public int TiaMajor { get; init; }

    public int ExitCode { get; init; }

    public bool Ok => ExitCode == ExitCodes.Ok && Final?.Type == WorkerLine.TypeResult;

    public WorkerMessage? Final { get; init; }

    public List<WorkerMessage> Warnings { get; init; } = new();

    public List<WorkerMessage> Items { get; init; } = new();

    public string StdErr { get; init; } = "";

    public string ErrorText => Final?.Type == WorkerLine.TypeError
        ? (Final.Text ?? ExitCodes.Describe(ExitCode))
        : Ok ? "" : ExitCodes.Describe(ExitCode) + (StdErr.Length > 0 ? ": " + LastLine(StdErr) : "");

    /// <summary>Il messaggio per l'utente, uguale per ogni comando.</summary>
    public string Explain() => WorkerErrorText.Explain(ExitCode, Final?.Code,
        Final?.Type == WorkerLine.TypeError ? Final.Text : StdErr.Length > 0 ? LastLine(StdErr) : null, TiaMajor);

    public T? Result<T>() =>
        Final?.Data is JsonElement e ? e.Deserialize<T>(WorkerRunner.JsonOptions) : default;

    private static string LastLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
}

/// <summary>
/// Avvia un worker, legge le JSON lines, sorveglia l'inattivita' e annulla.
/// Un solo job alla volta in tutta l'app. In attach TIA non e' figlio del
/// worker e resta vivo; in headless il TIA avviato dal worker cade con lui.
/// </summary>
public sealed class WorkerRunner
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly SemaphoreSlim OneJob = new(1, 1);
    private static readonly TimeSpan HardCap = TimeSpan.FromMinutes(60);
    private static readonly HashSet<string> Logged = new(StringComparer.OrdinalIgnoreCase);

    public static bool Busy => OneJob.CurrentCount == 0;

    /// <summary>Inattivita' massima per comando (nessuna riga su stdout).</summary>
    public static TimeSpan IdleTimeout(string command) => command switch
    {
        "hello" or "instances" => TimeSpan.FromSeconds(30),
        "probe" => TimeSpan.FromSeconds(150),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>Dove sta il worker per questa versione di TIA e dove e' stato cercato (WorkerLocator).</summary>
    public static WorkerLookup Locate(int tiaMajor) =>
        WorkerLocator.Find(tiaMajor, DataPaths.AppDir, AppServices.Settings.WorkerDir);

    public static string? WorkerPath(int tiaMajor) => Locate(tiaMajor).Path;

    /// <summary>hello e instances non toccano progetti: possono girare anche durante un job.</summary>
    public static bool IsExclusive(string command) => command is not ("hello" or "instances");

    /// <param name="parameters">Parametri del comando (pid, project, out, plc, no-scl): finiscono in --request.</param>
    public async Task<WorkerOutcome> RunAsync(
        int tiaMajor,
        string command,
        IReadOnlyDictionary<string, object?> parameters,
        Action<WorkerMessage>? onLine,
        CancellationToken ct)
    {
        WorkerLookup lookup = Locate(tiaMajor);
        if (!lookup.Found)
        {
            Log.Warn("Worker per TIA V" + tiaMajor + " non trovato. Cercato in:" + Environment.NewLine + "  " +
                     string.Join(Environment.NewLine + "  ", lookup.Searched));
            return new WorkerOutcome
            {
                TiaMajor = tiaMajor,
                ExitCode = ExitCodes.WorkerNotFound,
                StdErr = lookup.ExeName + " non trovato in " + lookup.Searched.Count + " percorsi (elenco in Diagnostica e nel log)",
            };
        }

        string exe = lookup.Path!;
        lock (Logged)
        {
            if (Logged.Add(exe))
            {
                Log.Info("Worker TIA V" + tiaMajor + ": " + exe + " (" + lookup.Origin + ")");
            }
        }

        bool exclusive = IsExclusive(command);
        if (exclusive && !await OneJob.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new WorkerOutcome { TiaMajor = tiaMajor, ExitCode = ExitCodes.Busy, StdErr = "Un altro job e' gia' in corso." };
        }

        string? requestFile = null;
        try
        {
            string args = command;
            if (parameters.Count > 0)
            {
                Directory.CreateDirectory(DataPaths.Work);
                requestFile = Path.Combine(DataPaths.Work, "request_" + Guid.NewGuid().ToString("N")[..8] + ".json");
                Dictionary<string, object?> flat = parameters.Where(p => p.Value != null)
                    .ToDictionary(p => p.Key, p => p.Value is bool b ? (object)(b ? "true" : "false") : p.Value);
                await File.WriteAllTextAsync(requestFile, MiniJson.Serialize(flat), new UTF8Encoding(false), ct).ConfigureAwait(false);
                args += " --request \"" + requestFile + "\"";
            }

            return await RunProcessAsync(tiaMajor, exe, command, args, onLine, ct).ConfigureAwait(false);
        }
        finally
        {
            if (requestFile != null)
            {
                TryDelete(requestFile);
            }

            if (exclusive)
            {
                OneJob.Release();
            }
        }
    }

    private static async Task<WorkerOutcome> RunProcessAsync(
        int tiaMajor, string exe, string command, string args, Action<WorkerMessage>? onLine, CancellationToken ct)
    {
        ProcessStartInfo psi = new(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };

        // Ogni worker riceve solo la cartella Openness della sua versione: TIA_PUBLIC_API
        // puntata alla V21 faceva fallire il worker V18, che la legge come ripiego.
        AppSettings s = AppServices.Settings;
        if (tiaMajor <= 18)
        {
            psi.Environment.Remove("TIA_PUBLIC_API");
            if (!string.IsNullOrWhiteSpace(s.TiaPublicApiV18))
            {
                psi.Environment["TIA_PUBLIC_API_V18"] = s.TiaPublicApiV18;
            }
        }
        else
        {
            string? v21 = !string.IsNullOrWhiteSpace(s.TiaPublicApiV21) ? s.TiaPublicApiV21 : s.TiaPublicApi;
            if (!string.IsNullOrWhiteSpace(v21))
            {
                psi.Environment["TIA_PUBLIC_API"] = v21;
            }
        }

        Log.Info("Worker: " + Path.GetFileName(exe) + " " + args);
        using Process p = new() { StartInfo = psi, EnableRaisingEvents = true };
        p.Start();

        StringBuilder stderr = new();
        WorkerMessage? final = null;
        List<WorkerMessage> warnings = new();
        List<WorkerMessage> items = new();
        long lastActivity = Environment.TickCount64;
        Stopwatch clock = Stopwatch.StartNew();

        Task errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(line);
                }
            }
        });

        Task outTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                Interlocked.Exchange(ref lastActivity, Environment.TickCount64);
                WorkerMessage? msg = Parse(line);
                if (msg == null)
                {
                    lock (stderr)
                    {
                        stderr.AppendLine("[stdout non JSON] " + line);
                    }

                    continue;
                }

                if (msg.Type is WorkerLine.TypeResult or WorkerLine.TypeError)
                {
                    final = msg;
                }
                else if (msg.Type == WorkerLine.TypeWarn)
                {
                    warnings.Add(msg);
                }
                else if (msg.Type == WorkerLine.TypeItem)
                {
                    items.Add(msg);
                }

                onLine?.Invoke(msg);
            }
        });

        TimeSpan idle = IdleTimeout(command);
        bool cancelSent = false;
        string? abortReason = null;
        while (!p.HasExited)
        {
            await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(500)).ConfigureAwait(false);
            if (p.HasExited)
            {
                break;
            }

            TimeSpan quiet = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastActivity));
            string? reason = ct.IsCancellationRequested ? "annullato dall'utente"
                : quiet > idle ? "nessuna risposta dal worker da " + (int)quiet.TotalSeconds + " s"
                : clock.Elapsed > HardCap ? "superato il limite di " + HardCap.TotalMinutes + " minuti"
                : null;
            if (reason == null || cancelSent)
            {
                continue;
            }

            // Prima si chiede al worker di fermarsi da solo, poi dopo 15 s si chiude d'ufficio.
            abortReason = reason;
            cancelSent = true;
            Log.Warn("Worker: " + reason + ", invio cancel");
            try
            {
                await p.StandardInput.WriteLineAsync("cancel").ConfigureAwait(false);
                await p.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                if (!p.HasExited)
                {
                    Log.Warn("Worker non chiuso dopo cancel: kill dell'albero di processi");
                    try
                    {
                        p.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            });
        }

        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        int exit = p.ExitCode;
        if (final == null && abortReason != null)
        {
            exit = ct.IsCancellationRequested ? ExitCodes.Cancelled : ExitCodes.Timeout;
            final = new WorkerMessage(WorkerLine.TypeError, ct.IsCancellationRequested ? "cancelled" : "timeout",
                abortReason, null, null, null, null, null, exit, null);
        }

        string err;
        lock (stderr)
        {
            err = stderr.ToString();
        }

        Log.Info("Worker uscito con " + exit.ToString(CultureInfo.InvariantCulture) + " in " + (int)clock.Elapsed.TotalSeconds + " s" +
                 (final?.Type == WorkerLine.TypeError ? ": " + final.Text : ""));
        if (err.Length > 0)
        {
            Log.Info("Worker stderr:\n" + (err.Length > 4000 ? err[^4000..] : err));
        }

        return new WorkerOutcome { TiaMajor = tiaMajor, ExitCode = exit, Final = final, Warnings = warnings, Items = items, StdErr = err };
    }

    public static WorkerMessage? Parse(string line)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("type", out JsonElement type))
            {
                return null;
            }

            return new WorkerMessage(
                type.GetString() ?? "",
                Str(r, "code"),
                Str(r, "text"),
                Str(r, "phase"),
                Int(r, "done"),
                Int(r, "total"),
                Str(r, "item"),
                Str(r, "state"),
                Int(r, "exitCode"),
                r.TryGetProperty("data", out JsonElement data) ? data.Clone() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
