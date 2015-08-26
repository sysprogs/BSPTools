using BSPEngine;
using BSPGenerationTools;
using LinkerScriptGenerator;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace msp432_bsp_generator
{
    class Program
    {                      
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                throw new Exception("Usage: msp432_bsp_generator.exe <MSP432 SW package directory>");
            }

            var bspBuilder = new Msp432Builder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules"));

            var devices = BSPGeneratorTools.ReadMCUDevicesFromCommaDelimitedCSVFile(
                bspBuilder.Directories.RulesDir + @"\msp432devices.csv",
                "Part Number",
                "Non-volatile Memory (KB)",
                "RAM(KB)",
                "CPU",
                true);

            var allMCUFamilyBuilders = new List<MCUFamilyBuilder>();
            foreach (var fn in Directory.GetFiles(bspBuilder.Directories.RulesDir + @"\Families", "*.xml"))
            {
                allMCUFamilyBuilders.Add(new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(fn)));
            }

            var rejects = BSPGeneratorTools.AssignMCUsToFamilies(devices, allMCUFamilyBuilders);

            var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));
            var flags = new ToolFlags();
            var projectFiles = new List<string>();
            commonPseudofamily.CopyFamilyFiles(ref flags, projectFiles);

            var exampleDirs = new List<string>();
            foreach (var sample in commonPseudofamily.CopySamples())
            {
                exampleDirs.Add(sample);
            }

            bool noPeripheralRegisters = args.Contains("/noperiph");
            var familyDefinitions = new List<MCUFamily>();
            var mcuDefinitions = new List<MCU>();
            var frameworks = new List<EmbeddedFramework>();

            foreach (var mcuFamilyBuilder in allMCUFamilyBuilders)
            {
                var rejectedMCUs = mcuFamilyBuilder.RemoveUnsupportedMCUs(true);
                if (rejectedMCUs.Length != 0)
                {
                    Console.WriteLine("Unsupported {0} MCUs:", mcuFamilyBuilder.Definition.Name);
                    foreach (var mcu in rejectedMCUs)
                    {
                        Console.WriteLine("\t{0}", mcu.Name);
                    }
                }

                mcuFamilyBuilder.AttachStartupFiles(new[] 
                {
                    StartupFilesParser.Parse(
                        mcuFamilyBuilder.Definition.Name, 
                        mcuFamilyBuilder.Definition.PrimaryHeaderDir, 
                        mcuFamilyBuilder.Definition.Name + "_startup_gcc.c")
                 });
                
                if (!noPeripheralRegisters)
                {                    
                    var headerFiles = Directory.GetFiles(mcuFamilyBuilder.Definition.PrimaryHeaderDir + "\\inc", "*.h");
                    var headerFileRegex = new Regex(mcuFamilyBuilder.Definition.DeviceRegex, RegexOptions.IgnoreCase);
                    var familyHeaderFiles = headerFiles.Where(headerFile => 
                        headerFileRegex.Match(headerFile.Substring(headerFile.LastIndexOf("\\") + 1)).Success).ToArray();

                    if (familyHeaderFiles.Length == 0)
                    {
                        throw new Exception("No header file found for MCU family");
                    }
                    else if (familyHeaderFiles.Length > 1)
                    {
                        throw new Exception("Only one header file expected for MCU family");
                    }

                    var registersParser = new RegistersParser(familyHeaderFiles[0]);
                                    
                    mcuFamilyBuilder.AttachPeripheralRegisters(new[]
                    {
                        new MCUDefinitionWithPredicate
                        {
                            MCUName = mcuFamilyBuilder.Definition.Name,
                            RegisterSets = registersParser.Parse(),
                            MatchPredicate = m => true
                        }
                    });                       
                }

                var familyObject = mcuFamilyBuilder.GenerateFamilyObject(true);

                familyObject.AdditionalSourceFiles = LoadedBSP.Combine(familyObject.AdditionalSourceFiles, projectFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).ToArray());
                familyObject.AdditionalHeaderFiles = LoadedBSP.Combine(familyObject.AdditionalHeaderFiles, projectFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).ToArray());

                familyObject.AdditionalSystemVars = LoadedBSP.Combine(familyObject.AdditionalSystemVars, commonPseudofamily.Definition.AdditionalSystemVars);
                familyObject.CompilationFlags = familyObject.CompilationFlags.Merge(flags);

                familyDefinitions.Add(familyObject);
                mcuFamilyBuilder.GenerateLinkerScripts(false);

                foreach (var mcu in mcuFamilyBuilder.MCUs)
                {
                    mcuDefinitions.Add(mcu.GenerateDefinition(mcuFamilyBuilder, bspBuilder, !noPeripheralRegisters));
                }

                foreach (var fw in mcuFamilyBuilder.GenerateFrameworkDefinitions())
                {
                    frameworks.Add(fw);
                }

                foreach (var sample in mcuFamilyBuilder.CopySamples())
                {
                    exampleDirs.Add(sample);
                }
            }

            BoardSupportPackage bsp = new BoardSupportPackage
            {
                PackageID = "com.sysprogs.arm.ti.msp432",
                PackageDescription = "TI MSP432 Devices",
                GNUTargetID = "arm-eabi",
                GeneratedMakFileName = "msp432.mak",
                MCUFamilies = familyDefinitions.ToArray(),
                SupportedMCUs = mcuDefinitions.ToArray(),
                Frameworks = frameworks.ToArray(),
                Examples = exampleDirs.ToArray(),
                PackageVersion = "1.0"
            };

            bspBuilder.Save(bsp, true);
        }        

        class Msp432Builder : BSPBuilder
        {
            const uint FLASHBase = 0x00000000, SRAMBase = 0x20000000;

            public Msp432Builder(BSPDirectories dirs)
                : base(dirs)
            {
                ShortName = "MSP432";
            }

            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                flashBase = FLASHBase;
                ramBase = SRAMBase;
            }

            public override MemoryLayout GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                var layout = new MemoryLayout { DeviceName = mcu.Name, Memories = new List<Memory>() };

                layout.Memories.Add(new Memory
                {
                    Name = "FLASH",
                    Access = MemoryAccess.Undefined,
                    Type = MemoryType.FLASH,
                    Start = FLASHBase,
                    Size = (uint)mcu.FlashSize
                });

                layout.Memories.Add(new Memory
                {
                    Name = "SRAM",
                    Access = MemoryAccess.Undefined,
                    Type = MemoryType.RAM,
                    Start = SRAMBase,
                    Size = (uint)mcu.RAMSize
                });

                return layout;
            }
        }
    }
}
