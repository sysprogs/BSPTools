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
    class Program
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
                if (pathInsidePackage.Contains("nrf_drv_config.h") || pathInsidePackage.Contains("app_usbd_string_config.h"))
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
                public string LdOriginalName;
                public LDDevice ldsfile;
                public string UserFriendlyName

                {
                    get
                    {
                        return string.Format("{0} ({1})", Name, Description);
                    }
                }
                public SoftDevice(string name, uint flash, uint sram, string devicePrefix, string desc, string pDirSdk)
                {
                    Name = name;
                    FLASHSize = flash;
                    SRAMSize = sram;
                    DevicePrefix = devicePrefix;
                    Description = desc;
                    LdOriginalName = "";
                    ldsfile = null;
                }
                public SoftDevice(string name, string devicePrefix, string desc, string pDirSdk)
                {
                    Name = name;
                    ldsfile = FindLdsFile(pDirSdk, Name);
                    LdOriginalName = ldsfile.GetFileLd();
                    FLASHSize = (uint)ldsfile.FileLdFlash.Origin;
                    SRAMSize = (uint)ldsfile.FileLdRAM.Origin;
                    DevicePrefix = devicePrefix;
                    Description = desc;
                }

                static LDDevice FindLdsFile(string pDir, string sdname)
                {
                    LDDevice softdev = new LDDevice(sdname.ToLower());
                    foreach (var ldFile in Directory.GetFiles(pDir, "*.ld", SearchOption.AllDirectories))
                        softdev.CheckLdFile(ldFile);
                    return softdev;
                }

                public bool IsCompatible(string name)
                {
                    return name.StartsWith(DevicePrefix, StringComparison.InvariantCultureIgnoreCase);
                }
            }


            public List<SoftDevice> SoftDevices = new List<SoftDevice>();


            public const string SoftdevicePropertyID = "com.sysprogs.bspoptions.nrf5x.softdevice";

            public override void GenerateLinkerScriptsAndUpdateMCU(string ldsDirectory, string familyFilePrefix, MCUBuilder mcu, MemoryLayout layout, string generalizedName)
            {
                DoGenerateLinkerScriptsAndUpdateMCU(ldsDirectory, familyFilePrefix, mcu, layout, generalizedName, "");
            }

            void DoGenerateLinkerScriptsAndUpdateMCU(string ldsDirectory, string familyFilePrefix, MCUBuilder mcu, MemoryLayout layout, string generalizedName, string ldsSuffix)
            {
                using (var gen = new LdsFileGenerator(LDSTemplate, layout))
                {
                    using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_nosoftdev" + ldsSuffix + ".lds")))
                        gen.GenerateLdsFile(sw);
                }

                foreach (var sd in SoftDevices)
                {
                    if (!sd.IsCompatible(mcu.Name))
                        continue;
                    if (sd.ldsfile == null)
                    {
                        //IoT
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
                    }
                    else
                    {
                        string[] prtext ={   "PROVIDE(_sbss = __bss_start__);","PROVIDE(_ebss = __bss_end__);",
                                "PROVIDE(_sdata = __data_start__);","PROVIDE(_sidata = __etext);","PROVIDE(_estack = __StackTop);",
                            "PROVIDE(_edata =__data_end__);","PROVIDE(__isr_vector = __StackTop);" };

                        string[] sflashtext = { "  .softdevice :", "  {", "    KEEP(*(.softdevice))", "    FILL(0xFFFFFFFF);" };
                        string[] ssramlashtext = { "  .softdevice_sram :", "  {", "    FILL(0x00000000);", };

                        List<string> fr = new List<string>(File.ReadAllLines(sd.LdOriginalName));
                        //---

                        var m = Regex.Match(fr.Find(s => s.Contains("INCLUDE")) ?? " ", "INCLUDE[ ]*\"([a-z0-9_.]*)");
                        if (m.Success)
                        {
                            string[] incf = Directory.GetFiles(bspBuilder.Directories.InputDir, m.Groups[1].Value, SearchOption.AllDirectories);
                            if (incf.Count() > 1)
                                throw new Exception("more include file");
                            if (!File.Exists(Path.Combine(ldsDirectory, m.Groups[1].Value)))
                                File.Copy(incf[0], Path.Combine(ldsDirectory, m.Groups[1].Value));
                        }///---

                        var strFlashOr = string.Format("0x{0:x}", sd.ldsfile.FileLdFlash.Origin);
                        var strRamOr = string.Format("0x{0:x}", sd.ldsfile.FileLdRAM.Origin - SRAMBase);
                        fr.Insert(fr.FindIndex(s => s.Contains("FLASH")), $"  FLASH_SOFTDEVICE (RX) : ORIGIN = {string.Format("0x{0:x}", FLASHBase)}, LENGTH = {strFlashOr}");
                        fr.Insert(fr.FindIndex(s => s.Contains("RAM")), $"  SRAM_SOFTDEVICE (RWX) : ORIGIN = {string.Format("0x{0:x}", SRAMBase)}, LENGTH = {strRamOr}");
                        var indSectAdd = fr.FindIndex(s => s.Contains("ALIGN(4);")) + 1;
                        fr.InsertRange(indSectAdd, sflashtext);
                        fr.Insert(indSectAdd + sflashtext.Count(), $"    . = {strFlashOr};");
                        fr.Insert(indSectAdd + sflashtext.Count() + 1, "    } > FLASH_SOFTDEVICE");
                        indSectAdd = fr.FindIndex(s => s.Contains("} > FLASH_SOFTDEVICE")) + 1;
                        fr.InsertRange(indSectAdd, ssramlashtext);
                        fr.Insert(indSectAdd + ssramlashtext.Count(), $"    . = {strRamOr};");
                        fr.Insert(indSectAdd + ssramlashtext.Count() + 1, "    } > SRAM_SOFTDEVICE");

                        fr.AddRange(prtext);

                        File.WriteAllLines(Path.Combine(ldsDirectory, generalizedName + "_" + sd.Name.ToLower() + "_reserve" + ldsSuffix + ".lds"), fr);

                        fr.Insert(fr.FindIndex(s => s.Contains("MEMORY")), $"GROUP(" + sd.Name + "_softdevice.o)");
                        File.WriteAllLines(Path.Combine(ldsDirectory, generalizedName + "_" + sd.Name.ToLower() + ldsSuffix + ".lds"), fr);
                        //no softdev
                        fr = new List<string>(File.ReadAllLines(sd.LdOriginalName));

                        var strFlLen = string.Format("0x{0:x}", sd.ldsfile.FileLdFlash.Origin + sd.ldsfile.FileLdFlash.Length);
                        var indFl = fr.FindIndex(s => s.Contains("FLASH"));
                        fr.RemoveAt(indFl);
                        fr.Insert(indFl, $"  FLASH (RX) :  ORIGIN = {string.Format("0x{0:x}", FLASHBase)}, LENGTH = {strFlLen}");
                        fr.AddRange(prtext);
                        File.WriteAllLines(Path.Combine(Path.Combine(ldsDirectory, generalizedName + "_nosoftdev" + ldsSuffix + ".lds")), fr);
                    }
                }
                mcu.LinkerScriptPath = string.Format("$$SYS:BSP_ROOT$$/{0}LinkerScripts/{1}_$${2}$$.lds", familyFilePrefix, generalizedName, SoftdevicePropertyID);
            }

            internal void GenerateSoftdeviceLibraries()
        {
            foreach (var sd in SoftDevices)
            {
                string sdDir = BSPRoot + @"\nRF5x\components\softdevice\" + sd.Name + @"\hex";
                string abi = "";
                if (sd.Name == "S132" || sd.Name == "S140")
                {
                    sdDir = BSPRoot + @"\nRF5x\components\softdevice\" + sd.Name + @"\hex";
                    abi = " \"-mfloat-abi=hard\" \"-mfpu=fpv4-sp-d16\"";
                }
                if (sd.Name == "s1xx_iot")
                {
                    sdDir = BSPRoot + @"\nRF5x\components\softdevice\" + sd.Name;
                    abi = " \"-mfloat-abi=hard\" \"-mfpu=fpv4-sp-d16\"";
                }

                string hexFileName = Path.GetFullPath(Directory.GetFiles(sdDir, "*.hex")[0]);
                var info = new ProcessStartInfo { FileName = BSPRoot + @"\nRF5x\SoftdeviceLibraries\ConvertSoftdevice.bat", Arguments = sd.Name + " " + hexFileName + abi, UseShellExecute = false };
                info.EnvironmentVariables["PATH"] += @";e:\sysgcc\arm-eabi\bin";
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
    class MaxFileLd
    {
        private string _Typ;
        private ulong _MaxValue;
        public ulong Length;
        public ulong Origin;
        string _NameFileLd { get; set; }
        public MaxFileLd(string pTyp) { _Typ = pTyp; }
        public void CheckLdFile(string pNameFile)
        {
            var m = Regex.Match(File.ReadAllText(pNameFile), $".*{_Typ}.*ORIGIN[ =]+0x([a-fA-F0-9]+).*LENGTH[ =]+0x([a-fA-F0-9]+)");
            if (m.Success)
            {

                ulong Valueld = ulong.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber) + ulong.Parse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber);
                if (_MaxValue < Valueld)
                {
                    _MaxValue = Valueld;
                    _NameFileLd = pNameFile;
                    Origin = ulong.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
                    Length = ulong.Parse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber);
                }
            }
        }
        public string GetNameFile() { return _NameFileLd; }
        public static bool operator ==(MaxFileLd f1, MaxFileLd f2)
        {
            return (f1._NameFileLd == f2._NameFileLd);
        }
        public static bool operator !=(MaxFileLd f1, MaxFileLd f2)
        {
            return (f1._NameFileLd != f2._NameFileLd);
        }
    }

    class LDDevice
    {
        public MaxFileLd FileLdFlash;
        public MaxFileLd FileLdRAM;
        private string _Typ;
        public LDDevice(string pTyp)
        {
            _Typ = pTyp;
            FileLdFlash = new MaxFileLd("FLASH");
            FileLdRAM = new MaxFileLd("RAM");
        }
        public void CheckLdFile(string pNameFile)
        {
            if (!pNameFile.Contains(_Typ))
                return;
            FileLdFlash.CheckLdFile(pNameFile);
            FileLdRAM.CheckLdFile(pNameFile);
        }

        public string GetFileLd()
        {
            if (FileLdFlash != FileLdRAM)
                throw new Exception(":");
            return FileLdFlash.GetNameFile();
        }
    }

        static NordicBSPBuilder bspBuilder;
        static void Main(string[] args)
    {

        if (args.Length < 1)
            throw new Exception("Usage: nrf5x.exe <Nordic SW package directory>");
        bool usingIoTSDK = false;

        if (Directory.Exists(Path.Combine(args[0], @"components\iot\ble_6lowpan")))
        {
            usingIoTSDK = true;
            Console.WriteLine("Detected IoT SDK");
        }

        if (usingIoTSDK)
        {
            bspBuilder = new NordicBSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules_iot"));
            bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("s1xx_iot", 0x1f000, 0x2800, "nrf52", "IoT", bspBuilder.Directories.InputDir));
        }
        else
        {
            bspBuilder = new NordicBSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules"));
            bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("S132", "nrf52832", "Bluetooth LE", bspBuilder.Directories.InputDir));
            bspBuilder.SoftDevices.Add(new NordicBSPBuilder.SoftDevice("S140", "nrf52840", "Bluetooth LE", bspBuilder.Directories.InputDir));
        }
        List<MCUBuilder> devices = new List<MCUBuilder>();

#if NRF51_SUPPORT
            if (!usingIoTSDK)
                foreach (string part in new string[] { "nRF51822", "nRF51422" })
                {
                    devices.Add(new MCUBuilder { Name = part + "_XXAA", FlashSize = 256 * 1024, RAMSize = 16 * 1024, Core = CortexCore.M0 });
                    devices.Add(new MCUBuilder { Name = part + "_XXAB", FlashSize = 128 * 1024, RAMSize = 16 * 1024, Core = CortexCore.M0 });
                    devices.Add(new MCUBuilder { Name = part + "_XXAC", FlashSize = 256 * 1024, RAMSize = 32 * 1024, Core = CortexCore.M0 });
                }
#endif

        devices.Add(new MCUBuilder { Name = "nRF52832_XXAA", FlashSize = 512 * 1024, RAMSize = 64 * 1024, Core = CortexCore.M4 });
        devices.Add(new MCUBuilder { Name = "nRF52840_XXAA", FlashSize = 1024 * 1024, RAMSize = 256 * 1024, Core = CortexCore.M4 });

        List<MCUFamilyBuilder> allFamilies = new List<MCUFamilyBuilder>();
        foreach (var fn in Directory.GetFiles(bspBuilder.Directories.RulesDir + @"\Families", "*.xml"))
            allFamilies.Add(new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(fn)));

        var rejects = BSPGeneratorTools.AssignMCUsToFamilies(devices, allFamilies);

        List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
        List<MCUFamilyBuilder.CopiedSample> exampleDirs = new List<MCUFamilyBuilder.CopiedSample>();

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
            // Startup Files
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

            SysVarEntry defaultConfigFolder51 = new SysVarEntry { Key = "com.sysprogs.nordic.default_config_suffix", Value = "pca10040/s132" };// s132_pca10036" };
            SysVarEntry defaultConfigFolder52 = new SysVarEntry { Key = "com.sysprogs.nordic.default_config_suffix", Value = "pca10056/s140" };// s130_pca10028" };

            SysVarEntry defaultConfigFolder51_pca = new SysVarEntry { Key = "com.sysprogs.nordic.default_config_suffix_pca", Value = "pca10040" };// s132_pca10036" };
            SysVarEntry defaultConfigFolder52_pca = new SysVarEntry { Key = "com.sysprogs.nordic.default_config_suffix_pca", Value = "pca10056" };// s130_pca10028" };

                foreach (var mcu in fam.MCUs)
            {
                var mcuDef = mcu.GenerateDefinition(fam, bspBuilder, !noPeripheralRegisters);
                    var compatibleSoftdevs = new PropertyEntry.Enumerated.Suggestion[] { new PropertyEntry.Enumerated.Suggestion { InternalValue = "nosoftdev", UserFriendlyName = "None" } }.Concat(bspBuilder.SoftDevices.Where(sd => sd.IsCompatible(mcu.Name)).Select(s => new PropertyEntry.Enumerated.Suggestion { InternalValue = s.Name, UserFriendlyName = s.UserFriendlyName })).Concat(bspBuilder.SoftDevices.Where(sd => sd.IsCompatible(mcu.Name)).Select(s => new PropertyEntry.Enumerated.Suggestion { InternalValue = s.Name + "_reserve", UserFriendlyName = s.UserFriendlyName+"_reserve" })).ToArray();

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

                if (mcu.Name.StartsWith("nRF52"))
                {
                    var prop = mcuDef.ConfigurableProperties.PropertyGroups[0].Properties.Find(p => p.UniqueID == "com.sysprogs.bspoptions.arm.floatmode") as PropertyEntry.Enumerated;
                    var idx = Array.FindIndex(prop.SuggestionList, p => p.UserFriendlyName == "Hardware");
                    prop.DefaultEntryIndex = idx;
                    prop.SuggestionList[idx].UserFriendlyName = "Hardware (required when using a softdevice)";   //Otherwise the system_nrf52.c file won't initialize the FPU and the internal initialization of the softdevice will later fail.

                    mcuDef.AdditionalSystemVars = LoadedBSP.Combine(mcuDef.AdditionalSystemVars, new SysVarEntry[] { defaultConfigFolder51, defaultConfigFolder51_pca });
                }
                else
                    mcuDef.AdditionalSystemVars = LoadedBSP.Combine(mcuDef.AdditionalSystemVars, new SysVarEntry[] { defaultConfigFolder52, defaultConfigFolder52_pca });

                mcuDefinitions.Add(mcuDef);
            }

            if (fam.Definition.ConditionalFlags != null)
                condFlags.AddRange(fam.Definition.ConditionalFlags);

            foreach (var fw in fam.GenerateFrameworkDefinitions())
                frameworks.Add(fw);

            foreach (var sample in fam.CopySamples(null, new SysVarEntry[] { defaultConfigFolder51 }))
                exampleDirs.Add(sample);
        }
        bspBuilder.GenerateSoftdeviceLibraries();

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
            strPAckVersion = "14.0r0";
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
        };

        bspBuilder.Save(bsp, true);
    }
}
}
