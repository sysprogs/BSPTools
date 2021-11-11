using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace pic32
{
    class PIC32BSPGenerator : IDisposable
    {
        private string _BaseDir;
        StreamWriter _Log;
        private FamilyDefinition _MIPSTemplate;
        private FamilyDefinition _ARMTemplate;

        public PIC32BSPGenerator(string baseDir)
        {
            _BaseDir = baseDir;
            var logDir = Path.Combine(baseDir, "Logs");
            Directory.CreateDirectory(logDir);
            _Log = new StreamWriter(Path.Combine(logDir, "bsplog.txt")) { AutoFlush = true };

            _MIPSTemplate = XmlTools.LoadObject<FamilyDefinition>(Path.Combine(_BaseDir, @"rules\pic32-mips.xml"));
            _ARMTemplate = XmlTools.LoadObject<FamilyDefinition>(Path.Combine(_BaseDir, @"rules\pic32-arm.xml"));
        }

        void LogInfo(string line)
        {
            Console.WriteLine(line);
        }

        void LogWarning(string line, string fn = null)
        {
            if (fn != null)
                _Log.WriteLine($"{fn}: WARNING: {line}");
            else
                _Log.WriteLine("WARNING: " + line);
        }

        public void GenerateSingleBSP(string pdscFile, string subdir, bool noCopy)
        {
            var outputDir = Path.Combine(_BaseDir, "Output", subdir);
            Directory.CreateDirectory(outputDir);

            string shortName = Path.GetFileName(outputDir);
            var packDir = Path.GetDirectoryName(pdscFile);

            var parser = new RegisterAddressParser(packDir);

            LogInfo($"Copying {shortName}...");
            if (!noCopy)
                PathTools.CopyDirectoryRecursive(packDir, outputDir);
            LogInfo($"Translating {shortName}...");

            var doc = new XmlDocument();
            doc.Load(pdscFile);

            var desc = doc.DocumentElement.SelectSingleNode("description")?.InnerText;
            if (string.IsNullOrEmpty(desc))
                throw new Exception("Missing package description");

            List<MCUFamily> families = new List<MCUFamily>();
            List<MCU> mcus = new List<MCU>();

            Regex rgMemory = new Regex("^[ \t]+([^ ]+)[ \t]*(|\\([^\\(\\)]+\\))[ \t]*:[ \t]+ORIGIN[ \t]*=[ \t]*0x([0-9a-fA-F]+),[ \t]*LENGTH[ \t]*=[ \t]*0x([0-9a-fA-F]+)[ \t]*$");
            bool isARM = false;

            foreach (var xmlFamily in doc.SelectNodes("package/devices/family").OfType<XmlElement>())
            {
                var firstCore = xmlFamily.SelectSingleNode("device/processor/@Dcore")?.InnerText;
                isARM = firstCore.StartsWith("Cortex");
                var template = isARM ? _ARMTemplate : _MIPSTemplate;

                var family = new MCUFamily
                {
                    ID = xmlFamily.GetAttribute("Dfamily"),
                    CompilationFlags = template.CompilationFlags,
                    ConfigurableProperties = template.ConfigurableProperties,
                };

                families.Add(family);

                foreach (var xmlDev in xmlFamily.SelectNodes("device").OfType<XmlElement>())
                {
                    var name = xmlDev.GetAttribute("Dname");
                    var xmlProcessor = xmlDev.SelectSingleNode("processor") as XmlElement;

                    var core = xmlProcessor.GetAttribute("Dcore");
                    string dfpSuffix = "";
                    var header = xmlDev.SelectSingleNode("compile/@header")?.InnerText;
                    if (header != null)
                    {
                        int idx = header.IndexOf('/');
                        if (idx != -1 && header.StartsWith("PIC32"))
                            dfpSuffix = "/" + header.Substring(0, idx);
                    }

                    var fpu = xmlProcessor.GetAttribute("Dfpu");
                    var mpu = xmlProcessor.GetAttribute("Dmpu");

                    if (!name.StartsWith("PIC32"))
                        throw new Exception("Unexpected device name: " + name);

                    var nameWithoutPrefix = name.Substring(3);
                    var linkerScript = Path.Combine(packDir, $@"xc32\{nameWithoutPrefix}\p{nameWithoutPrefix}.ld");
                    int FLASHSize = 0, RAMSize = 0;
                    List<MCUMemory> memories = new List<MCUMemory>();

                    if (File.Exists(linkerScript))
                    {
                        foreach (var line in File.ReadAllLines(linkerScript))
                        {
                            var m = rgMemory.Match(line);
                            if (m.Success)
                            {
                                var mem = new MCUMemory
                                {
                                    Name = m.Groups[1].Value,
                                    Address = ulong.Parse(m.Groups[3].Value, NumberStyles.HexNumber, null),
                                    Size = ulong.Parse(m.Groups[4].Value, NumberStyles.HexNumber, null),
                                    Flags = TranslateFlags(m.Groups[1].Value, m.Groups[2].Value),
                                };

                                if (m.Groups[2].Value.Contains("w"))
                                    RAMSize += (int)mem.Size;
                                else if (!mem.Name.StartsWith("sfr"))
                                    FLASHSize += (int)mem.Size;

                                memories.Add(mem);
                            }
                        }

                        ReplaceSizeIfFound(ref FLASHSize, memories, "program_mem");
                        ReplaceSizeIfFound(ref RAMSize, memories, "data_mem");
                    }

                    var mcu = new MCU
                    {
                        FamilyID = family.ID,
                        ID = name,
                        CompilationFlags = new ToolFlags
                        {
                            COMMONFLAGS = $"\"-mdfp=$$SYS:BSP_ROOT_FORWARD$${dfpSuffix}\" -mprocessor={nameWithoutPrefix}"
                        },
                        MemoryMap = new AdvancedMemoryMap
                        {
                            Memories = memories.ToArray(),
                        },
                        FLASHSize = FLASHSize,
                        RAMSize = RAMSize,
                        MCUDefinitionFile = TranslateHardwareRegisters(packDir, xmlDev, parser, outputDir, out var firstPort)
                    };

                    if (firstPort == null && isARM)
                        firstPort = "0";

                    if (firstPort != null)
                        mcu.AdditionalSystemVars = new[] { new SysVarEntry { Key = "com.sysprogs.pic32.default_port", Value = firstPort } };

                    mcus.Add(mcu);
                }
            }

            var samplesDir = Path.Combine(outputDir, "Samples");
            Directory.CreateDirectory(samplesDir);
            PathTools.CopyDirectoryRecursive(Path.Combine(_BaseDir, "rules\\" + (isARM ? "Samples-ARM" : "Samples-MIPS")), samplesDir);

            var version = doc.SelectSingleNode("package/releases/release/@version")?.InnerText ?? throw new Exception("Could not determine the pack version");

            var bsp = new BoardSupportPackage
            {
                PackageID = "com.sysprogs.pic32." + shortName.ToLower(),
                PackageVersion = version,
                GNUTargetID = "pic32",
                PackageDescription = desc,
                MCUFamilies = families.ToArray(),
                SupportedMCUs = mcus.ToArray(),
                Examples = Directory.GetDirectories(samplesDir).Select(f => "Samples/" + Path.GetFileName(f)).ToArray()
            };

            BSPBuilder.SaveBSP(bsp, outputDir, false);
        }

        private void ReplaceSizeIfFound(ref int size, IEnumerable<MCUMemory> memories, string substring)
        {
            var matches = memories.Where(m => m.Name.Contains(substring)).ToArray();
            if (matches.Length > 0)
                size = matches.Sum(m => (int)m.Size);
        }

        class RegisterAddressList
        {
            public string Sourcefile;
            public Dictionary<string, ulong> RegisterAddresses = new Dictionary<string, ulong>();

            public override string ToString() => Sourcefile;
        }

        class RegisterAddressParser
        {
            Dictionary<string, string> _FilesByName = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

            public RegisterAddressParser(string packRoot)
            {
                foreach (var fn in Directory.GetFiles(packRoot, "*.s", SearchOption.AllDirectories))
                {
                    _FilesByName[Path.GetFileNameWithoutExtension(fn)] = fn;
                }
            }

            public RegisterAddressList TryGetRegistersForDevice(string devName)
            {
                if (!_FilesByName.TryGetValue(devName, out var fn))
                {
                    devName = "p" + devName.Substring(3);
                    if (!_FilesByName.TryGetValue(devName, out fn))
                        return null;
                }

                Regex rgKV = new Regex("^([A-Z0-9_]+)[ \t]*=[ \t]*0x([0-9A-Fa-f]+)");

                var result = new RegisterAddressList { Sourcefile = fn };

                foreach (var line in File.ReadAllLines(fn))
                {
                    var m = rgKV.Match(line);
                    if (m.Success)
                        result.RegisterAddresses[m.Groups[1].Value] = ulong.Parse(m.Groups[2].Value, NumberStyles.HexNumber);
                }

                return result;
            }
        }

        string TranslateHardwareRegisters(string packDir, XmlElement devNode, RegisterAddressParser parser, string outputDir, out string firstPort)
        {
            var name = devNode.GetStringAttribute("Dname");
            MCUDefinition def;
            firstPort = null;

            var nsmgr = new XmlNamespaceManager(devNode.OwnerDocument.NameTable);
            nsmgr.AddNamespace("at", "http://www.atmel.com/schemas/pack-device-atmel-extension");
            nsmgr.AddNamespace("mchp", "http://crownking/pack-device-microchip-extension");

            var atdfFile = devNode.SelectSingleNode("environment/at:extension/at:atdf/@name", nsmgr)?.InnerText;
            var picFile = devNode.SelectSingleNode("environment/mchp:extension/mchp:pic/@name", nsmgr)?.InnerText;
            if (!string.IsNullOrEmpty(atdfFile))
                atdfFile = Path.GetFullPath(Path.Combine(packDir, atdfFile));
            if (!string.IsNullOrEmpty(picFile))
                picFile = Path.GetFullPath(Path.Combine(packDir, picFile));

            if (!string.IsNullOrEmpty(atdfFile) && File.Exists(atdfFile))
                def = ParseATDFFile(atdfFile, name, parser);    //ATDF files are usually more detailed than PIC files
            else if (!string.IsNullOrEmpty(picFile) && File.Exists(picFile))
                def = ParsePICFile(picFile, name, parser);
            else
                return null;

            string firstPortAlt = null;

            foreach (var set in def.RegisterSets)
            {
                if (!set.UserFriendlyName.Contains("GPIO") && !set.UserFriendlyName.Contains("PORT"))
                    continue;

                foreach (var reg in set.Registers)
                {
                    if (reg.Name.Length == 5 && reg.Name.StartsWith("PORT"))
                        firstPortAlt = "" + reg.Name[4];

                    if (reg.Name.Length == 5 && reg.Name.StartsWith("TRIS"))
                    {
                        firstPort = "" + reg.Name[4];
                        break;
                    }
                }

                if (firstPort != null)
                    break;
            }

            if (firstPort == null)
                firstPort = firstPortAlt;

            var dir = Path.Combine(outputDir, "DeviceDefinitions");
            Directory.CreateDirectory(dir);

            var ser = new XmlSerializer(typeof(MCUDefinition));
            using (var fs = File.Create(Path.Combine(dir, $"{name}.xml.gz")))
            using (var gs = new GZipStream(fs, CompressionMode.Compress, true))
                ser.Serialize(gs, def);

            return $"DeviceDefinitions/{name}.xml";
        }

        class ParsedModule
        {
            public Dictionary<string, KnownSubRegisterValue[]> ValueGroups = new Dictionary<string, KnownSubRegisterValue[]>();
            public Dictionary<string, XmlElement[]> RegisterGroups = new Dictionary<string, XmlElement[]>();
            public string Name;
        }


        MCUDefinition ParsePICFile(string picFile, string deviceName, RegisterAddressParser parser)
        {
            var doc = new XmlDocument();
            doc.Load(picFile);

            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("edc", "http://crownking/edc");

            var addressList = parser.TryGetRegistersForDevice(deviceName);

            Dictionary<string, List<HardwareRegister>> sets = new Dictionary<string, List<HardwareRegister>>();

            foreach (var xmlSector in doc.SelectNodes("edc:PIC/edc:PhysicalSpace/edc:SFRDataSector", nsmgr).OfType<XmlElement>())
            {
                foreach (var xmlChild in xmlSector.ChildNodes.OfType<XmlElement>())
                {
                    if (xmlChild.Name == "edc:SFRDef")
                    {
                        ProcessSFRDef(xmlChild, sets, addressList, picFile, nsmgr);
                    }
                    else if (xmlChild.Name == "edc:AdjustPoint")
                    {
                    }
                    else if (xmlChild.Name == "edc:MuxedSFRDef")
                    {
                        var def = xmlChild.SelectSingleNode("edc:SelectSFR/edc:SFRDef", nsmgr) as XmlElement;
                        if (def != null)
                            ProcessSFRDef(def, sets, addressList, picFile, nsmgr);
                    }
                    else
                        throw new Exception("Unsupported XML element:" + xmlChild.Name);
                }
            }

            return new MCUDefinition
            {
                MCUName = deviceName,
                RegisterSets = sets.Select(kv => new HardwareRegisterSet { UserFriendlyName = kv.Key, Registers = kv.Value.ToArray() }).OrderBy(s => s.UserFriendlyName).ToArray(),
            };
        }

        private void ProcessSFRDef(XmlElement xmlChild, Dictionary<string, List<HardwareRegister>> sets, RegisterAddressList registerAddresses, string picFile, XmlNamespaceManager nsmgr)
        {
            var addr = xmlChild.GetUlongAttribute("edc:_addr");
            var periph = xmlChild.GetAttribute("ltx:baseofperipheral");
            if (string.IsNullOrEmpty(periph))
                periph = xmlChild.GetAttribute("ltx:memberofperipheral");

            int idx = periph.IndexOf(' ');
            if (idx != -1)
                periph = periph.Substring(0, idx);

            var access = xmlChild.GetStringAttribute("edc:access");
            var name = xmlChild.GetStringAttribute("edc:name");
            var width = xmlChild.GetUlongAttribute("edc:nzwidth");

            if (string.IsNullOrEmpty(periph?.Trim()))
                periph = "OTHER";

            if (registerAddresses.RegisterAddresses.TryGetValue(name, out var ldAddr))
                addr = ldAddr;
            else
            {
                //Fallback: try to guess virtual address based on the physical address
                addr |= 0xA0000000;
            }

            if (!sets.TryGetValue(periph, out var list))
                sets[periph] = list = new List<HardwareRegister>();

            int bitOffset = 0;
            List<HardwareSubRegister> subregs = new List<HardwareSubRegister>();

            foreach (var xmlSubreg in xmlChild.SelectNodes("edc:SFRModeList/edc:SFRMode[1]/*", nsmgr).OfType<XmlElement>())
            {
                if (xmlSubreg.Name == "edc:SFRFieldDef")
                {
                    int subWidth = (int)xmlSubreg.GetUlongAttribute("edc:nzwidth");
                    var subName = xmlSubreg.GetStringAttribute("edc:cname");
                    bitOffset += subWidth;

                    subregs.Add(new HardwareSubRegister { Name = subName, FirstBit = bitOffset, SizeInBits = subWidth });
                }
                else if (xmlSubreg.Name == "edc:AdjustPoint")
                {
                    bitOffset += (int)xmlSubreg.GetUlongAttribute("edc:offset");
                }
            }

            list.Add(new HardwareRegister { Name = name, Address = $"0x{addr:x8}", ReadOnly = !access.Contains("w"), SizeInBits = (int)width, SubRegisters = subregs.Count == 0 ? null : subregs.ToArray() });
        }

        MCUDefinition ParseATDFFile(string atdfFile, string deviceName, RegisterAddressParser parser)
        {
            var doc = new XmlDocument();
            doc.Load(atdfFile);

            List<HardwareRegisterSet> sets = new List<HardwareRegisterSet>();

            var addressList = parser.TryGetRegistersForDevice(deviceName);

            var dev = doc.DocumentElement.SelectSingleNode($"devices/device[@name='{deviceName}']") as XmlElement ?? throw new Exception("Failed to locate device node for " + deviceName);

            Dictionary<string, ulong> addressSpaces = new Dictionary<string, ulong>();
            /*foreach(var seg in dev.SelectNodes("address-spaces/address-space/memory-segment").OfType<XmlElement>())
                addressSpaces[seg.GetStringAttribute("name")] = seg.GetUlongAttribute("start");*/

            Dictionary<string, ParsedModule> modules = new Dictionary<string, ParsedModule>();
            foreach (var xmlModule in doc.DocumentElement.SelectElements($"modules/module"))
            {
                var module = new ParsedModule { Name = xmlModule.GetStringAttribute("name") };

                foreach (var xmlGroup in xmlModule.SelectElements("value-group"))
                {
                    string groupName = xmlGroup.GetAttribute("name");
                    if (string.IsNullOrEmpty(groupName))
                        continue;

                    KnownSubRegisterValue[] values = ParseValueGroup(xmlGroup);
                    if (values != null)
                        module.ValueGroups[groupName] = values;
                }

                foreach (var xmlGroup in xmlModule.SelectElements("register-group"))
                {
                    var name = xmlGroup.GetStringAttribute("name");
                    module.RegisterGroups[name] = xmlGroup.SelectElements("register").ToArray();
                }

                modules[module.Name] = module;
            }

            foreach (var xmlModule in dev.SelectElements("peripherals/module"))
            {
                var xmlInstance = xmlModule.SelectSingleNode("instance") as XmlElement;
                if (xmlInstance == null)
                    continue;

                var moduleName = xmlModule.GetStringAttribute("name");
                if (moduleName == "CORE")
                    continue;

                if (!modules.TryGetValue(moduleName, out var module))
                {
                    LogWarning("Missing module: " + moduleName, atdfFile);
                    continue;
                }

                foreach (var xmlGroup in xmlInstance.SelectElements("register-group"))
                {
                    var groupName = xmlGroup.GetStringAttribute("name");

                    if (!(xmlGroup.TryGetUlongAttribute("offset") is ulong baseAddr))
                    {
                        LogWarning("Missing offset for: " + xmlGroup.GetStringAttribute("name"), atdfFile);
                        continue;
                    }

                    List<HardwareRegister> registers = new List<HardwareRegister>();

                    if (!module.RegisterGroups.TryGetValue(xmlModule.GetStringAttribute("name"), out var foundRegisters))
                    {
                        LogWarning("Could not find registers for " + xmlGroup.GetStringAttribute("name"), atdfFile);
                        continue;
                    }

                    foreach (var xmlReg in foundRegisters)
                    {
                        ulong? off = xmlReg.TryGetUlongAttribute("offset");
                        if (!off.HasValue)
                        {
                            LogWarning("No offset provided for " + xmlReg.GetStringAttribute("name"));
                            continue;
                        }

                        var regName = xmlReg.GetStringAttribute("name");
                        ulong addr = baseAddr + off.Value;
                        if (addressList == null)
                        {
                            //Could not find the .S file with the list of all registers.
                            //We might end up using incorrect addresses
                        }
                        else if (addressList.RegisterAddresses.TryGetValue(regName, out var ldAddr))
                        {
                            //Register addresses in the ATDF file do not match the actual addresses used by the linker.
                            //In most cases, it's just the virtual vs. physical address, but some registers (e.g. PORTD on PIC32MX534F064H) also have invalid offset.
                            //Hence, we replace them with the actual addresses that will be used by the code accessing these registers.
                            addr = ldAddr;
                        }
                        else
                        {
                            LogWarning($"Skipping the {regName} register that is missing in {addressList.Sourcefile}");
                            continue;
                        }

                        registers.Add(new HardwareRegister
                        {
                            Name = regName,
                            ReadOnly = xmlReg.GetAttribute("RW") == "R",
                            SizeInBits = (int)xmlReg.GetUlongAttribute("size") * 8,
                            Address = $"0x{addr:x8}",
                            SubRegisters = TranslateSubRegisters(xmlReg, module)
                        });
                    }

                    sets.Add(new HardwareRegisterSet { UserFriendlyName = groupName, Registers = registers.ToArray() });
                }
            }

            sets.Sort((x, y) => x.UserFriendlyName.CompareTo(y.UserFriendlyName));

            return new MCUDefinition { MCUName = deviceName, RegisterSets = sets.ToArray() };
        }

        static HardwareSubRegister[] TranslateSubRegisters(XmlElement xmlReg, ParsedModule module)
        {
            List<HardwareSubRegister> result = new List<HardwareSubRegister>();

            foreach (var xmlField in xmlReg.SelectElements("bitfield"))
            {
                var subreg = new HardwareSubRegister { Name = xmlField.GetAttribute("name") };

                module.ValueGroups.TryGetValue(xmlField.GetAttribute("values"), out subreg.KnownValues);
                if (MCUFamilyBuilder.MaskToBitRange(xmlField.GetUlongAttribute("mask"), out subreg.FirstBit, out subreg.SizeInBits))
                    result.Add(subreg);
            }

            if (result.Count > 0)
                return result.ToArray();
            return null;
        }

        KnownSubRegisterValue[] ParseValueGroup(XmlElement xmlGroup)
        {
            KnownSubRegisterValue[] result = new KnownSubRegisterValue[0];

            foreach (var xmlValue in xmlGroup.SelectElements("value"))
            {
                var vName = xmlValue.GetAttribute("name");
                if (string.IsNullOrEmpty(vName))
                    continue;
                var vValue = xmlValue.GetUlongAttribute("value");

                if (vValue > 1024)
                {
                    LogWarning($"{vName} group has too many values ({vValue})");
                    return null;
                }

                if (vValue >= (uint)result.Length)
                    Array.Resize(ref result, (int)vValue + 1);

                result[(int)vValue] = new KnownSubRegisterValue { Name = vName };
            }

            for (int i = 0; i < result.Length; i++)
                if (result[i] == null)
                    result[i] = new KnownSubRegisterValue { Name = $"0x{i:x2}" };

            if (result.Length < 1)
                result = null;

            return result;
        }

        static MCUMemoryFlags TranslateFlags(string memName, string flags)
        {
            return MCUMemoryFlags.None;
        }

        public void Dispose()
        {
        }
    }
}
