using System.Globalization;
using TiaTracker.Contracts;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Hardware;
using TiaTracker.Core.Output;
using TiaTracker.Core.Planning;

namespace TiaTracker.Core.Documents;

/// <summary>Una riga aggiunta a mano a un documento (dispositivo di terzi, segnale cablato a parte...).</summary>
public sealed class DocManualRow
{
    public long Id { get; set; }
    public long CommessaId { get; set; }
    public string ListKind { get; set; } = "";
    public int Sort { get; set; }
    public Dictionary<string, string> Cells { get; set; } = new(StringComparer.Ordinal);
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Tutto cio' che serve per costruire i documenti di una commessa.</summary>
public sealed class DocContext
{
    public Commessa Commessa { get; init; } = new();
    public ProjectVersion? Version { get; init; }
    public HwSnapshot? HardwareSource { get; init; }
    public DateTime GeneratedLocal { get; init; } = DateTime.Now;

    /// <summary>Note dell'utente per chiave di riga, per tipo di documento.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Notes { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    public IReadOnlyList<DocManualRow> ManualRows { get; init; } = Array.Empty<DocManualRow>();

    public string? Note(string kind, string key) =>
        Notes.TryGetValue(kind, out IReadOnlyDictionary<string, string>? map) && map.TryGetValue(key, out string? n) ? n : null;
}

/// <summary>
/// I documenti di base della commessa, costruiti dai dati gia' raccolti
/// (Lista IP della commessa, export hardware e tag da TIA, registro). Solo
/// struttura: DOCX, PDF e CSV li scrive TiaTracker.Export.
/// </summary>
public static class DocumentBuilders
{
    // ---------- Lista IP ----------

    /// <summary>Lista IP: di norma la tabella della commessa; <paramref name="source"/> descrive righe di altra origine (nodi di una versione).</summary>
    public static DocDocument IpList(DocContext ctx, IReadOnlyList<IpDevice> rows, string? source = null)
    {
        List<DocColumn> cols = new()
        {
            new("ip", "Indirizzo IP", 1.2, Mono: true),
            new("name", "Dispositivo", 1.9),
            new("kind", "Tipo", 1.4),
            new("mask", "Subnet mask", 1.05, Mono: true),
            new("gateway", "Gateway", 0.9, Mono: true),
            new("pn", "Nome PROFINET", 1.7),
            new("mac", "MAC", 1.45, Mono: true),
            new("location", "Posizione", 0.8),
            new("note", "Note", 1.4),
            new("source", "Fonte", 0.6),
        };
        DocTable table = new() { Columns = cols, EmptyText = "Lista IP vuota: \"Aggiorna da TIA\" o \"Importa da PRONETA\" nella scheda Rete / IP." };
        foreach (IGrouping<string, IpDevice> net in rows
                     .GroupBy(r => string.IsNullOrWhiteSpace(r.Network) ? "" : r.Network!.Trim())
                     .OrderBy(g => g.Key.Length == 0 ? "~" : g.Key, StringComparer.OrdinalIgnoreCase))
        {
            string netName = net.Key.Length == 0 ? "Senza rete assegnata" : net.Key;
            table.Rows.Add(DocRow.Group((net.Key.Length == 0 ? netName : "Rete " + netName) + " · " + Count(net.Count(), "dispositivo", "dispositivi"),
                netName));
            foreach (IpDevice d in net.OrderBy(d => IpTableFormat.SortKey(d.Ip), StringComparer.Ordinal))
            {
                table.Rows.Add(new DocRow
                {
                    Key = d.TiaKey ?? d.Ip ?? d.Name ?? "",
                    Style = d.TiaMissing ? DocRowStyle.Muted : DocRowStyle.Normal,
                    Cells = { d.Ip, d.Name, d.Kind, d.Subnet, d.Gateway, d.ProfinetName, d.Mac, d.Location, d.Notes, SourceText(d) },
                });
            }
        }

        int tia = rows.Count(r => r.IsFromTia), proneta = rows.Count(r => r.Source == IpSources.Proneta);
        List<DocMeta> meta = Meta(ctx, source ?? "Lista IP della commessa: " + tia + " righe da TIA, " + proneta + " da PRONETA, " +
                                       (rows.Count - tia - proneta) + " scritte a mano");
        return Document(ctx, DocKinds.Ip, meta, new DocSection { Title = "Indirizzi", Table = table, IsPrimary = true });
    }

    private static string SourceText(IpDevice d) => d.Source switch
    {
        IpSources.Tia => d.TiaMissing ? "TIA (non piu' presente)" : "TIA",
        IpSources.Proneta => "PRONETA",
        _ => "manuale",
    };

    // ---------- Lista hardware ----------

    public static DocDocument HardwareList(DocContext ctx, HardwareExport? hw)
    {
        List<DocColumn> cols = new()
        {
            new("slot", "Slot", 0.45, DocAlign.Right),
            new("module", "Modulo", 2.0),
            new("type", "Tipo", 1.7),
            new("order", "Codice d'ordine", 1.45, Mono: true),
            new("firmware", "Firmware", 0.65),
            new("inputs", "Ingressi", 1.05, Mono: true),
            new("outputs", "Uscite", 1.05, Mono: true),
            new("channels", "Canali", 0.75),
            new("note", "Note", 1.3),
        };
        DocTable modulesTable = new() { Columns = cols, EmptyText = NoHardware };
        DocTable bom = new()
        {
            Columns = { new("order", "Codice d'ordine", 1.6, Mono: true), new("type", "Tipo", 2.4), new("qty", "Quantita'", 0.6, DocAlign.Right) },
            EmptyText = NoHardware,
        };

        if (hw != null)
        {
            List<HwModuleRow> modules = HardwareView.Modules(hw);
            foreach (IGrouping<string, HwModuleRow> station in modules
                         .GroupBy(m => m.DeviceName)
                         .OrderBy(g => g.Any(m => m.IsCpu) ? 0 : 1)
                         .ThenBy(g => IpTableFormat.SortKey(g.FirstOrDefault(m => m.IsHead)?.Ip), StringComparer.Ordinal)
                         .ThenBy(g => g.First().Station, StringComparer.OrdinalIgnoreCase))
            {
                HwModuleRow first = station.First();
                HwModuleRow? head = station.FirstOrDefault(m => m.IsHead);
                List<string> parts = new() { first.Station };
                if (!string.Equals(first.Station, first.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(first.DeviceName);
                }

                if (head?.Ip != null)
                {
                    parts.Add(head.Ip);
                }

                if (head?.IsCpu != true && first.Plc != null)
                {
                    parts.Add("PLC " + first.Plc);
                }

                modulesTable.Rows.Add(DocRow.Group(string.Join(" · ", parts), first.Station));
                foreach (HwModuleRow m in station.OrderBy(m => m.Slot ?? int.MaxValue))
                {
                    modulesTable.Rows.Add(new DocRow
                    {
                        Key = m.Key,
                        Cells =
                        {
                            m.Slot?.ToString(CultureInfo.InvariantCulture), m.Name, m.TypeName, m.OrderNumber, m.Firmware,
                            m.InputsText, m.OutputsText, m.Channels, ctx.Note(DocKinds.Hardware, m.Key) ?? m.Comment,
                        },
                    });
                }
            }

            foreach (IGrouping<string, HwModuleRow> g in modules.Where(m => m.OrderNumber != null && !m.OrderNumber.Contains('*'))
                         .GroupBy(m => m.OrderNumber!, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                bom.Rows.Add(new DocRow
                {
                    Key = g.Key,
                    Cells = { g.Key, g.Select(m => m.TypeName).FirstOrDefault(t => t != null), g.Count().ToString(CultureInfo.InvariantCulture) },
                });
            }
        }

        AddManual(ctx, DocKinds.Hardware, modulesTable);
        DocSection main = new() { Title = "Stazioni e moduli", Table = modulesTable, IsPrimary = true };
        DocSection summary = new()
        {
            Title = "Riepilogo dei codici d'ordine",
            Paragraphs = { "Quantita' per codice, dai moduli del progetto (guide e accessori compresi, codici generici esclusi)." },
            Table = bom,
        };
        return Document(ctx, DocKinds.Hardware, Meta(ctx, null), main, summary);
    }

    // ---------- Lista IO ----------

    public static DocDocument IoList(DocContext ctx, HardwareExport? hw)
    {
        List<DocColumn> cols = new()
        {
            new("address", "Indirizzo", 0.8, Mono: true),
            new("name", "Simbolo", 2.6, Mono: true),
            new("type", "Tipo dati", 1.3),
            new("comment", "Commento", 2.2),
            new("table", "Tabella tag", 1.5),
            new("note", "Note", 1.2),
        };
        DocTable signals = new() { Columns = cols, EmptyText = NoHardware };
        DocTable areas = new()
        {
            Columns =
            {
                new("station", "Stazione", 1.9), new("slot", "Slot", 0.45, DocAlign.Right), new("module", "Modulo", 1.9),
                new("order", "Codice d'ordine", 1.4, Mono: true), new("inputs", "Ingressi", 1.1, Mono: true),
                new("outputs", "Uscite", 1.1, Mono: true), new("channels", "Canali", 0.75), new("signals", "Segnali", 0.6, DocAlign.Right),
            },
            EmptyText = NoHardware,
        };

        if (hw != null)
        {
            List<HwModuleRow> modules = HardwareView.Modules(hw);
            List<IoSignal> io = IoResolver.Resolve(hw, modules);
            bool manyPlc = io.Select(s => s.Plc).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            string? lastGroup = null;
            foreach (IoSignal s in io.OrderBy(s => s.Module == null ? 1 : 0).ThenBy(s => s.Plc, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(s => s.Address.SortKey, StringComparer.Ordinal))
            {
                string group = (manyPlc ? s.Plc + " · " : "") + (s.Module == null
                    ? "Indirizzi senza modulo nel progetto"
                    : (s.Address.IsInput ? "Ingressi" : "Uscite") + " · " + s.Module.Station + " · " + s.Module.Name +
                      (s.Module.Slot.HasValue ? " (slot " + s.Module.Slot.Value.ToString(CultureInfo.InvariantCulture) + ")" : ""));
                if (group != lastGroup)
                {
                    signals.Rows.Add(DocRow.Group(group));
                    lastGroup = group;
                }

                signals.Rows.Add(new DocRow
                {
                    Key = s.Key,
                    Cells = { s.Address.ToString(), s.Name, Unquote(s.DataType), s.Comment, s.Table, ctx.Note(DocKinds.Io, s.Key) },
                });
            }

            Dictionary<string, int> perModule = io.Where(s => s.Module != null).GroupBy(s => s.Module!.Key).ToDictionary(g => g.Key, g => g.Count());
            foreach (HwModuleRow m in modules.Where(m => m.Inputs.Count > 0 || m.Outputs.Count > 0)
                         .OrderBy(m => m.Inputs.Concat(m.Outputs).Min(r => r.StartByte)))
            {
                areas.Rows.Add(new DocRow
                {
                    Key = m.Key,
                    Cells =
                    {
                        m.Station, m.Slot?.ToString(CultureInfo.InvariantCulture), m.Name, m.OrderNumber, m.InputsText, m.OutputsText, m.Channels,
                        (perModule.TryGetValue(m.Key, out int n) ? n : 0).ToString(CultureInfo.InvariantCulture),
                    },
                });
            }
        }

        AddManual(ctx, DocKinds.Io, signals);
        DocSection main = new()
        {
            Title = "Segnali",
            Paragraphs = { "Tag con indirizzo di ingresso o uscita dalle tabelle dei tag, ordinati per indirizzo e assegnati al modulo che ha quel byte." },
            Table = signals,
            IsPrimary = true,
        };
        DocSection modulesSection = new() { Title = "Moduli e aree di indirizzo", Table = areas };
        return Document(ctx, DocKinds.Io, Meta(ctx, null), main, modulesSection);
    }

    // ---------- Registro modifiche ----------

    public static DocDocument ChangeRegister(DocContext ctx, IReadOnlyList<ProjectVersion> versions, IReadOnlyList<Change> changes,
        IReadOnlyList<VersionLoad> loads)
    {
        DocTable v = new()
        {
            Columns =
            {
                new("version", "Versione", 0.8), new("project", "Progetto", 2.2, Mono: true), new("state", "Stato", 1.0),
                new("plc", "Sul PLC", 1.6), new("saved", "Ultimo salvataggio", 1.1), new("note", "Note", 1.8),
            },
        };
        foreach (ProjectVersion pv in versions.Where(x => !x.Missing))
        {
            string onPlc = string.Join(", ", loads.Where(l => l.VersionId == pv.Id && l.IsCurrent)
                .Select(l => l.PlcDisplay + " dal " + Local(l.LoadedUtc)));
            v.Rows.Add(new DocRow
            {
                Key = pv.ProjectName,
                Style = VersionStates.IsRetired(pv.State) ? DocRowStyle.Muted : DocRowStyle.Normal,
                Cells =
                {
                    pv.Label, pv.ProjectName, VersionStates.Italian(pv.State) + (pv.StateNote != null ? " (" + pv.StateNote + ")" : ""),
                    onPlc, pv.LastSavedUtc.HasValue ? Local(pv.LastSavedUtc.Value) : null, pv.Note,
                },
            });
        }

        Dictionary<long, string> labels = versions.ToDictionary(x => x.Id, x => x.Label);
        DocTable c = new()
        {
            Columns =
            {
                new("date", "Data", 0.8), new("title", "Titolo", 2.4), new("scope", "Ambito", 0.6), new("blocks", "Blocchi", 2.0, Mono: true),
                new("states", "Stato per versione", 2.4),
            },
            EmptyText = "Nessuna modifica registrata.",
        };
        foreach (Change ch in changes)
        {
            c.Rows.Add(new DocRow
            {
                Key = ch.Id.ToString(CultureInfo.InvariantCulture),
                Style = ch.Draft ? DocRowStyle.Muted : DocRowStyle.Normal,
                Cells =
                {
                    ch.Date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    ch.Title + (ch.Draft ? " (bozza)" : ""),
                    ChangeStates.ScopeItalian(ch.Scope),
                    string.Join(", ", ch.Blocks.Select(b => b.BlockName)),
                    string.Join(", ", ch.Versions.Where(x => labels.ContainsKey(x.VersionId))
                        .Select(x => labels[x.VersionId] + ": " + ChangeStates.Italian(x.State))),
                },
            });
        }

        List<DocMeta> meta = Meta(ctx, null, withData: false);
        meta.Insert(meta.Count - 1, new DocMeta("Contenuto", Count(v.Rows.Count, "versione", "versioni") + ", " +
                                                            Count(c.Rows.Count, "modifica", "modifiche") + " (bozze escluse)"));
        return Document(ctx, DocKinds.Register, meta,
            new DocSection { Title = "Versioni", Table = v },
            new DocSection { Title = "Modifiche", Table = c, IsPrimary = true });
    }

    // ---------- Piano attivita' ----------

    /// <summary>Attivita', milestone e fasi in ordine di piano, poi le variazioni delle date (lo storico delle tempistiche).</summary>
    public static DocDocument PlanList(DocContext ctx, IReadOnlyList<PlanTask> tasks, IReadOnlyList<PlanLink> links, IReadOnlyList<PlanEvent> events,
        IReadOnlyDictionary<long, string> versionLabels, WorkCalendar cal)
    {
        List<PlanTask> all = tasks.Select(t => t.Clone()).ToList();
        PlanRollup.Apply(all, cal);
        Dictionary<long, PlanTask> byId = all.ToDictionary(t => t.Id);
        ILookup<long?, PlanTask> byParent = all.ToLookup(t => t.ParentId != null && byId.ContainsKey(t.ParentId.Value) ? t.ParentId : null);
        ILookup<long, PlanLink> preds = links.ToLookup(l => l.SuccessorId);
        DateOnly today = DateOnly.FromDateTime(ctx.GeneratedLocal);

        DocTable plan = new()
        {
            Columns =
            {
                new("title", "Attivita'", 2.6), new("kind", "Tipo", 0.75), new("status", "Stato", 0.8), new("assignee", "Chi", 0.9),
                new("start", "Inizio", 0.75), new("end", "Fine", 0.75), new("days", "Gg", 0.4, DocAlign.Right), new("progress", "%", 0.45, DocAlign.Right),
                new("hours", "Ore", 0.7, DocAlign.Right), new("after", "Dopo", 1.6), new("version", "Versione", 0.65),
            },
            EmptyText = "Nessuna attivita' pianificata (scheda Pianificazione).",
        };

        void Walk(long? parent, int level)
        {
            foreach (PlanTask t in byParent[parent].OrderBy(t => t.Sort).ThenBy(t => t.Id))
            {
                if (t.IsPhase)
                {
                    plan.Rows.Add(DocRow.Group(
                        new string(' ', level * 3) + "Fase: " + t.Title + " · " + Dates(t) + " · " + t.Progress.ToString(CultureInfo.InvariantCulture) + "%",
                        t.Title));
                    Walk(t.Id, level + 1);
                    continue;
                }

                string after = string.Join(", ", preds[t.Id].Where(l => byId.ContainsKey(l.PredecessorId))
                    .Select(l => byId[l.PredecessorId].Title + (l.LagDays != 0 ? " (" + (l.LagDays > 0 ? "+" : "") + l.LagDays + ")" : "")));
                string hours = t.ActualHours == null && t.EstimatedHours == null ? ""
                    : (t.ActualHours ?? 0).ToString("0.#", CultureInfo.InvariantCulture) + " / " + (t.EstimatedHours?.ToString("0.#", CultureInfo.InvariantCulture) ?? "-");
                plan.Rows.Add(new DocRow
                {
                    Key = t.Id.ToString(CultureInfo.InvariantCulture),
                    Style = t.Status == PlanStatus.Cancelled ? DocRowStyle.Muted : DocRowStyle.Normal,
                    Cells =
                    {
                        new string(' ', level * 3) + t.Title, PlanStates.Italian(t.Kind),
                        t.IsLate(today) ? "In ritardo" : PlanStates.Italian(t.Status), t.Assignee,
                        Day(t.Start), t.IsMilestone ? "" : Day(t.End), t.IsMilestone ? "" : cal.Duration(t.Start, t.End).ToString(CultureInfo.InvariantCulture),
                        t.IsMilestone ? "" : (t.Status == PlanStatus.Done ? 100 : t.Progress).ToString(CultureInfo.InvariantCulture), hours, after,
                        t.VersionId is long v && versionLabels.TryGetValue(v, out string? label) ? label : null,
                    },
                });
            }
        }

        Walk(null, 0);

        DocTable changes = new()
        {
            Columns =
            {
                new("when", "Quando", 0.9), new("task", "Attivita'", 2.0), new("from", "Prima", 1.1), new("to", "Dopo", 1.1),
                new("shift", "Spost.", 0.55, DocAlign.Right), new("reason", "Motivo", 2.0), new("origin", "Origine", 0.7), new("user", "Chi", 0.7),
            },
            EmptyText = "Nessuna variazione delle date registrata.",
        };
        foreach (PlanEvent e in events.Where(e => e.Kind == PlanEventKinds.Dates && e.UndoneBy == null).OrderBy(e => e.Utc).ThenBy(e => e.Id))
        {
            changes.Rows.Add(new DocRow
            {
                Key = e.Id.ToString(CultureInfo.InvariantCulture),
                Cells =
                {
                    Local(e.Utc), e.TaskTitle, PlanFields.Display(e.Field, e.OldValue), PlanFields.Display(e.Field, e.NewValue),
                    e.ShiftText, e.Reason,
                    e.Cascade ? "cascata" : "a mano", e.User,
                },
            });
        }

        PlanKpi k = PlanKpi.Compute(all, today, cal);
        List<DocMeta> meta = Meta(ctx, null, withData: false);
        meta.Insert(meta.Count - 1, new DocMeta("Avanzamento",
            k.ProgressPercent.ToString(CultureInfo.InvariantCulture) + "% · " + Count(k.Tasks, "attivita'", "attivita'") + ", " + k.Done + " fatte" +
            (k.Late > 0 ? ", " + k.Late + " in ritardo" : "") + (k.PlanEnd != null ? " · fine prevista " + Day(k.PlanEnd.Value) : "")));
        return Document(ctx, DocKinds.Plan, meta,
            new DocSection { Title = "Piano", Table = plan, IsPrimary = true },
            new DocSection
            {
                Title = "Variazioni delle date",
                Paragraphs = { "Ogni spostamento, a mano o in cascata per le dipendenze, con il motivo indicato. \"Spost.\" in giorni di calendario." },
                Table = changes,
            });
    }

    private static string Dates(PlanTask t) => t.Start == t.End ? Day(t.Start) : Day(t.Start) + " - " + Day(t.End);

    private static string Day(DateOnly d) => d.ToString("dd/MM/yy", CultureInfo.InvariantCulture);

    // ---------- comuni ----------

    private const string NoHardware =
        "Nessun export hardware per questa versione: \"Aggiorna da TIA\" (scheda Documenti o Rete / IP) o uno snapshot.";

    private static DocDocument Document(DocContext ctx, string kind, List<DocMeta> meta, params DocSection[] sections)
    {
        string title = DocKinds.Title(kind);
        return new DocDocument
        {
            Kind = kind,
            Title = title,
            Subtitle = ctx.Commessa.Display + (string.IsNullOrWhiteSpace(ctx.Commessa.Customer) ? "" : " · " + ctx.Commessa.Customer),
            Meta = meta,
            Sections = sections.ToList(),
            Landscape = true,
            FileStem = OutputLayout.Safe(ctx.Commessa.Code) + "_" + DocKinds.FilePart(kind),
            Footer = title + " · " + ctx.Commessa.Display,
        };
    }

    /// <summary>Commessa, cliente, versione e fonte dei dati (solo se <paramref name="withData"/>), data di generazione.</summary>
    private static List<DocMeta> Meta(DocContext ctx, string? source, bool withData = true)
    {
        List<DocMeta> meta = new() { new DocMeta("Commessa", ctx.Commessa.Display) };
        if (!string.IsNullOrWhiteSpace(ctx.Commessa.Customer))
        {
            meta.Add(new DocMeta("Cliente", ctx.Commessa.Customer!));
        }

        if (withData)
        {
            if (ctx.Version != null)
            {
                ProjectVersion v = ctx.Version;
                meta.Add(new DocMeta("Versione", v.Label + " · " + v.ProjectName + (v.TiaVersionText != null ? " · TIA " + v.TiaVersionText : "") +
                                                 (v.State != VersionState.None ? " · " + VersionStates.Italian(v.State) : "")));
            }

            if (source != null)
            {
                meta.Add(new DocMeta("Fonte dati", source));
            }
            else if (ctx.HardwareSource is HwSnapshot hs)
            {
                meta.Add(new DocMeta("Fonte dati", "Export da TIA del " + Local(hs.CreatedUtc) +
                                                   (hs.Source == SnapshotSources.Attach ? " (TIA aperto)" : " (copia del progetto)") +
                                                   (hs.ProjectModified == true ? " · progetto con modifiche NON salvate" : "")));
            }
        }

        meta.Add(new DocMeta("Generato il", ctx.GeneratedLocal.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)));
        return meta;
    }

    private static void AddManual(DocContext ctx, string kind, DocTable table)
    {
        List<DocManualRow> manual = ctx.ManualRows.Where(r => r.ListKind == kind).OrderBy(r => r.Sort).ToList();
        if (manual.Count == 0)
        {
            return;
        }

        table.Rows.Add(DocRow.Group("Righe aggiunte a mano"));
        foreach (DocManualRow r in manual)
        {
            table.Rows.Add(new DocRow
            {
                Key = "manuale:" + r.Id.ToString(CultureInfo.InvariantCulture),
                IsManual = true,
                ManualId = r.Id,
                Cells = table.Columns.Select(c => r.Cells.TryGetValue(c.Key, out string? v) ? v : null).ToList(),
            });
        }
    }

    private static string? Unquote(string? type) => type?.Trim().Trim('"');

    private static string Count(int n, string one, string many) => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    private static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
}
