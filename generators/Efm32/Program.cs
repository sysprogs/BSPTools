/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using BSPEngine;
using BSPGenerationTools;
using LinkerScriptGenerator;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Collections.Specialized;

namespace SLab_bsp_generator
{
    class Program
    {
        class SLabBSPBuilder : BSPBuilder
        {
            const uint FLASHBase = 0x00000000, SRAMBase = 0x20000000;
            private readonly Dictionary<string, List<Memory>> _Memories;

            public SLabBSPBuilder(BSPDirectories dirs)
                : base(dirs)
            {
                ShortName = "SiLab_EFM32";
            }


            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                flashBase = FLASHBase;
                ramBase = SRAMBase;
            }

            public override MemoryLayout GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                //No additional memory information available for this MCU. Build a basic memory layout from known RAM/FLASH sizes.
                MemoryLayout layout = new MemoryLayout();
                layout.Memories = new List<Memory>();
                layout.DeviceName = mcu.Name;
                string aFileName = family.BSP.Directories.InputDir + "\\platform\\Device\\SiliconLabs\\" + family.FamilyFilePrefix.Substring(0, family.FamilyFilePrefix.Length - 1) + "\\Include\\" + mcu + ".h";
                Match m;
                Regex rg = new Regex(@"(#define RAM_MEM_BASE[ \t]*.*0x)([0-9][U][L])+.*");
            /*    while(!File.Exists(aFileName))
                {
                    aFileName = aFileName.Remove(aFileName.Length - 3, 1);
                    if (aFileName.Length < 3)
                        throw new Exception("No file include");
                }*/
                var RAMStart = 0;
                foreach (var ln in File.ReadAllLines(aFileName))
                {
                    m = Regex.Match(ln, @"#define RAM_MEM_BASE[ \t]+.*0x([\d]+)UL.*");
                    RAMStart = 0;
                    if (m.Success)
                    {
                        RAMStart = Convert.ToInt32(m.Groups[1].Value, 16);
                        break;
                    }
                }
                if (RAMStart == 0)
                    throw new Exception("no RAM Start");
                layout.Memories.Insert(0, new Memory
                {
                    Name = "FLASH",
                    Access = MemoryAccess.Undefined,
                    Type = MemoryType.FLASH,
                    Start = FLASHBase,
                    Size = (uint)mcu.FlashSize
                });
                layout.Memories.Insert(0, new Memory
                {
                    Name = "SRAM",
                    Access = MemoryAccess.Undefined,
                    Type = MemoryType.RAM,
                    Start = (uint)RAMStart,
                    Size = (uint)mcu.RAMSize
                });

                return layout;
            }
        }

        enum enTypText { Text, MacroSection, MacroElseSection };

        static IEnumerable<StartupFileGenerator.InterruptVectorTable> ParseStartupFiles(string startupFileName, MCUFamilyBuilder fam)
        {
            List<StartupFileGenerator.InterruptVector[]> list = new List<StartupFileGenerator.InterruptVector[]>();
            list.Add(StartupFileGenerator.ParseInterruptVectors(startupFileName,
                     @"const pFunc __Vectors.*",
                     @"[ \t]*\};",
                     @"([^ \t,]+)[,]?[ \t]+// ([^\(]+)",
                     @"([^ \t,]+)[,]?.*",
                     @"^[ \t]*/.*",
                     null,
                     1,
                     2));
            List<StartupFileGenerator.InterruptVector> vectors = new List<StartupFileGenerator.InterruptVector>(list[0]);
            list.RemoveAt(0);

            //Fix the vector names from comments
            for (int i = 0; i < vectors.Count; i++)
            {
                if (vectors[i] == null)
                    continue;

                if (i == 0)
                {
                    vectors[i].Name = "_estack";
                    continue;
                }
                else if (i == 1)
                {
                    vectors[i].Name = "Reset_Handler";
                    continue;
                }
                else if (vectors[i].OptionalComment == "Reserved")
                {
                    vectors[i] = null;
                    continue;
                }
                else
                {
                    if (vectors[i] != null)
                        if (vectors[i].Name == "Default_Handler")
                            vectors[i] = null;
                }

            }
            yield return new StartupFileGenerator.InterruptVectorTable
            {
                FileName = Path.ChangeExtension(Path.GetFileName(startupFileName), ".c"),
                MatchPredicate = null,
                Vectors = vectors.ToArray()
            };
        }

        private static IEnumerable<MCUDefinitionWithPredicate> ParsePeripheralRegisters(string dir, MCUFamilyBuilder fam)
        {
            List<MCUDefinitionWithPredicate> RegistersPeriphs = new List<MCUDefinitionWithPredicate>();
            Dictionary<string, HardwareRegisterSet[]> periphs = PeripheralRegisterGenerator.GenerateFamilyPeripheralRegistersEFM32(dir + "\\Include", fam.Definition.FamilySubdirectory);

            foreach (var subfamily in periphs.Keys)
            {
                MCUDefinitionWithPredicate mcu_def = new MCUDefinitionWithPredicate { MCUName = subfamily, RegisterSets = periphs[subfamily], MatchPredicate = m => (subfamily == m.Name), };
                RegistersPeriphs.Add(mcu_def);
            }
            return RegistersPeriphs;
        }

        static List<MCUBuilder> RemoveDuplicateMCU(ref List<MCUBuilder> rawmcu_list)
        {
            foreach (var amcu in rawmcu_list)
            {
                var idx = amcu.Name.IndexOf("-");
                if (idx > 0)
                    amcu.Name = amcu.Name.Remove(idx);

            }
            return rawmcu_list;
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: EFM32.exe <SLab SW package directory>");

            var bspBuilder = new SLabBSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules"));

            var devices = BSPGeneratorTools.ReadMCUDevicesFromCommaDelimitedCSVFile(bspBuilder.Directories.RulesDir + @"\McuSiliconLabs.csv",
                "Part No.", "Flash (kB)", "Ram (kB)", "MCU Core", true);
            RemoveDuplicateMCU(ref devices);
            var devicesOld = BSPGeneratorTools.ReadMCUDevicesFromCommaDelimitedCSVFile(bspBuilder.Directories.RulesDir + @"\McuSiliconLabsOld.csv",
                "Part No.", "Flash (kB)", "Ram (kB)", "MCU Core", true);
            RemoveDuplicateMCU(ref devicesOld);
            foreach (var d in devicesOld)
                if (!devices.Contains(d))
                    devices.Add(d);
            if (devices.Where(d => d.RAMSize == 0 || d.FlashSize == 0).Count() > 0)
                throw new Exception($"Some devices are RAM Size ({devices.Where(d => d.RAMSize == 0).Count()})  = 0 or FLASH Size({devices.Where(d => d.FlashSize == 0).Count()})  = 0 ");

            
            List<MCUFamilyBuilder> allFamilies = new List<MCUFamilyBuilder>();
            foreach (var fn in Directory.GetFiles(bspBuilder.Directories.RulesDir + @"\Families", "*.xml"))
                allFamilies.Add(new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(fn)));

            var rejects = BSPGeneratorTools.AssignMCUsToFamilies(devices, allFamilies);
            List<MCUFamily> familyDefinitions = new List<MCUFamily>();
            List<MCU> mcuDefinitions = new List<MCU>();
            List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
            List<MCUFamilyBuilder.CopiedSample> exampleDirs = new List<MCUFamilyBuilder.CopiedSample>();


            bool noPeripheralRegisters = args.Contains("/noperiph");
            List<KeyValuePair<string, string>> macroToHeaderMap = new List<KeyValuePair<string, string>>();

            var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));

            foreach (var fw in commonPseudofamily.GenerateFrameworkDefinitions())
                frameworks.Add(fw);

            var flags = new ToolFlags();
            List<string> projectFiles = new List<string>();
            commonPseudofamily.CopyFamilyFiles(ref flags, projectFiles);

            foreach (var sample in commonPseudofamily.CopySamples())
                exampleDirs.Add(sample);

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

                var famObj = fam.GenerateFamilyObject(true);

                famObj.AdditionalSourceFiles = LoadedBSP.Combine(famObj.AdditionalSourceFiles, projectFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).ToArray());
                famObj.AdditionalHeaderFiles = LoadedBSP.Combine(famObj.AdditionalHeaderFiles, projectFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).ToArray());

                famObj.AdditionalSystemVars = LoadedBSP.Combine(famObj.AdditionalSystemVars, commonPseudofamily.Definition.AdditionalSystemVars);
                famObj.CompilationFlags = famObj.CompilationFlags.Merge(flags);
                famObj.CompilationFlags.PreprocessorMacros = LoadedBSP.Combine(famObj.CompilationFlags.PreprocessorMacros, new string[] { "$$com.sysprogs.bspoptions.primary_memory$$_layout" });

                familyDefinitions.Add(famObj);
                fam.GenerateLinkerScripts(false);
                if (!noPeripheralRegisters)
                    fam.AttachPeripheralRegisters(ParsePeripheralRegisters(bspBuilder.Directories.OutputDir + "\\" + fam.Definition.FamilySubdirectory + "\\Devices", fam));

                foreach (var mcu in fam.MCUs)
                    mcuDefinitions.Add(mcu.GenerateDefinition(fam, bspBuilder, !noPeripheralRegisters));

                foreach (var fw in fam.GenerateFrameworkDefinitions())
                    frameworks.Add(fw);

                foreach (var sample in fam.CopySamples())
                    exampleDirs.Add(sample);
            }

            BoardSupportPackage bsp = new BoardSupportPackage
            {
                PackageID = "com.sysprogs.arm.silabs.efm32",
                PackageDescription = "Silabs EFM32 Devices",
                GNUTargetID = "arm-eabi",
                GeneratedMakFileName = "efm32.mak",
                MCUFamilies = familyDefinitions.ToArray(),
                SupportedMCUs = mcuDefinitions.ToArray(),
                Frameworks = frameworks.ToArray(),
                Examples = exampleDirs.Where(s => !s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                TestExamples = exampleDirs.Where(s => s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                FileConditions = bspBuilder.MatchedFileConditions.ToArray(),
                PackageVersion = "1.0"
            };

            bspBuilder.Save(bsp, true);

        }
    }
}
