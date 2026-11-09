using System.Globalization;

namespace TiaTracker.App.Services;

/// <summary>Log su file giornaliero in logs\. Mai eccezioni verso il chiamante.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        try
        {
            string file = Path.Combine(DataPaths.Logs, "tiatracker_" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + level + " " + message + Environment.NewLine;
            lock (Gate)
            {
                Directory.CreateDirectory(DataPaths.Logs);
                File.AppendAllText(file, line);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
