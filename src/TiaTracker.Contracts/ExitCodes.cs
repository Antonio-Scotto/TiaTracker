namespace TiaTracker.Contracts
{
    /// <summary>
    /// Codici di uscita del worker. L'app decide cosa mostrare guardando solo
    /// questi e il campo code dell'ultima riga "error".
    /// </summary>
    public static class ExitCodes
    {
        public const int Ok = 0;
        public const int Unexpected = 1;
        public const int BadArguments = 2;
        public const int OpennessNotFound = 3;
        public const int AccessDenied = 4;
        public const int InstanceNotFound = 5;
        public const int OpenFailed = 6;
        public const int PlcNotFound = 7;

        /// <summary>Solo dall'app: l'exe del worker non c'e' (nessun processo avviato).</summary>
        public const int WorkerNotFound = 8;

        /// <summary>Solo dall'app: un altro job worker e' gia' in corso.</summary>
        public const int Busy = 9;

        public const int PlcOnline = 10;
        public const int TiaCrashed = 11;
        public const int Partial = 12;
        public const int Cancelled = 20;
        public const int Timeout = 21;

        public static string Describe(int code)
        {
            switch (code)
            {
                case Ok: return "completato";
                case Unexpected: return "errore imprevisto";
                case BadArguments: return "argomenti non validi";
                case OpennessNotFound: return "DLL Openness non trovate";
                case AccessDenied: return "accesso Openness negato";
                case InstanceNotFound: return "istanza o progetto non trovato";
                case OpenFailed: return "apertura del progetto fallita";
                case PlcNotFound: return "PLC non trovato";
                case WorkerNotFound: return "worker TiaTracker non trovato";
                case Busy: return "un altro lavoro e' gia' in corso";
                case PlcOnline: return "PLC online: export non possibile";
                case TiaCrashed: return "TIA Portal non risponde";
                case Partial: return "completato in parte";
                case Cancelled: return "annullato";
                case Timeout: return "tempo scaduto";
                default: return "codice " + code;
            }
        }
    }
}
