/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using System;
using System.Collections.Generic;
using System.Text;
using BSPEngine;
using System.IO;
using LinkerScriptGenerator;
using System.Xml.Serialization;
using BSPGenerationTools;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;

namespace stm32_bsp_generator
{
    class Program
    {
        class STM32BSPBuilder : BSPBuilder
        {
            List<KeyValuePair<Regex, MemoryLayout>> _SpecialMemoryLayouts = new List<KeyValuePair<Regex, MemoryLayout>>();

            List<KeyValuePair<Regex, XmlElement>> _KnownSTM32Devices = new List<KeyValuePair<Regex,XmlElement>>();

            public void LoadDevicesFromCube(ZipFile zf, string familyFile) // Load diveces from Cube
            {
                var entry = zf.Entries.First(e => e.FileName == familyFile);
                MemoryStream db = new MemoryStream();
                zf.ExtractEntry(entry, db);

                STM32CubeDeviceDatabase.LoadXml(Encoding.UTF8.GetString(db.ToArray()));
                foreach (XmlElement node in STM32CubeDeviceDatabase.DocumentElement.SelectNodes("device"))
                {
                    foreach (var id in node.SelectSingleNode("PN").InnerText.Split(','))
                        _KnownSTM32Devices.Add(new KeyValuePair<Regex, XmlElement>(new Regex(id.Replace('x', '.')), node));
                }
            }

            public STM32BSPBuilder(BSPDirectories dirs, string cubeDir)
                : base(dirs)
            {
                ShortName = "STM32";
                var zf = new ZipFile(File.OpenRead(cubeDir + @"\plugins\projectmanager.jar"));

                LoadDevicesFromCube(zf, "devices/STM32F0.db");
                LoadDevicesFromCube(zf, "devices/STM32F1.db");
                LoadDevicesFromCube(zf, "devices/STM32F2.db");
                LoadDevicesFromCube(zf, "devices/STM32F3.db");
                LoadDevicesFromCube(zf, "devices/STM32F4.db");
                LoadDevicesFromCube(zf, "devices/STM32F7.db");
                LoadDevicesFromCube(zf, "devices/STM32L0.db");
                LoadDevicesFromCube(zf, "devices/STM32L1.db");
                LoadDevicesFromCube(zf, "devices/STM32L4.db");
                LoadDevicesFromCube(zf, "devices/STM32W.db");

                foreach (var line in File.ReadAllLines(dirs.RulesDir + @"\stm32memory.csv"))
                {
                    string[] items = line.Split(',');
                    if (!items[0].StartsWith("STM32"))  // || items[0].StartsWith("STM32F328") || items[0].StartsWith("STM32F334") || items[0].StartsWith("STM32F358")) // TODO: support these as well
                        continue;

                    MemoryLayout layout = new MemoryLayout { DeviceName = items[0], Memories = new List<Memory>() };
                    var flash = AddMemory(layout, "FLASH", MemoryType.FLASH, items, 13, true);
                    var sram = AddMemory(layout, "SRAM", MemoryType.RAM, items, 1, true);
                    var sram2 = AddMemory(layout, "SRAM2", MemoryType.RAM, items, 3, false);
                    var sram3 = AddMemory(layout, "SRAM3", MemoryType.RAM, items, 5, false);

                    if (sram3 != null && sram3.Start == sram2.End)
                    {
                        sram2.Size += sram3.Size;
                        if (!layout.Memories.Remove(sram3))
                            throw new Exception("Cannot remove old memory after merging it");
                    }

                    if (sram2 != null && sram2.Start == sram.End)
                    {
                        sram.Size += sram2.Size;
                        if (!layout.Memories.Remove(sram2))
                            throw new Exception("Cannot remove old memory after merging it");
                    }

                    AddMemory(layout, "CCM", MemoryType.RAM, items, 7, false);
                    AddMemory(layout, "EEPROM", MemoryType.FLASH, items, 9, false);
                    AddMemory(layout, "EEPROM2", MemoryType.FLASH, items, 11, false);
                    var flash2 = AddMemory(layout, "FLASH2", MemoryType.FLASH, items, 15, false);
                    AddMemory(layout, "BACKUP_SRAM", MemoryType.RAM, items, 17, false);

                    if (flash2 != null && flash2.Start == flash.End)
                    {
                        flash.Size += flash2.Size;
                        if (!layout.Memories.Remove(flash2))
                            throw new Exception("Cannot remove old memory after merging it");
                    }

                    if (flash.Start != FLASHBase)
                        throw new Exception("Unexpected FLASH start!");
                    if (sram.Start != SRAMBase)
                        throw new Exception("Unexpected SRAM start!");

                    _SpecialMemoryLayouts.Add(new KeyValuePair<Regex, MemoryLayout>(new Regex(items[0].Replace('x', '.') + ".*"), layout));
                }
            }

            public override void GenerateLinkerScriptsAndUpdateMCU(string ldsDirectory, string familyFilePrefix, MCUBuilder mcu, MemoryLayout layout, string generalizedName)
            {
                base.GenerateLinkerScriptsAndUpdateMCU(ldsDirectory, familyFilePrefix, mcu, layout, generalizedName);
                if (familyFilePrefix.StartsWith("STM32F7"))
                {
                    //We only use this layout for SRAM configurations because the ST system file expects vectors at 0x20010000 and not at 0x20000000.
                    //If the end user wants to distinguish between different memory types, they will need to modify the linker script.
                    var updatedLayout = layout.Clone();
                    var sram = updatedLayout.Memories.First(m => m.Start == SRAMBase);
                    sram.Start += 0x10000;
                    sram.Size -= 0x10000;

                    updatedLayout.Memories.Add(new Memory { Name = "DTCM_RAM", Size = 64 * 1024, Start = SRAMBase, Access = MemoryAccess.Readable | MemoryAccess.Writable | MemoryAccess.Executable });
                    updatedLayout.Memories.Add(new Memory { Name = "ITCM_RAM", Size = 16 * 1024, Start = 0, Access = MemoryAccess.Readable | MemoryAccess.Writable | MemoryAccess.Executable });
                    using (var gen = new LdsFileGenerator(LDSTemplate, updatedLayout) { RedirectMainFLASHToRAM = true })
                    {
                        using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_sram.lds")))
                            gen.GenerateLdsFile(sw);
                    }

                }
            }

            XmlDocument STM32CubeDeviceDatabase = new XmlDocument();
            Regex rgMemoryDef = new Regex(@"I(RAM[1-2]?|ROM[1-9]?)\(0x([0-9A-F]+)-0x([0-9A-F]+)\)");

            List<Memory> LookupMemorySizesFromSTM32CubeDatabase(string mcuName)
            {
                var node = _KnownSTM32Devices.FirstOrDefault(kv => kv.Key.IsMatch(mcuName)).Value;
                if (node == null)
                {
                    Console.WriteLine("Cannot find device in STM32 database: " + mcuName);
                    return null;
                }

                List<Memory> mems = new List<Memory>();
                var memNode = node.SelectSingleNode("Mem");
                if (memNode == null)
                    memNode = node.SelectSingleNode("Cpu");

                foreach(var mem in memNode.InnerText.Split(' '))
                {
                    var m = rgMemoryDef.Match(mem);
                    if (m.Success)
                    {
                        uint start = uint.Parse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber), end = uint.Parse(m.Groups[3].Value, System.Globalization.NumberStyles.HexNumber);
                        string name = m.Groups[1].Value;
                        if (name == "ROM")
                            name = "FLASH";
                        else if (name == "RAM")
                            name = "SRAM";
                        else if (name == "RAM2")
                            name = "CCM";
                        else
                            throw new Exception("Unexpected memory name");

                        Memory mobj = new Memory { Name = name , Start = start, Size = end - start + 1, Type = (name == "FLASH") ? MemoryType.FLASH : MemoryType.RAM };
                        mems.Add(mobj);
                    }
                }

                mems.Sort((a, b) => a.Type.CompareTo(b.Type));

                if (mcuName.StartsWith("STM32L4", StringComparison.InvariantCultureIgnoreCase))
                {
                    //The definition files in the CubeMX set the main SRAM size 128K while it should actually be 96K. We fix it manually.
                    if (mems.Count != 2 || mems[1].Name != "SRAM" || mems[1].Size != 131072)
                        throw new Exception("Unexpected L4 memory size. Definitions finally fixed? Please investigate.");

                    mems[1].Size = 96 * 1024;
                    mems.Add(new Memory {
                        Name = "SRAM2",
                        Size = (128 - 96) * 1024,
                        Type = MemoryType.RAM,
                        Start = 0x10000000,
                    });
                }
                return mems;
            }

            public const string PaddedKnownMemoryMismatches = ";STM32F334K4;STM32F334C4;STM32F334C6;STM32F334C8;STM32F334K6;STM32F334K8;STM32F334R6;STM32F334R8;STM32F205RB;STM32F205RC;STM32F205VB;STM32F205VC;STM32F205ZC;STM32F302CC;STM32F302RC;STM32F302VC;STM32F303CB;STM32F303RB;STM32F303VB;STM32F328C8;";

            public override MemoryLayout GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                var stm32Mems = LookupMemorySizesFromSTM32CubeDatabase(mcu.Name);

                foreach (var kv in _SpecialMemoryLayouts)
                    if (kv.Key.IsMatch(mcu.Name))
                    {
                        if (stm32Mems != null)
                        {
                            foreach(var mem in stm32Mems)
                            {
                                var foundMem = kv.Value.Memories.FirstOrDefault(m => m.Name == mem.Name);
                                if (foundMem == null)
                                    Console.WriteLine("Cannot find {0} in {1}", mem.Name, mcu.Name);
                                else if (foundMem.Start != mem.Start)
                                    Console.WriteLine("Memory base mismatch for {0} in {1}", mem.Name, mcu.Name);
                                else if (foundMem.Size != mem.Size)
                                {
                                    if (!PaddedKnownMemoryMismatches.Contains(mcu.Name))
                                        Console.WriteLine("Memory size mismatch for {0} in {1} (ST has {2}, we have {3})", mem.Name, mcu.Name, mem.SizeWithSuffix, foundMem.SizeWithSuffix);
                                }
                            }
                        }

                        return kv.Value;
                    }

                MemoryLayout layout = new MemoryLayout { DeviceName = mcu.Name, Memories = new List<Memory>() };

                if (stm32Mems != null)
                {
                    layout.Memories.AddRange(stm32Mems);
                }
                else
                {
                    layout.Memories.Add(new Memory
                    {
                        Name = "FLASH",
                        Access = MemoryAccess.Undefined,
                        Type = MemoryType.FLASH,
                        Start = FLASHBase,
                        Size = (uint)mcu.FlashSize,
                    });

                    layout.Memories.Add(new Memory
                    {
                        Name = "SRAM",
                        Access = MemoryAccess.Undefined,
                        Type = MemoryType.RAM,
                        Start = SRAMBase,
                        Size = (uint)mcu.RAMSize,
                    });
                }


                if (layout.Memories.First(m => m.Name == "FLASH").Start != FLASHBase)
                    throw new Exception("Unexpected FLASH start!");
                if (layout.Memories.First(m => m.Name == "SRAM").Start != SRAMBase)
                    throw new Exception("Unexpected SRAM start!");

                return layout;
            }

            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                flashBase = FLASHBase;
                ramBase = SRAMBase;
            }
        }

        static IEnumerable<StartupFileGenerator.InterruptVectorTable> ParseStartupFiles(string dir, MCUFamilyBuilder fam)
        {
            var mainClassifier = fam.Definition.Subfamilies.First(f => f.IsPrimary);

            var allFiles = Directory.GetFiles(dir);

            foreach(var fn in allFiles)
            {
                string subfamily = Path.GetFileNameWithoutExtension(fn);
                if (!subfamily.StartsWith("startup_"))
                    continue;
                subfamily = subfamily.Substring(8);
                 yield return new  StartupFileGenerator.InterruptVectorTable{
                    FileName = Path.ChangeExtension(Path.GetFileName(fn), ".c"),
                    MatchPredicate = m => (allFiles.Length == 1) || StringComparer.InvariantCultureIgnoreCase.Compare(mainClassifier.TryMatchMCUName(m.Name), subfamily) == 0,
                    Vectors = StartupFileGenerator.ParseInterruptVectors(fn, "g_pfnVectors:", @"/\*{10,999}|^[^/\*]+\*/$", @"^[ \t]+\.word[ \t]+([^ ]+)", null, @"^[ \t]+/\*", ".equ[ \t]+([^ \t]+),[ \t]+(0x[0-9a-fA-F]+)", 1, 2)
                };
            }
        }

        private static IEnumerable<MCUDefinitionWithPredicate> ParsePeripheralRegisters(string dir, MCUFamilyBuilder fam)
        {
            var mainClassifier = fam.Definition.Subfamilies.First(f => f.IsPrimary);
            List<Task<MCUDefinitionWithPredicate>> tasks = new List<Task<MCUDefinitionWithPredicate>>();
            Console.Write("Parsing {0} registers in background threads", fam.Definition.Name);
            RegisterParserErrors errors = new RegisterParserErrors();

            foreach (var fn in Directory.GetFiles(dir, "*.h"))
            {
                string subfamily = Path.GetFileNameWithoutExtension(fn);
                if (subfamily.Length != 11 && subfamily.Length != 12)
                    continue;

                /*if (subfamily != "stm32f301x8")
                 continue;*/
                 
                Func<MCUDefinitionWithPredicate> func = () =>
                    {
                        RegisterParserConfiguration cfg = XmlTools.LoadObject<RegisterParserConfiguration>(fam.BSP.Directories.RulesDir + @"\PeripheralRegisters.xml");
                        var r = new MCUDefinitionWithPredicate
                            {
                                MCUName = subfamily,
                                RegisterSets = PeripheralRegisterGenerator.GenerateFamilyPeripheralRegisters(fn, cfg, errors),
                                MatchPredicate = m => StringComparer.InvariantCultureIgnoreCase.Compare(mainClassifier.TryMatchMCUName(m.Name), subfamily) == 0,
                            };
                        Console.Write(".");
                        return r;
                    };

                 //func();
                tasks.Add(Task.Run(func));
            }

            Task.WaitAll(tasks.ToArray());
            var errorCnt = errors.ErrorCount;
            if (errorCnt != 0)
            {
                throw new Exception("Found " + errorCnt + " errors while parsing headers");

                //   for (int i = 0; i < errors.ErrorCount;i++)
                //     Console.WriteLine("\n er  " + i + "  -  " + errors.DetalErrors(i));

            }

            Console.WriteLine("done");
            return from r in tasks select r.Result;
        }


        class SamplePrioritizer
        {
            List<KeyValuePair<Regex, int>> _Rules = new List<KeyValuePair<Regex, int>>();
            public SamplePrioritizer(string rulesFile)
            {
                int index = 1;
                foreach (var line in File.ReadAllLines(rulesFile))
                {
                    _Rules.Add(new KeyValuePair<Regex, int>(new Regex(line, RegexOptions.IgnoreCase), index++));
                }
            }

            public int GetScore(string path)
            {
                string fn = Path.GetFileName(path);
                foreach (var rule in _Rules)
                    if (rule.Key.IsMatch(fn))
                        return rule.Value;
                return 1000;
            }

            public int Prioritize(string left, string right)
            {
                int sc1 = GetScore(left), sc2 = GetScore(right);
                if (sc1 != sc2)
                    return sc1 - sc2;
                return StringComparer.InvariantCultureIgnoreCase.Compare(left, right);
            }

        }

        static void Main(string[] args)
        {
            if (args.Length < 2)
                throw new Exception("Usage: stm32.exe <SW package directory> <STM32Cube directory>");

            var bspBuilder = new STM32BSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules"), args[1]);
            var devices = BSPGeneratorTools.ReadMCUDevicesFromCommaDelimitedCSVFile(bspBuilder.Directories.RulesDir + @"\stm32devices.csv", "Part Number", "FLASH Size (Prog)", "Internal RAM Size", "Core", true);
            List<MCUFamilyBuilder> allFamilies = new List<MCUFamilyBuilder>();
            foreach(var fn in Directory.GetFiles(bspBuilder.Directories.RulesDir + @"\families", "*.xml"))
                allFamilies.Add(new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(fn)));

            var rejects = BSPGeneratorTools.AssignMCUsToFamilies(devices, allFamilies);

            if (rejects.Count > 0)
            {
                Console.WriteLine("Globally unsupported MCUs:");
                foreach (var r in rejects)
                    Console.WriteLine("\t{0}", r.Name);
            }

            List<MCUFamily> familyDefinitions = new List<MCUFamily>();
            List<MCU> mcuDefinitions = new List<MCU>();
            List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
            List<string> exampleDirs = new List<string>();

            bool noPeripheralRegisters = args.Contains("/noperiph");

            var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));
            foreach (var fw in commonPseudofamily.GenerateFrameworkDefinitions())
                frameworks.Add(fw);

            foreach (var fam in allFamilies)
            {
                var rejectedMCUs = fam.RemoveUnsupportedMCUs(true);
                if (rejectedMCUs.Length != 0)
                {
                    Console.WriteLine("Unsupported {0} MCUs:", fam.Definition.Name);
                    foreach (var mcu in rejectedMCUs)
                        Console.WriteLine("\t{0}", mcu.Name);
                }

                fam.AttachStartupFiles(ParseStartupFiles(fam.Definition.StartupFileDir, fam));
                if (!noPeripheralRegisters)
                    fam.AttachPeripheralRegisters(ParsePeripheralRegisters(fam.Definition.PrimaryHeaderDir, fam));

                familyDefinitions.Add(fam.GenerateFamilyObject(true));
                fam.GenerateLinkerScripts(false);
                foreach (var mcu in fam.MCUs)
                    mcuDefinitions.Add(mcu.GenerateDefinition(fam, bspBuilder, !noPeripheralRegisters));

                foreach (var fw in fam.GenerateFrameworkDefinitions())
                    frameworks.Add(fw);
             
                foreach (var sample in fam.CopySamples())
                    exampleDirs.Add(sample);
            }

            foreach (var sample in commonPseudofamily.CopySamples(null, allFamilies.Where(f => f.Definition.AdditionalSystemVars != null).SelectMany(f => f.Definition.AdditionalSystemVars)))
                exampleDirs.Add(sample);

            var prioritizer = new SamplePrioritizer(Path.Combine(bspBuilder.Directories.RulesDir, "SamplePriorities.txt"));
            exampleDirs.Sort((a,b) => prioritizer.Prioritize(a, b));

            BoardSupportPackage bsp = new BoardSupportPackage
            {
                PackageID = "com.sysprogs.arm.stm32",
                PackageDescription = "STM32 Devices",
                GNUTargetID = "arm-eabi",
                GeneratedMakFileName = "stm32.mak",
                MCUFamilies = familyDefinitions.ToArray(),
                SupportedMCUs = mcuDefinitions.ToArray(),
                Frameworks = frameworks.ToArray(),
                Examples = exampleDirs.ToArray(),
                PackageVersion = "3.5",
                IntelliSenseSetupFile = "stm32_compat.h",
                FileConditions = bspBuilder.MatchedFileConditions.ToArray(),
                MinimumEngineVersion = "5.1",
                FirstCompatibleVersion = "3.0",
            };

            File.Copy(@"..\..\stm32_compat.h", Path.Combine(bspBuilder.BSPRoot, "stm32_compat.h"), true);
            Console.WriteLine("Saving BSP...");
            bspBuilder.Save(bsp, true);
        }


        static void CompareMCULists(string MCUList1, string MCUList2)
        {
            List<string> list1 = new List<string>();
            foreach (var line in File.ReadAllLines(MCUList1))
            {
                string[] items = line.Split(',');
                if (!items[0].StartsWith("STM32"))
                    continue;
                if (items[0].StartsWith("STM32TS60"))
                    continue;

                string mcuName = items[0];
                list1.Add(mcuName);
            }

            List<string> list2 = new List<string>();
            foreach (var line in File.ReadAllLines(MCUList2))
            {
                string[] items = line.Split(',');
                if (!items[0].StartsWith("STM32"))
                    continue;
                if (items[0].StartsWith("STM32TS60"))
                    continue;

                string mcuName = items[0];
                list2.Add(mcuName);
            }

            Console.WriteLine("Items missing from second list:");
            foreach (var line in list1)
            { 
                if(!list2.Contains(line))
                    Console.WriteLine(line);

            }

            Console.WriteLine("Items added to second list:");
            foreach (var line in list2)
            {
                if (!list1.Contains(line))
                    Console.WriteLine(line);
            }
        }

        static void MergeOldAndNewMCULists(string oldMCUList, string newMCUList, string mergedMCUList)
        {
            string[] oldheaders = null;
            string[] newheaders = null;

            Dictionary<string, string> mergedDict = new Dictionary<string, string>();

            // Add all the entries from the newer list to the dictionary
            foreach (var line in File.ReadAllLines(newMCUList, Encoding.UTF8))
            {
                if (newheaders == null)
                {
                    newheaders = line.Split(new char[] { ',' });
                    continue;
                }
                // Assume the first entry is the mcu name!
                int index = line.IndexOf(',');
                mergedDict.Add(line.Substring(0, index), line.Substring(index + 1));
            }

            // Add the entries that are in the old list but not in the new list to the dictionary
            foreach (var line in File.ReadAllLines(oldMCUList, Encoding.UTF8))
            {
                if (oldheaders == null)
                {
                    oldheaders = line.Split(new char[] { ',' });
                    continue;
                }
                // Assume the first entry is the mcu name!
                int index = line.IndexOf(',');
                if (!mergedDict.ContainsKey(line.Substring(0, index)))
                {
                    string fitted_line = FitOldLineToNewHeaderOrder(line, oldheaders, newheaders);
                    mergedDict.Add(fitted_line.Substring(0, index), fitted_line.Substring(index + 1));
                }
            }

            List<string> mergedList = new List<string>();
            foreach (var key in mergedDict.Keys)
            {
                mergedList.Add(key + "," + mergedDict[key]);
            }
            mergedList.Sort();
            mergedList.Insert(0, string.Join(",", newheaders));

            File.WriteAllLines(mergedMCUList, mergedList.ToArray(), Encoding.UTF8);
        }

        static string FitOldLineToNewHeaderOrder(string line, string[] oldHeaders, string[] newHeaders)
        {
            List<string> built_line = new List<string>();
            string[] split_line = line.Split(new char[]{','});

            foreach (var col in newHeaders)
            {
                int index = -1;
                for (int i = 0; i < oldHeaders.Length; i++)
                {
                    if (oldHeaders[i].ToLower() == col.ToLower())
                    {
                        index = i;
                        break;
                    }
                }

                if (index >= 0)
                    built_line.Add(split_line[index]);
                else
                    built_line.Add("");
            }

            return string.Join(",", built_line);
        }

        static Memory AddMemory(MemoryLayout layout, string name, MemoryType type, string[] data, int baseIndex, bool required)
        {
            if (data[baseIndex] == "")
            {
                if (required)
                    throw new Exception(name + " not defined!");
                return null;
            }

            layout.Memories.Add(new Memory
            {
                Name = name,
                Access = MemoryAccess.Undefined,
                Type = type,
                Start = ParseHex(data[baseIndex]),
                Size = uint.Parse(data[baseIndex + 1]) * 1024
            });

            return layout.Memories[layout.Memories.Count - 1];
        }

        static uint ParseHex(string text)
        {
            text = text.Replace(" ", "");
            if (text.StartsWith("0x"))
                return uint.Parse(text.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier);
            else
                return uint.Parse(text);
        }

        static int ParseInt(string str)
        {
            if (str == "" || str == "-")
                return 0;
            return int.Parse(str);
        }

        static ToolFlags MakeToolFlagsForCortex(string cortex)
        {
            ToolFlags flags = new ToolFlags();

            string cortexFlags = null;
            switch (cortex)
            {
                case "ARM Cortex-M0":
                    cortexFlags = "-mcpu=cortex-m0 -mthumb";
                    break;
                case "ARM Cortex-M0+":
                    cortexFlags = "-mcpu=cortex-m0plus -mthumb";
                    break;
                case "ARM Cortex-M3":
                    cortexFlags = "-mcpu=cortex-m3 -mthumb";
                    break;
                case "ARM Cortex-M4":
                    cortexFlags = "-mcpu=cortex-m4 -mthumb";
                    break;
                default:
                    throw new Exception("Unknown MCU: " + cortex);
            }
            flags.CFLAGS = flags.CXXFLAGS = flags.ASFLAGS = flags.LDFLAGS = cortexFlags;

            return flags;
        }

        static string FamilyFromMCU(string mcuName)
        {
            if (mcuName.StartsWith("STM32F3"))
            {
                if (mcuName[7] == '0' || mcuName[7] == '1')
                    return "STM32F30xxx";
                else if (mcuName[7] == '7' || mcuName[7] == '8')
                    return "STM32F37xxx";
                throw new Exception("Cannot detect family for " + mcuName);
            }
            return mcuName.Substring(0, 7) + "xxxx";
        }

        const uint FLASHBase = 0x08000000, SRAMBase = 0x20000000;


        static void CopyDirectoryRecursive(string sourceDirectory, string destinationDirectory)
        {
            sourceDirectory = sourceDirectory.TrimEnd('/', '\\');
            destinationDirectory = destinationDirectory.TrimEnd('/', '\\');

            foreach (var file in Directory.GetFiles(sourceDirectory))
            {
                string relPath = file.Substring(sourceDirectory.Length + 1);
                string dest = Path.Combine(destinationDirectory, relPath);
                File.Copy(file, dest, true);
                File.SetAttributes(dest, File.GetAttributes(dest) & ~FileAttributes.ReadOnly);
            }

            foreach (var dir in Directory.GetDirectories(sourceDirectory))
            {
                string relPath = dir.Substring(sourceDirectory.Length + 1);
                string newDir = Path.Combine(destinationDirectory, relPath);
                if (!Directory.Exists(newDir))
                    Directory.CreateDirectory(newDir);
                CopyDirectoryRecursive(dir, newDir);
            }
        }

    }
}
