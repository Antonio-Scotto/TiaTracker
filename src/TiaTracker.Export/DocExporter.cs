using System.Globalization;
using TiaTracker.Core.Documents;

namespace TiaTracker.Export;

public enum DocFormat
{
    Docx,
    Pdf,
    Csv,
}

/// <summary>Un file scritto (o no): percorso, se e' stato salvato con un altro nome perche' l'originale era aperto, errore.</summary>
public sealed record DocFileResult(DocFormat Format, string Path, bool Renamed, string? Error)
{
    public bool Ok => Error == null;
}

/// <summary>
/// Scrive i documenti su disco: prima in un file temporaneo nella stessa
/// cartella, poi al posto del vecchio. Se il vecchio e' aperto (Word, Acrobat)
/// il nuovo si salva accanto con data e ora nel nome, senza perdere nulla.
/// </summary>
public static class DocExporter
{
    public static readonly IReadOnlyList<DocFormat> All = new[] { DocFormat.Docx, DocFormat.Pdf, DocFormat.Csv };

    public static string Extension(DocFormat f) => f switch
    {
        DocFormat.Docx => ".docx",
        DocFormat.Pdf => ".pdf",
        _ => ".csv",
    };

    public static string Name(DocFormat f) => f switch
    {
        DocFormat.Docx => "DOCX",
        DocFormat.Pdf => "PDF",
        _ => "CSV",
    };

    public static void Render(DocDocument doc, DocFormat format, Stream output)
    {
        switch (format)
        {
            case DocFormat.Docx:
                DocxRenderer.Write(doc, output);
                break;
            case DocFormat.Pdf:
                PdfRenderer.Write(doc, output);
                break;
            default:
                CsvRenderer.Write(doc, output);
                break;
        }
    }

    public static List<DocFileResult> Write(DocDocument doc, string folder, IEnumerable<DocFormat> formats, DateTime? nowLocal = null)
    {
        Directory.CreateDirectory(folder);
        List<DocFileResult> results = new();
        foreach (DocFormat f in formats.Distinct())
        {
            string target = Path.Combine(folder, doc.FileStem + Extension(f));
            string temp = Path.Combine(folder, doc.FileStem + ".tiatracker-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            try
            {
                using (FileStream fs = new(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    Render(doc, f, fs);
                }

                try
                {
                    File.Move(temp, target, overwrite: true);
                    results.Add(new DocFileResult(f, target, false, null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    string stamp = (nowLocal ?? DateTime.Now).ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                    string alt = Path.Combine(folder, doc.FileStem + "_" + stamp + Extension(f));
                    File.Move(temp, alt, overwrite: true);
                    results.Add(new DocFileResult(f, alt, true, null));
                }
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                results.Add(new DocFileResult(f, target, false, ex.Message));
            }
        }

        return results;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
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
