using System.Net;
using System.Net.Sockets;
using System.Text;
using TiaTracker.Core.Domain;

namespace TiaTracker.Core.Output;

/// <summary>
/// Tabella IP da e verso altri programmi: TSV (copia/incolla con Google Fogli
/// ed Excel), HTML (incollato in Google Documenti diventa una tabella), CSV e
/// Markdown. In lettura riconosce le intestazioni in italiano e inglese.
/// </summary>
public static class IpTableFormat
{
    public sealed record Column(string Header, Func<IpDevice, string?> Get, Action<IpDevice, string?> Set, string[] Aliases);

    public static readonly IReadOnlyList<Column> Columns = new Column[]
    {
        new("Rete", d => d.Network, (d, v) => d.Network = v, new[] { "rete", "network", "lan", "sottorete" }),
        new("Indirizzo IP", d => d.Ip, (d, v) => d.Ip = v, new[] { "indirizzo ip", "ip", "indirizzo", "ip address", "address", "ipv4" }),
        new("Subnet mask", d => d.Subnet, (d, v) => d.Subnet = v, new[] { "subnet mask", "subnet", "maschera", "mask", "netmask" }),
        new("Gateway", d => d.Gateway, (d, v) => d.Gateway = v, new[] { "gateway", "gw", "router" }),
        new("Dispositivo", d => d.Name, (d, v) => d.Name = v, new[] { "dispositivo", "nome", "nome dispositivo", "device", "name", "device name" }),
        new("Tipo", d => d.Kind, (d, v) => d.Kind = v, new[] { "tipo", "type", "modello", "model", "articolo" }),
        new("Nome PROFINET", d => d.ProfinetName, (d, v) => d.ProfinetName = v, new[] { "nome profinet", "profinet", "profinet name", "pn name", "nome pn" }),
        new("Posizione", d => d.Location, (d, v) => d.Location = v, new[] { "posizione", "location", "quadro", "armadio", "zona" }),
        new("MAC", d => d.Mac, (d, v) => d.Mac = v, new[] { "mac", "indirizzo mac", "mac address" }),
        new("Note", d => d.Notes, (d, v) => d.Notes = v, new[] { "note", "notes", "descrizione", "commento", "commenti" }),
    };

    public static string ToTsv(IEnumerable<IpDevice> devices, bool header = true)
    {
        StringBuilder sb = new();
        if (header)
        {
            sb.AppendLine(string.Join("\t", Columns.Select(c => c.Header)));
        }

        foreach (IpDevice d in devices)
        {
            sb.AppendLine(string.Join("\t", Columns.Select(c => Clean(c.Get(d)).Replace('\t', ' '))));
        }

        return sb.ToString();
    }

    /// <summary>CSV con il punto e virgola (Excel italiano, Google Fogli lo riconosce da solo).</summary>
    public static string ToCsv(IEnumerable<IpDevice> devices)
    {
        StringBuilder sb = new();
        sb.AppendLine(string.Join(";", Columns.Select(c => Quote(c.Header))));
        foreach (IpDevice d in devices)
        {
            sb.AppendLine(string.Join(";", Columns.Select(c => Quote(c.Get(d)))));
        }

        return sb.ToString();
    }

    public static string ToHtmlTable(IEnumerable<IpDevice> devices, string? title = null)
    {
        StringBuilder sb = new();
        if (title != null)
        {
            sb.Append("<h3>").Append(WebUtility.HtmlEncode(title)).Append("</h3>");
        }

        sb.Append("<table border=\"1\" style=\"border-collapse:collapse\"><thead><tr>");
        foreach (Column c in Columns)
        {
            sb.Append("<th style=\"background:#1f3864;color:#ffffff;padding:4px\">").Append(WebUtility.HtmlEncode(c.Header)).Append("</th>");
        }

        sb.Append("</tr></thead><tbody>");
        foreach (IpDevice d in devices)
        {
            sb.Append("<tr>");
            foreach (Column c in Columns)
            {
                sb.Append("<td style=\"padding:4px\">").Append(WebUtility.HtmlEncode(Clean(c.Get(d)))).Append("</td>");
            }

            sb.Append("</tr>");
        }

        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    public static string ToMarkdown(IEnumerable<IpDevice> devices)
    {
        StringBuilder sb = new();
        sb.AppendLine("| " + string.Join(" | ", Columns.Select(c => c.Header)) + " |");
        sb.AppendLine("|" + string.Concat(Columns.Select(_ => "---|")));
        foreach (IpDevice d in devices)
        {
            sb.AppendLine("| " + string.Join(" | ", Columns.Select(c => Clean(c.Get(d)).Replace("|", "\\|", StringComparison.Ordinal))) + " |");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Testo incollato (TSV da Google Fogli/Excel) o CSV. Se la prima riga ha
    /// intestazioni note le colonne si mappano per nome, altrimenti per posizione.
    /// </summary>
    public static List<IpDevice> Parse(string text)
    {
        List<string[]> rows = SplitRows(text);
        if (rows.Count == 0)
        {
            return new List<IpDevice>();
        }

        // Intestazione = almeno due nomi di colonna noti e nessun indirizzo IP nella riga
        // ("PROFINET" da solo e' un valore della colonna Rete, non un'intestazione).
        int[] map = HeaderMap(rows[0]);
        bool hasHeader = map.Count(i => i >= 0) >= 2 && !rows[0].Any(IsValidIp);
        if (!hasHeader)
        {
            map = Enumerable.Range(0, Columns.Count).ToArray();
        }

        List<IpDevice> result = new();
        foreach (string[] row in rows.Skip(hasHeader ? 1 : 0))
        {
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            IpDevice d = new();
            for (int c = 0; c < Columns.Count; c++)
            {
                int src = map[c];
                if (src >= 0 && src < row.Length)
                {
                    string v = row[src].Trim();
                    Columns[c].Set(d, v.Length == 0 ? null : v);
                }
            }

            result.Add(d);
        }

        return result;
    }

    private static int[] HeaderMap(string[] header)
    {
        int[] map = Enumerable.Repeat(-1, Columns.Count).ToArray();
        for (int i = 0; i < header.Length; i++)
        {
            string h = header[i].Trim().ToLowerInvariant();
            for (int c = 0; c < Columns.Count; c++)
            {
                if (map[c] < 0 && (h == Columns[c].Header.ToLowerInvariant() || Columns[c].Aliases.Contains(h)))
                {
                    map[c] = i;
                    break;
                }
            }
        }

        return map;
    }

    private static List<string[]> SplitRows(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string first = lines.FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        char sep = first.Contains('\t') ? '\t' : first.Count(c => c == ';') >= first.Count(c => c == ',') ? ';' : ',';
        List<string[]> rows = new();
        foreach (string line in lines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            rows.Add(sep == '\t' ? line.Split('\t') : SplitQuoted(line, sep));
        }

        return rows;
    }

    private static string[] SplitQuoted(string line, char sep)
    {
        List<string> cells = new();
        StringBuilder cur = new();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cur.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    cur.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == sep)
            {
                cells.Add(cur.ToString());
                cur.Clear();
            }
            else
            {
                cur.Append(ch);
            }
        }

        cells.Add(cur.ToString());
        return cells.ToArray();
    }

    public static bool IsValidIp(string? ip) =>
        !string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip.Trim(), out IPAddress? a) && a.AddressFamily == AddressFamily.InterNetwork &&
        ip.Trim().Count(c => c == '.') == 3;

    /// <summary>Chiave per ordinare gli IP come numeri (192.168.0.10 dopo 192.168.0.9).</summary>
    public static string SortKey(string? ip)
    {
        if (!IsValidIp(ip))
        {
            return "~" + (ip ?? "");
        }

        return string.Concat(ip!.Trim().Split('.').Select(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture).ToString("D3", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string Clean(string? s) => (s ?? "").Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Quote(string? s) => Csv.Quote(s);
}
