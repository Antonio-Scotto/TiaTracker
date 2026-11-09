using TiaTracker.Core.Snapshots;

namespace TiaTracker.Core.Tests;

public class CanonicalizerTests
{
    // Un FC SCL come lo esporta TIA V21 (ridotto).
    private const string Original = """
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <Engineering version="V21" />
          <DocumentInfo>
            <Created>2026-09-04T19:45:27.8441359Z</Created>
            <ExportSetting>WithDefaults</ExportSetting>
          </DocumentInfo>
          <SW.Blocks.FC ID="0">
            <AttributeList>
              <AutoNumber>true</AutoNumber>
              <CodeModifiedDate>2026-09-04T10:00:00Z</CodeModifiedDate>
              <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
                <Section Name="Input"><Member Name="enable" Datatype="Bool" Remanence="NonRetain"><Comment><MultiLanguageText Lang="it-IT">abilita   il motore</MultiLanguageText></Comment></Member></Section>
                <Section Name="Static"><Member Name="speed" Datatype="Real"><StartValue>25.0</StartValue></Member></Section>
              </Sections></Interface>
              <Name>MOTOR_FC</Name>
              <Number>50</Number>
              <ProgrammingLanguage>SCL</ProgrammingLanguage>
            </AttributeList>
            <ObjectList>
              <SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits">
                <AttributeList>
                  <NetworkSource><StructuredText xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4">
                    <Access Scope="LocalVariable" UId="21"><Symbol UId="22"><Component Name="speed" UId="23" /></Symbol></Access>
                    <Blank Num="1" UId="24" />
                    <Token Text=":=" UId="25" />
                    <Blank Num="1" UId="26" />
                    <Access Scope="LiteralConstant" UId="27"><Constant UId="28"><ConstantValue UId="29">50.0</ConstantValue></Constant></Access>
                    <Token Text=";" UId="30" />
                    <NewLine Num="2" UId="31" />
                    <LineComment UId="32"><Text UId="33"> giri   al minuto</Text></LineComment>
                  </StructuredText></NetworkSource>
                  <ProgrammingLanguage>SCL</ProgrammingLanguage>
                </AttributeList>
                <ObjectList>
                  <MultilingualText ID="6" CompositionName="Title"><ObjectList><MultilingualTextItem ID="7" CompositionName="Items"><AttributeList><Culture>it-IT</Culture><Text>Velocita</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
                </ObjectList>
              </SW.Blocks.CompileUnit>
            </ObjectList>
          </SW.Blocks.FC>
        </Document>
        """;

    // Stesso blocco riesportato: altra data, altri ID e UId, indentazione SCL diversa,
    // namespace di un'altra versione Openness, attributi in altro ordine, numero cambiato (AutoNumber).
    private const string Reexported = """
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <Engineering version="V21" />
          <DocumentInfo>
            <Created>2026-10-04T08:00:00.0000000Z</Created>
          </DocumentInfo>
          <SW.Blocks.FC ID="10">
            <AttributeList>
              <AutoNumber>true</AutoNumber>
              <CodeModifiedDate>2026-10-01T10:00:00Z</CodeModifiedDate>
              <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v6">
                <Section Name="Input"><Member Datatype="Bool" Remanence="NonRetain" Name="enable"><Comment><MultiLanguageText Lang="it-IT">abilita il motore</MultiLanguageText></Comment></Member></Section>
                <Section Name="Static"><Member Name="speed" Datatype="Real"><StartValue>25.0</StartValue></Member></Section>
              </Sections></Interface>
              <Name>MOTOR_FC</Name>
              <Number>51</Number>
              <ProgrammingLanguage>SCL</ProgrammingLanguage>
            </AttributeList>
            <ObjectList>
              <SW.Blocks.CompileUnit ID="30" CompositionName="CompileUnits">
                <AttributeList>
                  <NetworkSource><StructuredText xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v5">
                    <Blank Num="4" UId="100" />
                    <Access Scope="LocalVariable" UId="101"><Symbol UId="102"><Component Name="speed" UId="103" /></Symbol></Access>
                    <Blank Num="2" UId="104" />
                    <Token Text=":=" UId="105" />
                    <Access Scope="LiteralConstant" UId="107"><Constant UId="108"><ConstantValue UId="109">50.0</ConstantValue></Constant></Access>
                    <Token Text=";" UId="110" />
                    <NewLine Num="1" UId="111" />
                    <NewLine Num="1" UId="112" />
                    <LineComment UId="113"><Text UId="114">giri al minuto </Text></LineComment>
                  </StructuredText></NetworkSource>
                  <ProgrammingLanguage>SCL</ProgrammingLanguage>
                </AttributeList>
                <ObjectList>
                  <MultilingualText ID="60" CompositionName="Title"><ObjectList><MultilingualTextItem ID="70" CompositionName="Items"><AttributeList><Culture>it-IT</Culture><Text>Velocita</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
                </ObjectList>
              </SW.Blocks.CompileUnit>
            </ObjectList>
          </SW.Blocks.FC>
        </Document>
        """;

    private static BlockHashes H(string xml) => Hasher.Compute(XmlCanonicalizer.Canonicalize(XmlCanonicalizer.StripDocumentInfo(xml)));

    [Fact]
    public void RiesportazioneIdentica()
    {
        BlockHashes a = H(Original), b = H(Reexported);
        Assert.Equal(a.All, b.All);
        Assert.Equal(a.Code, b.Code);
        Assert.Equal(a.Interface, b.Interface);
        Assert.Equal(a.Text, b.Text);
        Assert.Single(a.Units);
        Assert.Equal("Velocita", a.Units[0].Title);
    }

    [Fact]
    public void ModificaDelCodice()
    {
        BlockHashes a = H(Original), b = H(Original.Replace("50.0", "55.0"));
        Assert.NotEqual(a.All, b.All);
        Assert.NotEqual(a.Code, b.Code);
        Assert.Equal(a.Interface, b.Interface);
        Assert.Equal(a.Text, b.Text);
        Assert.NotEqual(a.Units[0].HCode, b.Units[0].HCode);
    }

    [Fact]
    public void SoloCommento()
    {
        BlockHashes a = H(Original), b = H(Original.Replace("giri   al minuto", "rpm"));
        Assert.NotEqual(a.All, b.All);
        Assert.Equal(a.Code, b.Code);
        Assert.NotEqual(a.Text, b.Text);
    }

    [Fact]
    public void ValoreIniziale()
    {
        BlockHashes a = H(Original), b = H(Original.Replace("<StartValue>25.0</StartValue>", "<StartValue>30.0</StartValue>"));
        Assert.Equal(a.Interface, b.Interface);
        Assert.NotEqual(a.Init, b.Init);
        Assert.Equal(a.Code, b.Code);
    }

    [Fact]
    public void Interfaccia()
    {
        BlockHashes a = H(Original), b = H(Original.Replace("Datatype=\"Real\"", "Datatype=\"LReal\""));
        Assert.NotEqual(a.Interface, b.Interface);
        Assert.Equal(a.Code, b.Code);
    }

    [Fact]
    public void NumeroFissoContaNeiMeta()
    {
        string fixedA = Original.Replace("<AutoNumber>true</AutoNumber>", "<AutoNumber>false</AutoNumber>");
        string fixedB = fixedA.Replace("<Number>50</Number>", "<Number>60</Number>");
        Assert.NotEqual(H(fixedA).Meta, H(fixedB).Meta);
    }

    [Fact]
    public void ArchiviatoSenzaDocumentInfo()
    {
        string stored = XmlCanonicalizer.StripDocumentInfo(Original);
        Assert.DoesNotContain("DocumentInfo", stored);
        Assert.Contains("<Blank Num=\"1\" UId=\"24\" />", stored);
        Assert.Equal(XmlCanonicalizer.StripDocumentInfo(Original), XmlCanonicalizer.StripDocumentInfo(Original.Replace("19:45:27", "08:00:00")));
    }
}
