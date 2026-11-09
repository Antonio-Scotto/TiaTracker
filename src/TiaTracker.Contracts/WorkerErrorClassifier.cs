using System;

namespace TiaTracker.Contracts
{
    /// <summary>
    /// Traduce il testo di un'eccezione Openness in codice di uscita. TIA puo'
    /// rispondere in inglese, italiano o tedesco a seconda della lingua
    /// dell'interfaccia: si cercano le parole chiave nelle tre lingue.
    /// Sta qui (e non nel worker) per essere testabile senza TIA.
    /// </summary>
    public static class WorkerErrorClassifier
    {
        public sealed class Classification
        {
            public Classification(int exitCode, string code)
            {
                ExitCode = exitCode;
                Code = code;
            }

            public int ExitCode { get; }
            public string Code { get; }
        }

        public static Classification Classify(string exceptionType, string message)
        {
            string m = (message ?? string.Empty).ToLowerInvariant();
            string t = exceptionType ?? string.Empty;

            if (IsOnline(m))
            {
                return new Classification(ExitCodes.PlcOnline, "plc-online");
            }

            // La finestra "Openness access" lasciata senza risposta: TIA chiude da solo
            // la richiesta con un errore di sicurezza "timed out".
            if (t.IndexOf("Security", StringComparison.Ordinal) >= 0 &&
                (m.Contains("timed out") || m.Contains("scadut") || m.Contains("zeitüberschreitung")))
            {
                return new Classification(ExitCodes.Timeout, "access-prompt-timeout");
            }

            if (t.IndexOf("Security", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("UnauthorizedAccess", StringComparison.Ordinal) >= 0 ||
                m.Contains("siemens tia openness") ||
                m.Contains("access denied") || m.Contains("accesso negato") || m.Contains("zugriff verweigert"))
            {
                return new Classification(ExitCodes.AccessDenied, "access-denied");
            }

            if (t.IndexOf("NonRecoverable", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("COMException", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("RemotingException", StringComparison.Ordinal) >= 0 ||
                m.Contains("0x800706ba") || m.Contains("0x800706be") || m.Contains("rpc server") ||
                m.Contains("server rpc") || m.Contains("has been terminated") || m.Contains("process has exited") ||
                m.Contains("connection to tia portal") || m.Contains("lost connection") ||
                m.Contains("connessione con tia portal"))
            {
                return new Classification(ExitCodes.TiaCrashed, "tia-crashed");
            }

            if (m.Contains("already open") || m.Contains("gia' aperto") || m.Contains("già aperto") ||
                m.Contains("bereits geöffnet") || m.Contains("locked") || m.Contains("bloccat") ||
                m.Contains("license") || m.Contains("licenza") || m.Contains("lizenz") ||
                m.Contains("upgrade") || m.Contains("newer version") || m.Contains("versione piu") ||
                m.Contains("cannot be opened") || m.Contains("could not be opened") ||
                m.Contains("impossibile aprire"))
            {
                return new Classification(ExitCodes.OpenFailed, "open-failed");
            }

            return new Classification(ExitCodes.Unexpected, "unexpected");
        }

        /// <summary>
        /// "Not supported in online mode" e varianti: GenerateSource ed Export
        /// rifiutano di lavorare con il PLC collegato.
        /// </summary>
        public static bool IsOnline(string lowerMessage)
        {
            if (string.IsNullOrEmpty(lowerMessage) || !lowerMessage.Contains("online"))
            {
                return false;
            }

            return lowerMessage.Contains("not supported") || lowerMessage.Contains("not possible") ||
                   lowerMessage.Contains("online mode") || lowerMessage.Contains("modalità online") ||
                   lowerMessage.Contains("modalita' online") || lowerMessage.Contains("non support") ||
                   lowerMessage.Contains("nicht unterstützt") || lowerMessage.Contains("nicht möglich") ||
                   lowerMessage.Contains("online-modus") || lowerMessage.Contains("onlinemodus");
        }

        /// <summary>
        /// Eccezione di export su un singolo blocco: blocchi F-system e
        /// know-how protetti non sono errori, vanno saltati.
        /// </summary>
        public static string ClassifyItemFailure(string message)
        {
            string m = (message ?? string.Empty).ToLowerInvariant();
            if (m.Contains("f-system") || m.Contains("f system") || m.Contains("system block") ||
                m.Contains("blocco di sistema f") || m.Contains("f-systembaustein") ||
                m.Contains("safety system"))
            {
                return "f-system";
            }

            if (m.Contains("know-how") || m.Contains("know how") || m.Contains("knowhow"))
            {
                return "protected";
            }

            return null;
        }
    }
}
