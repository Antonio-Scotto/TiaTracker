using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using TiaTracker.Contracts;
using TiaTracker.Worker.Commands;
using TiaTracker.Worker.Protocol;

namespace TiaTracker.Worker
{
    /// <summary>
    /// tiatracker-worker-vNN: processo separato net48 che parla con TIA via
    /// Openness. Sola lettura. stdout = protocollo JSON lines, stderr = log.
    ///
    ///   hello                                   verifica DLL, senza TIA
    ///   instances                               PID e progetto dei TIA aperti
    ///   probe    (--pid N | --project P)        progetto, IsModified, PLC, stato online
    ///   snapshot (--pid N | --project P) --out D [--plc NOME] [--no-scl] [--no-hw] [--no-tags]
    ///   hardware (--pid N | --project P) --out D [--no-tags] [--dump-attributes]
    ///   --request file.json                     stessi parametri in un file
    /// </summary>
    internal static class Program
    {
#if TIA_V18
        internal const int TiaMajor = 18;
#else
        internal const int TiaMajor = 21;
#endif

        internal static string Version
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
        }

        private static int Main(string[] argv)
        {
            Out.Init();

            // Prima di qualunque tipo Siemens.
            string missing = OpennessResolver.Register();
            if (missing != null)
            {
                return Out.Error(ExitCodes.OpennessNotFound, "openness-not-found", missing);
            }

            Cancel.Listen();
            return Run(argv);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] argv)
        {
            try
            {
                Args args = Args.Parse(argv);
                switch (args.Command)
                {
                    case "hello":
                        return HelloCommand.Run();
                    case "instances":
                        return InstancesCommand.Run();
                    case "probe":
                        return ProbeCommand.Run(args);
                    case "snapshot":
                        return SnapshotCommand.Run(args);
                    case "hardware":
                        return HardwareCommand.Run(args);
                    default:
                        return Out.Error(ExitCodes.BadArguments, "bad-arguments", "Comando sconosciuto: " + args.Command);
                }
            }
            catch (ArgumentException2 ex)
            {
                return Out.Error(ExitCodes.BadArguments, "bad-arguments", ex.Message);
            }
            catch (WorkerException ex)
            {
                return Out.Error(ex.ExitCode, ex.Code, ex.Message);
            }
            catch (OperationCanceledException ex)
            {
                return Out.Error(ExitCodes.Cancelled, "cancelled", ex.Message);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                Exception inner = ex;
                while (inner.InnerException != null && (inner is TargetInvocationException || inner is TypeInitializationException))
                {
                    inner = inner.InnerException;
                }

                WorkerErrorClassifier.Classification c = WorkerErrorClassifier.Classify(inner.GetType().Name, inner.Message);
                return Out.Error(c.ExitCode, c.Code, inner.Message);
            }
        }
    }
}
