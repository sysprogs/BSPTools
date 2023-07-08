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
using System.Text.RegularExpressions;

namespace stm32_bsp_generator
{
    interface IDeviceListProvider
    {
        List<MCUBuilder> LoadDeviceList(Program.STM32BSPBuilder bspBuilder);
    }


    static class DeviceListProviders
    {
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

                    if (false)
                    {
                        ms.Position = 0;
                        byte[] data = new byte[ms.Length];
                        ms.Read(data, 0, data.Length);
                        File.WriteAllBytes(Path.Combine(@"e:\temp", Path.GetFileName(absoluteUri.AbsolutePath)), data);
                    }

                    ms.Position = 0;
                    return ms;
                }
            }

            Dictionary<string, XmlElement> _DevicesBySpecializedName = new Dictionary<string, XmlElement>();
            public readonly long STM32CubeTimestamp;

            public DeviceMemoryDatabase(string STM32CubeDir)
            {
                var fn = Path.Combine(STM32CubeDir, "STM32CubeMX.exe");
                STM32CubeTimestamp = File.GetLastWriteTimeUtc(fn).ToFileTime();
                var data = File.ReadAllBytes(fn);
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
                    var pnArray = el.SelectSingleNode("PN").InnerText.Split(',');
                    //Contains IDs for the following toolchains: EWARM, MDK-ARM, TrueSTUDIO, RIDE, TASKING, SW4STM32.
                    var pn = pnArray[5];

                    foreach (var v in (el.SelectSingleNode("variants")?.InnerText ?? "").Split(','))
                        _DevicesBySpecializedName[pn + v] = el;
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

                static Regex rgFLASH = new Regex("^FLASH[0-9]*$");
                static Regex rgRAM = new Regex("^(S?RAM[0-9]*|.*RAM)(|_D[0-9]+)$");

                public Memory ToMemoryDefinition()
                {
                    MemoryType type;
                    if (rgFLASH.IsMatch(Name))
                        type = MemoryType.FLASH;
                    else if (rgRAM.IsMatch(Name))
                        type = MemoryType.RAM;
                    else
                        throw new Exception("Unknown memory type " + Name);

                    return new Memory { Name = (Name == "RAM") ? "SRAM" : Name, Start = Start, Size = Size * 1024, Type = type };
                }
            }

            internal RawMemory[] LookupMemories(string RPN, string RefName, string explicitCore, out string[] linkerScripts, out string define, out string fpu)
            {
                XmlElement node;
                if (!_DevicesBySpecializedName.TryGetValue(RefName, out node))
                    throw new Exception("Could not find memory layout for " + RefName);

                linkerScripts = node.SelectNodes("SW4STM32/linkers/linker").OfType<XmlElement>().Select(n => n.InnerText).ToArray();
                fpu = ((XmlElement)node.ParentNode).GetAttribute("fpu");

                var memories = node.SelectNodes("memories/memory").OfType<XmlElement>().Select(n => new RawMemory(n)).ToArray();
                if (memories.Length == 0 && explicitCore != null)
                    memories = node.SelectNodes($"memories/C{explicitCore}/memory").OfType<XmlElement>().Select(n => new RawMemory(n)).ToArray();

                if (memories.Length == 0)
                    throw new Exception("Missing memories for " + RefName);

                define = node.SelectSingleNode("define")?.InnerText ?? node.ParentNode.SelectSingleNode("define")?.InnerText ?? throw new Exception("Unknown preprocessor macro for " + RPN);
                return memories;
            }
        }

        public class CubeProvider : IDeviceListProvider
        {
            public class STM32MCUBuilder : MCUBuilder
            {
                public readonly ParsedMCU MCU;
                public string[] PredefinedLinkerScripts => MCU.LinkerScripts;
                public readonly DeviceMemoryDatabase.RawMemory[] Memories;

                public STM32MCUBuilder(ParsedMCU parsedMCU, DeviceMemoryDatabase db)
                {
                    MCU = parsedMCU;

                    Memories = parsedMCU.Memories.ToArray();
                    if (Memories.Length == 0)
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

                public MemoryLayoutAndSubstitutionRules ToMemoryLayout(BSPReportWriter report)
                {
                    var layout = new MemoryLayout { DeviceName = Name, Memories = Memories.Select(m => m.ToMemoryDefinition()).ToList() };

                    const string FLASHMemoryName = "FLASH";
                    const string FLASHMemoryName2 = "FLASH1";

                    Memory ram;
                    Dictionary<string, string> memorySubstitutionRulesForRAMMode = null;

                    if (MCU.Name.StartsWith("STM32H7") && MCU.Name.EndsWith("M4"))
                        ram = layout.TryLocateAndMarkPrimaryMemory(MemoryType.RAM, MemoryLocationRule.ByAddress(0x30000000), MemoryLocationRule.ByName("RAM_D2"));
                    else if (MCU.Name.StartsWith("STM32MP1"))
                        ram = layout.TryLocateAndMarkPrimaryMemory(MemoryType.RAM, MemoryLocationRule.ByName("RAM1"));
                    else if (MCU.Name.StartsWith("STM32WL"))
                        ram = layout.TryLocateAndMarkPrimaryMemory(MemoryType.RAM, MemoryLocationRule.ByName("SRAM", "SRAM1")) ?? layout.TryLocateOnlyMemory(MemoryType.RAM, true);
                    else
                        ram = layout.TryLocateAndMarkPrimaryMemory(MemoryType.RAM, MemoryLocationRule.ByAddress(0x20000000));

                    if (MCU.Name.StartsWith("STM32H7") && !MCU.Name.EndsWith("M4"))
                    {
                        //STM32H7 system file expects the ISR to be located at address 0x24000000  (D1_AXISRAM_BASE) and not at 0x20000000.
                        var mainMemoryForRAMMode = layout.TryLocateMemory(MemoryType.RAM, MemoryLocationRule.ByAddress(0x24000000), MemoryLocationRule.ByName("RAM_D2")) ?? throw new Exception("Failed to locate main memory for RAM mode");

                        memorySubstitutionRulesForRAMMode = new Dictionary<string, string> {
                            { FLASHMemoryName, mainMemoryForRAMMode.Name } ,
                            { FLASHMemoryName2, mainMemoryForRAMMode.Name } ,
                            { ram.Name, mainMemoryForRAMMode.Name } ,
                        };
                    }

                    if (ram == null)
                        report.ReportMergeableError("Could not locate primary RAM for the MCU(s)", MCU.Name, true);

                    if (layout.TryLocateAndMarkPrimaryMemory(MemoryType.FLASH, MemoryLocationRule.ByName(FLASHMemoryName, FLASHMemoryName2)) == null)
                        throw new Exception("No FLASH found");

                    return new MemoryLayoutAndSubstitutionRules(layout, memorySubstitutionRulesForRAMMode);
                }

                public override MCU GenerateDefinition(MCUFamilyBuilder fam, BSPBuilder bspBuilder, bool requirePeripheralRegisters, bool allowIncompleteDefinition = false, MCUFamilyBuilder.CoreSpecificFlags flagsToAdd = MCUFamilyBuilder.CoreSpecificFlags.All)
                {
                    var mcu = base.GenerateDefinition(fam, bspBuilder, requirePeripheralRegisters, allowIncompleteDefinition, flagsToAdd);

                    var layout = ToMemoryLayout(fam.BSP.Report);
                    var sram = layout.Layout.Memories.FirstOrDefault(m => m.Type == MemoryType.RAM && m.IsPrimary);

                    if (sram != null)
                    {
                        mcu.RAMBase = sram.Start;
                        mcu.RAMSize = (int)sram.Size;
                    }

                    mcu.MemoryMap = layout.Layout.ToMemoryMap();
                    mcu.AdditionalSystemVars = LoadedBSP.Combine(new[] { new SysVarEntry { Key = "com.sysprogs.stm32.hal_device_family", Value = MCU.Define } }, mcu.AdditionalSystemVars);

                    return mcu;
                }
            }

            public struct ParsedMCU
            {
                public readonly string Name;    //Generic name, may contain brackets (e.g. STM32F031C(4-6)Tx)
                public readonly string RefName; //Specialized name (e.g. STM32F031C4Tx)
                public readonly string RPN;     //Short name (e.g.STM32F031C4)

                public readonly string[] LinkerScripts;

                public readonly CortexCore Core;
                public readonly FPUType FPU;
                public readonly string CoreSuffix;
                public readonly DeviceMemoryDatabase.RawMemory[] Memories;

                public bool IsMultiCore => CoreSuffix != null;

                public readonly int[] RAMs;
                public readonly int FLASH;

                public readonly string Define;

                static XmlElement LoadMCUDefinition(string familyDir, string name)
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load($@"{familyDir}\{name}.xml");
                    return doc.DocumentElement;
                }

                static CortexCore ParseCore(string coreText)
                {
                    switch (coreText)
                    {
                        case "M0":
                            return CortexCore.M0;
                        case "M0+":
                            return CortexCore.M0Plus;
                        case "M3":
                            return CortexCore.M3;
                        case "M33":
                            return CortexCore.M33;
                        case "M4":
                            return CortexCore.M4;
                        case "M7":
                            return CortexCore.M7;
                        case "A7":
                            return CortexCore.A7;
                        default:
                            throw new Exception("Don't know how to map core: " + coreText);
                    }
                }

                public ParsedMCU(XmlElement n, string familyDir, DeviceMemoryDatabase db, string[] cores, int coreIndex)
                {
                    Name = n.GetAttribute("Name");
                    RefName = n.GetAttribute("RefName");
                    RPN = n.GetAttribute("RPN");

                    const string prefix = "Arm Cortex-";
                    if (!cores[coreIndex].StartsWith(prefix, StringComparison.InvariantCultureIgnoreCase))
                        throw new Exception("Unknown core: " + cores[coreIndex]);

                    string shortCore = cores[coreIndex].Substring(prefix.Length);
                    Core = ParseCore(shortCore);

                    if (cores.Length > 1)
                    {
                        if (shortCore == "M0+")
                            CoreSuffix = "M0PLUS";
                        else
                            CoreSuffix = shortCore.Replace('+', 'p');

                        if (coreIndex > 0)
                        {
                            //Secondary core uses a special name.
                            Name += "_" + CoreSuffix;
                            RPN += "_" + CoreSuffix;
                        }
                    }
                    else
                        CoreSuffix = null;

                    Memories = db.LookupMemories(RPN, RefName, CoreSuffix, out LinkerScripts, out Define, out string fpu);

                    FPU = ParseFPU(fpu);

                    if (cores.Length > 1 && coreIndex > 0 && Core == CortexCore.M4)
                        FPU = FPUType.SP;

                    //RAMs = mcuDef.SelectNodes("mcu:Ram", nsmgr2).OfType<XmlElement>().Select(n2 => int.Parse(n2.InnerText)).ToArray();
                    RAMs = n.SelectNodes("Ram").OfType<XmlElement>().Select(n2 => int.Parse(n2.InnerText)).ToArray();
                    if (RAMs.Length < 1)
                        throw new Exception("No RAMs defined for " + Name);

                    var flash = n.SelectNodes("Flash").OfType<XmlElement>().Select(n2 => int.Parse(n2.InnerText)).ToArray();
                    if (flash.Length != 1)
                        throw new Exception("Multiple or missing FLASH definitions of " + Name);
                    FLASH = flash[0];
                }

                static FPUType ParseFPU(string fpuString)
                {
                    switch (fpuString)
                    {
                        case "SP":
                            return FPUType.SP;
                        case "DP":
                            return FPUType.DP;
                        case "none":
                        case "None":
                            return FPUType.None;
                        default:
                            throw new Exception("Unknown FPU type: " + fpuString);
                    }
                }

                public override string ToString()
                {
                    return Name;
                }

                public MCUBuilder ToMCUBuilder(DeviceMemoryDatabase db, bool useSpecializedName = false)
                {
                    return new STM32MCUBuilder(this, db)
                    {
                        Name = useSpecializedName ? RefName : RPN,
                        FlashSize = FLASH * 1024,
                        RAMSize = 0,    //This will be adjusted later in our override of GenerateDefinition()
                        Core = Core,
                        FPU = FPU,
                    };
                }

                public ConfigSnapshot Config => new ConfigSnapshot { FLASH = FLASH, RAMs = string.Join("|", Memories.Select(r => $"{r.Name}={r.Size}").ToArray()), Define = Define };
            }

            //MCUs with the same value of ConfigSnapshot can use the same linker script, MCU definition, etc
            public struct ConfigSnapshot
            {
                public int FLASH;
                public string RAMs; //Using this instead of int[] saves us the pain of redefining GetHashCode()/Equals() used by GroupBy().
                public string Define;
            }

            public List<MCUBuilder> LoadDeviceList(Program.STM32BSPBuilder bspBuilder)
            {
                List<MCUBuilder> result = new List<MCUBuilder>();
                XmlDocument doc = new XmlDocument();
                string familyDir = Path.Combine(bspBuilder.STM32CubeDir, @"db\mcu");
                var db = new DeviceMemoryDatabase(bspBuilder.STM32CubeDir);

                doc.Load(Path.Combine(familyDir, @"families.xml"));
                List<ParsedMCU> lstMCUs = new List<ParsedMCU>();
                foreach (var m in doc.DocumentElement.SelectNodes("Family/SubFamily/Mcu").OfType<XmlElement>())
                {
                    string[] cores = m.SelectNodes("Core").OfType<XmlNode>().Select(n => n.InnerText).ToArray();

                    for (int i = 0; i < cores.Length; i++)
                    {
                        if ((db.STM32CubeTimestamp == 133047056504658633 || db.STM32CubeTimestamp == 133310388898546160) && m.GetAttribute("Name").StartsWith("STM32MP13"))
                            continue;

                        lstMCUs.Add(new ParsedMCU(m, familyDir, db, cores, i));
                    }
                }
                var rawMCUs = lstMCUs.ToArray();

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
                            result.Add(subGrp.First().ToMCUBuilder(db, true));
                        }
                    }
                }

                return result;
            }
        }
    }
}
