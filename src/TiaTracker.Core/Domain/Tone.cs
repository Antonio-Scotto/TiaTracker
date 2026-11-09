namespace TiaTracker.Core.Domain;

/// <summary>
/// Colore semantico di uno stato o di un badge, indipendente dalla UI: l'app
/// lo traduce nei pennelli del tema (fondo, testo, pallino).
/// </summary>
public enum Tone
{
    Neutral,
    Accent,
    Success,
    Warning,
    Danger,
    Purple,
    Orange,
    Teal,
    Muted,
}
