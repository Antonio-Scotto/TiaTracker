using System.Security.Principal;
using TiaTracker.Core.Workers;

namespace TiaTracker.App.Services;

/// <summary>
/// Controlli rapidi all'avvio (solo file system, nessun worker avviato): se
/// manca un worker lo si dice subito con una barra, non al primo snapshot.
/// </summary>
public static class WorkerHealth
{
    public const string BannerKey = "worker-health";
    public const string OpennessGroup = "Siemens TIA Openness";

    /// <summary>V21 sempre; V18 solo se l'archivio ha versioni fino a V18.</summary>
    public static List<int> NeededMajors()
    {
        List<int> majors = new() { 21 };
        try
        {
            if (AppServices.Versions.TiaMajors().Any(m => m <= 18))
            {
                majors.Add(18);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Versioni TIA dell'archivio non lette: " + ex.Message);
        }

        return majors;
    }

    public static List<WorkerLookup> Missing() =>
        NeededMajors().Select(WorkerRunner.Locate).Where(l => !l.Found).ToList();

    /// <summary>Mostra o toglie la barra "worker non trovato". Restituisce true se e' tutto a posto.</summary>
    public static bool CheckAndNotify(Action openDiagnostics)
    {
        List<WorkerLookup> missing = Missing();
        if (missing.Count == 0)
        {
            Notify.DismissKey(BannerKey);
            return true;
        }

        string which = string.Join(" e ", missing.Select(m => "TIA V" + m.TiaMajor));
        Notify.Banner(BannerKey, NoticeKind.Warning,
            "Worker Openness per " + which + " non trovato",
            "Gli snapshot e la lettura di hardware/IP/IO da TIA non funzioneranno finche' manca. " +
            "Diagnostica mostra dove e' stato cercato e permette di indicarne la cartella.",
            new NoticeAction("Diagnostica", openDiagnostics));
        return false;
    }

    /// <summary>
    /// L'utente e' nel gruppo "Siemens TIA Openness"? Si guarda il token di
    /// accesso, cioe' i gruppi validi dall'ultimo accesso a Windows: dopo
    /// l'aggiunta al gruppo serve disconnettersi. Null se non determinabile.
    /// </summary>
    public static bool? InOpennessGroup()
    {
        try
        {
            using WindowsIdentity id = WindowsIdentity.GetCurrent();
            if (id.Groups == null)
            {
                return null;
            }

            foreach (IdentityReference sid in id.Groups)
            {
                try
                {
                    string name = sid.Translate(typeof(NTAccount)).Value;
                    if (name.EndsWith("\\" + OpennessGroup, StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(OpennessGroup, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (SystemException)
                {
                    // SID non traducibile (es. dominio non raggiungibile): si passa al successivo.
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
