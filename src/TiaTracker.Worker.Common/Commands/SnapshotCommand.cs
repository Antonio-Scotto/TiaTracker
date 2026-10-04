using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.ExternalSources;
using TiaTracker.Contracts;
using TiaTracker.Worker.Protocol;

namespace TiaTracker.Worker.Commands
{
    /// <summary>
    /// Export grezzo in sola lettura: XML per ogni blocco e UDT, SCL per quelli
    /// che lo permettono, attributi e manifest.json. Niente hash, niente
    /// normalizzazione: li fa il Core, ricalcolabili dagli XML salvati.
    /// </summary>
    internal static class SnapshotCommand
    {
        internal static int Run(Args args)
        {
            string outRoot = args.Get("out");
            if (string.IsNullOrEmpty(outRoot))
            {
                throw new ArgumentException2("Manca --out <cartella>.");
            }

            bool withScl = !args.Flag("no-scl");
            Directory.CreateDirectory(outRoot);

            Stopwatch total = Stopwatch.StartNew();
            Stopwatch phase = Stopwatch.StartNew();
            using (TiaSession session = SessionFactory.Open(args))
            {
                long openMs = phase.ElapsedMilliseconds;
                bool? modified = session.IsModified();
                List<PlcTarget> plcs = SessionFactory.SelectPlcs(session, args.Get("plc"));

                // Con il PLC collegato GenerateSource ed Export falliscono: errore
                // chiaro subito, mai retry, mai andare offline da soli.
                foreach (PlcTarget plc in plcs)
                {
                    if (plc.IsOnline())
                    {
                        throw new WorkerException(ExitCodes.PlcOnline, "plc-online",
                            "Il PLC " + plc.Name + " e' online in TIA Portal: andare offline e riprovare.");
                    }
                }

                SnapshotResult result = new SnapshotResult();
                foreach (PlcTarget plc in plcs)
                {
                    string dir = Path.Combine(outRoot, Safe(plc.Name));
                    SnapshotManifest manifest = new SnapshotManifest
                    {
                        WorkerVersion = Program.Version,
                        TiaMajor = Program.TiaMajor,
                        Source = session.Source,
                        Pid = session.Pid,
                        ProjectPath = session.ProjectPath(),
                        ProjectName = session.Project.Name,
                        IsModified = modified,
                        PlcName = plc.Name,
                        DeviceName = plc.DeviceName,
                        OnlineState = plc.OnlineState(),
                        StartedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        WithScl = withScl,
                    };
                    manifest.TimingsMs["open"] = openMs;

                    int code = ExportPlc(plc, dir, manifest, withScl);
                    manifest.IsModifiedAtEnd = session.IsModified();
                    manifest.FinishedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    manifest.TimingsMs["total"] = total.ElapsedMilliseconds;
                    string manifestPath = WriteManifest(dir, manifest);
                    result.Manifests.Add(manifestPath);

                    foreach (ManifestItem i in manifest.Items)
                    {
                        result.Items++;
                        switch (i.State)
                        {
                            case ItemStates.Ok: result.Ok++; break;
                            case ItemStates.Failed: result.Failed++; break;
                            case ItemStates.Protected: result.Protected++; break;
                            default: result.Skipped++; break;
                        }

                        if (i.ChangedDuringExport)
                        {
                            result.ChangedDuringExport++;
                        }
                    }

                    if (code != ExitCodes.Ok)
                    {
                        result.Partial = true;
                        return Out.Error(code, code == ExitCodes.Cancelled ? "cancelled" : "tia-crashed",
                            code == ExitCodes.Cancelled
                                ? "Snapshot annullato: manifest parziale in " + manifestPath
                                : "TIA Portal ha smesso di rispondere durante l'export: manifest parziale in " + manifestPath);
                    }

                    if (modified != manifest.IsModifiedAtEnd)
                    {
                        Out.Warn("modified-changed", "Lo stato 'modificato' del progetto e' cambiato durante l'export.");
                    }
                }

                // Hardware, rete e tag dopo i blocchi: un errore qui e' solo un avviso,
                // i blocchi esportati restano validi.
                if (!args.Flag("no-hw"))
                {
                    result.Hardware = CollectHardware(session, outRoot, args);
                }

                return Out.Result("snapshot", result);
            }
        }

        private static string CollectHardware(TiaSession session, string outRoot, Args args)
        {
            try
            {
                HardwareExport hw;
                // Tag di tutti i PLC del progetto: la Lista IO e' della commessa, non del solo PLC fotografato.
                return HardwareCollector.Collect(session, HardwareCommand.SnapshotDir(outRoot), session.Plcs(), !args.Flag("no-tags"), false, out hw);
            }
            catch (OperationCanceledException)
            {
                Out.Warn("hardware-skipped", "Lettura dell'hardware annullata: lo snapshot dei blocchi e' completo.");
                return null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                Out.Warn("hardware-failed", "Hardware, rete e tag non letti: " + FirstLine(ex.Message));
                return null;
            }
        }

        /// <summary>Restituisce Ok, Cancelled o TiaCrashed (manifest parziale).</summary>
        private static int ExportPlc(PlcTarget plc, string dir, SnapshotManifest manifest, bool withScl)
        {
            Stopwatch phase = Stopwatch.StartNew();
            Out.Status(StatusCodes.Collecting, "Raccolta blocchi di " + plc.Name);
            List<CollectedItem> items = BlockCollector.Collect(plc.Software);
            manifest.TimingsMs["collect"] = phase.ElapsedMilliseconds;

            // Attributi prima dell'export, in blocco.
            phase.Restart();
            AttributeReader reader = new AttributeReader();
            List<ManifestItem> entries = new List<ManifestItem>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                CollectedItem c = items[i];
                Dictionary<string, string> attrs = reader.Read(c.Object,
                    c.Family == Families.Block ? AttributeReader.BlockAttributes : AttributeReader.TypeAttributes);
                ManifestItem m = new ManifestItem
                {
                    Family = c.Family,
                    Kind = c.Kind,
                    Name = c.Name,
                    GroupPath = c.GroupPath,
                    Attributes = attrs,
                    State = ItemStates.Ok,
                };

                string v;
                if (attrs.TryGetValue("Number", out v))
                {
                    int n;
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    {
                        m.Number = n;
                    }
                }

                if (attrs.TryGetValue("ProgrammingLanguage", out v))
                {
                    m.Language = v;
                }

                if (attrs.TryGetValue("InstanceOfName", out v) && v.Length > 0)
                {
                    m.InstanceOf = v;
                }

                entries.Add(m);
                Out.Progress("attributes", i + 1, items.Count, c.Name);
                Cancel.ThrowIfRequested();
            }

            manifest.Items = entries;
            manifest.TimingsMs["attributes"] = phase.ElapsedMilliseconds;

            string xmlDir = Path.Combine(dir, "xml");
            string sclDir = Path.Combine(dir, "scl");
            Directory.CreateDirectory(Path.Combine(xmlDir, "blocks"));
            Directory.CreateDirectory(Path.Combine(xmlDir, "types"));
            if (withScl)
            {
                Directory.CreateDirectory(sclDir);
            }

            HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long xmlMs = 0, sclMs = 0;
            Stopwatch sw = new Stopwatch();
            for (int i = 0; i < items.Count; i++)
            {
                if (Cancel.Requested)
                {
                    MarkRest(entries, i, "annullato");
                    manifest.Partial = true;
                    manifest.TimingsMs["xml"] = xmlMs;
                    manifest.TimingsMs["scl"] = sclMs;
                    return ExitCodes.Cancelled;
                }

                CollectedItem c = items[i];
                ManifestItem m = entries[i];
                Out.Progress("export", i + 1, items.Count, c.Name);

                string isProtected;
                if (m.Attributes.TryGetValue("IsKnowHowProtected", out isProtected) && isProtected == "true")
                {
                    m.State = ItemStates.Protected;
                    m.Reason = "know-how protetto";
                    Out.Item(m.Family, m.Name, m.State, m.Reason);
                    continue;
                }

                string fileBase = Unique(usedNames, m.Family + "/" + Safe(c.Name));
                string xmlRel = Path.Combine("xml", m.Family == Families.Block ? "blocks" : "types", Path.GetFileName(fileBase) + ".xml");
                sw.Restart();
                try
                {
                    FileInfo target = new FileInfo(Path.Combine(dir, xmlRel));
                    if (target.Exists)
                    {
                        target.Delete();
                    }

                    if (c.Block != null)
                    {
                        c.Block.Export(target, ExportOptions.WithDefaults);
                    }
                    else
                    {
                        c.Type.Export(target, ExportOptions.WithDefaults);
                    }

                    m.XmlFile = xmlRel;
                }
                catch (Exception ex)
                {
                    int fatal = Fatal(ex);
                    if (fatal != 0)
                    {
                        m.State = ItemStates.Failed;
                        m.Reason = ex.Message;
                        MarkRest(entries, i + 1, "export interrotto");
                        manifest.Partial = true;
                        if (fatal == ExitCodes.PlcOnline)
                        {
                            throw new WorkerException(ExitCodes.PlcOnline, "plc-online", "Export rifiutato, PLC online: " + ex.Message);
                        }

                        manifest.Warnings.Add("TIA non risponde su " + c.Name + ": " + ex.Message);
                        return ExitCodes.TiaCrashed;
                    }

                    string kind = WorkerErrorClassifier.ClassifyItemFailure(ex.Message);
                    m.State = kind == "protected" ? ItemStates.Protected : kind != null ? ItemStates.Skipped : ItemStates.Failed;
                    m.Reason = (kind != null ? kind + ": " : "") + FirstLine(ex.Message);
                    Out.Item(m.Family, m.Name, m.State, m.Reason);
                    continue;
                }
                finally
                {
                    xmlMs += sw.ElapsedMilliseconds;
                }

                if (!withScl)
                {
                    continue;
                }

                string why;
                if (!BlockCollector.CanGenerateSource(c, m.Language, out why))
                {
                    m.SclReason = why;
                    continue;
                }

                string ext = c.Type != null ? ".udt" : m.Kind == "GlobalDB" ? ".db" : ".scl";
                string sclRel = Path.Combine("scl", Path.GetFileName(fileBase) + ext);
                sw.Restart();
                try
                {
                    FileInfo target = new FileInfo(Path.Combine(dir, sclRel));
                    if (target.Exists)
                    {
                        target.Delete();
                    }

                    IGenerateSource source = (IGenerateSource)c.Object;
                    plc.Software.ExternalSourceGroup.GenerateSource(new[] { source }, target, GenerateOptions.None);
                    m.SclFile = sclRel;
                }
                catch (Exception ex)
                {
                    int fatal = Fatal(ex);
                    if (fatal == ExitCodes.PlcOnline)
                    {
                        throw new WorkerException(ExitCodes.PlcOnline, "plc-online", "GenerateSource rifiutato, PLC online: " + ex.Message);
                    }

                    if (fatal != 0)
                    {
                        MarkRest(entries, i + 1, "export interrotto");
                        manifest.Partial = true;
                        manifest.Warnings.Add("TIA non risponde su " + c.Name + " (SCL): " + ex.Message);
                        return ExitCodes.TiaCrashed;
                    }

                    m.SclReason = "GenerateSource: " + FirstLine(ex.Message);
                }
                finally
                {
                    sclMs += sw.ElapsedMilliseconds;
                }
            }

            manifest.TimingsMs["xml"] = xmlMs;
            manifest.TimingsMs["scl"] = sclMs;

            // Seconda passata sulle date: un blocco toccato in TIA durante l'export
            // ha XML e attributi di momenti diversi.
            phase.Restart();
            AttributeReader dates = new AttributeReader();
            for (int i = 0; i < items.Count; i++)
            {
                Dictionary<string, string> after = dates.Read(items[i].Object, AttributeReader.DateAttributes);
                foreach (KeyValuePair<string, string> kv in after)
                {
                    string before;
                    if (entries[i].Attributes.TryGetValue(kv.Key, out before) && before != kv.Value)
                    {
                        entries[i].ChangedDuringExport = true;
                        Out.Warn("changed-during-export", entries[i].Name + ": " + kv.Key + " cambiato durante l'export");
                        break;
                    }
                }

                Out.Progress("verify", i + 1, items.Count, items[i].Name);
            }

            manifest.TimingsMs["verify"] = phase.ElapsedMilliseconds;
            return ExitCodes.Ok;
        }

        /// <summary>0 se l'errore riguarda solo il blocco; altrimenti il codice di uscita.</summary>
        private static int Fatal(Exception ex)
        {
            WorkerErrorClassifier.Classification c = WorkerErrorClassifier.Classify(ex.GetType().Name, ex.Message);
            return c.ExitCode == ExitCodes.PlcOnline || c.ExitCode == ExitCodes.TiaCrashed ? c.ExitCode : 0;
        }

        private static void MarkRest(List<ManifestItem> entries, int from, string reason)
        {
            for (int k = from; k < entries.Count; k++)
            {
                if (entries[k].State == ItemStates.Ok && entries[k].XmlFile == null)
                {
                    entries[k].State = ItemStates.Failed;
                    entries[k].Reason = reason;
                }
            }
        }

        private static string WriteManifest(string dir, SnapshotManifest manifest)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "manifest.json");
            File.WriteAllText(path, MiniJson.Serialize(manifest, indented: true), new UTF8Encoding(false));
            return path;
        }

        private static string Unique(HashSet<string> used, string name)
        {
            string candidate = name;
            int n = 2;
            while (!used.Add(candidate))
            {
                candidate = name + "~" + n.ToString(CultureInfo.InvariantCulture);
                n++;
            }

            return candidate;
        }

        internal static string Safe(string name)
        {
            StringBuilder sb = new StringBuilder(name.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char ch in name)
            {
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }

            return sb.ToString();
        }

        private static string FirstLine(string s)
        {
            if (s == null)
            {
                return string.Empty;
            }

            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            return nl < 0 ? s : s.Substring(0, nl);
        }
    }
}
