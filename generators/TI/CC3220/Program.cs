/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using BSPEngine;
using BSPGenerationTools;
using LinkerScriptGenerator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CC3200_bsp_generator
{
    class Program
    {
        class CC3220BSPBuilder : BSPBuilder
        {
            const uint SRAMBase = 0x20004000;

            public CC3220BSPBuilder(BSPDirectories dirs)
                : base(dirs, null, 5)
            {
                ShortName = "CC3220";
            }

            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                flashBase = 0;
                ramBase = SRAMBase;
            }

            public override bool OnFilePathTooLong(string pathInsidePackage)
            {
                return true;
            }

            public override MemoryLayout GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                //No additional memory information available for this MCU. Build a basic memory layout from known RAM/FLASH sizes.
                MemoryLayout layout = new MemoryLayout { DeviceName = mcu.Name, Memories = new List<Memory>() };

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

        private static IEnumerable<MCUDefinitionWithPredicate> ParsePeripheralRegisters(string dir)
        {
            List<MCUDefinitionWithPredicate> RegistersPeriphs = new List<MCUDefinitionWithPredicate>();
            MCUDefinitionWithPredicate mcu_def = new MCUDefinitionWithPredicate
            {
                MCUName = "CC_3220",
                RegisterSets = PeripheralRegisterGenerator.GeneratePeripheralRegisters(dir),
                MatchPredicate = null
            };

            RegistersPeriphs.Add(mcu_def);
            return RegistersPeriphs;
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: cc3220.exe <cc3220 SW package directory>");
            string DirSDK = args[0];
            var bspBuilder = new CC3220BSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules"));

            bool noPeripheralRegisters = args.Contains("/noperiph");
            bool noPack = args.Contains("/nopack");

            MCUFamilyBuilder famBuilder = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(Path.Combine(bspBuilder.Directories.RulesDir, "families\\cc3220.xml")));

            string deviceDefinitionFile = @"DeviceDefinitions/CC_3220.xml";

            foreach(var name in new[] { "CC3220SF", "CC3220S" })
            {
                famBuilder.MCUs.Add(new MCUBuilder
                {
                    Core = CortexCore.M4,
                    FlashSize = name.EndsWith("SF") ? 1024 * 1024 : 0,
                    RAMSize = 256 * 1024,
                    Name = name,
                    MCUDefinitionFile = deviceDefinitionFile,
                    LinkerScriptPath = $"$$SYS:BSP_ROOT$$/source/ti/boards/{name}_LAUNCHXL/{name}_LAUNCHXL_$$com.sysprogs.cc3220.rtos$$.lds",
                    StartupFile = null
                });
            }


            List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
            List<MCUFamilyBuilder.CopiedSample> exampleDirs = new List<MCUFamilyBuilder.CopiedSample>();

            MCUFamilyBuilder commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));

            var famObj = famBuilder.GenerateFamilyObject(true);
            List<string> projectFiles = new List<string>();
            commonPseudofamily.CopyFamilyFiles(ref famObj.CompilationFlags, projectFiles);

            foreach (var fw in commonPseudofamily.GenerateFrameworkDefinitions())
                frameworks.Add(fw);

            foreach (var sample in commonPseudofamily.CopySamples())
                exampleDirs.Add(sample);

            /*commonPseudofamily.Definition.FamilySubdirectory = "";
            if (!noPeripheralRegisters)
                commonPseudofamily.AttachPeripheralRegisters(ParsePeripheralRegisters(Path.Combine(DirSDK, @"source\ti\devices\cc32xx\inc")));*/


            List<MCU> mcuDefinitions = new List<MCU>();
            foreach (var mcu in famBuilder.MCUs)
                mcuDefinitions.Add(mcu.GenerateDefinition(famBuilder, bspBuilder, !noPeripheralRegisters, true));

            BoardSupportPackage bsp = new BoardSupportPackage
            {
                PackageID = "com.sysprogs.arm.ti.cc3220",
                PackageDescription = "TI CC3220 Devices",
                GNUTargetID = "arm-eabi",
                GeneratedMakFileName = "cc3220.mak",
                MCUFamilies = new[] { famObj },
                SupportedMCUs = mcuDefinitions.ToArray(),
                Frameworks = frameworks.ToArray(),
                Examples = exampleDirs.Where(s => !s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                TestExamples = exampleDirs.Where(s => s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                FileConditions = bspBuilder.MatchedFileConditions.ToArray(),
                PackageVersion = "1.40.01"
            };
            bspBuilder.Save(bsp, !noPack);

            StandaloneBSPValidator.Program.Main(new[] { "..\\..\\cc3220.validatejob", "f:\\bsptest" });
        }
    }
}
