using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using LinkerScriptGenerator;
using BSPEngine;

namespace stm32_bsp_generator
{
    interface IDeviceListProvider
    {
        List<MCUBuilder> LoadDeviceList(Program.STM32BSPBuilder bspBuilder);
    }


    static class DeviceListProviders
    {
        public class CSVProvider : IDeviceListProvider
        {
            public List<MCUBuilder> LoadDeviceList(Program.STM32BSPBuilder bspBuilder)
            {
                var devices = BSPGeneratorTools.ReadMCUDevicesFromCommaDelimitedCSVFile(bspBuilder.Directories.RulesDir + @"\stm32devices.csv", "Part Number", "FLASH Size (Prog)", "Internal RAM Size", "Core", true);
                var devicesOld = BSPGeneratorTools.ReadMCUDevicesFromCommaDelimitedCSVFile(bspBuilder.Directories.RulesDir + @"\stm32devicesOld.csv", "Part Number", "FLASH Size (Prog)", "Internal RAM Size", "Core", true);
                foreach (var d in devicesOld)
                    if (!devices.Contains(d))
                        devices.Add(d);
                return devices;
            }
        }

        public class DeviceMemoryDatabase
        {
            private XmlDocument _Document;

            int LocateZipSignature(byte[] data)
            {
                byte[] signature = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
                for (int offset = 0; offset < data.Length; offset++)
                {
                    bool found = true;
                    for (int j = 0; j < signature.Length; j++)
                    {
                        if (data[offset + j] != signature[j])
                        {
                            found = false;
                            break;
                        }
                    }

                    if (found)
                        return offset;
                }

                throw new Exception("Failed to locate a ZIP signature");
            }

            class Resolver : XmlResolver
            {
                private ZipFile _ZipFile;
                Dictionary<string, ZipFile.Entry> _Entries = new Dictionary<string, ZipFile.Entry>(StringComparer.InvariantCultureIgnoreCase);

                public Resolver(ZipFile zf)
                {
                    _ZipFile = zf;
                    foreach (var e in zf.Entries)
                        _Entries[Path.GetFileName(e.FileName)] = e;
                }

                public override object GetEntity(Uri absoluteUri, string role, Type ofObjectToReturn)
                {
                    var fn = Path.GetFileName(absoluteUri.ToString());
                    var ms = new MemoryStream();
                    _ZipFile.ExtractEntry(_Entries[fn], ms);
                    ms.Position = 0;
                    return ms;
                }
            }

            Dictionary<string, XmlElement> _DevicesByPNs = new Dictionary<string, XmlElement>();

            public DeviceMemoryDatabase(string STM32CubeDir)
            {
                var data = File.ReadAllBytes(Path.Combine(STM32CubeDir, "STM32CubeMX.exe"));
                int offset = LocateZipSignature(data);
                var zf = new ZipFile(new MemoryStream(data, offset, data.Length - offset) { Position = offset });
                MemoryStream output = null;
                foreach (var entry in zf.Entries)
                {
                    if (entry.FileName.Contains("stm32boards.db"))
                    {
                        output = new MemoryStream();
                        zf.ExtractEntry(entry, output);
                        break;
                    }
                }

                if (output == null)
                    throw new Exception("Could not locate STM32 device database");

                _Document = new XmlDocument();
                _Document.XmlResolver = new Resolver(zf);
                _Document.LoadXml(Encoding.UTF8.GetString(output.ToArray()));

                foreach (XmlElement el in _Document.DocumentElement.SelectNodes("family/subFamily/device"))
                {
                    foreach (var pn in el.SelectSingleNode("PN").InnerText.Split(','))
                    {
                        _DevicesByPNs[pn] = el;

                        foreach (var v in (el.SelectSingleNode("variants")?.InnerText ?? "").Split(','))
                            _DevicesByPNs[pn + v] = el;
                    }

                }
            }

            public struct RawMemory
            {
                public string Name;
                public uint Start, Size;

                public int SortWeight;

                public RawMemory(XmlElement n)
                {
                    SortWeight = 0;
                    Name = n.GetAttribute("name");
                    Start = Program.ParseHex(n.GetAttribute("start"));
                    Size = Program.ParseHex(n.GetAttribute("size"));
                }

                public override string ToString()
                {
                    return Name;
                }

                public Memory ToMemoryDefinition()
                {
                    MemoryType type;
                    if (Name.StartsWith("RAM_D"))
                        type = MemoryType.RAM;
                    else
                    {
                        switch (Name)
                        {
                            case "FLASH":
                            case "FLASH2":
                                type = MemoryType.FLASH;
                                break;
                            case "RAM":
                            case "RAM2":
                            case "CCMRAM":
                            case "DTCMRAM":
                            case "ITCMRAM":
                                type = MemoryType.RAM;
                                break;
                            default:
                                throw new Exception("Unknown memory type " + Name);
                        }
                    }

                    return new Memory { Name = (Name == "RAM") ? "SRAM" : Name, Start = Start, Size = Size * 1024, Type = type };
                }
            }

            internal RawMemory[] LookupMemories(string RPN)
            {
                var node = _DevicesByPNs[RPN];
                return node.SelectNodes("memories/memory").OfType<XmlElement>().Select(n => new RawMemory(n)).ToArray();
            }
        }

        public class CubeProvider : IDeviceListProvider
        {
            public class STM32MCUBuilder : MCUBuilder
            {
                private readonly ParsedMCU MCU;

                public readonly DeviceMemoryDatabase.RawMemory[] Memories;

                public STM32MCUBuilder(ParsedMCU parsedMCU, DeviceMemoryDatabase db, string specializedName)
                {
                    MCU = parsedMCU;
                    Memories = db.LookupMemories(specializedName ?? parsedMCU.RPN);
                    if (Memories.Length < 1)
                        throw new Exception("Could not locate memories for " + parsedMCU.Name);

                    for (int i = 0; i < Memories.Length; i++)
                    {
                        Memories[i].SortWeight = 100 + i;
                        if (Memories[i].Name == "FLASH")
                            Memories[i].SortWeight = 0;
                        else if (Memories[i].Name == "RAM")
                            Memories[i].SortWeight = 1;
                    }

                    Memories = Memories.OrderBy(m => m.SortWeight).ToArray();
                }

                public MemoryLayout ToMemoryLayout(bool patchMemoryNames)
                {
                    var layout = new MemoryLayout { DeviceName = Name, Memories = Memories.Select(m => m.ToMemoryDefinition()).ToList() };
                    if (patchMemoryNames)
                    {
                        if (layout.Memories.FirstOrDefault(m => m.Name == "SRAM") == null)
                        {
                            var ram1 = layout.Memories.First(m => m.Name == "RAM_D1");
                            ram1.Name = "SRAM";
                        }
                    }
                    return layout;
                }

                public override MCU GenerateDefinition(MCUFamilyBuilder fam, BSPBuilder bspBuilder, bool requirePeripheralRegisters, bool allowIncompleteDefinition = false, MCUFamilyBuilder.CoreSpecificFlags flagsToAdd = MCUFamilyBuilder.CoreSpecificFlags.All)
                {
                    var mcu = base.GenerateDefinition(fam, bspBuilder, requirePeripheralRegisters, allowIncompleteDefinition, flagsToAdd);

                    var layout = ToMemoryLayout(true);
                    var sram = layout.Memories.First(m => m.Name == "SRAM");

                    mcu.RAMBase = sram.Start;
                    mcu.RAMSize = (int)sram.Size;

                    mcu.MemoryMap = new AdvancedMemoryMap
                    {
                        Memories = layout.Memories.Select(MakeMCUMemory).ToArray()
                    };

                    return mcu;
                }

                private MCUMemory MakeMCUMemory(Memory arg)
                {
                    var mem = new MCUMemory
                    {
                        Address = arg.Start,
                        Size = arg.Size,
                        Name = arg.Name,
                    };

                    if (arg.Name == "FLASH")
                        mem.Flags |= MCUMemoryFlags.IsDefaultFLASH;
                    else
                        mem.LoadedFromMemory = "FLASH";

                    return mem;
                }
            }

            public struct ParsedMCU
            {
                public string Name, RefName, RPN;

                public CortexCore Core;
                public readonly DeviceMemoryDatabase.RawMemory[] Memories;
                public int[] RAMs;
                public int FLASH;

                static XmlElement LoadMCUDefinition(string familyDir, string name)
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load($@"{familyDir}\{name}.xml");
                    return doc.DocumentElement;
                }

                public ParsedMCU(XmlElement n, string familyDir, DeviceMemoryDatabase db)
                {
                    Name = n.GetAttribute("Name");
                    RefName = n.GetAttribute("RefName");
                    RPN = n.GetAttribute("RPN");

                    //var mcuDef = LoadMCUDefinition(familyDir, Name);
                    //var nsmgr2 = new XmlNamespaceManager(mcuDef.OwnerDocument.NameTable);
                    //nsmgr2.AddNamespace("mcu", "http://mcd.rou.st.com/modules.php?name=mcu");

                    var core = n.SelectSingleNode("Core").InnerText;
                    switch (core)
                    {
                        case "ARM Cortex-M0":
                            Core = CortexCore.M0;
                            break;
                        case "ARM Cortex-M0+":
                            Core = CortexCore.M0Plus;
                            break;
                        case "ARM Cortex-M3":
                            Core = CortexCore.M3;
                            break;
                        case "ARM Cortex-M4":
                            Core = CortexCore.M4;
                            break;
                        case "ARM Cortex-M7":
                            Core = CortexCore.M7;
                            break;
                        default:
                            throw new Exception("Don't know how to map core: " + core);
                    }

                    Memories = db.LookupMemories(RefName);

                    //RAMs = mcuDef.SelectNodes("mcu:Ram", nsmgr2).OfType<XmlElement>().Select(n2 => int.Parse(n2.InnerText)).ToArray();
                    RAMs = n.SelectNodes("Ram").OfType<XmlElement>().Select(n2 => int.Parse(n2.InnerText)).ToArray();
                    if (RAMs.Length < 1)
                        throw new Exception("No RAMs defined for " + Name);

                    var flash = n.SelectNodes("Flash").OfType<XmlElement>().Select(n2 => int.Parse(n2.InnerText)).ToArray();
                    if (flash.Length != 1)
                        throw new Exception("Multiple or missing FLASH definitions of " + Name);
                    FLASH = flash[0];
                }

                public override string ToString()
                {
                    return Name;
                }

                public MCUBuilder ToMCUBuilder(DeviceMemoryDatabase db, string nameOverride = null)
                {
                    return new STM32MCUBuilder(this, db, nameOverride)
                    {
                        Name = nameOverride ?? RPN,
                        FlashSize = FLASH * 1024,
                        RAMSize = 0,    //This will be adjusted later in our override of GenerateDefinition()
                        Core = Core
                    };
                }

                public ConfigSnapshot Config => new ConfigSnapshot { FLASH = FLASH, RAMs = string.Join("|", Memories.Select(r => $"{r.Name}={r.Size}").ToArray()) };
            }

            //MCUs with the same value of ConfigSnapshot can use the same linker script, MCU definition, etc
            public struct ConfigSnapshot
            {
                public int FLASH;
                public string RAMs; //Using this instead of int[] saves us the pain of redefining GetHashCode()/Equals() used by GroupBy().
            }

            public List<MCUBuilder> LoadDeviceList(Program.STM32BSPBuilder bspBuilder)
            {
                List<MCUBuilder> result = new List<MCUBuilder>();
                XmlDocument doc = new XmlDocument();
                string familyDir = Path.Combine(bspBuilder.STM32CubeDir, @"db\mcu");
                var db = new DeviceMemoryDatabase(bspBuilder.STM32CubeDir);

                doc.Load(Path.Combine(familyDir, @"families.xml"));
                var rawMCUs = doc.DocumentElement.SelectNodes("Family/SubFamily/Mcu").OfType<XmlElement>().Select(n => new ParsedMCU(n, familyDir, db)).ToArray();

                foreach (var grp in rawMCUs.GroupBy(m => m.RPN))
                {
                    //As of November 2017, some MCUs in sharing the same RPN (namely STM32F103RC) have different FLASH/RAM sizes.
                    //We need to detect this and create multiple MCU entries for them, e.g. STM32F103RCTx and STM32F103RCYx
                    var mcusByConfig = grp.GroupBy(m => m.Config).ToArray();
                    if (mcusByConfig.Length == 1)
                    {
                        result.Add(mcusByConfig.First().First().ToMCUBuilder(db));
                    }
                    else
                    {
                        foreach (var subGrp in mcusByConfig)
                        {
                            result.Add(subGrp.First().ToMCUBuilder(db, subGrp.First().RefName));
                        }
                    }
                }

                return result;
            }
        }
    }
}
