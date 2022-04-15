using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace renesas_ra_bsp_generator
{
    class RenesasDeviceDatabase
    {
        public struct DeviceVariant
        {
            public string Name;
            public string MemoryRegionsText;
            public PinConfigurationTranslator.DevicePinout Pinout;

            public override string ToString() => Name;
        }

        public class ParsedDevice
        {
            public string Name;
            public CortexCore Core;
            public List<DeviceVariant> Variants = new List<DeviceVariant>();
            public string FamilyName;
            public MemoryArea[] MemoryMap;
            public MCUDefinition HardwareRegisters;
            
            public override string ToString() => Name;

            public string FinalMCUName => Name;

            public string UniqueMemoryRegionsDefinition => Variants.Select(v => v.MemoryRegionsText).Distinct().Single();
        }

        public static CortexCore ParseCortexCore(string core)
        {
            if (!core.StartsWith("cortex-", StringComparison.InvariantCultureIgnoreCase))
                throw new Exception("Unexpected core: " + core);

            return (CortexCore)Enum.Parse(typeof(CortexCore), core.Substring(7), true);
        }

        public struct MemoryArea
        {
            public string Type;
            public ulong Start, End;
            public ulong Size => End - Start;
        }

        public static ParsedDevice[] DiscoverDevices(string packDirectory)
        {
            Dictionary<string, ParsedDevice> result = new Dictionary<string, ParsedDevice>();
            foreach (var fn in Directory.GetFiles(packDirectory, "Renesas.RA_mcu_*.pack"))
            {
                using (var zf = ZipFile.Open(fn))
                {
                    Dictionary<string, MemoryArea[]> deviceMemories = LoadDeviceMemories(zf);

                    var pgrFiles = zf.Entries.Where(e => Path.GetFileName(e.FileName).StartsWith("pgr") && e.FileName.EndsWith(".xmi")).ToArray();
                    if (pgrFiles.Length != 1)
                        throw new Exception("Unexpected number of device list files");

                    Dictionary<string, ZipFile.Entry> rzoneFiles = new Dictionary<string, ZipFile.Entry>(StringComparer.InvariantCultureIgnoreCase);
                    Dictionary<string, ZipFile.Entry> pincfgFiles = new Dictionary<string, ZipFile.Entry>(StringComparer.InvariantCultureIgnoreCase);
                    Dictionary<string, ZipFile.Entry> sfrxFiles = new Dictionary<string, ZipFile.Entry>(StringComparer.InvariantCultureIgnoreCase);
                    Dictionary<string, PinConfigurationTranslator.DevicePinout> knownPinouts = new Dictionary<string, PinConfigurationTranslator.DevicePinout>();

                    foreach (var e in zf.Entries)
                    {
                        if (e.FileName.EndsWith(".rzone", StringComparison.InvariantCultureIgnoreCase))
                            rzoneFiles[Path.GetFileNameWithoutExtension(e.FileName)] = e;
                        else if (e.FileName.EndsWith(".pincfg", StringComparison.InvariantCultureIgnoreCase))
                            pincfgFiles[Path.GetFileNameWithoutExtension(e.FileName)] = e;
                        else if (e.FileName.EndsWith(".sfrx", StringComparison.InvariantCultureIgnoreCase))
                            sfrxFiles[Path.GetFileNameWithoutExtension(e.FileName)] = e;
                        else if (e.FileName.IndexOf(".pinmapping/PinCfg", StringComparison.InvariantCultureIgnoreCase) != -1 && e.FileName.EndsWith(".xml", StringComparison.InvariantCultureIgnoreCase))
                        {
                            var pinout = PinConfigurationTranslator.ParseDevicePinout(zf.ExtractEntry(e));
                            knownPinouts[pinout.ID] = pinout;
                        }
                    }

                    var xml = new XmlDocument();
                    xml.LoadXml(Encoding.UTF8.GetString(zf.ExtractEntry(pgrFiles[0])));

                    foreach (var target in xml.DocumentElement.SelectNodes("Target").OfType<XmlElement>())
                    {
                        var targetName = target.GetAttribute("shortName");
                        foreach (var cpu in target.SelectNodes("CpuType/Cpu").OfType<XmlElement>())
                        {
                            var familyName = cpu.GetAttribute("shortName");
                            var parsedCore = ParseCortexCore(cpu.GetAttribute("compilerCpuType"));

                            foreach (var dev in cpu.SelectNodes("PinCountRZTC/Device").OfType<XmlElement>())
                            {
                                var fullName = dev.GetAttribute("shortName");
                                var shortName = dev.GetAttribute("DeviceCommand");

                                if (!result.TryGetValue(shortName, out var devObj))
                                {
                                    result[shortName] = devObj = new ParsedDevice
                                    {
                                        Name = shortName,
                                        Core = parsedCore,
                                        FamilyName = familyName,
                                        MemoryMap = deviceMemories[fullName],
                                        HardwareRegisters = ParseSFRXFile(zf.ExtractXMLFile(sfrxFiles[shortName]), shortName)
                                    };
                                }

                                devObj.Variants.Add(BuildDeviceVariant(fullName, zf.ExtractEntry(rzoneFiles[fullName]), zf.ExtractEntry(pincfgFiles[fullName]), knownPinouts));

                                if (deviceMemories.TryGetValue(fullName, out var thisMap) && !Enumerable.SequenceEqual(thisMap, devObj.MemoryMap))
                                    throw new Exception($"Inconistent memory maps for {devObj.Variants[0]}/{fullName}");
                            }
                        }
                    }
                }
            }

            return result.Values.ToArray();
        }

        private static MCUDefinition ParseSFRXFile(XmlDocument doc, string mcuName)
        {
            return new MCUDefinition
            {
                MCUName = mcuName,
                RegisterSets = doc.DocumentElement.SelectNodes("moduletable/module").OfType<XmlElement>().Select(TransformRegisterSet).Where(s => s != null).ToArray()
            };
        }

        public static DeviceVariant BuildDeviceVariant(string deviceName, byte[] rzoneFileContents, byte[] pincfgFileContents, Dictionary<string, PinConfigurationTranslator.DevicePinout> knownPinouts)
        {
            var xml = new XmlDocument();
            xml.LoadXml(Encoding.UTF8.GetString(rzoneFileContents));
            StringBuilder memoryRegionsFile = new StringBuilder();
            foreach (var mem in xml.DocumentElement.SelectElements("resources/memories/memory"))
            {
                var name = mem.GetStringAttribute("name");
                var size = mem.GetUlongAttribute("size");
                var start = mem.GetUlongAttribute("start");

                memoryRegionsFile.AppendLine($"{name}_START = 0x{start:x8};");
                memoryRegionsFile.AppendLine($"{name}_LENGTH = 0x{size:x};");
            }

            var xmlPinfg = new XmlDocument();
            xmlPinfg.LoadXml(Encoding.UTF8.GetString(pincfgFileContents));
            var nsmgr = new XmlNamespaceManager(xmlPinfg.NameTable);
            nsmgr.AddNamespace("v1", "http://www.tasking.com/schema/pinsettings/v1.1");
            var dev = xmlPinfg.DocumentElement.SelectSingleNode("v1:deviceSetting/@id", nsmgr)?.InnerXml ?? throw new Exception("Failed to locate the pinout ID");

            return new DeviceVariant { Name = deviceName, MemoryRegionsText = memoryRegionsFile.ToString(), Pinout = knownPinouts[dev] };
        }

        private static Dictionary<string, MemoryArea[]> LoadDeviceMemories(DisposableZipFile zf)
        {
            Dictionary<string, MemoryArea[]> result = new Dictionary<string, MemoryArea[]>();
            foreach (var e in zf.Entries)
            {
                var fn = Path.GetFileName(e.FileName).ToLower();
                if (fn.StartsWith("memory") && fn.EndsWith(".xmi"))
                {
                    var xml = new XmlDocument();
                    xml.LoadXml(Encoding.UTF8.GetString(zf.ExtractEntry(e)));

                    var map = xml.DocumentElement.SelectElements("memoryMap").Single();
                    result[map.GetAttribute("device")] = map.SelectElements("memoryArea")
                        .Select(a => new MemoryArea
                        {
                            Type = a.GetAttribute("type"),
                            Start = a.GetUlongAttribute("startAddress"),
                            End = a.GetUlongAttribute("endAddress"),
                        }).ToArray();
                }
            }
            return result;
        }

        #region Peripheral registers

        private static HardwareRegisterSet TransformRegisterSet(XmlElement el)
        {
            var name = el.GetAttribute("name");
            if (name == null)
                return null;

            var set =  new HardwareRegisterSet
            {
                UserFriendlyName = name,
                Registers = el.SelectNodes("register").OfType<XmlElement>().SelectMany(r => new[] { r }.Concat(r.SelectNodes("register").OfType<XmlElement>())).Select(r =>
                {
                    var regSize = r.GetAttribute("size");
                    var regAccess = r.GetAttribute("access");

                    var reg = new HardwareRegister { Name = r.GetAttribute("name"), Address = r.GetAttribute("address") };
                    if (reg.Name == null || reg.Address == null)
                        return null;

                    switch (regSize ?? "")
                    {
                        case "B":
                            reg.SizeInBits = 8;
                            break;
                        case "W":
                            reg.SizeInBits = 16;
                            break;
                        case "LW":
                            reg.SizeInBits = 32;
                            break;
                        default:
                            return null;
                    }

                    switch (regAccess)
                    {
                        case "R":
                            reg.ReadOnly = true;
                            break;
                        case "RW":
                            reg.ReadOnly = false;
                            break;
                        default:
                            return null;
                    }

                    reg.SubRegisters = r.SelectNodes("bitfield").OfType<XmlElement>().Select(TransformSubregister).Where(sr => sr != null).ToArray();
                    return reg;
                }).Where(r => r != null).ToArray()
            };

            return set;
        }

        private static HardwareSubRegister TransformSubregister(XmlElement el)
        {
            string name = el.GetAttribute("name");
            if (!int.TryParse(el.GetAttribute("bit"), out int bit))
                return null;
            if (!int.TryParse(el.GetAttribute("bitlength"), out int bitlength))
                return null;

            return new HardwareSubRegister { FirstBit = bit, SizeInBits = bitlength, Name = name };
        }
        #endregion
    }
}
