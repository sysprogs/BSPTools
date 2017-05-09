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

namespace Tiva_bsp_generator
{
    class Program
    {
        class TivaBSPBuilder : BSPBuilder
        {
            const uint FLASHBase = 0x00000000, SRAMBase = 0x20000000;

            public TivaBSPBuilder(BSPDirectories dirs)
                : base(dirs)
            {
                ShortName = "Tiva";
            }

            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                flashBase = FLASHBase;
                ramBase = SRAMBase;
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
        }

        static IEnumerable<StartupFileGenerator.InterruptVectorTable> ParseStartupFiles(string dir, string startupFileName, MCUFamilyBuilder fam)
        {
            FileInfo[] startups = (new DirectoryInfo(dir)).GetFiles(startupFileName, SearchOption.AllDirectories);
            List<StartupFileGenerator.InterruptVector[]> list = new List<StartupFileGenerator.InterruptVector[]>();

            // Read in all the relevant startup files
            foreach (var startup in startups)
            {
                if (!(startup.FullName.ToUpperInvariant().Contains(fam.Definition.Name.ToUpperInvariant()) || ((startup.FullName == "TM4C123") && startup.FullName.ToUpperInvariant().Contains("LM4F232"))))
                    continue;

                list.Add(StartupFileGenerator.ParseInterruptVectors(startup.FullName, @"void \(\* const g_pfnVectors\[\]\)\(void\) \=", @"[ \t]*\};", @"([^ \t/]+)[,]?[ \t]+// ([^\(]+)", @"[ \t]*\(void \(\*\)\(void\)\)\(\(uint32_t\)pui32Stack \+ sizeof\(pui32Stack\)\)\,", @"\{|^[ /t]*// (.*)", null, 1, 2));
            }

            List<StartupFileGenerator.InterruptVector> vectors = new List<StartupFileGenerator.InterruptVector>(list[0]);
            list.RemoveAt(0);
            foreach(var entry in list)
            {
                if ((entry.Length != vectors.Count) && (entry.Length != 224) && entry.Length != 138)//224 is length of two boot loader demo example interrupt tables, ignoring the extra entries there!
                    throw new Exception("Interrupt vector counts different!");

                for (int i = 0; i < vectors.Count; i++)
                {
                    if (entry[i].OptionalComment == vectors[i].OptionalComment)
                        continue;

                    throw new Exception();
                }
            }

            //Fix the vector names from comments
            for (int i = 0; i <vectors.Count; i++)
            {
                if(i == 0)
                {
                    vectors[i].Name = "_estack";
                    continue;
                }
                else if(i == 1)
                {
                    vectors[i].Name = "Reset_Handler";
                    continue;
                }
                else if(vectors[i].OptionalComment == "Reserved")
                {
                    vectors[i] = null;
                    continue;
                }

                TextInfo txt_info = new CultureInfo("").TextInfo;
                vectors[i].Name = txt_info.ToTitleCase(vectors[i].OptionalComment.Replace(" and "," ")).Replace(" ", "") + "ISR";
                if (vectors[i].Name.StartsWith("The"))
                    vectors[i].Name = vectors[i].Name.Substring(3);
                vectors[i].Name = vectors[i].Name.Replace('/', '_');
            }

            yield return new StartupFileGenerator.InterruptVectorTable
            {
                FileName = Path.GetFileName(startupFileName),
                MatchPredicate = m => true,
                Vectors = vectors.ToArray()
            };
       }

        private static IEnumerable<MCUDefinitionWithPredicate> ParsePeripheralRegisters(string dir)
        {
            HardwareRegisterSet[] periphs = PeripheralRegisterGenerator.GenerateFamilyPeripheralRegisters(
                Path.Combine(dir, @"inc\hw_memmap.h"),
                PeripheralRegisterGenerator.FindRelevantHeaderFiles(Path.Combine(dir, @"inc")));
            yield return new MCUDefinitionWithPredicate
            {
                MCUName = "Tiva",
                RegisterSets = periphs,
                MatchPredicate = m => true,
            };
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: tiva.exe <Tiva SW package directory>");

            var bspBuilder = new TivaBSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules"));

            var devices = BSPGeneratorTools.ReadMCUDevicesFromCommaDelimitedCSVFile(bspBuilder.Directories.RulesDir + @"\Tivadevices.csv",
                "Part Number", "Flash (KB)", "SRAM(kB)", "CPU", true);

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

                foreach(var mcu in fam.MCUs)
                {
                    string fn = string.Format("{0}\\inc\\{1}.h", fam.Definition.PrimaryHeaderDir, mcu.Name);
                    if (!File.Exists(fn))
                        throw new Exception("Missing device header file");
                    macroToHeaderMap.Add(new KeyValuePair<string, string>(mcu.Name, mcu.Name.ToLower() + ".h"));
                }

                fam.AttachStartupFiles(ParseStartupFiles(fam.Definition.PrimaryHeaderDir, "startup_gcc.c", fam));
                if (!noPeripheralRegisters)
                    fam.AttachPeripheralRegisters(ParsePeripheralRegisters(fam.Definition.PrimaryHeaderDir));

                var famObj = fam.GenerateFamilyObject(true);

                famObj.AdditionalSourceFiles = LoadedBSP.Combine(famObj.AdditionalSourceFiles, projectFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).ToArray());
                famObj.AdditionalHeaderFiles = LoadedBSP.Combine(famObj.AdditionalHeaderFiles, projectFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).ToArray());

                famObj.AdditionalSystemVars = LoadedBSP.Combine(famObj.AdditionalSystemVars, commonPseudofamily.Definition.AdditionalSystemVars);
                famObj.CompilationFlags = famObj.CompilationFlags.Merge(flags);

                familyDefinitions.Add(famObj);
                fam.GenerateLinkerScripts(false);
                foreach (var mcu in fam.MCUs)
                    mcuDefinitions.Add(mcu.GenerateDefinition(fam, bspBuilder, !noPeripheralRegisters));

                foreach (var fw in fam.GenerateFrameworkDefinitions())
                    frameworks.Add(fw);

                foreach (var sample in fam.CopySamples())
                    exampleDirs.Add(sample);
            }

            using (var sw = File.CreateText(Path.Combine(bspBuilder.BSPRoot, "SDK", "inc", "tiva_device.h")))
            {
                sw.WriteLine("#pragma once");
                sw.WriteLine();
                bool first = true;
                foreach(var kv in macroToHeaderMap)
                {
                    sw.WriteLine("#{0}if defined({1})", first ? "" : "el", kv.Key);
                    sw.WriteLine("\t#include \"{0}\"", kv.Value);
                    first = false;
                }

                sw.WriteLine("#else");
                sw.WriteLine("#error Device type not specified");
                sw.WriteLine("#endif");
            }

            BoardSupportPackage bsp = new BoardSupportPackage
            {
                PackageID = "com.sysprogs.arm.ti.tiva",
                PackageDescription = "TI Tiva Devices",
                GNUTargetID = "arm-eabi",
                GeneratedMakFileName = "tiva.mak",
                MCUFamilies = familyDefinitions.ToArray(),
                SupportedMCUs = mcuDefinitions.ToArray(),
                Frameworks = frameworks.ToArray(),
                Examples = exampleDirs.Where(s => !s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                TestExamples = exampleDirs.Where(s => s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),

                PackageVersion = "2.1.3.156r2"
            };

            bspBuilder.Save(bsp, true);
        }
    }
}
