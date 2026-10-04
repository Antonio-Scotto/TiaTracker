using PdfSharp.Fonts;

namespace TiaTracker.Export;

/// <summary>
/// Font per i PDF (build Core di PDFsharp, che non legge da sola i font di
/// sistema): Calibri, Consolas e Arial da %WINDIR%\Fonts, grassetto e corsivo
/// simulati se manca il file, Arial o Segoe UI se manca la famiglia.
/// </summary>
internal sealed class WindowsFontResolver : IFontResolver
{
    private static readonly object Gate = new();
    private static bool _installed;

    /// <summary>Regular, grassetto, corsivo, grassetto corsivo.</summary>
    private static readonly Dictionary<string, string[]> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Calibri"] = new[] { "calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf" },
        ["Consolas"] = new[] { "consola.ttf", "consolab.ttf", "consolai.ttf", "consolaz.ttf" },
        ["Arial"] = new[] { "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf" },
        ["Segoe UI"] = new[] { "segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf" },
        ["Courier New"] = new[] { "cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf" },
    };

    private static readonly string[] Fallbacks = { "Calibri", "Arial", "Segoe UI" };

    private readonly string _fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Una volta per processo, prima del primo PDF (PDFsharp non accetta cambi dopo il primo uso).</summary>
    public static void Install()
    {
        lock (Gate)
        {
            if (_installed)
            {
                return;
            }

            GlobalFontSettings.FontResolver ??= new WindowsFontResolver();
            _installed = true;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        int wanted = (bold ? 1 : 0) + (italic ? 2 : 0);
        foreach (string family in Fallbacks.Prepend(familyName))
        {
            if (!Families.TryGetValue(family, out string[]? files))
            {
                continue;
            }

            if (File.Exists(Path.Combine(_fontsDir, files[wanted])))
            {
                return new FontResolverInfo(files[wanted]);
            }

            if (File.Exists(Path.Combine(_fontsDir, files[0])))
            {
                return new FontResolverInfo(files[0], bold, italic);
            }
        }

        return null;
    }

    public byte[]? GetFont(string faceName)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(faceName, out byte[]? data))
            {
                return data;
            }

            string path = Path.Combine(_fontsDir, faceName);
            if (!File.Exists(path))
            {
                return null;
            }

            data = File.ReadAllBytes(path);
            _cache[faceName] = data;
            return data;
        }
    }
}
