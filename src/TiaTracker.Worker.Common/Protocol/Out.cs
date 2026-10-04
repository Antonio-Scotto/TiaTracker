using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using TiaTracker.Contracts;

namespace TiaTracker.Worker.Protocol
{
    /// <summary>
    /// stdout e' riservato al protocollo: una riga JSON per messaggio.
    /// Tutto il resto (diagnostica) va su stderr. Esattamente un result o un
    /// error finale; progress limitato a 4 righe al secondo.
    /// </summary>
    internal static class Out
    {
        private static readonly object Gate = new object();
        private static readonly Stopwatch ProgressClock = Stopwatch.StartNew();
        private static TextWriter _stdout;
        private static long _lastProgressMs = -1000;
        private static bool _finished;

        internal static void Init()
        {
            Stream stream = Console.OpenStandardOutput();
            _stdout = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            Console.SetOut(Console.Error);
        }

        internal static bool Finished
        {
            get { lock (Gate) { return _finished; } }
        }

        internal static void Status(string code, string text)
        {
            Write(new WorkerLine { Type = WorkerLine.TypeStatus, Code = code, Text = text });
        }

        internal static void Progress(string phase, int done, int total, string item, bool force = false)
        {
            lock (Gate)
            {
                long now = ProgressClock.ElapsedMilliseconds;
                if (!force && done < total && now - _lastProgressMs < 250)
                {
                    return;
                }

                _lastProgressMs = now;
            }

            Write(new WorkerLine { Type = WorkerLine.TypeProgress, Phase = phase, Done = done, Total = total, Item = item });
        }

        internal static void Item(string family, string name, string state, string text)
        {
            Write(new WorkerLine { Type = WorkerLine.TypeItem, Family = family, Item = name, State = state, Text = text });
        }

        internal static void Warn(string code, string text)
        {
            Write(new WorkerLine { Type = WorkerLine.TypeWarn, Code = code, Text = text });
        }

        internal static int Result(string command, object data)
        {
            Final(new WorkerLine { Type = WorkerLine.TypeResult, Command = command, Data = data, ExitCode = ExitCodes.Ok });
            return ExitCodes.Ok;
        }

        internal static int Error(int exitCode, string code, string text)
        {
            Final(new WorkerLine { Type = WorkerLine.TypeError, ExitCode = exitCode, Code = code, Text = text });
            return exitCode;
        }

        private static void Final(WorkerLine line)
        {
            lock (Gate)
            {
                if (_finished)
                {
                    Console.Error.WriteLine("Riga finale gia' emessa, scarto: " + line.Type + " " + line.Text);
                    return;
                }

                _finished = true;
                WriteUnlocked(line);
            }
        }

        private static void Write(WorkerLine line)
        {
            lock (Gate)
            {
                if (_finished)
                {
                    return;
                }

                WriteUnlocked(line);
            }
        }

        private static void WriteUnlocked(WorkerLine line)
        {
            try
            {
                (_stdout ?? Console.Out).WriteLine(MiniJson.Serialize(line));
            }
            catch (IOException)
            {
                // L'app ha chiuso la pipe: non c'e' piu' nessuno da avvisare.
            }
        }
    }

    /// <summary>
    /// Righe di stato periodiche durante una chiamata bloccante lunga (apertura
    /// progetto): il watchdog dell'app misura l'inattivita', non la durata.
    /// </summary>
    internal sealed class Heartbeat : IDisposable
    {
        private readonly Timer _timer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private Heartbeat(string code, string text, int periodMs)
        {
            _timer = new Timer(_ => Out.Status(code, text + " (" + (int)_clock.Elapsed.TotalSeconds + " s)"), null, periodMs, periodMs);
        }

        internal static Heartbeat Start(string code, string text, int periodMs = 15000)
        {
            return new Heartbeat(code, text, periodMs);
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }

    /// <summary>Annullamento: l'app scrive "cancel" su stdin.</summary>
    internal static class Cancel
    {
        private static volatile bool _requested;

        internal static bool Requested
        {
            get { return _requested; }
        }

        internal static void Listen()
        {
            Thread t = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = Console.In.ReadLine()) != null)
                    {
                        if (line.Trim().Equals("cancel", StringComparison.OrdinalIgnoreCase))
                        {
                            _requested = true;
                            Console.Error.WriteLine("Annullamento richiesto");
                        }
                    }
                }
                catch (IOException)
                {
                }
            }) { IsBackground = true, Name = "stdin" };
            t.Start();
        }

        internal static void ThrowIfRequested()
        {
            if (_requested)
            {
                throw new OperationCanceledException("Annullato dall'utente");
            }
        }
    }
}
