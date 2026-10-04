using TiaTracker.Contracts;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Hardware;
using TiaTracker.Core.Network;
using TiaTracker.Data;

namespace TiaTracker.Core.Tests;

/// <summary>Un export hardware come quello di un impianto vero, in piccolo.</summary>
internal static class SampleHardware
{
    public const string Plc = "10A1 - 1515F-2 PN";

    private static HwItem Item(string path, string name, int? pos, Dictionary<string, string>? attrs = null) => new()
    {
        Name = name, Path = path + "/" + name, Position = pos, Attributes = attrs ?? new Dictionary<string, string>(),
    };

    private static HwNode Node(string name, string? subnet, string ip, string pn) => new()
    {
        Name = name, NodeType = "Ethernet", Subnet = subnet, NetType = subnet != null ? "Ethernet" : null,
        Attributes = new Dictionary<string, string>
        {
            ["Address"] = ip, ["SubnetMask"] = "255.255.255.0", ["UseRouter"] = "false", ["PnDeviceName"] = pn, ["IpProtocolSelection"] = "Project",
        },
    };

    public static HardwareExport Build()
    {
        // CPU con due interfacce e un DI16 centrale.
        HwItem cpu = Item("S7-1500/ET200MP station_1", Plc, 1, new() { ["OrderNumber"] = "6ES7 515-2FN03-0AB0", ["FirmwareVersion"] = "V3.0", ["TypeName"] = "CPU 1515F-2 PN" });
        cpu.Classification = "CPU";
        cpu.PlcName = Plc;
        HwItem x1 = Item(cpu.Path, "PROFINET interface_1", 32768);
        x1.Interface = new HwInterface { InterfaceType = "Ethernet", ControlledIoSystems = { "PROFINET IO-System" } };
        x1.Interface.Nodes.Add(Node("X1", "PN/IE_1", "192.168.10.100", "10a1 - 1515f-2 pn.profinet interface_1"));
        HwItem x2 = Item(cpu.Path, "PROFINET interface_2", 33024);
        x2.Interface = new HwInterface { InterfaceType = "Ethernet" };
        x2.Interface.Nodes.Add(Node("X2", null, "192.168.1.100", "10a1 - 1515f-2 pn.profinet interface_2"));
        cpu.Items.Add(x1);
        cpu.Items.Add(x2);
        HwItem rail = Item("S7-1500/ET200MP station_1", "Rail_0", 0, new() { ["OrderNumber"] = "6ES7 590-1***0-0AA0" });
        HwItem di = Item("S7-1500/ET200MP station_1", "DI 16x24VDC HF_1", 2, new() { ["OrderNumber"] = "6ES7 521-1BH00-0AB0", ["TypeName"] = "DI 16x24VDC HF" });
        HwItem diSub = Item(di.Path, "DI 16x24VDC HF_1", 1);
        diSub.Addresses.Add(new HwAddress { IoType = "Input", StartAddress = 10000, Length = 16 });
        diSub.Channels.Add(new HwChannelGroup { Type = "Digital", IoType = "Input", Count = 16 });
        di.Items.Add(diSub);
        HwDevice station = new() { Name = "S7-1500/ET200MP station_1", GroupPath = "", PlcNames = { Plc }, Items = { rail, cpu, di } };

        // Stazione ET200SP sul sistema IO della CPU, con un F-DI (48 bit = 6 byte con PROFIsafe).
        HwItem rack = Item("ET 200SP station_1", "Rack_0", 0, new() { ["TypeName"] = "Rack" });
        HwItem im = Item("ET 200SP station_1", "20A1 - ET200 Main Cabinet", 0, new() { ["OrderNumber"] = "6ES7 155-6AU01-0BN0", ["TypeName"] = "IM 155-6 PN ST" });
        HwItem imItf = Item(im.Path, "PROFINET interface", 32768);
        imItf.Interface = new HwInterface { InterfaceType = "Ethernet" };
        imItf.Interface.Nodes.Add(Node("X1", "PN/IE_1", "192.168.10.3", "20a1 - et200 main cabinet"));
        imItf.Interface.Connectors.Add(new HwIoConnector { IoSystem = "PROFINET IO-System", IoSystemNumber = 100, Attributes = { ["PnDeviceNumber"] = "49" } });
        im.Items.Add(imItf);
        HwItem fdi = Item("ET 200SP station_1", "F-DI 8x24VDC HF_1", 1, new() { ["OrderNumber"] = "6ES7 136-6BA00-0CA0", ["TypeName"] = "F-DI 8x24VDC HF" });
        HwItem fdiSub = Item(fdi.Path, "F-DI 8x24VDC HF_1", 1);
        fdiSub.Addresses.Add(new HwAddress { IoType = "Input", StartAddress = 200, Length = 48 });
        fdiSub.Addresses.Add(new HwAddress { IoType = "Output", StartAddress = 200, Length = 32 });
        fdiSub.Addresses.Add(new HwAddress { IoType = "Diagnosis", StartAddress = -1, Length = 0 });
        fdiSub.Channels.Add(new HwChannelGroup { Type = "Digital", IoType = "Input", Count = 8 });
        fdi.Items.Add(fdiSub);
        HwDevice et200 = new() { Name = "ET 200SP station_1", GroupPath = "ET200", Items = { rack, im, fdi } };

        // Inverter GSD fra i non raggruppati, con telegramma.
        HwItem dap = Item("GSD device_76", "sPARE", 0, new() { ["OrderNumber"] = "FR-E800-E", ["TypeName"] = "PROFINET IO Reference Device for FR-E800" });
        HwItem dapItf = Item(dap.Path, "Interface", 32768);
        dapItf.Interface = new HwInterface { InterfaceType = "Ethernet" };
        dapItf.Interface.Nodes.Add(Node("X1", "PN/IE_1", "192.168.10.20", "inverter-1"));
        dapItf.Interface.Connectors.Add(new HwIoConnector { IoSystem = "PROFINET IO-System" });
        dap.Items.Add(dapItf);
        HwItem tgm = Item("GSD device_76", "Telegram 1 (PROFIdrive)_1", 1, new() { ["TypeName"] = "Telegram 1 (PROFIdrive)" });
        HwItem tgmSub = Item(tgm.Path, "Standard telegram 1", 1);
        tgmSub.Addresses.Add(new HwAddress { IoType = "Input", StartAddress = 1800, Length = 32 });
        tgmSub.Addresses.Add(new HwAddress { IoType = "Output", StartAddress = 1800, Length = 32 });
        tgm.Items.Add(tgmSub);
        HwDevice gsd = new() { Name = "GSD device_76", GroupPath = "Dispositivi non raggruppati", Items = { dap, tgm } };

        HardwareExport hw = new() { ProjectName = "DEMO", TiaMajor = 21, Culture = "en-US", Devices = { station, et200, gsd } };
        hw.Subnets.Add(new HwSubnet { Name = "PN/IE_1", NetType = "Ethernet", IoSystems = { "PROFINET IO-System" } });
        hw.TagTables.Add(new TagTableFile
        {
            Plc = Plc, Name = "Default tag table",
            Tags =
            {
                new HwTag { Name = "PULSANTE_RESET", DataType = "Bool", Address = "%I10000.3", Comment = "Reset quadro" },
                new HwTag { Name = "EMERGENZA_OK", DataType = "Bool", Address = "%I203.1" },
                new HwTag { Name = "CLOCK_100MS", DataType = "Bool", Address = "%M0.0" },
                new HwTag { Name = "SENZA_MODULO", DataType = "Bool", Address = "%I9999.0" },
            },
        });
        hw.TagTables.Add(new TagTableFile
        {
            Plc = Plc, Name = "18E1_INVERTER",
            Tags =
            {
                new HwTag { Name = "INV_RX", DataType = "\"MTS_E800-TELEGRAM-1-RX_UDT\"", Address = "%I1800.0" },
                new HwTag { Name = "INV_TX", DataType = "\"MTS_E800-TELEGRAM-1-TX_UDT\"", Address = "%Q1800.0" },
            },
        });
        return hw;
    }
}

public class PlcAddressTests
{
    [Theory]
    [InlineData("%I0.0", 'I', 'X', 0, 0)]
    [InlineData("%Q10000.7", 'Q', 'X', 10000, 7)]
    [InlineData("%IW64", 'I', 'W', 64, null)]
    [InlineData("%QD100", 'Q', 'D', 100, null)]
    [InlineData("%IB5", 'I', 'B', 5, null)]
    [InlineData("%E1.2", 'I', 'X', 1, 2)]
    [InlineData("%AW4", 'Q', 'W', 4, null)]
    [InlineData("%M5.3", 'M', 'X', 5, 3)]
    [InlineData("%I1800.0:P", 'I', 'X', 1800, 0)]
    public void Riconosce(string text, char area, char width, int b, int? bit)
    {
        Assert.True(PlcAddress.TryParse(text, out PlcAddress a));
        Assert.Equal(area, a.Area);
        Assert.Equal(width, a.Width);
        Assert.Equal(b, a.Byte);
        Assert.Equal(bit, a.Bit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DB10.DBX0.0")]
    [InlineData("%IW64.1")]
    [InlineData("%Z0.0")]
    public void Rifiuta(string? text) => Assert.False(PlcAddress.TryParse(text, out _));

    [Fact]
    public void OrdineIngressiPoiUscite()
    {
        List<string> sorted = new[] { "%Q0.0", "%I1.0", "%I0.7", "%M0.0", "%I0.1" }
            .Select(t => { PlcAddress.TryParse(t, out PlcAddress a); return a; })
            .OrderBy(a => a.SortKey, StringComparer.Ordinal).Select(a => a.ToString()).ToList();
        Assert.Equal(new[] { "%I0.1", "%I0.7", "%I1.0", "%Q0.0", "%M0.0" }, sorted);
    }
}

public class TagTableXmlTests
{
    private const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <Engineering version="V21" />
          <SW.Tags.PlcTagTable ID="0">
            <AttributeList><Name>70A1_PLC_DOUT</Name></AttributeList>
            <ObjectList>
              <SW.Tags.PlcTag ID="1" CompositionName="Tags">
                <AttributeList>
                  <DataTypeName>Bool</DataTypeName>
                  <LogicalAddress>%Q10000.0</LogicalAddress>
                  <Name>MC_EMERGENCY_RESET_LED_DOUT</Name>
                </AttributeList>
                <ObjectList>
                  <MultilingualText ID="2" CompositionName="Comment">
                    <ObjectList>
                      <MultilingualTextItem ID="3" CompositionName="Items">
                        <AttributeList><Culture>it-IT</Culture><Text>Led reset</Text></AttributeList>
                      </MultilingualTextItem>
                      <MultilingualTextItem ID="4" CompositionName="Items">
                        <AttributeList><Culture>en-US</Culture><Text>Reset lamp</Text></AttributeList>
                      </MultilingualTextItem>
                    </ObjectList>
                  </MultilingualText>
                </ObjectList>
              </SW.Tags.PlcTag>
              <SW.Tags.PlcTag ID="5" CompositionName="Tags">
                <AttributeList>
                  <DataTypeName>Bool</DataTypeName>
                  <LogicalAddress>%Q10000.1</LogicalAddress>
                  <Name>TELERUTTORE_AVANTI</Name>
                </AttributeList>
                <ObjectList>
                  <MultilingualText ID="6" CompositionName="Comment">
                    <ObjectList>
                      <MultilingualTextItem ID="7" CompositionName="Items">
                        <AttributeList><Culture>en-US</Culture><Text /></AttributeList>
                      </MultilingualTextItem>
                    </ObjectList>
                  </MultilingualText>
                </ObjectList>
              </SW.Tags.PlcTag>
            </ObjectList>
          </SW.Tags.PlcTagTable>
        </Document>
        """;

    [Fact]
    public void NomeTipoIndirizzoCommentoNellaLingua()
    {
        (string? table, List<HwTag> tags) = TagTableXmlParser.Parse(Xml, "en-US");
        Assert.Equal("70A1_PLC_DOUT", table);
        Assert.Equal(2, tags.Count);
        Assert.Equal("MC_EMERGENCY_RESET_LED_DOUT", tags[0].Name);
        Assert.Equal("%Q10000.0", tags[0].Address);
        Assert.Equal("Reset lamp", tags[0].Comment);
        Assert.Null(tags[1].Comment);

        Assert.Equal("Led reset", TagTableXmlParser.Parse(Xml, "it-IT").Tags[0].Comment);
        Assert.Equal("Led reset", TagTableXmlParser.Parse(Xml, null).Tags[0].Comment);
    }

    [Fact]
    public void LettoreRiempieITagDagliXml()
    {
        using FakeTree t = new();
        HardwareExport hw = new() { Culture = "en-US" };
        hw.TagTables.Add(new TagTableFile { Plc = "PLC_1", Name = "70A1_PLC_DOUT", File = "tags/PLC_1/70A1_PLC_DOUT.xml" });
        hw.TagTables.Add(new TagTableFile { Plc = "PLC_1", Name = "rotta", File = "tags/PLC_1/manca.xml" });
        t.File(@"tags\PLC_1\70A1_PLC_DOUT.xml", Xml);
        string json = t.File("hardware.json", MiniJson.Serialize(hw, indented: true));

        HardwareExport loaded = HardwareReader.Load(json);

        Assert.Equal(2, loaded.TagTables[0].Tags.Count);
        Assert.Equal(ItemStates.Failed, loaded.TagTables[1].State);
        Assert.Single(loaded.Warnings);
    }
}

public class HardwareViewTests
{
    [Fact]
    public void MiniJsonEStjParlanoLaStessaLingua()
    {
        HardwareExport hw = SampleHardware.Build();
        HardwareExport back = HardwareReader.Deserialize(MiniJson.Serialize(hw));
        Assert.Equal(3, back.Devices.Count);
        Assert.Equal("192.168.10.100", back.Devices[0].Items[1].Items[0].Interface!.Nodes[0].Attributes["Address"]);
        Assert.Equal(48, back.Devices[1].Items[2].Items[0].Addresses[0].Length);
        Assert.Equal(HardwareReader.Serialize(hw), HardwareReader.Serialize(back));
    }

    [Fact]
    public void ModuliConStazioneAreeInByteEPlc()
    {
        List<HwModuleRow> m = HardwareView.Modules(SampleHardware.Build());

        Assert.DoesNotContain(m, r => r.Name == "Rack_0"); // rack virtuale: niente codice, niente indirizzi
        Assert.Contains(m, r => r.Name == "Rail_0");       // guida: ha un codice d'ordine

        HwModuleRow cpu = m.Single(r => r.IsCpu);
        Assert.True(cpu.IsHead);
        Assert.Equal("192.168.10.100", cpu.Ip);

        HwModuleRow di = m.Single(r => r.Name == "DI 16x24VDC HF_1");
        Assert.Equal("%IB10000…10001", di.InputsText);
        Assert.Equal("16 DI", di.Channels);
        Assert.Equal(SampleHardware.Plc, di.Plc);

        HwModuleRow fdi = m.Single(r => r.Name == "F-DI 8x24VDC HF_1");
        Assert.Equal("20A1 - ET200 Main Cabinet", fdi.Station);
        Assert.Equal("%IB200…205", fdi.InputsText);   // 48 bit = 6 byte
        Assert.Equal("%QB200…203", fdi.OutputsText);
        Assert.Equal(SampleHardware.Plc, fdi.Plc);       // dal sistema IO della CPU
        Assert.Equal("ET200", fdi.GroupPath);

        HwModuleRow tgm = m.Single(r => r.Name == "Telegram 1 (PROFIdrive)_1");
        Assert.Equal("sPARE", tgm.Station);
        Assert.Equal("%IB1800…1803", tgm.InputsText);
    }

    [Fact]
    public void SegnaliAssegnatiAlModulo()
    {
        HardwareExport hw = SampleHardware.Build();
        List<IoSignal> io = IoResolver.Resolve(hw, HardwareView.Modules(hw));

        Assert.Equal(new[] { "%I203.1", "%I1800.0", "%I9999.0", "%I10000.3", "%Q1800.0" }, io.Select(s => s.Address.ToString()));
        Assert.Equal("F-DI 8x24VDC HF_1", io[0].Module!.Name);
        Assert.Equal("Telegram 1 (PROFIdrive)_1", io[1].Module!.Name);
        Assert.Null(io[2].Module);
        Assert.Equal("DI 16x24VDC HF_1", io[3].Module!.Name);
        Assert.Equal("Reset quadro", io[3].Comment);
        Assert.DoesNotContain(io, s => s.Name == "CLOCK_100MS");
    }

    [Fact]
    public void ListaIpDaiNodi()
    {
        List<IpDevice> ip = IpSync.FromHardware(SampleHardware.Build());

        Assert.Equal(4, ip.Count);
        Assert.Contains(ip, d => d.Name == SampleHardware.Plc + " (X1)" && d.Ip == "192.168.10.100" && d.Network == "PN/IE_1");
        Assert.Contains(ip, d => d.Name == SampleHardware.Plc + " (X2)" && d.Ip == "192.168.1.100" && d.Network == null);
        IpDevice et = ip.Single(d => d.Ip == "192.168.10.3");
        Assert.Equal("20A1 - ET200 Main Cabinet", et.Name);
        Assert.Equal("IM 155-6 PN ST", et.Kind);
        Assert.Equal("20a1 - et200 main cabinet", et.ProfinetName);
        Assert.All(ip, d => Assert.Equal(IpSources.Tia, d.Source));
        Assert.Equal(ip.Count, ip.Select(d => d.TiaKey).Distinct().Count());
    }
}

public class IpSyncTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AggiungeRiconosceConservaESegnala()
    {
        List<IpDevice> tia = IpSync.FromHardware(SampleHardware.Build());
        List<IpDevice> existing = new()
        {
            new IpDevice { Name = "HMI quadro", Ip = "192.168.10.50", Location = "Quadro 1" },             // manuale, resta
            new IpDevice { Name = "ET200 vecchio nome", Ip = "192.168.10.3", Location = "QE", Notes = "x" }, // stesso IP: diventa di TIA
        };

        (List<IpDevice> rows, IpSyncSummary s) = IpSync.Merge(existing, tia, 7, Now);

        Assert.Equal(1, s.Adopted);
        Assert.Equal(3, s.Added);
        Assert.Equal(5, rows.Count);
        IpDevice et = rows.Single(r => r.Ip == "192.168.10.3");
        Assert.Equal("20A1 - ET200 Main Cabinet", et.Name);
        Assert.Equal("QE", et.Location);
        Assert.Equal("x", et.Notes);
        Assert.True(et.IsFromTia);
        Assert.Equal(7, et.TiaVersionId);
        Assert.False(rows.Single(r => r.Ip == "192.168.10.50").IsFromTia);

        // Secondo giro identico: niente da fare.
        (List<IpDevice> again, IpSyncSummary s2) = IpSync.Merge(rows, tia, 7, Now.AddHours(1));
        Assert.False(s2.Changed);
        Assert.Equal(5, again.Count);

        // L'inverter sparisce da TIA: la riga resta, segnata.
        List<IpDevice> fewer = tia.Where(d => d.Ip != "192.168.10.20").ToList();
        (List<IpDevice> third, IpSyncSummary s3) = IpSync.Merge(again, fewer, 8, Now.AddHours(2));
        Assert.Equal(1, s3.Missing);
        Assert.True(third.Single(r => r.Ip == "192.168.10.20").TiaMissing);
    }

    [Fact]
    public void RigheIpSalvateConLaFonte()
    {
        using Db db = Db.OpenInMemory();
        CommessaRepository repo = new(db);
        Commessa c = new() { Code = "AUT1", Name = "X", OutputDir = @"D:\Out", SettingsJson = new CommessaSettings { AutoDocuments = false }.ToJson() };
        repo.Insert(c);
        Assert.Equal(@"D:\Out", repo.Get(c.Id)!.OutputDir);
        Assert.False(CommessaSettings.Parse(repo.Get(c.Id)!.SettingsJson).AutoDocuments);

        (List<IpDevice> rows, _) = IpSync.Merge(Array.Empty<IpDevice>(), IpSync.FromHardware(SampleHardware.Build()), null, Now);
        rows[0].TiaMissing = true;
        repo.SaveIpDevices(c.Id, rows);

        List<IpDevice> back = repo.IpDevices(c.Id);
        Assert.Equal(rows.Count, back.Count);
        Assert.All(back, d => Assert.Equal(IpSources.Tia, d.Source));
        Assert.True(back[0].TiaMissing);
        Assert.Equal(rows[1].TiaKey, back[1].TiaKey);
        Assert.Equal(Now, back[1].TiaSeenUtc);
    }

    [Fact]
    public void ExportHardwareNelDb()
    {
        using Db db = Db.OpenInMemory();
        CommessaRepository commesse = new(db);
        Commessa c = new() { Code = "AUT2", Name = "Y" };
        commesse.Insert(c);
        long v = db.Insert("INSERT INTO version (commessa_id, label, sort_key, project_name, first_seen_utc) VALUES ($c, 'V1', 'V1', 'P1', '2026-01-01T00:00:00.000Z')",
            ("$c", c.Id));
        ContentStore content = new(db);
        HardwareRepository repo = new(db);
        string hash = content.Put(HardwareReader.Serialize(SampleHardware.Build()), "hardware");

        repo.Insert(new HwSnapshot { VersionId = v, CreatedUtc = Now, Source = "attach", Format = 1, DataHash = hash, DeviceCount = 3, IpCount = 4 });
        repo.Insert(new HwSnapshot { VersionId = v, CreatedUtc = Now.AddHours(1), Source = "file", Format = 1, DataHash = hash, DeviceCount = 3 });

        Assert.Equal("file", repo.Latest(v)!.Source);
        Assert.Equal(2, repo.ByCommessa(c.Id).Count);
        Assert.Equal("file", repo.LatestByVersion(c.Id)[v].Source);
        Assert.Equal(3, HardwareReader.Deserialize(content.GetText(hash)!).Devices.Count);
    }
}
