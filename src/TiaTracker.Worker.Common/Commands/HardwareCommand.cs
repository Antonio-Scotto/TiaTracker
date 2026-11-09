using System.Collections.Generic;
using System.IO;
using TiaTracker.Contracts;
using TiaTracker.Worker.Protocol;

namespace TiaTracker.Worker.Commands
{
    /// <summary>
    /// hardware (--pid N | --project P) --out D [--no-tags] [--dump-attributes]:
    /// solo hardware, rete e tag, senza export dei blocchi. Serve ad aggiornare
    /// Lista IP, Lista Hardware e Lista IO in pochi secondi.
    /// </summary>
    internal static class HardwareCommand
    {
        internal static int Run(Args args)
        {
            string outDir = args.Get("out");
            if (string.IsNullOrEmpty(outDir))
            {
                throw new ArgumentException2("Manca --out <cartella>.");
            }

            using (TiaSession session = SessionFactory.Open(args))
            {
                // Senza PLC (solo HMI o periferia) l'hardware si legge lo stesso.
                List<PlcTarget> plcs = session.Plcs();
                HardwareExport hw;
                string path = HardwareCollector.Collect(session, outDir, plcs, !args.Flag("no-tags"), args.Flag("dump-attributes"), out hw);
                return Out.Result("hardware", Summary(path, hw));
            }
        }

        internal static HardwareResult Summary(string path, HardwareExport hw)
        {
            HardwareResult r = new HardwareResult
            {
                Hardware = path,
                Devices = hw.Devices.Count,
                TagTables = hw.TagTables.Count,
                Partial = hw.Partial,
                Warnings = hw.Warnings.Count,
            };
            foreach (HwDevice d in hw.Devices)
            {
                Count(d.Items, r);
            }

            foreach (TagTableFile t in hw.TagTables)
            {
                r.Tags += t.Tags.Count;
            }

            return r;
        }

        private static void Count(List<HwItem> items, HardwareResult r)
        {
            foreach (HwItem i in items)
            {
                r.Items++;
                if (i.Interface != null)
                {
                    r.Nodes += i.Interface.Nodes.Count;
                }

                Count(i.Items, r);
            }
        }

        /// <summary>Cartella dell'hardware dentro l'export di uno snapshot (accanto alle cartelle dei PLC).</summary>
        internal static string SnapshotDir(string outRoot)
        {
            return Path.Combine(outRoot, "_hardware");
        }
    }
}
