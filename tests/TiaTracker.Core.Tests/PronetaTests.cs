using TiaTracker.Core.Domain;
using TiaTracker.Core.Network;

namespace TiaTracker.Core.Tests;

public class PronetaTests
{
    private const string Csv =
        "\uFEFFsep=;\r\n" +
        "\"Online Topology\";\"\";\"\"\r\n" +
        "\"#\";\"Name\";\"Device Type\";\"IP Address\";\"Subnet Mask\";\"MAC Address\";\"Role\";\"IO Controller\";\"Vendor Name\";\"Order Number\";\"Firmware Version\";\"Hardware Revision\";\"#\";\"Name\";\"IP Address\";\"Subnet Mask\";\"MAC Address\";\"#\";\"Port ID\";\"Port Description\";\"Partner Port ID\";\"Partner Device Name\";\"Power Budget [dB]\";\"#\";\"Module Name\";\"Vendor\";\"Order Number\";\"Serial Number\";\"Firmware Version\";\"Hardware Revision\";\"\"\r\n" +
        "\"1\";\"20a1 - et200 main cabinet\";\"ET 200SP\";\"192.168.10.3\";\"255.255.255.0\";\"02:00:00:00:01:00\";\"Device\";\"10a1 - 1515f-2 pn.profinet interface_1\";\"SIEMENS AG\";\"6ES7 155-6AU01-0BN0\";\"V4.2\";\"1\";\"1\";\"20a1 - et200 main cabinet\";\"192.168.10.3\";\"255.255.255.0\";\"02:00:00:00:01:00\";\"1\";\"port-001\";\"\";\"port-002\";\"switch_1\";\"\";\"0\";\"0x1\";\"SIEMENS AG\";\"6ES7 155-6AU01-0BN0\";\"S C-123\";\"V4.2\";\"1\";\"\"\r\n" +
        "\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"1\";\"0x2\";\"SIEMENS AG\";\"6ES7 136-6BA00-0CA0\";\"S C-456\";\"V1.0\";\"1\";\"\"\r\n" +
        "\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\"\r\n" +
        "\"2\";\"plcdistributore.profinet interface_1\";\"S7-1500\";\"192.168.10.191\";\"255.255.255.0\";\"02:00:00:00:02:28\";\"Controller\";\"\";\"SIEMENS AG\";\"6ES7 514-2SN03-0AB0\";\"V3.1.3\";\"4\";\"1\";\"plcdistributore.profinet interface_1\";\"192.168.10.191\";\"255.255.255.0\";\"02:00:00:00:02:28\";\"1\";\"port-001\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\"\r\n" +
        "\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"2\";\"plcdistributore.profinet interface_2\";\"192.168.1.101\";\"255.255.255.0\";\"02:00:00:00:02:2b\";\"2\";\"port-002\";\"\";\"port-001\";\"switch_1\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\"\r\n" +
        "\"3\";\"nb-dell\";\"SIMATIC-PC\";\"192.168.66.55\";\"255.255.255.0\";\"02:00:00:00:03:5b\";\"Not Defined\";\"\";\"SIEMENS AG\";\"\";\"\";\"\";\"1\";\"nb-dell\";\"192.168.66.55\";\"255.255.255.0\";\"02:00:00:00:03:5b\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\";\"\"\r\n";

    private const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Topology xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <DeviceCollection>
            <Device>
              <NameOfStation>xd54e1xa-xavtrxaunloadxascrewxahoppere1e3</NameOfStation>
              <ReadableNameOfStation>54e1 - vtr unload screw hopper</ReadableNameOfStation>
              <IpAddress>192.168.10.54</IpAddress>
              <GatewayIp>192.168.10.54</GatewayIp>
              <NetworkMask>255.255.255.0</NetworkMask>
              <DeviceType>FR-E800-E</DeviceType>
              <MAC>02:00:00:00:01:01</MAC>
              <ManufacturerName>Mitsubishi Electric Corporation</ManufacturerName>
              <Role>Device</Role>
              <ImRecord><OrderID>FR-E800-E</OrderID><SerialNumber>1000393728</SerialNumber><SoftwareRevision>V0.0.1</SoftwareRevision></ImRecord>
              <ArData><ArConnections><ArConnection>
                <ReadableInitiatorStationName>10a1 - 1515f-2 pn.profinet interface_1</ReadableInitiatorStationName>
              </ArConnection></ArConnections></ArData>
            </Device>
            <Device>
              <ReadableNameOfStation>fabiobaldoai</ReadableNameOfStation>
              <IpAddress>192.168.1.234</IpAddress>
              <GatewayIp>192.168.100.1</GatewayIp>
              <NetworkMask>255.255.255.0</NetworkMask>
              <DeviceType>SIMATIC-PC</DeviceType>
              <MAC>02:00:00:00:04:79</MAC>
              <Role>Not Defined</Role>
              <ImRecord />
              <ArData />
            </Device>
          </DeviceCollection>
          <PronetaPC><ReadableNameOfStation>pc-ufficio</ReadableNameOfStation><IpAddress>192.168.10.210</IpAddress></PronetaPC>
        </Topology>
        """;

    [Fact]
    public void CsvDellaTopologiaOnline()
    {
        Assert.True(PronetaFile.LooksLikeProneta(Csv));
        List<PronetaDevice> d = PronetaFile.Parse(Csv);

        Assert.Equal(4, d.Count);
        Assert.Equal("20a1 - et200 main cabinet", d[0].Name);
        Assert.Equal("192.168.10.3", d[0].Ip);
        Assert.Equal("02:00:00:00:01:00", d[0].Mac);
        Assert.Equal("10a1 - 1515f-2 pn.profinet interface_1", d[0].IoController);
        Assert.Equal("6ES7 155-6AU01-0BN0", d[0].OrderNumber);
        Assert.True(d[2].IsExtraInterface);
        Assert.Equal("192.168.1.101", d[2].Ip);
        Assert.Equal("plcdistributore.profinet interface_2", d[2].Name);
        Assert.True(d[3].IsPc);
    }

    [Fact]
    public void XmlDellaTopologia()
    {
        Assert.True(PronetaFile.LooksLikeProneta(Xml));
        List<PronetaDevice> d = PronetaFile.Parse(Xml);

        Assert.Equal(2, d.Count); // il PC di PRONETA resta fuori
        Assert.Equal("54e1 - vtr unload screw hopper", d[0].Name);
        Assert.Null(d[0].Gateway); // PRONETA ripete l'IP del dispositivo quando non c'e' router
        Assert.Equal("1000393728", d[0].SerialNumber);
        Assert.Equal("10a1 - 1515f-2 pn.profinet interface_1", d[0].IoController);
        Assert.Equal("192.168.100.1", d[1].Gateway);
        Assert.True(d[1].IsPc);
    }

    [Fact]
    public void AbbinaPerNomePoiIpPoiMac()
    {
        List<IpDevice> rows = new()
        {
            new IpDevice { Name = "20A1 - ET200 Main Cabinet", Ip = "192.168.10.3", ProfinetName = "20a1 - et200 main cabinet", Source = IpSources.Tia, TiaKey = "k1" },
            new IpDevice { Name = "Distributore", Ip = "192.168.10.192", Location = "Quadro D" }, // IP diverso da quello in rete
            new IpDevice { Name = "Inverter", Mac = "02-00-00-00-01-01" },
        };
        List<PronetaDevice> devices = PronetaFile.Parse(Csv).Concat(PronetaFile.Parse(Xml)).ToList();
        devices[1] = new PronetaDevice { Name = "distributore", Ip = "192.168.10.191", Mac = "02:00:00:00:02:28" };
        rows[1].ProfinetName = "distributore";

        List<PronetaMatch> plan = PronetaImport.Plan(rows, devices);

        Assert.Equal("nome PROFINET", plan[0].MatchedBy);
        Assert.True(plan[0].MacChanges);
        Assert.True(plan[1].IpDiffers);
        Assert.Equal("192.168.10.192", plan[1].TableIp);
        Assert.Equal(PronetaMatchKind.New, plan[2].Kind); // seconda interfaccia: nuova
        Assert.False(plan[3].Apply);                      // PC
        Assert.Equal("MAC", plan[4].MatchedBy);           // inverter abbinato per MAC
        Assert.False(plan[5].Apply);                      // PC dell'XML

        List<IpDevice> result = PronetaImport.Apply(rows, plan);
        Assert.Equal("02-00-00-00-01-00", result[0].Mac);
        Assert.Equal(IpSources.Tia, result[0].Source);
        Assert.Equal("192.168.10.192", result[1].Ip); // l'IP della tabella resta
        Assert.Equal("Quadro D", result[1].Location);
        Assert.Equal("192.168.10.54", result[2].Ip);  // riga vuota completata dall'abbinamento per MAC
        IpDevice added = Assert.Single(result.Skip(3));
        Assert.Equal("plcdistributore", added.Name);
        Assert.Equal(IpSources.Proneta, added.Source);
        Assert.Equal("02-00-00-00-02-2B", added.Mac);
    }
}
