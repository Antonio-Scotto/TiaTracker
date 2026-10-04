using System.Globalization;
using System.Xml.Linq;

namespace TiaTracker.Core.Scanning;

public enum OpenKind
{
    Closed,

    /// <summary>Lock presente, TIA in esecuzione, .info scritto da questo PC.</summary>
    Open,

    /// <summary>Lock scritto da un altro computer (cartella condivisa).</summary>
    OpenOtherPc,

    /// <summary>Lock presente ma TIA non gira: residuo di un crash.</summary>
    StaleLock,
}

public sealed record OpenState(OpenKind Kind, string? User, string? Computer, DateTime? SinceUtc)
{
    public static readonly OpenState Closed = new(OpenKind.Closed, null, null, null);

    public bool IsOpen => Kind == OpenKind.Open;

    public string Describe()
    {
        string since = SinceUtc.HasValue
            ? SinceUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
            : "";
        return Kind switch
        {
            OpenKind.Open => $"Aperta in TIA da {User} su {Computer} alle {since}",
            OpenKind.OpenOtherPc => $"Aperta da altro PC: {User} su {Computer} alle {since}",
            OpenKind.StaleLock => "Lock residuo" + (Computer != null ? $" ({User} su {Computer}, {since})" : "") + ": TIA non e' in esecuzione",
            _ => "Chiusa",
        };
    }
}

/// <summary>
/// Progetto aperto in TIA = file .info nascosto (User, Computer, Time in UTC)
/// + System\~PEData.1 o IM\SearchIndex\write.lock + processo TIA attivo.
/// Solo file system: l'arbitro esatto resta il comando instances del worker.
/// </summary>
public static class OpenStateDetector
{
    public static OpenState Detect(string projectFolder, string projectName, bool tiaRunning, string machineName)
    {
        string infoPath = Path.Combine(projectFolder, projectName + ".info");
        bool lockFile = File.Exists(Path.Combine(projectFolder, "System", "~PEData.1")) ||
                        File.Exists(Path.Combine(projectFolder, "IM", "SearchIndex", "write.lock"));

        if (!File.Exists(infoPath))
        {
            return lockFile && !tiaRunning ? new OpenState(OpenKind.StaleLock, null, null, null) : OpenState.Closed;
        }

        (string? user, string? computer, DateTime? time) = ReadInfo(infoPath);
        if (computer != null && !computer.Equals(machineName, StringComparison.OrdinalIgnoreCase))
        {
            return new OpenState(OpenKind.OpenOtherPc, user, computer, time);
        }

        if (!tiaRunning)
        {
            return new OpenState(OpenKind.StaleLock, user, computer, time);
        }

        return new OpenState(OpenKind.Open, user, computer, time);
    }

    internal static (string? User, string? Computer, DateTime? TimeUtc) ReadInfo(string path)
    {
        try
        {
            XDocument doc = XDocument.Load(path);
            XElement? root = doc.Root;
            string? user = root?.Element("User")?.Value;
            string? computer = root?.Element("Computer")?.Value;
            DateTime? time = null;
            string? raw = root?.Element("Time")?.Value;
            if (raw != null && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t))
            {
                time = t;
            }

            return (user, computer, time);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return (null, null, null);
        }
    }
}
