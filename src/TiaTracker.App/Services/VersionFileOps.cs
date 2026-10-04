using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TiaTracker.App.Services;

/// <summary>
/// Operazioni sui file di una versione: nuova versione per copia, eliminazione
/// nel Cestino (si recupera), apertura in TIA Portal, backup .rar su rete.
/// </summary>
public static class VersionFileOps
{
    /// <summary>
    /// Copia la cartella progetto con un nome nuovo e rinomina il .apNN. Lock,
    /// TMP e Logs restano fuori: la copia non deve risultare aperta in TIA.
    /// </summary>
    public static string Duplicate(string sourceFolder, string sourceProjectFile, string newName)
    {
        DirectoryInfo source = new(sourceFolder);
        string targetFolder = Path.Combine(source.Parent!.FullName, newName);
        if (Directory.Exists(targetFolder) || File.Exists(targetFolder + ".rar"))
        {
            throw new IOException("Esiste gia' " + targetFolder + " (o il suo .rar).");
        }

        try
        {
            ProjectCopier.CopyProjectTree(source, new DirectoryInfo(targetFolder));
            string oldProject = Path.Combine(targetFolder, Path.GetFileName(sourceProjectFile));
            string newProject = Path.Combine(targetFolder, newName + Path.GetExtension(sourceProjectFile));
            if (!string.Equals(oldProject, newProject, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(oldProject, newProject);
            }

            Log.Info("Nuova versione " + newName + " copiata da " + sourceFolder);
            return newProject;
        }
        catch
        {
            // Una copia a meta' non deve diventare una versione.
            TryRecycle(targetFolder);
            throw;
        }
    }

    /// <summary>Sposta cartella o file nel Cestino di Windows.</summary>
    public static void Recycle(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        SHFILEOPSTRUCT op = new()
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        int rc = SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted)
        {
            throw new IOException("Spostamento nel Cestino non riuscito (codice " + rc.ToString(CultureInfo.InvariantCulture) + "): " + path);
        }

        Log.Info("Nel Cestino: " + path);
    }

    private static void TryRecycle(string path)
    {
        try
        {
            Recycle(path);
        }
        catch (IOException ex)
        {
            Log.Warn(ex.Message);
        }
    }

    /// <summary>Apre il progetto con TIA Portal (associazione .apNN di Windows).</summary>
    public static void OpenInTia(string projectFile)
    {
        Process.Start(new ProcessStartInfo(projectFile) { UseShellExecute = true });
        Log.Info("Aperto in TIA: " + projectFile);
    }

    // ---------- backup .rar ----------

    public static string? RarPath()
    {
        string? fromSettings = AppServices.Settings.RarPath;
        if (!string.IsNullOrWhiteSpace(fromSettings) && File.Exists(fromSettings))
        {
            return fromSettings;
        }

        foreach (string? root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
        {
            string candidate = Path.Combine(root ?? "", "WinRAR", "Rar.exe");
            if (root != null && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Comprime la cartella progetto in un .rar nella cartella di rete. Si
    /// comprime prima in locale e poi si copia: sulla rete non resta mai un
    /// archivio a meta'.
    /// </summary>
    public static async Task<string> BackupRar(string projectFolder, string projectName, string destDir, Action<string> progress, CancellationToken ct)
    {
        string rar = RarPath() ?? throw new FileNotFoundException("Rar.exe non trovato: installare WinRAR o impostare RarPath in settings.json.");
        if (!Directory.Exists(destDir))
        {
            throw new DirectoryNotFoundException("Cartella di backup non raggiungibile: " + destDir);
        }

        string work = ProjectCopier.NewWorkDir();
        try
        {
            string local = Path.Combine(work, projectName + ".rar");
            string args = "a -r -ep1 -m3 -y -idc -x*\\TMP\\* -x*\\Logs\\* -x*.info -x*~PEData.* -x*write.lock " +
                          "\"" + local + "\" \"" + projectFolder.TrimEnd('\\') + "\"";
            progress("Compressione di " + projectName + "...");
            Log.Info("Rar: " + rar + " " + args);

            ProcessStartInfo psi = new(rar, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync(ct);
            Task<string> stdout = p.StandardOutput.ReadToEndAsync(ct);
            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                p.Kill(entireProcessTree: true);
                throw;
            }

            // 0 = ok, 1 = avvisi (file bloccati saltati): l'archivio c'e' comunque.
            if (p.ExitCode > 1 || !File.Exists(local))
            {
                throw new IOException("Rar.exe uscito con codice " + p.ExitCode + ": " + (await err).Trim());
            }

            string target = Path.Combine(destDir, projectName + ".rar");
            if (File.Exists(target))
            {
                target = Path.Combine(destDir, projectName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture) + ".rar");
            }

            progress("Copia su " + destDir + "...");
            string part = target + ".part";
            await Task.Run(() =>
            {
                File.Copy(local, part, overwrite: true);
                File.Move(part, target);
            }, ct);

            long size = new FileInfo(target).Length;
            Log.Info($"Backup rar: {target} ({size / 1048576.0:0.0} MB)" + (p.ExitCode == 1 ? " con avvisi: " + (await stdout).Trim() : ""));
            return target;
        }
        finally
        {
            ProjectCopier.Cleanup(work);
        }
    }

    // ---------- Cestino (SHFileOperation con FOF_ALLOWUNDO) ----------

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
