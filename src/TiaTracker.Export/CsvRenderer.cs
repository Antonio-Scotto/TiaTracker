using System.Text;
using TiaTracker.Core.Documents;
using TiaTracker.Core.Output;

namespace TiaTracker.Export;

/// <summary>
/// La tabella principale del documento in CSV per Excel: una riga per riga di
/// dati, le righe di gruppo diventano la prima colonna "Gruppo" (filtrabile).
/// </summary>
public static class CsvRenderer
{
    public static string Render(DocDocument doc)
    {
        DocTable? t = doc.Primary?.Table;
        if (t == null)
        {
            return "";
        }

        bool groups = t.Rows.Any(r => r.Style == DocRowStyle.Group);
        StringBuilder sb = new();
        List<string?> header = new();
        if (groups)
        {
            header.Add("Gruppo");
        }

        header.AddRange(t.Columns.Select(c => c.Header));
        sb.Append(Csv.Line(header)).Append("\r\n");

        string? group = null;
        foreach (DocRow r in t.Rows)
        {
            if (r.Style == DocRowStyle.Group)
            {
                group = r.Key.Length > 0 ? r.Key : DocLayout.Cell(r, 0);
                continue;
            }

            List<string?> cells = new(t.Columns.Count + 1);
            if (groups)
            {
                cells.Add(group);
            }

            for (int i = 0; i < t.Columns.Count; i++)
            {
                cells.Add(DocLayout.Cell(r, i));
            }

            sb.Append(Csv.Line(cells)).Append("\r\n");
        }

        return sb.ToString();
    }

    public static void Write(DocDocument doc, Stream output)
    {
        using StreamWriter w = new(output, Csv.Encoding, 65536, leaveOpen: true);
        w.Write(Render(doc));
    }
}
