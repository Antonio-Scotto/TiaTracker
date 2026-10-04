using System.Xml.Linq;

namespace TiaTracker.Core.Network;

/// <summary>
/// La topologia salvata da PRONETA in XML (Topology/DeviceCollection/Device):
/// nome di stazione leggibile, IP, maschera, gateway, MAC, ruolo, controller
/// (dalla connessione AR) e record I&amp;M (codice, numero di serie, firmware).
/// Il PC che ha fatto la scansione (PronetaPC) resta fuori.
/// </summary>
public static class PronetaXml
{
    public static bool LooksLikeProneta(string text)
    {
        string t = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        return t.StartsWith('<') && t.Contains("<Topology", StringComparison.Ordinal) && t.Contains("<DeviceCollection", StringComparison.Ordinal);
    }

    public static List<PronetaDevice> Parse(string xml)
    {
        XDocument doc = XDocument.Parse(xml);
        XElement? collection = doc.Root?.Element("DeviceCollection");
        if (collection == null)
        {
            return new List<PronetaDevice>();
        }

        List<PronetaDevice> devices = new();
        int n = 0;
        foreach (XElement d in collection.Elements("Device"))
        {
            n++;
            string? ip = Text(d, "IpAddress");
            string? gateway = Text(d, "GatewayIp");
            XElement? im = d.Element("ImRecord");
            string? controller = d.Element("ArData")?.Descendants("ReadableInitiatorStationName").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
            devices.Add(new PronetaDevice
            {
                Number = n,
                Name = Text(d, "ReadableNameOfStation") ?? Text(d, "NameOfStation") ?? "",
                DeviceType = Text(d, "DeviceType"),
                Ip = ip,
                Mask = Text(d, "NetworkMask"),
                Mac = Text(d, "MAC"),
                Role = Text(d, "Role"),
                IoController = controller,
                Vendor = Text(d, "ManufacturerName") ?? (im != null ? Text(im, "ManufacturerName") : null),
                OrderNumber = im != null ? Text(im, "OrderID") : null,
                Firmware = im != null ? Text(im, "SoftwareRevision") : null,
                SerialNumber = im != null ? Text(im, "SerialNumber") : null,
                // Senza router PROFINET ripete l'IP del dispositivo.
                Gateway = gateway == null || gateway == ip || gateway == "0.0.0.0" ? null : gateway,
            });
        }

        return devices;
    }

    private static string? Text(XElement e, string name)
    {
        string? v = e.Element(name)?.Value.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }
}

/// <summary>Un file di PRONETA, CSV o XML: si riconosce dal contenuto.</summary>
public static class PronetaFile
{
    public static bool LooksLikeProneta(string text) => PronetaXml.LooksLikeProneta(text) || PronetaCsv.LooksLikeProneta(text);

    public static List<PronetaDevice> Parse(string text) =>
        PronetaXml.LooksLikeProneta(text) ? PronetaXml.Parse(text) : PronetaCsv.Parse(text);
}
