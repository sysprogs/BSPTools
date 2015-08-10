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
                if ((pathInsidePackage.EndsWith(".ld") || pathInsidePackage.EndsWith(".eww") || pathInsidePackage.EndsWith(".uvmpw")) && (pathInsidePackage.Contains("experimental") || pathInsidePackage.Contains("\\ant\\") || pathInsidePackage.Contains("\\ser_")))
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
                public bool NRF5142xOnly;

                public string UserFriendlyName
                {
                    get
                    {
                        return string.Format("{0} ({1})", Name, Description);
                    }
                }

                public SoftDevice(string name, uint flash, uint sram, bool nrf4, string desc)
                {
                    Name = name;
                    FLASHSize = flash;
                    SRAMSize = sram;
                    NRF5142xOnly = nrf4;
                    Description = desc;
                }

                public bool IsCompatible(string name)
                {
                    if (NRF5142xOnly && name.StartsWith("NRF518", StringComparison.CurrentCultureIgnoreCase))
                        return false;
                    return true;
                }
            }


            public List<SoftDevice> SoftDevices = new List<SoftDevice>
            {
                new SoftDevice("S110", 0x18000, 0x2000, false, "Bluetooth LE Peripheral"),
                new SoftDevice("S120", 0x1d000, 0x2800, false, "Bluetooth LE Master"),
                new SoftDevice("S130", 0x1c000, 0x2800, false, "Bluetooth LE Universal"),
                new SoftDevice("S210", 0xd000,  0x900, true, "ANT Master"),
                new SoftDevice("S310", 0x1d000, 0x2200, true, "Bluetooth LE/ANT"),
            };

            public const string SoftdevicePropertyID = "com.sysprogs.bspoptions.nrf5x.softdevice";

            public override void GenerateLinkerScriptsAndUpdateMCU(string ldsDirectory, string familyFilePrefix, MCUBuilder mcu, MemoryLayout layout, string generalizedName)
            {
                using (var gen = new LdsFileGenerator(LDSTemplate, layout))
                {
                    using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_nosoftdev.lds")))
                        gen.GenerateLdsFile(sw);
                    using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_nosoftdev_reserve.lds")))
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
                        using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_" + sd.Name.ToLower() + ".lds")))
                            gen.GenerateLdsFile(sw, new string[] { "", "GROUP(" + sd.Name + "_softdevice.o)", "" });
                    }

                    using (var gen = new LdsFileGenerator(LDSTemplate, layoutCopy))
                    {
                        using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_" + sd.Name.ToLower() + "_reserve.lds")))
                            gen.GenerateLdsFile(sw);
                    }
                }

                mcu.LinkerScriptPath = string.Format("$$SYS:BSP_ROOT$$/{0}LinkerScripts/{1}_$${2}$$$${3}$$.lds", familyFilePrefix, generalizedName, SoftdevicePropertyID, "com.sysprogs.bspoptions.nrf5x.softdevice_suffix");
            }

            internal void GenerateSoftdeviceLibraries()
            {
                foreach (var sd in SoftDevices)
                {
                    Process.Start(BSPRoot + @"\nRF51\SoftdeviceLibraries\ConvertSoftdevice.bat", sd.Name).WaitForExit();
                    string softdevLib = string.Format(@"{0}\nRF51\SoftdeviceLibraries\{1}_softdevice.o", BSPRoot, sd.Name);
                    if (!File.Exists(softdevLib))
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

            List<MCUFamilyBuilder> allFamilies = new List<MCUFamilyBuilder>();
            foreach (var fn in Directory.GetFiles(bspBuilder.Directories.RulesDir + @"\Families", "*.xml"))
                allFamilies.Add(new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(fn)));

            var rejects = BSPGeneratorTools.AssignMCUsToFamilies(devices, allFamilies);
            var vectorTable = new StartupFileGenerator.InterruptVectorTable
            {
                FileName = "startup_nrf51x.c",
                Vectors = StartupFileGenerator.ParseInterruptVectors(Path.Combine(allFamilies[0].Definition.StartupFileDir, "arm_startup_nrf51.s"),
                    "^__Vectors",
                    @"__Vectors_End",
                    @"^[ \t]+DCD[ \t]+([^ \t]+)[ \t]+; *([^ \t].*)$",
                    null,
                    @"^[ \t]+;.*",
                    null,
                    1,
                    2),

                AdditionalResetHandlerLines = new string[]
                {
                    "asm volatile(\".equ NRF_POWER_RAMON_ADDRESS,0x40000524\");",
                    "asm volatile(\".equ NRF_POWER_RAMON_RAMxON_ONMODE_Msk,3\");",
                    "asm volatile(\"LDR     R0, =NRF_POWER_RAMON_ADDRESS\");",
                    "asm volatile(\"LDR     R2, [R0]\");",
                    "asm volatile(\"MOVS    R1, #NRF_POWER_RAMON_RAMxON_ONMODE_Msk\");",
                    "asm volatile(\"ORR     R2, R2, R1\");",
                    "asm volatile(\"STR     R2, [R0]\");",
                }
            };

            List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
            List<string> exampleDirs = new List<string>();


            vectorTable.Vectors = new StartupFileGenerator.InterruptVector[] { new StartupFileGenerator.InterruptVector { Name = "_estack" } }.Concat(vectorTable.Vectors).ToArray();

            bool noPeripheralRegisters = true;

            List<MCUFamily> familyDefinitions = new List<MCUFamily>();
            List<MCU> mcuDefinitions = new List<MCU>();

            var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));
            var flags = new ToolFlags();
            List<string> projectFiles = new List<string>();
            commonPseudofamily.CopyFamilyFiles(ref flags, projectFiles);
            flags = flags.Merge(commonPseudofamily.Definition.CompilationFlags);

            List<ConditionalToolFlags> condFlags = new List<ConditionalToolFlags>();

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
                    ID = "com.sysprogs.arm.nordic.nrfx." + id,
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
            

            foreach (var fam in allFamilies)
            {
                var rejectedMCUs = fam.RemoveUnsupportedMCUs(true);
                if (rejectedMCUs.Length != 0)
                {
                    Console.WriteLine("Unsupported {0} MCUs:", fam.Definition.Name);
                    foreach (var mcu in rejectedMCUs)
                        Console.WriteLine("\t{0}", mcu.Name);
                }

                fam.Definition.AdditionalFrameworks = fam.Definition.AdditionalFrameworks.Concat(bleFrameworks).ToArray();

                fam.AttachStartupFiles(new StartupFileGenerator.InterruptVectorTable[] { vectorTable });
                fam.AttachPeripheralRegisters(new MCUDefinitionWithPredicate[] { SVDParser.ParseSVDFile(Path.Combine(fam.Definition.PrimaryHeaderDir, "nrf51.xml"), "nRF51") });

                var famObj = fam.GenerateFamilyObject(true);

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
                PackageDescription = "Nordic NRF51 Devices",
                GNUTargetID = "arm-eabi",
                GeneratedMakFileName = "nrf5x.mak",
                MCUFamilies = familyDefinitions.ToArray(),
                SupportedMCUs = mcuDefinitions.ToArray(),
                Frameworks = frameworks.ToArray(),
                Examples = exampleDirs.ToArray(),
                PackageVersion = "1.0",
                FileConditions = bspBuilder.MatchedFileConditions.ToArray(),
                MinimumEngineVersion = "5.0",
                ConditionalFlags = condFlags.ToArray()
            };

            bspBuilder.Save(bsp, true);
        }
    }
}
