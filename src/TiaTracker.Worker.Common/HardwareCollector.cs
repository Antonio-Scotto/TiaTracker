using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
using TiaTracker.Contracts;
using TiaTracker.Worker.Commands;
using TiaTracker.Worker.Protocol;

namespace TiaTracker.Worker
{
    /// <summary>
    /// Hardware del progetto in sola lettura, per Lista IP, Lista Hardware e
    /// Lista IO: dispositivi (radice, cartelle e "non raggruppati", dove stanno
    /// le stazioni ET200), moduli con codice d'ordine e firmware, indirizzi I/O,
    /// canali, interfacce di rete con IP e nome PROFINET, sistemi IO; tabelle dei
    /// tag esportate in XML (una chiamata per tabella). Al meglio: un elemento
    /// illeggibile diventa un avviso, solo TIA caduto o l'annullamento fermano.
    /// </summary>
    internal sealed class HardwareCollector
    {
        private static readonly string[] DeviceAttrs = { "TypeName", "Comment", "Author" };
        private static readonly string[] ItemAttrs = { "OrderNumber", "FirmwareVersion", "TypeName", "Comment", "Author" };
        private static readonly string[] NodeAttrs =
        {
            "Address", "SubnetMask", "RouterAddress", "UseRouter", "PnDeviceName", "PnDeviceNameAutoGeneration",
            "PnDeviceNameSetDirectly", "IpProtocolSelection", "MacAddress", "UseIpProtocol",
        };

        private static readonly string[] ConnectorAttrs = { "PnDeviceNumber" };
        private static readonly string[] TagAttrs = { "Name", "DataTypeName", "LogicalAddress" };
        private const int MaxDepth = 12;
        private const int MaxWarnings = 300;

        private readonly HardwareExport _hw;
        private readonly AttributeReader _reader;
        private readonly string _culture;
        private readonly Dictionary<string, List<string>> _attributeDump;

        private HardwareCollector(HardwareExport hw, string culture, bool dump)
        {
            _hw = hw;
            _culture = culture;
            _reader = new AttributeReader(culture);
            _attributeDump = dump ? new Dictionary<string, List<string>>(StringComparer.Ordinal) : null;
        }

        /// <summary>Raccoglie e scrive hardware.json (e tags\) in <paramref name="outDir"/>; restituisce il percorso del json.</summary>
        internal static string Collect(TiaSession session, string outDir, List<PlcTarget> tagPlcs, bool withTags, bool dumpAttributes,
            out HardwareExport hw)
        {
            Directory.CreateDirectory(outDir);
            Stopwatch total = Stopwatch.StartNew();
            hw = new HardwareExport
            {
                WorkerVersion = Program.Version,
                TiaMajor = Program.TiaMajor,
                Source = session.Source,
                Pid = session.Pid,
                ProjectPath = session.ProjectPath(),
                ProjectName = session.Project.Name,
                IsModified = session.IsModified(),
                StartedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };

            string culture = null;
            try
            {
                culture = session.Project.LanguageSettings.ReferenceLanguage.Culture.Name;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Lingua di riferimento non leggibile: " + ex.Message);
            }

            hw.Culture = culture;
            HardwareCollector c = new HardwareCollector(hw, culture, dumpAttributes);

            Stopwatch phase = Stopwatch.StartNew();
            Out.Status(StatusCodes.Collecting, "Lettura dell'hardware di " + session.Project.Name);
            c.Walk(session.Project);
            hw.TimingsMs["hardware"] = phase.ElapsedMilliseconds;

            if (withTags)
            {
                phase.Restart();
                c.ExportTags(tagPlcs, outDir);
                hw.TimingsMs["tags"] = phase.ElapsedMilliseconds;
            }

            hw.FinishedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            hw.TimingsMs["total"] = total.ElapsedMilliseconds;
            if (c._attributeDump != null)
            {
                c.WriteDump(Path.Combine(outDir, "attributes.txt"));
            }

            string path = Path.Combine(outDir, "hardware.json");
            File.WriteAllText(path, MiniJson.Serialize(hw, indented: true), new UTF8Encoding(false));
            return path;
        }

        // ---------- dispositivi ----------

        private void Walk(Project project)
        {
            List<KeyValuePair<Device, string>> all = new List<KeyValuePair<Device, string>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            Guard("dispositivi", () =>
            {
                foreach (Device d in project.Devices)
                {
                    Add(all, seen, d, "");
                }
            });
            Guard("cartelle dei dispositivi", () =>
            {
                foreach (DeviceUserGroup g in project.DeviceGroups)
                {
                    Group(all, seen, g, g.Name);
                }
            });
            Guard("dispositivi non raggruppati", () =>
            {
                foreach (Device d in project.UngroupedDevicesGroup.Devices)
                {
                    Add(all, seen, d, "Dispositivi non raggruppati");
                }
            });

            for (int i = 0; i < all.Count; i++)
            {
                Cancel.ThrowIfRequested();
                Out.Progress("hardware", i + 1, all.Count, all[i].Key.Name);
                AddDevice(all[i].Key, all[i].Value);
            }

            Guard("subnet", () =>
            {
                foreach (Subnet sn in project.Subnets)
                {
                    HwSubnet s = new HwSubnet { Name = sn.Name };
                    s.NetType = Try(() => sn.NetType.ToString());
                    Guard("subnet " + sn.Name, () =>
                    {
                        foreach (IoSystem io in sn.IoSystems)
                        {
                            s.IoSystems.Add(io.Name);
                        }
                    });
                    _hw.Subnets.Add(s);
                }
            });
        }

        private void Group(List<KeyValuePair<Device, string>> all, HashSet<string> seen, DeviceUserGroup group, string path)
        {
            foreach (Device d in group.Devices)
            {
                Add(all, seen, d, path);
            }

            foreach (DeviceUserGroup child in group.Groups)
            {
                Group(all, seen, child, path + "/" + child.Name);
            }
        }

        private static void Add(List<KeyValuePair<Device, string>> all, HashSet<string> seen, Device d, string path)
        {
            // I nomi dei dispositivi sono unici nel progetto: si evita di contarne uno due volte.
            if (seen.Add(d.Name))
            {
                all.Add(new KeyValuePair<Device, string>(d, path));
            }
        }

        private void AddDevice(Device device, string groupPath)
        {
            HwDevice dev = new HwDevice { Name = device.Name, GroupPath = groupPath };
            Guard("dispositivo " + device.Name, () =>
            {
                dev.TypeIdentifier = Try(() => device.TypeIdentifier);
                dev.Attributes = _reader.Read(device, DeviceAttrs, "Device|" + dev.TypeIdentifier);
                Dump("Device " + dev.TypeIdentifier, device);
                foreach (DeviceItem item in device.DeviceItems)
                {
                    AddItem(dev, dev.Items, item, device.Name, 0);
                }
            });
            _hw.Devices.Add(dev);
        }

        private void AddItem(HwDevice dev, List<HwItem> into, DeviceItem item, string parentPath, int depth)
        {
            HwItem hw = new HwItem { Path = parentPath };
            Guard("elemento " + parentPath, () =>
            {
                hw.Name = item.Name;
                hw.Path = parentPath + "/" + hw.Name;
                hw.TypeIdentifier = Try(() => item.TypeIdentifier);
                hw.Position = TryInt(() => item.PositionNumber);
                hw.Classification = Try(() => item.Classification.ToString());
                hw.IsBuiltIn = TryBool(() => item.IsBuiltIn);
                hw.Attributes = _reader.Read(item, ItemAttrs, hw.TypeIdentifier);
                Dump("DeviceItem " + hw.TypeIdentifier, item);

                Guard("indirizzi di " + hw.Path, () =>
                {
                    foreach (Address a in item.Addresses)
                    {
                        hw.Addresses.Add(new HwAddress { IoType = a.IoType.ToString(), StartAddress = a.StartAddress, Length = a.Length });
                    }
                });

                if (hw.Addresses.Count > 0)
                {
                    ReadChannels(item, hw);
                }

                NetworkInterface ni = TryService<NetworkInterface>(item);
                if (ni != null)
                {
                    hw.Interface = ReadInterface(ni, hw.Path);
                }

                SoftwareContainer sc = TryService<SoftwareContainer>(item);
                PlcSoftware plc = sc == null ? null : Try(() => sc.Software) as PlcSoftware;
                if (plc != null)
                {
                    hw.PlcName = plc.Name;
                    dev.PlcNames.Add(plc.Name);
                }
            });
            into.Add(hw);

            if (depth >= MaxDepth)
            {
                return;
            }

            Guard("sottoelementi di " + hw.Path, () =>
            {
                foreach (DeviceItem child in item.DeviceItems)
                {
                    Cancel.ThrowIfRequested();
                    AddItem(dev, hw.Items, child, hw.Path, depth + 1);
                }
            });
        }

        private void ReadChannels(DeviceItem item, HwItem hw)
        {
            Guard("canali di " + hw.Path, () =>
            {
                Dictionary<string, HwChannelGroup> groups = new Dictionary<string, HwChannelGroup>(StringComparer.Ordinal);
                foreach (Channel ch in item.Channels)
                {
                    string type = Try(() => ch.Type.ToString());
                    string io = Try(() => ch.IoType.ToString());
                    string key = type + "|" + io;
                    HwChannelGroup g;
                    if (!groups.TryGetValue(key, out g))
                    {
                        g = new HwChannelGroup { Type = type, IoType = io };
                        groups[key] = g;
                        hw.Channels.Add(g);
                    }

                    g.Count++;
                }
            });
        }

        private HwInterface ReadInterface(NetworkInterface ni, string path)
        {
            HwInterface itf = new HwInterface();
            itf.InterfaceType = Try(() => ni.InterfaceType.ToString());
            itf.OperatingMode = Try(() => ni.InterfaceOperatingMode.ToString());
            Guard("nodi di " + path, () =>
            {
                foreach (Node n in ni.Nodes)
                {
                    HwNode node = new HwNode { Name = Try(() => n.Name) };
                    node.NodeType = Try(() => n.NodeType.ToString());
                    Subnet sn = Try(() => n.ConnectedSubnet);
                    if (sn != null)
                    {
                        node.Subnet = Try(() => sn.Name);
                        node.NetType = Try(() => sn.NetType.ToString());
                    }

                    node.Attributes = _reader.Read(n, NodeAttrs, "Node|" + node.NodeType);
                    Dump("Node " + node.NodeType, n);
                    itf.Nodes.Add(node);
                }
            });
            Guard("IO connector di " + path, () =>
            {
                foreach (IoConnector c in ni.IoConnectors)
                {
                    HwIoConnector conn = new HwIoConnector();
                    IoSystem io = Try(() => c.ConnectedToIoSystem);
                    if (io != null)
                    {
                        conn.IoSystem = Try(() => io.Name);
                        conn.IoSystemNumber = TryInt(() => io.Number);
                    }

                    conn.Attributes = _reader.Read(c, ConnectorAttrs, "IoConnector");
                    itf.Connectors.Add(conn);
                }
            });
            Guard("IO controller di " + path, () =>
            {
                foreach (IoController c in ni.IoControllers)
                {
                    IoSystem io = Try(() => c.IoSystem);
                    if (io != null)
                    {
                        itf.ControlledIoSystems.Add(Try(() => io.Name));
                    }
                }
            });
            return itf;
        }

        // ---------- tag ----------

        private void ExportTags(List<PlcTarget> plcs, string outDir)
        {
            List<KeyValuePair<PlcTarget, KeyValuePair<PlcTagTable, string>>> tables = new List<KeyValuePair<PlcTarget, KeyValuePair<PlcTagTable, string>>>();
            foreach (PlcTarget plc in plcs)
            {
                if (plc.IsOnline())
                {
                    Warn("Tag di " + plc.Name + " non letti: PLC online in TIA Portal.");
                    continue;
                }

                List<KeyValuePair<PlcTagTable, string>> found = new List<KeyValuePair<PlcTagTable, string>>();
                Guard("tabelle tag di " + plc.Name, () => TagGroup(plc.Software.TagTableGroup, "", found));
                foreach (KeyValuePair<PlcTagTable, string> t in found)
                {
                    tables.Add(new KeyValuePair<PlcTarget, KeyValuePair<PlcTagTable, string>>(plc, t));
                }
            }

            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tables.Count; i++)
            {
                Cancel.ThrowIfRequested();
                PlcTarget plc = tables[i].Key;
                PlcTagTable table = tables[i].Value.Key;
                string group = tables[i].Value.Value;
                string name = Try(() => table.Name) ?? "?";
                Out.Progress("tags", i + 1, tables.Count, plc.Name + ": " + name);

                TagTableFile entry = new TagTableFile { Plc = plc.Name, GroupPath = group, Name = name, State = ItemStates.Ok };
                string rel = Path.Combine("tags", SnapshotCommand.Safe(plc.Name),
                    Unique(used, SnapshotCommand.Safe(plc.Name) + "/" + SnapshotCommand.Safe((group.Length > 0 ? group.Replace('/', '_') + "_" : "") + name)) + ".xml");
                try
                {
                    FileInfo target = new FileInfo(Path.Combine(outDir, rel));
                    Directory.CreateDirectory(target.DirectoryName);
                    if (target.Exists)
                    {
                        target.Delete();
                    }

                    table.Export(target, ExportOptions.WithDefaults);
                    entry.File = rel.Replace('\\', '/');
                }
                catch (Exception ex)
                {
                    if (IsFatal(ex))
                    {
                        throw;
                    }

                    // Export rifiutato (tabella di sistema, protezione...): si leggono i tag uno a uno.
                    entry.Reason = "export: " + FirstLine(ex.Message);
                    ReadTags(table, entry);
                }

                _hw.TagTables.Add(entry);
            }
        }

        private static void TagGroup(PlcTagTableGroup group, string path, List<KeyValuePair<PlcTagTable, string>> found)
        {
            foreach (PlcTagTable t in group.TagTables)
            {
                found.Add(new KeyValuePair<PlcTagTable, string>(t, path));
            }

            foreach (PlcTagTableUserGroup g in group.Groups)
            {
                TagGroup(g, path.Length == 0 ? g.Name : path + "/" + g.Name, found);
            }
        }

        private void ReadTags(PlcTagTable table, TagTableFile entry)
        {
            try
            {
                foreach (PlcTag tag in table.Tags)
                {
                    Dictionary<string, string> a = _reader.Read(tag, TagAttrs, "PlcTag");
                    HwTag t = new HwTag();
                    string v;
                    t.Name = a.TryGetValue("Name", out v) ? v : Try(() => tag.Name);
                    t.DataType = a.TryGetValue("DataTypeName", out v) ? v : null;
                    t.Address = a.TryGetValue("LogicalAddress", out v) ? v : null;
                    t.Comment = Try(() => AttributeReader.Text(tag.Comment, _culture));
                    entry.Tags.Add(t);
                }
            }
            catch (Exception ex)
            {
                if (IsFatal(ex))
                {
                    throw;
                }

                entry.State = ItemStates.Failed;
                entry.Reason = (entry.Reason != null ? entry.Reason + "; " : "") + "lettura: " + FirstLine(ex.Message);
                Warn("Tag di " + entry.Plc + "/" + entry.Name + " non letti: " + FirstLine(ex.Message));
            }
        }

        // ---------- utilita' ----------

        /// <summary>Esegue un passo; un errore diventa un avviso, salvo TIA caduto e annullamento.</summary>
        private void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (IsFatal(ex))
                {
                    throw;
                }

                Warn(what + ": " + FirstLine(ex.Message));
            }
        }

        private void Warn(string text)
        {
            if (_hw.Warnings.Count < MaxWarnings)
            {
                _hw.Warnings.Add(text);
                if (_hw.Warnings.Count <= 20)
                {
                    Out.Warn("hardware", text);
                }
            }

            _hw.Partial = true;
        }

        private static bool IsFatal(Exception ex)
        {
            WorkerErrorClassifier.Classification c = WorkerErrorClassifier.Classify(ex.GetType().Name, ex.Message);
            return c.ExitCode == ExitCodes.TiaCrashed;
        }

        private static T Try<T>(Func<T> get) where T : class
        {
            try
            {
                return get();
            }
            catch (Exception ex)
            {
                if (IsFatal(ex))
                {
                    throw;
                }

                return null;
            }
        }

        private static int? TryInt(Func<int> get)
        {
            try
            {
                return get();
            }
            catch (Exception ex)
            {
                if (IsFatal(ex))
                {
                    throw;
                }

                return null;
            }
        }

        private static bool TryBool(Func<bool> get)
        {
            try
            {
                return get();
            }
            catch (Exception ex)
            {
                if (IsFatal(ex))
                {
                    throw;
                }

                return false;
            }
        }

        private static T TryService<T>(DeviceItem item) where T : class, IEngineeringService
        {
            try
            {
                return item.GetService<T>();
            }
            catch (Exception ex)
            {
                if (IsFatal(ex))
                {
                    throw;
                }

                return null;
            }
        }

        private void Dump(string key, IEngineeringObject obj)
        {
            if (_attributeDump == null || key == null || _attributeDump.ContainsKey(key))
            {
                return;
            }

            try
            {
                _attributeDump[key] = AttributeReader.Names(obj);
            }
            catch (Exception ex)
            {
                _attributeDump[key] = new List<string> { "(non leggibili: " + FirstLine(ex.Message) + ")" };
            }
        }

        private void WriteDump(string path)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, List<string>> kv in _attributeDump)
            {
                sb.AppendLine(kv.Key);
                foreach (string name in kv.Value)
                {
                    sb.AppendLine("  " + name);
                }
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
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

            int slash = candidate.IndexOf('/');
            return slash >= 0 ? candidate.Substring(slash + 1) : candidate;
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
