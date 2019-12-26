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

            public readonly Nrf5xRuleGenerator RuleGenerator;

            public NordicBSPBuilder(BSPDirectories dirs)
                : base(dirs)
            {
                ShortName = "nRF5x";
                OnValueForSmartBooleanProperties = "yes";   //Backward compatibility with v15 and older BSPs.
                RuleGenerator = new Nrf5xRuleGenerator(this);
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

            public override MemoryLayoutAndSubstitutionRules GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
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

                return new MemoryLayoutAndSubstitutionRules(layout);
            }


            public struct SoftDevice
            {
                public string Name;
                public string Description;
                public uint FLASHSize;
                public uint SRAMSize;
                public bool HardwareFP;
                public Regex DeviceRegex;
                public string LdOriginalName;

                public LDFileMemoryInfo LinkerScriptWithMaximumReservedRAM;
                public string UserFriendlyName => string.IsNullOrEmpty(Description) ? Name : $"{Name} ({Description})";

                public SoftDevice(string name, string deviceRegex, bool hardwareFP, string desc, string pDirSdk)
                {
                    Name = name;
                    HardwareFP = hardwareFP;
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

            public override void GenerateLinkerScriptsAndUpdateMCU(string ldsDirectory, string familyFilePrefix, MCUBuilder mcu, MemoryLayoutAndSubstitutionRules layout, string generalizedName)
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

            void DoBuildLinkerScriptBasedOnOriginalNordicScripts(string ldsDirectory, string generalizedName, SoftDevice sd, LinkerScriptGenerationPass pass)
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
                    string[] incf = Directory.GetFiles(Directories.InputDir, m.Groups[1].Value, SearchOption.AllDirectories);
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

            void BuildLinkerScriptBasedOnOriginalNordicScripts(string ldsDirectory, string generalizedName, SoftDevice sd)
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
                    if (sd.HardwareFP)
                        abi = " \"-mfloat-abi=hard\" \"-mfpu=fpv4-sp-d16\"";
                    else
                        abi = " \"-mfloat-abi=soft\"";

                    string hexFileName = Path.GetFullPath(Directory.GetFiles(sdDir, "*.hex")[0]);
                    var info = new ProcessStartInfo { FileName = BSPRoot + @"\nRF5x\SoftdeviceLibraries\ConvertSoftdevice.bat", Arguments = sd.Name + " " + hexFileName + abi, UseShellExecute = false };
                    info.EnvironmentVariables["PATH"] += @";c:\sysgcc\arm-eabi\bin";
                    Process.Start(info).WaitForExit();
                    string softdevLib = string.Format(@"{0}\nRF5x\SoftdeviceLibraries\{1}_softdevice.o", BSPRoot, sd.Name);
                    if (!File.Exists(softdevLib) || File.ReadAllBytes(softdevLib).Length < 32768)
                        throw new Exception("Failed to convert a softdevice");
                }
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

        class NordicMCUBuilder : MCUBuilder
        {
            public readonly string DefaultSoftdevice;

            public NordicMCUBuilder(string name, int flashKB, int ramKB, CortexCore core, string defaultSoftdevice, string defaultBoard)
            {
                Name = name;
                FlashSize = flashKB * 1024;
                RAMSize = ramKB * 1024;
                Core = core;
                DefaultSoftdevice = defaultSoftdevice;
                StartupFile = "$$SYS:BSP_ROOT$$/nRF5x/modules/nrfx/mdk/gcc_startup_nrf$$com.sysprogs.bspoptions.nrf5x.mcu.basename$$.S";
            }
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: nrf5x.exe <Nordic SW package directory>");

            using (var bspBuilder = new NordicBSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules", @"..\..\logs")))
            {
                bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("S132", "nrf52832.*", true, null, bspBuilder.Directories.InputDir));
                bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("S140", "nrf52840.*", true, null, bspBuilder.Directories.InputDir));
                bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("S112", "nrf5281.*", false, null, bspBuilder.Directories.InputDir));
                bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("S113", "nrf528.*", true, null, bspBuilder.Directories.InputDir));

                List<MCUBuilder> devices = new List<MCUBuilder>
                {
                    new NordicMCUBuilder("nRF52832_XXAA", 512,  64,  CortexCore.M4,       "S132", "PCA10040"),
                    new NordicMCUBuilder("nRF52810_XXAA", 192,  24,  CortexCore.M4_NOFPU, "S112", "PCA10040"),
                    new NordicMCUBuilder("nRF52811_XXAA", 192,  24,  CortexCore.M4_NOFPU, "S112", "PCA10056"),
                    new NordicMCUBuilder("nRF52833_XXAA", 512,  128, CortexCore.M4,       "S140", "PCA10100"),
                    new NordicMCUBuilder("nRF52840_XXAA", 1024, 256, CortexCore.M4,       "S140", "PCA10056"),
                };

                List<MCUFamilyBuilder> allFamilies = new List<MCUFamilyBuilder>();
                foreach (var fn in Directory.GetFiles(bspBuilder.Directories.RulesDir + @"\Families", "*.xml"))
                    allFamilies.Add(new NordicFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(fn)));

                var rejects = BSPGeneratorTools.AssignMCUsToFamilies(devices, allFamilies);

                List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
                List<MCUFamilyBuilder.CopiedSample> exampleDirs = new List<MCUFamilyBuilder.CopiedSample>();

                bool noPeripheralRegisters = args.Contains("/noperiph");

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

                    var rejectedMCUs = fam.RemoveUnsupportedMCUs();
                    if (rejectedMCUs.Length != 0)
                    {
                        Console.WriteLine("Unsupported {0} MCUs:", fam.Definition.Name);
                        foreach (var mcu in rejectedMCUs)
                            Console.WriteLine("\t{0}", mcu.Name);
                    }

                    bspBuilder.RuleGenerator.GenerateRulesForFamily(fam.Definition);

                    List<MCUDefinitionWithPredicate> hardwareRegisterFiles = new List<MCUDefinitionWithPredicate>();
                    foreach(var svd in Directory.GetFiles(fam.Definition.PrimaryHeaderDir, "*.svd"))
                    {
                        var name = Path.GetFileNameWithoutExtension(svd).ToUpper();
                        if (name == "NRF52")
                            name = "NRF52832";

                        if (!name.StartsWith("NRF52"))
                            continue;

                        var registers = SVDParser.ParseSVDFile(svd, name);
                        hardwareRegisterFiles.Add(new MCUDefinitionWithPredicate
                        {
                            MCUName = name,
                            RegisterSets = registers.RegisterSets,
                            MatchPredicate = m => m.Name.StartsWith(registers.MCUName, StringComparison.InvariantCultureIgnoreCase)
                        });
                    }

                    fam.AttachPeripheralRegisters(hardwareRegisterFiles);

                    var famObj = fam.GenerateFamilyObject(MCUFamilyBuilder.CoreSpecificFlags.All);

                    famObj.AdditionalSourceFiles = LoadedBSP.Combine(famObj.AdditionalSourceFiles, projectFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).ToArray());
                    famObj.AdditionalHeaderFiles = LoadedBSP.Combine(famObj.AdditionalHeaderFiles, projectFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).ToArray());

                    famObj.AdditionalSystemVars = LoadedBSP.Combine(famObj.AdditionalSystemVars, commonPseudofamily.Definition.AdditionalSystemVars);
                    famObj.CompilationFlags = famObj.CompilationFlags.Merge(flags);

                    familyDefinitions.Add(famObj);
                    fam.GenerateLinkerScripts(false);


                    foreach (var mcu in fam.MCUs)
                    {
                        var mcuDef = mcu.GenerateDefinition(fam, bspBuilder, !noPeripheralRegisters, false, MCUFamilyBuilder.CoreSpecificFlags.All & ~MCUFamilyBuilder.CoreSpecificFlags.PrimaryMemory);

                        if (mcu.Name.StartsWith("nRF52832"))
                        {
                            //Although documented as a legacy definition, skipping this breaks fds_internal_defs.h
                            mcuDef.CompilationFlags.PreprocessorMacros = mcuDef.CompilationFlags.PreprocessorMacros.Concat(new[] { "NRF52" }).ToArray();
                        }

                        var nosoftdev = new[]
                        {
                            new PropertyEntry.Enumerated.Suggestion {InternalValue = "nosoftdev", UserFriendlyName = "None"}
                        };

                        var compatibleSoftdevs = bspBuilder.SoftDevices.Where(sd => sd.IsCompatible(mcu.Name)).SelectMany(
                            s => new[]
                            {
                                new PropertyEntry.Enumerated.Suggestion {InternalValue = s.Name, UserFriendlyName = s.UserFriendlyName},
                                new PropertyEntry.Enumerated.Suggestion { InternalValue = s.Name + "_reserve", UserFriendlyName = $"{s.UserFriendlyName} (programmed separately)"}
                            });

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
                                    SuggestionList = nosoftdev.Concat(compatibleSoftdevs).ToArray(),
                                }
                            }
                        });

                        if (mcu.Name.StartsWith("nRF52") && !mcu.Name.StartsWith("nRF5281"))
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
                        else if (mcu.Name.StartsWith("nRF52811"))
                            defaultConfig = "pca10056e/s112";
                        else
                            defaultConfig = "pca10040/s132";

                        var extraEntries = new[] {
                            new SysVarEntry { Key = "com.sysprogs.nordic.default_config_suffix", Value = defaultConfig },
                            new SysVarEntry { Key = "com.sysprogs.nordic.default_config_suffix_blank", Value = "pca10040" }
                        };

                        mcuDef.AdditionalSystemVars = LoadedBSP.Combine(mcuDef.AdditionalSystemVars, extraEntries);

                        mcuDefinitions.Add(mcuDef);
                    }

                    if (fam.Definition.ConditionalFlags != null)
                        condFlags.AddRange(fam.Definition.ConditionalFlags);

                    foreach (var fw in fam.GenerateFrameworkDefinitions())
                        frameworks.Add(fw);

                    string dirpca = "pca10040e/s112";
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
                bspBuilder.RuleGenerator.PatchGeneratedFrameworks(frameworks, condFlags);

                //  CheckEntriesSample(Path.Combine(bspBuilder.Directories.OutputDir, @"nRF5x\components\libraries"),
                //                     Path.Combine(bspBuilder.Directories.OutputDir, "Samples"));

                Console.WriteLine("Building BSP archive...");

                BoardSupportPackage bsp = new BoardSupportPackage
                {
                    PackageID = "com.sysprogs.arm.nordic.nrf5x",
                    PackageDescription = "Nordic NRF52x Devices",
                    GNUTargetID = "arm-eabi",
                    GeneratedMakFileName = "nrf5x.mak",
                    MCUFamilies = familyDefinitions.ToArray(),
                    SupportedMCUs = mcuDefinitions.ToArray(),
                    Frameworks = frameworks.ToArray(),
                    Examples = exampleDirs.Where(s => !s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                    TestExamples = exampleDirs.Where(s => s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                    PackageVersion = "16.0",
                    FirstCompatibleVersion = "16.0",
                    FileConditions = bspBuilder.MatchedFileConditions.Values.ToArray(),
                    MinimumEngineVersion = "5.0",
                    ConditionalFlags = condFlags.ToArray(),
                    InitializationCodeInsertionPoints = commonPseudofamily.Definition.InitializationCodeInsertionPoints,
                };

                bspBuilder.ValidateBSP(bsp);

                var cfgFixSample = new ConfigurationFixSampleReference
                {
                    MCUID = "nRF52840_XXAA",
                    SamplePath = "$$SYS:BSP_ROOT$$/samples/BLEMouse"
                };

                bspBuilder.ReverseFileConditions.SaveIfConsistent(bspBuilder.Directories.OutputDir, bspBuilder.ExportRenamedFileTable(), true, cfgFixSample);

                bspBuilder.Save(bsp, false, false);
            }
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
