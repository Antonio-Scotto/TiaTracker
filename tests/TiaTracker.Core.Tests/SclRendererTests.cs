using TiaTracker.Core.Snapshots;

namespace TiaTracker.Core.Tests;

public class SclRendererTests
{
    private const string Iface = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
    private const string St = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4";
    private const string Flg = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";

    private static string Doc(string block) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><Document><Engineering version=\"V21\" /><DocumentInfo /> " + block + "</Document>";

    private static string Trimmed(string text) =>
        string.Join("\n", text.Replace("\r", "").Split('\n').Select(l => l.TrimEnd(' ')));

    private static string Unit(string language, string source, string title = "") =>
        $"""
        <SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits">
          <AttributeList><NetworkSource>{source}</NetworkSource><ProgrammingLanguage>{language}</ProgrammingLanguage></AttributeList>
          <ObjectList>
            <MultilingualText ID="6" CompositionName="Title"><ObjectList><MultilingualTextItem ID="7" CompositionName="Items">
              <AttributeList><Culture>it-IT</Culture><Text>{title}</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
          </ObjectList>
        </SW.Blocks.CompileUnit>
        """;

    [Fact]
    public void FbScl_InterfacciaECodice()
    {
        string xml = Doc($"""
            <SW.Blocks.FB ID="0"><AttributeList>
              <HeaderVersion>0.2</HeaderVersion>
              <Interface><Sections xmlns="{Iface}">
                <Section Name="Input">
                  <Member Name="Start" Datatype="Bool"><Comment><MultiLanguageText Lang="it-IT">Avvio</MultiLanguageText></Comment></Member>
                </Section>
                <Section Name="Output"><Member Name="Run" Datatype="Bool" /></Section>
                <Section Name="Static">
                  <Member Name="Edge" Datatype="R_TRIG" Version="1.0" />
                  <Member Name="Cfg" Datatype="Struct">
                    <Member Name="Delay" Datatype="Time"><StartValue>T#2S</StartValue></Member>
                  </Member>
                  <Member Name="Count" Datatype="Int" Remanence="Retain" />
                </Section>
                <Section Name="Temp" />
              </Sections></Interface>
              <MemoryLayout>Optimized</MemoryLayout>
              <Name>DEMO_FB</Name>
              <ProgrammingLanguage>SCL</ProgrammingLanguage>
            </AttributeList><ObjectList>
            {Unit("SCL", $"""
                <StructuredText xmlns="{St}">
                  <Access Scope="LocalVariable"><Symbol><Component Name="Run" /></Symbol></Access>
                  <Blank /><Token Text=":=" /><Blank />
                  <Access Scope="LocalVariable"><Symbol><Component Name="Start" /></Symbol></Access>
                  <Blank /><Token Text="AND" /><Blank />
                  <Access Scope="GlobalVariable"><Symbol>
                    <Component Name="Demo_DB"><BooleanAttribute Name="HasQuotes">true</BooleanAttribute></Component>
                    <Token Text="." /><Component Name="enable" />
                  </Symbol></Access>
                  <Token Text=";" /><NewLine />
                  <LineComment><Text> fine</Text></LineComment>
                </StructuredText>
                """)}
            </ObjectList></SW.Blocks.FB>
            """);

        string scl = SclRenderer.FromXml(xml)!;

        // TIA scrive "VAR_INPUT " con lo spazio in fondo: il confronto ignora gli spazi di fine riga.
        Assert.Equal(Trimmed("""
            FUNCTION_BLOCK "DEMO_FB"
            { S7_Optimized_Access := 'TRUE' }
            VERSION : 0.2
               VAR_INPUT
                  Start : Bool;   // Avvio
               END_VAR

               VAR_OUTPUT
                  Run : Bool;
               END_VAR

               VAR
                  Edge {InstructionName := 'R_TRIG'; LibVersion := '1.0'} : R_TRIG;
                  Cfg : Struct
                     Delay : Time := T#2S;
                  END_STRUCT;
               END_VAR
               VAR RETAIN
                  Count : Int;
               END_VAR


            BEGIN
            	#Run := #Start AND "Demo_DB".enable;
            	// fine
            END_FUNCTION_BLOCK

            """), Trimmed(scl));
    }

    [Fact]
    public void ReteLad_ContattiERamiDiventanoEspressioni()
    {
        // Rete: ( "A" AND NOT "B" ) OR #C  ->  bobina "Q"; poi "Pump" abilitato da "Q" via box con istanza.
        string net = $"""
            <FlgNet xmlns="{Flg}"><Parts>
              <Access Scope="GlobalVariable" UId="21"><Symbol><Component Name="A" /></Symbol></Access>
              <Access Scope="GlobalVariable" UId="22"><Symbol><Component Name="B" /></Symbol></Access>
              <Access Scope="LocalVariable" UId="23"><Symbol><Component Name="C" /></Symbol></Access>
              <Access Scope="GlobalVariable" UId="24"><Symbol><Component Name="Q" /></Symbol></Access>
              <Access Scope="GlobalVariable" UId="25"><Symbol><Component Name="Speed" /></Symbol></Access>
              <Part Name="Contact" UId="30" />
              <Part Name="Contact" UId="31"><Negated Name="operand" /></Part>
              <Part Name="Contact" UId="32" />
              <Part Name="O" UId="33"><TemplateValue Name="Card" Type="Cardinality">2</TemplateValue></Part>
              <Part Name="Coil" UId="34" />
              <Call UId="35"><CallInfo Name="Pump_FB" BlockType="FB">
                <Instance Scope="GlobalVariable" UId="36"><Component Name="Pump_IDB" /></Instance>
              </CallInfo></Call>
            </Parts><Wires>
              <Wire UId="40"><Powerrail /><NameCon UId="30" Name="in" /><NameCon UId="32" Name="in" /></Wire>
              <Wire UId="41"><IdentCon UId="21" /><NameCon UId="30" Name="operand" /></Wire>
              <Wire UId="42"><NameCon UId="30" Name="out" /><NameCon UId="31" Name="in" /></Wire>
              <Wire UId="43"><IdentCon UId="22" /><NameCon UId="31" Name="operand" /></Wire>
              <Wire UId="44"><IdentCon UId="23" /><NameCon UId="32" Name="operand" /></Wire>
              <Wire UId="45"><NameCon UId="31" Name="out" /><NameCon UId="33" Name="in1" /></Wire>
              <Wire UId="46"><NameCon UId="32" Name="out" /><NameCon UId="33" Name="in2" /></Wire>
              <Wire UId="47"><NameCon UId="33" Name="out" /><NameCon UId="34" Name="in" /></Wire>
              <Wire UId="48"><IdentCon UId="24" /><NameCon UId="34" Name="operand" /></Wire>
              <Wire UId="49"><NameCon UId="34" Name="out" /><NameCon UId="35" Name="en" /></Wire>
              <Wire UId="50"><IdentCon UId="25" /><NameCon UId="35" Name="Setpoint" /></Wire>
              <Wire UId="51"><OpenCon UId="52" /><NameCon UId="35" Name="Mode" /></Wire>
            </Wires></FlgNet>
            """;
        string xml = Doc($"""
            <SW.Blocks.FC ID="0"><AttributeList>
              <Interface><Sections xmlns="{Iface}"><Section Name="Return"><Member Name="Ret_Val" Datatype="Void" /></Section></Sections></Interface>
              <MemoryLayout>Optimized</MemoryLayout><Name>LAD_FC</Name><ProgrammingLanguage>LAD</ProgrammingLanguage>
            </AttributeList><ObjectList>{Unit("LAD", net, "Pompa")}{Unit("LAD", "")}</ObjectList></SW.Blocks.FC>
            """);

        string scl = SclRenderer.FromXml(xml)!;

        Assert.StartsWith("FUNCTION \"LAD_FC\" : Void\n", scl);
        Assert.Contains("\t// ---- Rete 1: Pompa (LAD, tradotta in SCL) ----\n", scl);
        Assert.Contains("\t\"Q\" := \"A\" AND NOT \"B\" OR #C;\n", scl);
        Assert.Contains("\tIF \"A\" AND NOT \"B\" OR #C THEN\n\t   \"Pump_IDB\"(Setpoint := \"Speed\");\n\tEND_IF;\n", scl);
        Assert.Contains("\t// ---- Rete 2 (LAD, tradotta in SCL) ----\n\t// rete vuota\n", scl);
        Assert.EndsWith("END_FUNCTION\n", scl);
    }

    [Fact]
    public void DbDiIstanza_RiferimentoAlFbEInterfaccia()
    {
        string xml = Doc($"""
            <SW.Blocks.InstanceDB ID="0"><AttributeList>
              <HeaderVersion>0.1</HeaderVersion><InstanceOfName>Pump_FB</InstanceOfName><InstanceOfType>FB</InstanceOfType>
              <Interface><Sections xmlns="{Iface}">
                <Section Name="Input"><Member Name="Setpoint" Datatype="Real"><StartValue>12.5</StartValue></Member></Section>
                <Section Name="Temp"><Member Name="tmp" Datatype="Int" /></Section>
              </Sections></Interface>
              <Name>Pump_IDB</Name><ProgrammingLanguage>DB</ProgrammingLanguage>
            </AttributeList></SW.Blocks.InstanceDB>
            """);

        string scl = SclRenderer.FromXml(xml)!;

        Assert.StartsWith("DATA_BLOCK \"Pump_IDB\"\nVERSION : 0.1\nNON_RETAIN\n\"Pump_FB\"\n", scl);
        Assert.Contains("      Setpoint : Real := 12.5;\n", scl);
        Assert.DoesNotContain("tmp", scl);
        Assert.EndsWith("BEGIN\n\nEND_DATA_BLOCK\n", scl);
    }

    [Fact]
    public void XmlNonValidoONonUnBlocco()
    {
        Assert.Null(SclRenderer.FromXml("non xml"));
        Assert.Null(SclRenderer.FromXml("<Document><Engineering /><SW.Tags.PlcTagTable ID=\"0\" /></Document>"));
        Assert.Null(SclRenderer.FromXml(null));
    }
}
