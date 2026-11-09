using TiaTracker.Contracts;

namespace TiaTracker.Core.Tests;

/// <summary>
/// Messaggi simulati: il PLC di impianto non si mette online per provare
/// l'errore, si verifica che i testi di TIA vengano riconosciuti.
/// </summary>
public class ClassifierTests
{
    [Theory]
    [InlineData("EngineeringTargetInvocationException", "Export is not supported in online mode.", ExitCodes.PlcOnline)]
    [InlineData("EngineeringTargetInvocationException", "GenerateSource is not possible while the device is online", ExitCodes.PlcOnline)]
    [InlineData("EngineeringNotSupportedException", "Funzione non supportata in modalità online.", ExitCodes.PlcOnline)]
    [InlineData("EngineeringSecurityException", "Security error.\r\n\r\nThe operation has timed out.", ExitCodes.Timeout)]
    [InlineData("EngineeringSecurityException", "Access denied: the user is not member of the group Siemens TIA Openness.", ExitCodes.AccessDenied)]
    [InlineData("EngineeringNonRecoverableException", "The TIA Portal process has been terminated.", ExitCodes.TiaCrashed)]
    [InlineData("COMException", "Il server RPC non è disponibile. (Eccezione da HRESULT: 0x800706BA)", ExitCodes.TiaCrashed)]
    [InlineData("EngineeringTargetInvocationException", "The project is already opened by another user.", ExitCodes.OpenFailed)]
    [InlineData("EngineeringTargetInvocationException", "The project must be upgraded to be opened.", ExitCodes.OpenFailed)]
    [InlineData("InvalidOperationException", "Qualcosa d'altro.", ExitCodes.Unexpected)]
    public void Classifica(string type, string message, int expected)
    {
        Assert.Equal(expected, WorkerErrorClassifier.Classify(type, message).ExitCode);
    }

    [Theory]
    [InlineData("F-system blocks cannot be exported.", "f-system")]
    [InlineData("The block is know-how protected.", "protected")]
    [InlineData("Inconsistent block.", null)]
    public void ErroreSuBlocco(string message, string? expected)
    {
        Assert.Equal(expected, WorkerErrorClassifier.ClassifyItemFailure(message));
    }

    [Fact]
    public void RequestPiatto()
    {
        Dictionary<string, string> d = MiniJson.ParseFlatObject(
            MiniJson.Serialize(new Dictionary<string, object> { ["project"] = @"C:\Lavori In Corso\à è\P.ap21", ["pid"] = "6232", ["no-scl"] = "true" }));
        Assert.Equal(@"C:\Lavori In Corso\à è\P.ap21", d["project"]);
        Assert.Equal("6232", d["pid"]);
        Assert.Equal("true", d["no-scl"]);
    }
}
