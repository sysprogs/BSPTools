/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using BSPEngine;
using BSPGenerationTools;
using LinkerScriptGenerator;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace nrf5x
{
    class Program
    {
        class NordicBSPBuilder : BSPBuilder
        {
            const uint FLASHBase = 0x00000000, SRAMBase = 0x20000000;

            public NordicBSPBuilder(BSPDirectories dirs)
                : base(dirs)
            {
                ShortName = "Tiva";
            }

            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                flashBase = FLASHBase;
                ramBase = SRAMBase;
            }

            public override bool OnFilePathTooLong(string pathInsidePackage)
            {
                if ((pathInsidePackage.EndsWith(".ld") || pathInsidePackage.EndsWith(".eww") || pathInsidePackage.EndsWith(".uvmpw")) || (pathInsidePackage.Contains("experimental") || pathInsidePackage.Contains("\\ant\\") || pathInsidePackage.Contains("\\ser_")))
                    return false;
                if (pathInsidePackage.Contains("nrf_drv_config.h"))
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
                public string DevicePrefix;

                public string UserFriendlyName
                {
                    get
                    {
                        return string.Format("{0} ({1})", Name, Description);
                    }
                }

                public SoftDevice(string name, uint flash, uint sram, string devicePrefix, string desc)
                {
                    Name = name;
                    FLASHSize = flash;
                    SRAMSize = sram;
                    DevicePrefix = devicePrefix;
                    Description = desc;
                }

                public bool IsCompatible(string name)
                {
                    return name.StartsWith(DevicePrefix, StringComparison.InvariantCultureIgnoreCase);
                }
            }


            public List<SoftDevice> SoftDevices = new List<SoftDevice>
            {
                new SoftDevice("S110", 0x18000, 0x2000, "nrf51", "Bluetooth LE Peripheral"),
                new SoftDevice("S120", 0x1d000, 0x2800, "nrf51", "Bluetooth LE Master"),
                new SoftDevice("S130", 0x1c000, 0x2800, "nrf51", "Bluetooth LE Universal"),
                new SoftDevice("S210", 0xd000,  0x900, " nrf514", "ANT Master"),
                new SoftDevice("S310", 0x1d000, 0x2200, "nrf514", "Bluetooth LE/ANT"),

                new SoftDevice("S132", 0x1f000, 0x2800, "nrf52", "Bluetooth LE"),
                //new SoftDevice("S212", 0x12000, 0x0a00, "nrf52", "ANT"),  //Not included in the SDK v 0.9.2
            };

            public const string SoftdevicePropertyID = "com.sysprogs.bspoptions.nrf5x.softdevice";
            public const string RAMSuffixPropertyID = "com.sysprogs.bspoptions.nrf5x.ramsuffix";

            public override void GenerateLinkerScriptsAndUpdateMCU(string ldsDirectory, string familyFilePrefix, MCUBuilder mcu, MemoryLayout layout, string generalizedName)
            {
                DoGenerateLinkerScriptsAndUpdateMCU(ldsDirectory, familyFilePrefix, mcu, layout, generalizedName, "");
                if (layout.DeviceName.StartsWith("nRF52"))
                {
                    foreach (var ram in new int[] { 16, 32, 64 })
                    {
                        var layout2 = layout.Clone();
                        layout2.Memories.First(m => m.Name == "SRAM").Size = (uint)(ram * 1024);
                        DoGenerateLinkerScriptsAndUpdateMCU(ldsDirectory, familyFilePrefix, mcu, layout2, generalizedName, "_" + ram + "k");
                    }

                    mcu.LinkerScriptPath = mcu.LinkerScriptPath.Replace(".lds", "$$" + RAMSuffixPropertyID + "$$.lds");
                }
            }

            void DoGenerateLinkerScriptsAndUpdateMCU(string ldsDirectory, string familyFilePrefix, MCUBuilder mcu, MemoryLayout layout, string generalizedName, string ldsSuffix)
            {
                using (var gen = new LdsFileGenerator(LDSTemplate, layout))
                {
                    using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_nosoftdev" + ldsSuffix + ".lds")))
                        gen.GenerateLdsFile(sw);
                    using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_nosoftdev_reserve"+ ldsSuffix + ".lds")))
                        gen.GenerateLdsFile(sw);
                }

                foreach (var sd in SoftDevices)
                {
                    if (!sd.IsCompatible(mcu.Name))
                        continue;

                    var softdevTemplate = LDSTemplate.ShallowCopy();
                    softdevTemplate.Sections = new List<Section>(softdevTemplate.Sections);
                    softdevTemplate.Sections.Insert(0, new Section { Name = ".softdevice", TargetMemory = "FLASH_SOFTDEVICE", Inputs = new List<SectionReference> { new SectionReference { NamePattern = ".softdevice", Flags = SectionReferenceFlags.Keep } }, Fill = new FillInfo { Pattern = uint.MaxValue, TotalSize = (int)sd.FLASHSize }, Flags = SectionFlags.Unaligned });
                    softdevTemplate.Sections.Insert(1, new Section { Name = ".softdevice_sram", TargetMemory = "SRAM_SOFTDEVICE", Inputs = new List<SectionReference>(), Fill = new FillInfo { Pattern = 0, TotalSize = (int)sd.SRAMSize }, Flags = SectionFlags.Unaligned });

                    var layoutCopy = layout.Clone();
                    var flash = layoutCopy.Memories.First(m => m.Name == "FLASH");
                    var ram = layoutCopy.Memories.First(m => m.Name == "SRAM");

                    if (flash.Size < sd.FLASHSize || ram.Size < sd.SRAMSize)
                        throw new Exception("Device too small for " + sd.Name);

                    layoutCopy.Memories.Insert(layoutCopy.Memories.IndexOf(flash), new Memory { Name = "FLASH_SOFTDEVICE", Access = MemoryAccess.Readable | MemoryAccess.Executable, Start = flash.Start, Size = sd.FLASHSize });
                    layoutCopy.Memories.Insert(layoutCopy.Memories.IndexOf(ram), new Memory { Name = "SRAM_SOFTDEVICE", Access = MemoryAccess.Readable | MemoryAccess.Writable | MemoryAccess.Executable, Start = ram.Start, Size = sd.SRAMSize });

                    flash.Size -= sd.FLASHSize;
                    flash.Start += sd.FLASHSize;

                    ram.Size -= sd.SRAMSize;
                    ram.Start += sd.SRAMSize;

                    using (var gen = new LdsFileGenerator(softdevTemplate, layoutCopy))
                    {
                        using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_" + sd.Name.ToLower() + ldsSuffix + ".lds")))
                            gen.GenerateLdsFile(sw, new string[] { "", "GROUP(" + sd.Name + "_softdevice.o)", "" });
                    }

                    using (var gen = new LdsFileGenerator(LDSTemplate, layoutCopy))
                    {
                        using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_" + sd.Name.ToLower() + "_reserve" + ldsSuffix +".lds")))
                            gen.GenerateLdsFile(sw);
                    }
                }

                mcu.LinkerScriptPath = string.Format("$$SYS:BSP_ROOT$$/{0}LinkerScripts/{1}_$${2}$$$${3}$$.lds", familyFilePrefix, generalizedName, SoftdevicePropertyID, "com.sysprogs.bspoptions.nrf5x.softdevice_suffix");
            }

            internal void GenerateSoftdeviceLibraries()
            {
                foreach (var sd in SoftDevices)
                {
                    string family = "nRF51";
                    string sdDir = BSPRoot + @"\nRF51\components\softdevice\" + sd.Name + @"\hex";
                    string abi = "";
                    if (!Directory.Exists(sdDir))
                    {
                        sdDir = BSPRoot + @"\nRF52\components\softdevice\" + sd.Name + @"\hex";
                        family = "nRF52";
                        abi = " \"-mfloat-abi=hard\" \"-mfpu=fpv4-sp-d16\"";
                    }
                    string hexFileName = Path.GetFullPath(Directory.GetFiles(sdDir, "*.hex")[0]);
                    Process.Start(BSPRoot + @"\" + family + @"\SoftdeviceLibraries\ConvertSoftdevice.bat", sd.Name + " " + hexFileName + abi).WaitForExit();
                    string softdevLib = string.Format(@"{0}\{1}\SoftdeviceLibraries\{2}_softdevice.o", BSPRoot, family, sd.Name);
                    if (!File.Exists(softdevLib) || File.ReadAllBytes(softdevLib).Length < 32768)
                        throw new Exception("Failed to convert a softdevice");
                }
            }
        }



        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: nrf5x.exe <Nordic SW package directory>");

            var bspBuilder = new NordicBSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules"));
            List<MCUBuilder> devices = new List<MCUBuilder>();
            foreach (string part in new string[] { "nRF51822", "nRF51422" })
            {
                devices.Add(new MCUBuilder { Name = part + "_XXAA", FlashSize = 256 * 1024, RAMSize = 16 * 1024, Core = CortexCore.M0 });
                devices.Add(new MCUBuilder { Name = part + "_XXAB", FlashSize = 128 * 1024, RAMSize = 16 * 1024, Core = CortexCore.M0 });
                devices.Add(new MCUBuilder { Name = part + "_XXAC", FlashSize = 256 * 1024, RAMSize = 32 * 1024, Core = CortexCore.M0 });
            }

            devices.Add(new MCUBuilder { Name = "nRF52832_XXAA", FlashSize = 512 * 1024, RAMSize = 32 * 1024, Core = CortexCore.M4 });

            List<MCUFamilyBuilder> allFamilies = new List<MCUFamilyBuilder>();
            foreach (var fn in Directory.GetFiles(bspBuilder.Directories.RulesDir + @"\Families", "*.xml"))
                allFamilies.Add(new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(fn)));

            var rejects = BSPGeneratorTools.AssignMCUsToFamilies(devices, allFamilies);

            List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
            List<string> exampleDirs = new List<string>();

            bool noPeripheralRegisters = true;

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
                Console.WriteLine("Processing " + fam.Definition.Name + " family...");
                string famBase = fam.Definition.Name.Substring(0, 5).ToLower();

                var rejectedMCUs = fam.RemoveUnsupportedMCUs(true);
                if (rejectedMCUs.Length != 0)
                {
                    Console.WriteLine("Unsupported {0} MCUs:", fam.Definition.Name);
                    foreach (var mcu in rejectedMCUs)
                        Console.WriteLine("\t{0}", mcu.Name);
                }

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
                                SourceFolder = allFamilies[0].Definition.PrimaryHeaderDir + @"\..\components\ble\" + dir,
                                TargetFolder = dir,
                                FilesToCopy = "*.c;*.h",
                            }
                        }
                    });
                }

                fam.Definition.AdditionalFrameworks = fam.Definition.AdditionalFrameworks.Concat(bleFrameworks).ToArray();

                var vectorTable = new StartupFileGenerator.InterruptVectorTable
                {
                    FileName = "startup_" + famBase + "x.c",
                    Vectors = StartupFileGenerator.ParseInterruptVectors(Path.Combine(fam.Definition.StartupFileDir, "arm_startup_" + famBase + ".s"),
                        "^__Vectors",
                        @"__Vectors_End",
                        @"^[ \t]+DCD[ \t]+([^ \t]+)[ \t]+; *([^ \t].*)$",
                        @"^[ \t]+DCD[ \t]+([^ \t]+)$",
                        @"^[ \t]+;.*",
                        null,
                        1,
                        2),
                };

                if (famBase.ToLower() == "nrf51")
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

                fam.AttachStartupFiles(new StartupFileGenerator.InterruptVectorTable[] { vectorTable });
                fam.AttachPeripheralRegisters(new MCUDefinitionWithPredicate[] { SVDParser.ParseSVDFile(Path.Combine(fam.Definition.PrimaryHeaderDir, famBase + (famBase == "nrf51" ? ".svd" : ".xml")), "nRF5" + famBase[4]) });

                var famObj = fam.GenerateFamilyObject(true);
                if (famBase == "nrf52")
                {
                    var prop = famObj.ConfigurableProperties.PropertyGroups[0].Properties.Find(p => p.UniqueID == "com.sysprogs.bspoptions.arm.floatmode") as PropertyEntry.Enumerated;
                    var idx = Array.FindIndex(prop.SuggestionList, p => p.UserFriendlyName == "Hardware");

                    prop.DefaultEntryIndex = idx;
                    prop.SuggestionList[idx].UserFriendlyName = "Hardware (required when using a softdevice)";   //Otherwise the system_nrf52.c file won't initialize the FPU and the internal initialization of the softdevice will later fail.
                }

                famObj.AdditionalSourceFiles = LoadedBSP.Combine(famObj.AdditionalSourceFiles, projectFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).ToArray());
                famObj.AdditionalHeaderFiles = LoadedBSP.Combine(famObj.AdditionalHeaderFiles, projectFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).ToArray());

                famObj.AdditionalSystemVars = LoadedBSP.Combine(famObj.AdditionalSystemVars, commonPseudofamily.Definition.AdditionalSystemVars);
                famObj.CompilationFlags = famObj.CompilationFlags.Merge(flags);

                familyDefinitions.Add(famObj);
                fam.GenerateLinkerScripts(false);
                foreach (var mcu in fam.MCUs)
                {
                    var mcuDef = mcu.GenerateDefinition(fam, bspBuilder, !noPeripheralRegisters);
                    var compatibleSoftdevs = new PropertyEntry.Enumerated.Suggestion[] { new PropertyEntry.Enumerated.Suggestion { InternalValue = "nosoftdev", UserFriendlyName = "None" } }.Concat(bspBuilder.SoftDevices.Where(sd => sd.IsCompatible(mcu.Name)).Select(s => new PropertyEntry.Enumerated.Suggestion { InternalValue = s.Name, UserFriendlyName = s.UserFriendlyName })).ToArray();

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
                                DefaultEntryIndex = 0,
                                SuggestionList = compatibleSoftdevs,
                            }
                        }
                    });

                    if (mcu.Name.StartsWith("nRF52"))
                    {
                        mcuDef.ConfigurableProperties.PropertyGroups[0].Properties.Add(new PropertyEntry.Enumerated
                        {
                            UniqueID = NordicBSPBuilder.RAMSuffixPropertyID,
                            Name = "RAM size",
                            DefaultEntryIndex = 0,
                            SuggestionList = new PropertyEntry.Enumerated.Suggestion[] {
                                new PropertyEntry.Enumerated.Suggestion {InternalValue = "_32k", UserFriendlyName = "32 KB (Preview)" },
                                new PropertyEntry.Enumerated.Suggestion {InternalValue = "_64k", UserFriendlyName = "64 KB (Final)" },
                            }
                        });
                    }

                    mcuDefinitions.Add(mcuDef);
                }

                if (fam.Definition.ConditionalFlags != null)
                    condFlags.AddRange(fam.Definition.ConditionalFlags);

                foreach (var fw in fam.GenerateFrameworkDefinitions())
                    frameworks.Add(fw);

                foreach (var sample in fam.CopySamples())
                    exampleDirs.Add(sample);
            }
            bspBuilder.GenerateSoftdeviceLibraries();

            Console.WriteLine("Building BSP archive...");

            BoardSupportPackage bsp = new BoardSupportPackage
            {
                PackageID = "com.sysprogs.arm.nordic.nrf5x",
                PackageDescription = "Nordic NRF5x Devices",
                GNUTargetID = "arm-eabi",
                GeneratedMakFileName = "nrf5x.mak",
                MCUFamilies = familyDefinitions.ToArray(),
                SupportedMCUs = mcuDefinitions.ToArray(),
                Frameworks = frameworks.ToArray(),
                Examples = exampleDirs.ToArray(),
                PackageVersion = "2.0",
                FileConditions = bspBuilder.MatchedFileConditions.ToArray(),
                MinimumEngineVersion = "5.0",
                ConditionalFlags = condFlags.ToArray(),
                FirstCompatibleVersion = "2.0",
            };

            bspBuilder.Save(bsp, true);
        }
    }
}
