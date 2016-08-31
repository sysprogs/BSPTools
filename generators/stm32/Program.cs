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

           public void GetMemoryMcu(MCUFamilyBuilder pfam)
            {
                if (pfam.FamilyFilePrefix.StartsWith("STM32W1"))
                {
                    string kvStr = "STM32W108HB";
                    MemoryLayout layoutW1 = new MemoryLayout { DeviceName = "STM32W108xx", Memories = new List<Memory>() };
                    layoutW1.Memories.Add(new Memory
                    {
                        Name = "FLASH",
                        Access = MemoryAccess.Undefined,// Readable | MemoryAccess.Writable | MemoryAccess.Executable
                        Type = MemoryType.FLASH,
                        Start = 0x08000000,
                        Size = 128 * 1024
                    });

                    layoutW1.Memories.Add(new Memory
                    {
                        Name = "SRAM",
                        Access = MemoryAccess.Undefined,// MemoryAccess.Writable,
                        Type = MemoryType.RAM,
                        Start = 0x20000000,
                        Size = 8 * 1024
                    });

                    _SpecialMemoryLayouts.Add(new KeyValuePair<Regex, MemoryLayout>(new Regex(kvStr.Replace('x', '.') + ".*"), layoutW1));

                }
                else {
                    string aDirIcf = pfam.Definition.StartupFileDir;
                    if (!aDirIcf.EndsWith("gcc"))
                        throw new Exception("No Gcc sturtup Tamplate");
                    aDirIcf = aDirIcf.Replace("\\gcc", "\\iar\\linker");
                    if (!Directory.Exists(aDirIcf))
                        throw new Exception("No dir " + aDirIcf);

                    foreach (var fnIcf in Directory.GetFiles(aDirIcf, "stm32*_flash.icf"))
                    {
                        string kvStr = Path.GetFileName(fnIcf).Replace("_flash.icf", "");
                        _SpecialMemoryLayouts.Add(new KeyValuePair<Regex, MemoryLayout>(new Regex(kvStr.Replace('x', '.') + ".*", RegexOptions.IgnoreCase), GetLayoutFromICF(fnIcf, kvStr)));
                    }
                }

            }
            const int  NO_DATA = -1;
            public MemoryLayout GetLayoutFromICF(string pFileNameICF,string pNameDev)
            {
                MemoryLayout layout = new MemoryLayout { DeviceName = pNameDev, Memories = new List<Memory>() };
                int StartFlash = NO_DATA;
                int SizeFlash = NO_DATA;
                /*int StartRAM = NO_DATA;
                int SizeRAM = NO_DATA;
                int StartCCM = NO_DATA;
                int SizeCCM = NO_DATA;
                */
                foreach (var ln in File.ReadAllLines(pFileNameICF))
                {
                    var m = Regex.Match(ln, @"define symbol __ICFEDIT_region_([\w\d]+)_start__[ ]*=[ ]*([x0-9A-Faf]+)[ ]*;");
                    if (m.Success)
                    {
                        StartFlash = (int)ParseHex(m.Groups[2].Value);
                        continue;
                    }
                     m = Regex.Match(ln, @"define symbol __ICFEDIT_region_([\w\d]+)_end__[ ]*=[ ]*([x0-9A-Faf]+)[ ]*;");
                    if (m.Success)
                    {
                        SizeFlash = (int)ParseHex(m.Groups[2].Value);
                        MemoryType aTypeData = MemoryType.RAM;
                        string aNameData = m.Groups[1].Value;
                        if (m.Groups[1].Value.Contains("ROM"))
                            aTypeData = MemoryType.FLASH;

                        if (m.Groups[1].Value == "ROM")
                            aNameData = "FLASH";
                        else  if (m.Groups[1].Value == "RAM")
                                aNameData = "SRAM";

                        if (StartFlash != NO_DATA && SizeFlash != NO_DATA)
                        {
                            SizeFlash  -= StartFlash;
                            if ((SizeFlash % 1024) != 0) SizeFlash += 1;
                            layout.Memories.Add(new Memory
                            {
                                Name = aNameData,
                                Access = MemoryAccess.Undefined,// Readable | MemoryAccess.Writable | MemoryAccess.Executable
                                Type = aTypeData,
                                Start = (uint)StartFlash,
                                Size = (uint)SizeFlash
                            });
                        }
                        else
                            throw new Exception("Error ld size flash");
                        StartFlash = NO_DATA;
                        continue;
                    }
                }

                return layout;
            }
            public STM32BSPBuilder(BSPDirectories dirs, string cubeDir)
                : base(dirs)
            {
                ShortName = "STM32";
            }

            public override void GenerateLinkerScriptsAndUpdateMCU(string ldsDirectory, string familyFilePrefix, MCUBuilder mcu, MemoryLayout layout, string generalizedName)
            {
                base.GenerateLinkerScriptsAndUpdateMCU(ldsDirectory, familyFilePrefix, mcu, layout, generalizedName);
            }

            public override MemoryLayout GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {

                foreach (var kv in _SpecialMemoryLayouts)
                    if (kv.Key.IsMatch(mcu.Name))
                        return kv.Value;

                MemoryLayout layout = new MemoryLayout { DeviceName = mcu.Name, Memories = new List<Memory>() };

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
                    Vectors = StartupFileGenerator.ParseInterruptVectors(fn, "g_pfnVectors:", @"/\*{10,999}|^[^/\*]+\*/
                $", @"^[ \t]+\.word[ \t]+([^ ]+)", null, @"^[ \t]+/\*|[ \t]+stm32.*|[ \t]+STM32.*", ".equ[ \t]+([^ \t]+),[ \t]+(0x[0-9a-fA-F]+)", 1, 2)
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

            //  func();
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
            List<MCUFamilyBuilder.CopiedSample> exampleDirs = new List<MCUFamilyBuilder.CopiedSample>();

            bool noPeripheralRegisters = args.Contains("/noperiph");

            var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));
            foreach (var fw in commonPseudofamily.GenerateFrameworkDefinitions())
                frameworks.Add(fw);

            foreach (var fam in allFamilies)
            {
                bspBuilder.GetMemoryMcu(fam);
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
            exampleDirs.Sort((a,b) => prioritizer.Prioritize(a.RelativePath, b.RelativePath));

            BoardSupportPackage bsp = new BoardSupportPackage
            {
                PackageID = "com.sysprogs.arm.stm32",
                PackageDescription = "STM32 Devices",
                GNUTargetID = "arm-eabi",
                GeneratedMakFileName = "stm32.mak",
                MCUFamilies = familyDefinitions.ToArray(),
                SupportedMCUs = mcuDefinitions.ToArray(),
                Frameworks = frameworks.ToArray(),
                Examples = exampleDirs.Where(s => !s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                TestExamples = exampleDirs.Where(s => s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                PackageVersion = "3.6",
                IntelliSenseSetupFile = "stm32_compat.h",
                FileConditions = bspBuilder.MatchedFileConditions.ToArray(),
                MinimumEngineVersion = "5.1",
                FirstCompatibleVersion = "3.0",
                InitializationCodeInsertionPoints = commonPseudofamily.Definition.InitializationCodeInsertionPoints,
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
