using TiaTracker.Core.Domain;

namespace TiaTracker.App.ViewModels;

/// <summary>Un chip (stato o badge) con il suo tono: il colore non dipende piu' dal testo.</summary>
public sealed record ChipVm(string Text, Tone Tone, string? ToolTip = null)
{
    public override string ToString() => Text;
}
