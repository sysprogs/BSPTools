/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using BSPEngine;
using LinkerScriptGenerator;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace nrf5x
{
    internal class Program
    {
        class NordicBSPBuilder : BSPBuilder
        {
            const uint FLASHBase = 0x00000000, SRAMBase = 0x20000000;

            public NordicBSPBuilder(BSPDirectories dirs)
                : base(dirs)
            {
                ShortName = "nRF5x";
                string extraSections = "|. = ALIGN(4);|PROVIDE(__start_fs_data = .);|KEEP(*(.fs_data))|PROVIDE(__stop_fs_data = .);|. = ALIGN(4);|";
                extraSections += "|. = ALIGN(4);|PROVIDE(__start_pwr_mgmt_data = .);|KEEP(*(.pwr_mgmt_data))|PROVIDE(__stop_pwr_mgmt_data = .);|. = ALIGN(4);|";

                LDSTemplate.Sections.First(s => s.Name == ".data").CustomContents = extraSections.Split('|');
            }

            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                flashBase = FLASHBase;
                ramBase = SRAMBase;
            }

            public override bool OnFilePathTooLong(string pathInsidePackage)
            {
                if (pathInsidePackage.EndsWith(".hex") || pathInsidePackage.EndsWith(".zip") || pathInsidePackage.EndsWith(".ld") || pathInsidePackage.EndsWith(".eww") || pathInsidePackage.EndsWith(".uvmpw") || pathInsidePackage.Contains("experimental") || pathInsidePackage.Contains("\\ant\\") || pathInsidePackage.Contains("\\ser_"))
                    return false;
                if (pathInsidePackage.Contains("nrf_drv_config.h") || pathInsidePackage.Contains("app_usbd_string_config.h") || pathInsidePackage.Contains(".emProject"))
                    return false;
                return base.OnFilePathTooLong(pathInsidePackage);
            }

            public override MemoryLayout GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                //No additional memory information available for this MCU. Build a basic memory layout from known RAM/FLASH sizes.
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


            public struct SoftDevice
            {
                public string Name;
                public string Description;
                public uint FLASHSize;
                public uint SRAMSize;
                public Regex DeviceRegex;
                public string LdOriginalName;

                public LDFileMemoryInfo LinkerScriptWithMaximumReservedRAM;
                public string UserFriendlyName => string.IsNullOrEmpty(Description) ? Name : $"{Name} ({Description})";

                public SoftDevice(string name, uint flash, uint sram, string deviceRegex, string desc, string pDirSdk)
                {
                    Name = name;
                    FLASHSize = flash;
                    SRAMSize = sram;
                    DeviceRegex = new Regex(deviceRegex, RegexOptions.IgnoreCase);
                    Description = desc;
                    LdOriginalName = "";
                    LinkerScriptWithMaximumReservedRAM = null;
                }

                public SoftDevice(string name, string deviceRegex, string desc, string pDirSdk)
                {
                    Name = name;
                    LinkerScriptWithMaximumReservedRAM = FindLdsFile(pDirSdk, Name);
                    LdOriginalName = LinkerScriptWithMaximumReservedRAM.FullPath;
                    FLASHSize = (uint)LinkerScriptWithMaximumReservedRAM.FLASH.Origin;
                    SRAMSize = (uint)LinkerScriptWithMaximumReservedRAM.RAM.Origin - SRAMBase;
                    DeviceRegex = new Regex(deviceRegex, RegexOptions.IgnoreCase);
                    Description = desc;
                }

                static LDFileMemoryInfo FindLdsFile(string pDir, string sdname)
                {
                    var allMatchingLinkerScripts = Directory.GetFiles(pDir, "*.ld", SearchOption.AllDirectories)
                        .Where(fn => !fn.Contains("bootloader"))
                        .Where(fn => fn.IndexOf($"\\{sdname}\\", StringComparison.InvariantCultureIgnoreCase) != -1)
                        .Select(fn => new LDFileMemoryInfo(fn))
                        .Where(i => i.HasAllNecessarySymbols)
                        .ToArray();

                    var maxRAM = allMatchingLinkerScripts.OrderBy(s => s.RAM.Origin).Last();
                    //            var maxRAM = allMatchingLinkerScripts.OrderBy(s => s.RAM.Length).Last();
                    var maxFLASH = allMatchingLinkerScripts.OrderBy(s => s.FLASH.Origin).Last();
                    //                   var maxFLASH = allMatchingLinkerScripts.OrderBy(s => s.FLASH.Length).Last();

                    if (!maxFLASH.FLASH.Equals(maxRAM.FLASH))
                        throw new Exception("Inconsistent maximum linker scripts"); //The 'max RAM' script has a different FLASH size than the 'max FLASH' script.

                    return maxRAM;
                }

                public bool IsCompatible(string name)
                {
                    return DeviceRegex.IsMatch(name);
                }
            }


            public List<SoftDevice> SoftDevices = new List<SoftDevice>();


            public const string SoftdevicePropertyID = "com.sysprogs.bspoptions.nrf5x.softdevice";

            public override void GenerateLinkerScriptsAndUpdateMCU(string ldsDirectory, string familyFilePrefix, MCUBuilder mcu, MemoryLayout layout, string generalizedName)
            {
                foreach (var sd in SoftDevices)
                {
                    if (!sd.IsCompatible(mcu.Name))
                        continue;
                    if (sd.LinkerScriptWithMaximumReservedRAM == null)
                    {
                        //Due to the complexity of the Nordic SDK, we need to reuse the original linker scripts instead of generating them from scratch.
                        throw new Exception("Could not locate the original linker script for this device.");
                    }
                    else
                    {
                        BuildLinkerScriptBasedOnOriginalNordicScripts(ldsDirectory, generalizedName, sd);
                    }
                }
                mcu.LinkerScriptPath = $"$$SYS:BSP_ROOT$$/{familyFilePrefix}LinkerScripts/{generalizedName}_$${SoftdevicePropertyID}$$.lds";
            }

            enum LinkerScriptGenerationPass
            {
                BeforeFirst,

                Regular,
                Reserve,
                Nosoftdev,

                AfterLast
            }

            static void DoBuildLinkerScriptBasedOnOriginalNordicScripts(string ldsDirectory, string generalizedName, SoftDevice sd, LinkerScriptGenerationPass pass)
            {
                string[] providedSymbols =
                {
                    "PROVIDE(_sbss = __bss_start__);",
                    "PROVIDE(_ebss = __bss_end__);",
                    "PROVIDE(_sdata = __data_start__);",
                    "PROVIDE(_sidata = __etext);",
                    "PROVIDE(_estack = __StackTop);",
                    "PROVIDE(_edata =__data_end__);",
                    "PROVIDE(__isr_vector = __StackTop);",
                    "PROVIDE(_etext = __etext);"
                };

                List<string> lines = File.ReadAllLines(sd.LdOriginalName).ToList();
                lines.Insert(0, $"/* Based on {sd.LdOriginalName} */");

                InsertPowerMgmtData(lines);

                var m = Regex.Match(lines.Find(s => s.Contains("INCLUDE")) ?? " ", "INCLUDE[ ]*\"([a-z0-9_.]*)");
                if (m.Success)
                {
                    string[] incf = Directory.GetFiles(bspBuilder.Directories.InputDir, m.Groups[1].Value, SearchOption.AllDirectories);
                    if (incf.Length > 1)
                        throw new Exception("Ambiguous 'common' include file.");
                    if (!File.Exists(Path.Combine(ldsDirectory, m.Groups[1].Value)))
                    {
                        string commonLds = Path.Combine(ldsDirectory, m.Groups[1].Value);

                        var commonLines = File.ReadAllLines(incf[0]).ToList();
                        var idx = commonLines.IndexOf("    .text :");
                        if (idx == -1)
                            throw new Exception("Could not find the beginning of section .text");
                        commonLines.Insert(idx, "    _stext = .;");

                        File.WriteAllLines(commonLds, commonLines.ToArray());
                    }
                }

                if (pass == LinkerScriptGenerationPass.Nosoftdev)
                {
                    var indFl = lines.FindOrThrow(s => s.Contains("FLASH"));
                    lines[indFl] = $"  FLASH (RX) :  ORIGIN = 0x{FLASHBase:x}, LENGTH = 0x{sd.LinkerScriptWithMaximumReservedRAM.FLASH.Origin + sd.LinkerScriptWithMaximumReservedRAM.FLASH.Length:x}";
                }
                else
                {
                    var mems = sd.LinkerScriptWithMaximumReservedRAM;

                    lines.Insert(lines.FindOrThrow(s => s.Contains("FLASH")), $"  FLASH_SOFTDEVICE (RX) : ORIGIN = 0x{FLASHBase:x8}, LENGTH = 0x{mems.FLASH.Origin - FLASHBase:x8}");
                    lines.Insert(lines.FindOrThrow(s => s.Contains("RAM")), $"  SRAM_SOFTDEVICE (RWX) : ORIGIN = 0x{SRAMBase:x8}, LENGTH = 0x{mems.RAM.Origin - SRAMBase:x8}");
                    var idxSectionList = lines.FindOrThrow(s => s == "SECTIONS") + 1;
                    while (lines[idxSectionList].Trim() == "{")
                        idxSectionList++;

                    if (lines[idxSectionList].Contains(". = ALIGN"))
                        idxSectionList++;

                    if (pass == LinkerScriptGenerationPass.Regular)
                    {
                        lines.InsertRange(idxSectionList, new[] {
                            "  .softdevice :",
                            "  {",
                            "    KEEP(*(.softdevice))",
                            "    FILL(0xFFFFFFFF);",
                            $"    . = 0x{mems.FLASH.Origin - FLASHBase:x8};",
                            "  } > FLASH_SOFTDEVICE",
                            "",
                            "  .softdevice_sram :",
                            "  {",
                            "    FILL(0xFFFFFFFF);",
                            $"    . = 0x{mems.RAM.Origin - SRAMBase:x8};",
                            "  } > SRAM_SOFTDEVICE"
                            });

                        lines.Insert(lines.FindOrThrow(s => s.Contains("MEMORY")), $"GROUP({sd.Name}_softdevice.o)");
                    }

                }

                lines.AddRange(providedSymbols);

                string suffix;
                if (pass == LinkerScriptGenerationPass.Nosoftdev)
                    suffix = "nosoftdev";
                else if (pass == LinkerScriptGenerationPass.Reserve)
                    suffix = $"{sd.Name.ToLower()}_reserve";
                else
                    suffix = sd.Name.ToLower();

                File.WriteAllLines(Path.Combine(ldsDirectory, $"{generalizedName}_{suffix}.lds"), lines);
            }

            static void BuildLinkerScriptBasedOnOriginalNordicScripts(string ldsDirectory, string generalizedName, SoftDevice sd)
            {
                for (LinkerScriptGenerationPass pass = LinkerScriptGenerationPass.BeforeFirst + 1; pass < LinkerScriptGenerationPass.AfterLast; pass++)
                    DoBuildLinkerScriptBasedOnOriginalNordicScripts(ldsDirectory, generalizedName, sd, pass);
            }

            private static void InsertPowerMgmtData(List<string> lines)
            {
                int idx = lines.IndexOf("  .log_const_data :");
                if (idx == -1)
                    throw new Exception("Could not find the beginning of section .text");

                lines.InsertRange(idx, new string[]
                {
                    "   .pwr_mgmt_data :",
                    "  {",
                    "    PROVIDE(__start_pwr_mgmt_data = .);",
                    "    KEEP(*(SORT(.pwr_mgmt_data*)))",
                    "    PROVIDE(__stop_pwr_mgmt_data = .);",
                    "  } > FLASH"
                });
            }

            internal void GenerateSoftdeviceLibraries()
            {
                foreach (var sd in SoftDevices)
                {
                    string sdDir = BSPRoot + @"\nRF5x\components\softdevice\" + sd.Name + @"\hex";
                    string abi = "";
                    if (sd.Name == "S132" || sd.Name == "S140")
                    {
                        abi = " \"-mfloat-abi=hard\" \"-mfpu=fpv4-sp-d16\"";
                    }
                    if (sd.Name == "s1xx_iot")
                    {
                        sdDir = BSPRoot + @"\nRF5x\components\softdevice\" + sd.Name;
                        abi = " \"-mfloat-abi=hard\" \"-mfpu=fpv4-sp-d16\"";
                    }
                    if (sd.Name == "S112")
                    {
                        abi = " \"-mfloat-abi=soft\"";
                    }

                    string hexFileName = Path.GetFullPath(Directory.GetFiles(sdDir, "*.hex")[0]);
                    var info = new ProcessStartInfo { FileName = BSPRoot + @"\nRF5x\SoftdeviceLibraries\ConvertSoftdevice.bat", Arguments = sd.Name + " " + hexFileName + abi, UseShellExecute = false };
                    info.EnvironmentVariables["PATH"] += @";c:\sysgcc\arm-eabi\bin";
                    Process.Start(info).WaitForExit();
                    string softdevLib = string.Format(@"{0}\nRF5x\SoftdeviceLibraries\{1}_softdevice.o", BSPRoot, sd.Name);
                    if (!File.Exists(softdevLib) || File.ReadAllBytes(softdevLib).Length < 32768)
                        throw new Exception("Failed to convert a softdevice");
                }
            }

            public void GenerateConditionsBoard(ref List<EmbeddedFramework> prFrBoard)
            {
                List<PropertyEntry.Enumerated.Suggestion> lstProp = new List<PropertyEntry.Enumerated.Suggestion>();
                var propertyGroup = prFrBoard.SingleOrDefault(fr => fr.ID.Equals("com.sysprogs.arm.nordic.nrf5x.boards")).
                                            ConfigurableProperties.PropertyGroups.
                                                SingleOrDefault(pg => pg.UniqueID.Equals("com.sysprogs.bspoptions.nrf5x.board."));

                var rgBoardIfdef = new Regex("#(if|elif) defined\\(BOARD_([A-Z0-9a-z_]+)\\)");
                var rgInclude = new Regex("#include \"([^\"]+)\"");

                var lines = File.ReadAllLines(Path.Combine(Directories.OutputDir, @"nRF5x\components\boards\boards.h"));
                lstProp.Add(new PropertyEntry.Enumerated.Suggestion() { InternalValue = "", UserFriendlyName = "None" });

                for (int i = 0; i < lines.Length; i++)
                {
                    var m = rgBoardIfdef.Match(lines[i]);
                    if (!m.Success)
                        continue;

                    string boardID = m.Groups[2].Value;
                    string file = rgInclude.Match(lines[i + 1]).Groups[1].Value;

                    MatchedFileConditions.Add(new FileCondition()
                    {
                        ConditionToInclude = new Condition.Equals()
                        {
                            Expression = "$$com.sysprogs.bspoptions.nrf5x.board.type$$",
                            ExpectedValue = boardID,
                            IgnoreCase = false
                        },
                        FilePath = "nRF5x/components/boards/" + file
                    });
                    lstProp.Add(new PropertyEntry.Enumerated.Suggestion() { InternalValue = boardID });
                }
                //--ConfigurableProperties--

                propertyGroup.Properties.Add(new PropertyEntry.Enumerated
                {
                    UniqueID = "type",
                    Name = "Board Type",
                    DefaultEntryIndex = Enumerable.Range(0, lstProp.Count).First(i => lstProp[i].InternalValue == "PCA10040"),
                    SuggestionList = lstProp.ToArray()
                });
            }
        }

        static StartupFileGenerator.InterruptVectorTable GenerateStartupFile(string pDir, string pFBase)
        {
            var vectorTable = new StartupFileGenerator.InterruptVectorTable
            {
                FileName = "startup_" + pFBase + "x.c",
                Vectors = StartupFileGenerator.ParseInterruptVectors(Path.Combine(pDir, "arm_startup_" + pFBase + ".s"),
                    "^__Vectors",
                    @"__Vectors_End",
                    @"^[ \t]+DCD[ \t]+([^ \t]+)[ \t]+; *([^ \t].*)$",
                    @"^[ \t]+DCD[ \t]+([^ \t]+)$",
                    @"^[ \t]+;.*",
                    null,
                    1,
                    2),
            };

            if (pFBase.ToLower() == "nrf51")
            {
                vectorTable.AdditionalResetHandlerLines = new string[]
                {
                        "asm volatile(\".equ NRF_POWER_RAMON_ADDRESS,0x40000524\");",
                        "asm volatile(\".equ NRF_POWER_RAMON_RAMxON_ONMODE_Msk,3\");",
                        "asm volatile(\"LDR     R0, =NRF_POWER_RAMON_ADDRESS\");",
                        "asm volatile(\"LDR     R2, [R0]\");",
                        "asm volatile(\"MOVS    R1, #NRF_POWER_RAMON_RAMxON_ONMODE_Msk\");",
                        "asm volatile(\"ORR     R2, R2, R1\");",
                        "asm volatile(\"STR     R2, [R0]\");",
                };
            }

            vectorTable.Vectors = new StartupFileGenerator.InterruptVector[] { new StartupFileGenerator.InterruptVector { Name = "_estack" } }.Concat(vectorTable.Vectors).ToArray();

            vectorTable.MatchPredicate = m => m.Name.StartsWith(pFBase);
            return vectorTable;
        }

        struct SingleMemoryInfo
        {
            public ulong Length;
            public ulong Origin;
        }

        class LDFileMemoryInfo
        {
            public readonly SingleMemoryInfo FLASH, RAM;
            public readonly string FullPath;

            bool _HasBLEObservers, _HasPowerMgt;

            public bool HasAllNecessarySymbols => _HasBLEObservers;// && _HasPowerMgt;

            public override string ToString() => FullPath;

            public LDFileMemoryInfo(string fn)
            {
                FullPath = fn;
                foreach (var line in File.ReadAllLines(fn))
                {
                    if (line.Contains("__stop_sdh_ble_observers"))
                        _HasBLEObservers = true;
                    if (line.Contains("__start_pwr_mgmt_data"))
                        _HasBLEObservers = true;

                    var m = Regex.Match(line, $".*(FLASH|RAM).*ORIGIN[ =]+0x([a-fA-F0-9]+).*LENGTH[ =]+0x([a-fA-F0-9]+)");
                    if (m.Success)
                    {
                        var info = new SingleMemoryInfo
                        {
                            Origin = ulong.Parse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber),
                            Length = ulong.Parse(m.Groups[3].Value, System.Globalization.NumberStyles.HexNumber)
                        };

                        switch (m.Groups[1].Value)
                        {
                            case "FLASH":
                                FLASH = info;
                                break;
                            case "RAM":
                                RAM = info;
                                break;
                            default:
                                throw new Exception("Unexpected memory: " + m.Groups[1].Value);
                        }
                    }
                }

                if (FLASH.Length == 0 || FLASH.Origin == 0)
                    throw new Exception("Missing FLASH in " + fn);
                if (RAM.Length == 0 || RAM.Origin == 0)
                    throw new Exception("Missing RAM in " + fn);
            }
        }

        class NordicFamilyBuilder : MCUFamilyBuilder
        {
            public NordicFamilyBuilder(BSPBuilder bspBuilder, FamilyDefinition definition)
                : base(bspBuilder, definition)
            {
            }

            protected override void OnMissingSampleFile(MissingSampleFileArgs args)
            {
                string path = args.ExpandedPath;
                if (path.Contains("pca10040e/s112"))
                {
                    string originalFn = path.Replace("pca10040e/s112", "pca10040/s132");
                    if (ReplaceFile(originalFn, path))
                        return;
                }

                base.OnMissingSampleFile(args);
            }

            bool ReplaceFile(string originalFn, string path)
            {
                if (File.Exists(originalFn))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.Copy(originalFn, path);
                    return true;
                }
                return false;
            }
        }

        static List<PropertyEntry.Enumerated> ReadCompositePropery(string nameFile)
        {
            List<PropertyEntry.Enumerated> lsPr = new List<PropertyEntry.Enumerated>();
            foreach (var ln in File.ReadAllLines(Path.Combine(bspBuilder.Directories.RulesDir, nameFile)))
            {
                if (ln.StartsWith("\t") || ln.StartsWith("//"))
                    continue;
                Match m = Regex.Match(ln, "(.*):(.*)");
                if (!m.Success)
                    continue;
                var name = m.Groups[1].Value;
                var prop = m.Groups[2].Value;

                List<PropertyEntry.Enumerated.Suggestion> lstSugg = new List<PropertyEntry.Enumerated.Suggestion>();
                foreach (var sg in prop.Trim(' ').Split(';'))
                {
                    Match m1 = Regex.Match(sg, "(.*)=(.*)");
                    lstSugg.Add(new PropertyEntry.Enumerated.Suggestion
                    {
                        UserFriendlyName = m1.Groups[1].Value,
                        InternalValue = m1.Groups[2].Value
                    });

                }
                lsPr.Add(new PropertyEntry.Enumerated { Name = name, UniqueID = name, SuggestionList = lstSugg.ToArray() });
            }

            return lsPr;
        }

        static List<string> ReadCompositeCondidtions(string nameFile, string name_fr)
        {
            List<string> lsPrContin = new List<string>();
            string nameLib = "";
            foreach (var ln in File.ReadAllLines(Path.Combine(bspBuilder.Directories.RulesDir, nameFile)))
            {
                if (ln.StartsWith("//") || ln == "")
                    continue;
                if (!ln.StartsWith("\t"))
                {
                    nameLib = ln.Remove(ln.IndexOf(':'));
                    continue;
                }

                lsPrContin.Add(ln.Trim('\t').Replace(":", $": $$com.sysprogs.bspoptions.nrf5x.{name_fr}.{nameLib}$$"));

            }

            return lsPrContin;
        }

        static void GenerateConditionsLibriries(Framework[] fr, string name_lib)//libraries
        {
            if (name_lib.StartsWith("experimental_"))
                name_lib = name_lib.Replace("experimental_", "");

            var compositeProp = ReadCompositePropery($"NRF5x{ name_lib}.txt");

            List<PropertyEntry.Boolean> lstProp = new List<PropertyEntry.Boolean>();

            lstGenFramworks.Add($"com.sysprogs.arm.nordic.nrf5x.{name_lib}");
            var propertysFr = fr.SingleOrDefault(f => f.ID.Equals($"com.sysprogs.arm.nordic.nrf5x.{name_lib}")).
                                        ConfigurableProperties.PropertyGroups.
                                            SingleOrDefault(pg => pg.UniqueID.Equals($"com.sysprogs.bspoptions.nrf5x.{name_lib}.")).Properties;

            List<string> lstConditions = new List<string>();
            var dirLib = $@"components\{name_lib}";
            if (name_lib == "modules_nrfx")
                dirLib = @"modules\nrfx";
            foreach (var fb in Directory.GetDirectories(Path.Combine(bspBuilder.Directories.InputDir, $@"{dirLib}")))
            {
                string namelibrary = fb.Remove(0, fb.LastIndexOf('\\') + 1);
                string Conditions = $@"{namelibrary}\\.*: $$com.sysprogs.bspoptions.nrf5x.{name_lib}.{namelibrary}$$ == yes";
                if (compositeProp.Where(n => n.Name == namelibrary).Count() > 0)
                    continue;

                lstConditions.Add(Conditions);

                lstProp.Add(new PropertyEntry.Boolean()
                { Name = namelibrary, UniqueID = namelibrary, DefaultValue = false, ValueForTrue = "yes" });

            }
            //--ConfigurableProperties--

            lstConditions.AddRange(ReadCompositeCondidtions($"NRF5x{ name_lib}.txt", name_lib));

            fr.SingleOrDefault(f => f.ID.Equals($"com.sysprogs.arm.nordic.nrf5x.{name_lib}")).
                CopyJobs.SingleOrDefault(c => c.SourceFolder.Equals($@"$$BSPGEN:INPUT_DIR$$\{dirLib}")).
                SimpleFileConditions = lstConditions.ToArray();


            propertysFr.AddRange(lstProp.ToArray());
            propertysFr.AddRange(compositeProp.ToArray());

        }
        static List<string> lstGenFramworks;
        static List<PropertyDictionary2.KeyValue> lstGenConditions;
        static void GenFramworkSample()
        {
            var fsample = XmlTools.LoadObject<EmbeddedProjectSample>(Path.Combine(bspBuilder.Directories.RulesDir, "FramworkSamples", "sample.xml"));

            lstGenFramworks.AddRange(fsample.RequiredFrameworks);
            fsample.RequiredFrameworks = lstGenFramworks.Distinct().ToArray();
            lstGenConditions.AddRange(fsample.DefaultConfiguration.Entries);
            fsample.DefaultConfiguration.Entries = lstGenConditions.ToArray();

            Directory.CreateDirectory(Path.Combine(bspBuilder.Directories.OutputDir, "FramworkSamples"));
            XmlTools.SaveObject(fsample, Path.Combine(bspBuilder.Directories.OutputDir, "FramworkSamples", "sample.xml"));
            foreach (var fl in Directory.GetFiles(Path.Combine(bspBuilder.Directories.RulesDir, "FramworkSamples"), "*.c"))
                File.Copy(fl, Path.Combine(bspBuilder.Directories.OutputDir, "FramworkSamples", Path.GetFileName(fl)));

        }
        static void CheckEntriesSample(string DirLibs, string dirSamples)
        {
            const string PrefixEntr = "com.sysprogs.bspoptions.nrf5x.libraries.";
            const string strreplace = "experimental_";
            Dictionary<string, EmbeddedProjectSample> lstSamples = new Dictionary<string, EmbeddedProjectSample>();
            foreach (var fl in Directory.GetFiles(dirSamples, "*.xml", SearchOption.AllDirectories))
                lstSamples.Add(fl.Replace(dirSamples, ""), XmlTools.LoadObject<EmbeddedProjectSample>(fl));

            var lstLibs = Directory.GetDirectories(DirLibs);
            for (int i = 0; i < lstLibs.Count(); i++)
                lstLibs[i] = lstLibs[i].Split(new char[] { '\\' }).Reverse().ToArray()[0];

            foreach (var smpl in lstSamples)
                foreach (var Entri in smpl.Value.DefaultConfiguration.Entries)
                    if (lstLibs.Where(l => PrefixEntr + l == Entri.Key).Count() == 0)
                    {
                        string msg = $"No library {Entri.Key} in samples " + smpl.Value.Name;
                        Console.WriteLine(msg);
                        Debug.WriteLine(msg);
                        if (Entri.Key.Contains(strreplace))
                            if (lstLibs.Where(l => PrefixEntr + l == Entri.Key.Replace(strreplace, "")).Count() != 0)
                                Entri.Key = Entri.Key.Replace(strreplace, "");
                    }

            foreach (var smpl in lstSamples)
            {
                var vv = Path.GetDirectoryName(smpl.Key).TrimStart('\\');
                if (Directory.Exists(Path.Combine(dirSamples, vv)))
                    Directory.Delete(Path.Combine(dirSamples, vv), true);
                Directory.CreateDirectory(Path.Combine(dirSamples, vv));
                XmlTools.SaveObject(smpl.Value, Path.Combine(dirSamples, smpl.Key.TrimStart('\\')));
            }
        }

        static NordicBSPBuilder bspBuilder;
        static void Main(string[] args)
        {

            if (args.Length < 1)
                throw new Exception("Usage: nrf5x.exe <Nordic SW package directory>");

            //    CheckEntriesSample(@"d:\DataJon\Projects\sysprogs\BSPTools\generators\nrf5x\Output\nRF5x\components\libraries",
            //            Path.Combine(@"d:\DataJon\Projects\sysprogs\BSPTools\generators\nrf5x\rules", "Samples"));
            //     return;
            bool usingIoTSDK = false;


            if (usingIoTSDK)
            {
                bspBuilder = new NordicBSPBuilder(new BSPDirectories(args[0], @"..\..\Output_i", @"..\..\rules_iot"));
                //        bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("s1xx_iot", 0x1f000, 0x2800, "nrf52", "IoT", bspBuilder.Directories.InputDir));
                bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("S132", "nrf52832.*", null, bspBuilder.Directories.InputDir));
                bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("S140", "nrf52840.*", null, bspBuilder.Directories.InputDir));
                //bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("S132", 0x1f000, 0x2800, "nrf52", "IoT", bspBuilder.Directories.InputDir));
            }
            else
            {
                bspBuilder = new NordicBSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules"));
                bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("S132", "nrf52832.*", null, bspBuilder.Directories.InputDir));
                bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("S140", "nrf52840.*", null, bspBuilder.Directories.InputDir));
                bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("S112", "nrf52810.*", null, bspBuilder.Directories.InputDir));
            }


            List<MCUBuilder> devices = new List<MCUBuilder>();
            lstGenFramworks = new List<string>();
            lstGenConditions = new List<PropertyDictionary2.KeyValue>();
#if NRF51_SUPPORT
            if (!usingIoTSDK)
                foreach (string part in new string[] { "nRF51822", "nRF51422" })
                {
                    devices.Add(new MCUBuilder { Name = part + "_XXAA", FlashSize = 256 * 1024, RAMSize = 16 * 1024, Core = CortexCore.M0 });
                    devices.Add(new MCUBuilder { Name = part + "_XXAB", FlashSize = 128 * 1024, RAMSize = 16 * 1024, Core = CortexCore.M0 });
                    devices.Add(new MCUBuilder { Name = part + "_XXAC", FlashSize = 256 * 1024, RAMSize = 32 * 1024, Core = CortexCore.M0 });
                }
#endif


            devices.Add(new MCUBuilder { Name = "nRF52832_XXAA", FlashSize = 512 * 1024, RAMSize = 64 * 1024, Core = CortexCore.M4, StartupFile = "$$SYS:BSP_ROOT$$/nRF5x/modules/nrfx/mdk/gcc_startup_nrf52.S" });
            devices.Add(new MCUBuilder { Name = "nRF52840_XXAA", FlashSize = 1024 * 1024, RAMSize = 256 * 1024, Core = CortexCore.M4, StartupFile = "$$SYS:BSP_ROOT$$/nRF5x/modules/nrfx/mdk/gcc_startup_nrf52840.S" });
            if (!usingIoTSDK)
                devices.Add(new MCUBuilder { Name = "nRF52810_XXAA", FlashSize = 192 * 1024, RAMSize = 24 * 1024, Core = CortexCore.M4_NOFPU, StartupFile = "$$SYS:BSP_ROOT$$/nRF5x/modules/nrfx/mdk/gcc_startup_nrf52810.S" });

            List<MCUFamilyBuilder> allFamilies = new List<MCUFamilyBuilder>();
            foreach (var fn in Directory.GetFiles(bspBuilder.Directories.RulesDir + @"\Families", "*.xml"))
                allFamilies.Add(new NordicFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(fn)));

            var rejects = BSPGeneratorTools.AssignMCUsToFamilies(devices, allFamilies);

            List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
            List<MCUFamilyBuilder.CopiedSample> exampleDirs = new List<MCUFamilyBuilder.CopiedSample>();

            //            bool noPeripheralRegisters = true;
            bool noPeripheralRegisters = false;

            List<MCUFamily> familyDefinitions = new List<MCUFamily>();
            List<MCU> mcuDefinitions = new List<MCU>();

            var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));
            var flags = new ToolFlags();
            List<string> projectFiles = new List<string>();
            commonPseudofamily.CopyFamilyFiles(ref flags, projectFiles);
            flags = flags.Merge(commonPseudofamily.Definition.CompilationFlags);

            List<ConditionalToolFlags> condFlags = new List<ConditionalToolFlags>();

            foreach (var fam in allFamilies)
            {
                fam.GenerateLinkerScripts(false);
                Console.WriteLine("Processing " + fam.Definition.Name + " family...");
                string famBase = fam.Definition.Name.Substring(0, 5).ToLower();

                var rejectedMCUs = fam.RemoveUnsupportedMCUs(true);
                if (rejectedMCUs.Length != 0)
                {
                    Console.WriteLine("Unsupported {0} MCUs:", fam.Definition.Name);
                    foreach (var mcu in rejectedMCUs)
                        Console.WriteLine("\t{0}", mcu.Name);
                }

                GenerateConditionsLibriries(fam.Definition.AdditionalFrameworks, "libraries");
                GenerateConditionsLibriries(fam.Definition.AdditionalFrameworks, "drivers_nrf");
                GenerateConditionsLibriries(fam.Definition.AdditionalFrameworks, "drivers_ext");
                GenerateConditionsLibriries(fam.Definition.AdditionalFrameworks, "iot");



                List<Framework> bleFrameworks = new List<Framework>();
                foreach (var line in File.ReadAllLines(bspBuilder.Directories.RulesDir + @"\BLEFrameworks.txt"))
                {
                    int idx = line.IndexOf('|');
                    string dir = line.Substring(0, idx);
                    string desc = line.Substring(idx + 1);

                    string id = Path.GetFileName(dir);
                    if (!id.StartsWith("ble_"))
                        id = "ble_" + id;

                    if (dir.StartsWith("services\\", StringComparison.CurrentCultureIgnoreCase))
                        id = "ble_svc_" + id.Substring(4);

                    bleFrameworks.Add(new Framework
                    {
                        Name = string.Format("Bluetooth LE - {0} ({1})", desc, Path.GetFileName(dir)),
                        ID = "com.sysprogs.arm.nordic." + famBase + "." + id,
                        ClassID = "com.sysprogs.arm.nordic.nrfx." + id,
                        ProjectFolderName = "BLE " + desc,
                        DefaultEnabled = false,
                        CopyJobs = new CopyJob[]
                        {
                            new CopyJob
                            {
                                SourceFolder = allFamilies[0].Definition.PrimaryHeaderDir + @"\..\..\..\components\ble\" + dir,
                                TargetFolder = dir,
                                FilesToCopy = "*.c;*.h",
                            }
                        }
                    });
                }

                fam.Definition.AdditionalFrameworks = fam.Definition.AdditionalFrameworks.Concat(bleFrameworks).ToArray();

                // Starting from SDK 14.0 we use the original Nordic startup files & linker scripts as they contain various non-trivial logic
#if GENERATE_STARTUP_FILES
                StartupFileGenerator.InterruptVectorTable[] aStartupVectors;
                if (usingIoTSDK)
                    aStartupVectors = new StartupFileGenerator.InterruptVectorTable[] {
                                                    GenerateStartupFile(fam.Definition.StartupFileDir,"nRF52")
                                                    };
                else
                    aStartupVectors = new StartupFileGenerator.InterruptVectorTable[] {
                                                    GenerateStartupFile(fam.Definition.StartupFileDir,"nRF51"),
                                                    GenerateStartupFile(fam.Definition.StartupFileDir,"nRF52")
                                                    };

                fam.AttachStartupFiles(aStartupVectors);
#endif

                //  SVD Files
                var aMcuDef1 = (new MCUDefinitionWithPredicate[] { SVDParser.ParseSVDFile(Path.Combine(fam.Definition.PrimaryHeaderDir, "nRF51.svd"), "nRF51") });
                aMcuDef1[0].MatchPredicate = m => m.Name.StartsWith("nRF51");

                var aMcuDef2 = (new MCUDefinitionWithPredicate[] { SVDParser.ParseSVDFile(Path.Combine(fam.Definition.PrimaryHeaderDir, "nRF52.svd"), "nRF52") });
                aMcuDef2[0].MatchPredicate = m => m.Name.StartsWith("nRF52");

                fam.AttachPeripheralRegisters(aMcuDef1.Concat(aMcuDef2));

                var famObj = fam.GenerateFamilyObject(true);

                famObj.AdditionalSourceFiles = LoadedBSP.Combine(famObj.AdditionalSourceFiles, projectFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).ToArray());
                famObj.AdditionalHeaderFiles = LoadedBSP.Combine(famObj.AdditionalHeaderFiles, projectFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).ToArray());

                famObj.AdditionalSystemVars = LoadedBSP.Combine(famObj.AdditionalSystemVars, commonPseudofamily.Definition.AdditionalSystemVars);
                famObj.CompilationFlags = famObj.CompilationFlags.Merge(flags);

                familyDefinitions.Add(famObj);
                fam.GenerateLinkerScripts(false);

                SysVarEntry suffixEntry = null;

                foreach (var mcu in fam.MCUs)
                {
                    var mcuDef = mcu.GenerateDefinition(fam, bspBuilder, !noPeripheralRegisters, false, MCUFamilyBuilder.CoreSpecificFlags.All & ~MCUFamilyBuilder.CoreSpecificFlags.PrimaryMemory);

                    if (mcu.Name.StartsWith("nRF52832"))
                    {
                        //Although documented as a legacy definition, skipping this breaks fds_internal_defs.h
                        mcuDef.CompilationFlags.PreprocessorMacros = mcuDef.CompilationFlags.PreprocessorMacros.Concat(new[] { "NRF52" }).ToArray();
                    }

                    var compatibleSoftdevs = new[]
                        {
                            new PropertyEntry.Enumerated.Suggestion {InternalValue = "nosoftdev", UserFriendlyName = "None"}
                        }
                        .Concat(bspBuilder.SoftDevices.Where(sd => sd.IsCompatible(mcu.Name))
                            .SelectMany(s => new[]
                            {
                                new PropertyEntry.Enumerated.Suggestion {InternalValue = s.Name, UserFriendlyName = s.UserFriendlyName},
                                new PropertyEntry.Enumerated.Suggestion { InternalValue = s.Name + "_reserve", UserFriendlyName = $"{s.UserFriendlyName} (programmed separately)"}
                             }))
                        .ToArray();

                    if (mcuDef.ConfigurableProperties == null)
                        mcuDef.ConfigurableProperties = new PropertyList { PropertyGroups = new List<PropertyGroup>() };
                    mcuDef.ConfigurableProperties.PropertyGroups.Add(new PropertyGroup
                    {
                        Properties = new List<PropertyEntry>
                        {
                            new PropertyEntry.Enumerated
                            {
                                UniqueID = NordicBSPBuilder.SoftdevicePropertyID,
                                Name = "Softdevice",
                                DefaultEntryIndex = 1,
                                SuggestionList = compatibleSoftdevs,
                            }
                        }
                    });

                    if (mcu.Name.StartsWith("nRF52") && !mcu.Name.StartsWith("nRF52810"))
                    {
                        var prop = mcuDef.ConfigurableProperties.PropertyGroups[0].Properties.Find(p => p.UniqueID == "com.sysprogs.bspoptions.arm.floatmode") as PropertyEntry.Enumerated;
                        var idx = Array.FindIndex(prop.SuggestionList, p => p.UserFriendlyName == "Hardware");
                        prop.DefaultEntryIndex = idx;
                        prop.SuggestionList[idx].UserFriendlyName = "Hardware (required when using a softdevice)";   //Otherwise the system_nrf52.c file won't initialize the FPU and the internal initialization of the softdevice will later fail.
                    }

                    string defaultConfig;
                    if (mcu.Name.StartsWith("nRF52840"))
                        defaultConfig = "pca10056/s140";
                    else if (mcu.Name.StartsWith("nRF52810"))
                        defaultConfig = "pca10040e/s112";
                    else
                        defaultConfig = "pca10040/s132";

                    suffixEntry = new SysVarEntry { Key = "com.sysprogs.nordic.default_config_suffix", Value = defaultConfig };
                    mcuDef.AdditionalSystemVars = LoadedBSP.Combine(mcuDef.AdditionalSystemVars, new SysVarEntry[] { suffixEntry,
                    new SysVarEntry { Key = "com.sysprogs.nordic.default_config_suffix_blank", Value = "pca10040" } });

                    mcuDefinitions.Add(mcuDef);
                }

                if (fam.Definition.ConditionalFlags != null)
                    condFlags.AddRange(fam.Definition.ConditionalFlags);

                foreach (var fw in fam.GenerateFrameworkDefinitions())
                    frameworks.Add(fw);
                string dirpca = "pca10040e/s112";
                if (usingIoTSDK)
                    dirpca = "pca10040/s132";
                foreach (var sample in fam.CopySamples(null, new SysVarEntry[] { new SysVarEntry { Key = "com.sysprogs.nordic.default_config_suffix", Value =dirpca },
                    new SysVarEntry { Key = "com.sysprogs.nordic.default_config_suffix_blank", Value = "pca10040" } }))
                    exampleDirs.Add(sample);
            }

            const string softdevExpression = "$$com.sysprogs.bspoptions.nrf5x.softdevice$$";

            foreach (var softdev in bspBuilder.SoftDevices)
                condFlags.Add(new ConditionalToolFlags
                {
                    FlagCondition = new Condition.Equals { Expression = softdevExpression, ExpectedValue = softdev.Name + "_reserve" },
                    Flags = new ToolFlags
                    {
                        PreprocessorMacros = familyDefinitions.First().CompilationFlags.PreprocessorMacros.Where(f => f.Contains(softdevExpression)).Select(f => f.Replace(softdevExpression, softdev.Name)).ToArray(),
                        IncludeDirectories = familyDefinitions.First().CompilationFlags.IncludeDirectories.Where(f => f.Contains(softdevExpression)).Select(f => f.Replace(softdevExpression, softdev.Name)).ToArray()
                    }
                });

            bspBuilder.GenerateSoftdeviceLibraries();
            bspBuilder.GenerateConditionsBoard(ref frameworks);
            GenFramworkSample();

            CheckEntriesSample(Path.Combine(bspBuilder.Directories.OutputDir, @"nRF5x\components\libraries"),
                                Path.Combine(bspBuilder.Directories.OutputDir, "Samples"));

            Console.WriteLine("Building BSP archive...");
            string strPackageID, strPackageDesc, strPAckVersion;
            if (usingIoTSDK)
            {
                strPackageID = "com.sysprogs.arm.nordic.nrf5x-iot";
                strPackageDesc = "Nordic NRF52 IoT";
                strPAckVersion = "0.9";

                foreach (var mcu in mcuDefinitions)
                    mcu.UserFriendlyName = mcu.ID + " (IoT)";
            }
            else
            {
                strPackageID = "com.sysprogs.arm.nordic.nrf5x";
                strPackageDesc = "Nordic NRF52x Devices";
                strPAckVersion = "15.2";
            }


            BoardSupportPackage bsp = new BoardSupportPackage
            {
                PackageID = strPackageID,
                PackageDescription = strPackageDesc,
                GNUTargetID = "arm-eabi",
                GeneratedMakFileName = "nrf5x.mak",
                MCUFamilies = familyDefinitions.ToArray(),
                SupportedMCUs = mcuDefinitions.ToArray(),
                Frameworks = frameworks.ToArray(),
                Examples = exampleDirs.Where(s => !s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                TestExamples = exampleDirs.Where(s => s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                PackageVersion = strPAckVersion,
                FileConditions = bspBuilder.MatchedFileConditions.ToArray(),
                MinimumEngineVersion = "5.0",
                ConditionalFlags = condFlags.ToArray(),
                InitializationCodeInsertionPoints = commonPseudofamily.Definition.InitializationCodeInsertionPoints,
            };

            bspBuilder.ValidateBSP(bsp);

            bspBuilder.Save(bsp, true, false);
        }
    }

    static class Extensions
    {
        public static int FindOrThrow(this List<string> lst, Predicate<string> pred)
        {
            int r = lst.FindIndex(pred);
            if (r == -1)
                throw new Exception("Could not find the predicate in the list");
            return r;
        }
    }
}
