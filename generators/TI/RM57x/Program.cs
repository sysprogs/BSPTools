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

namespace RM57x
{
    class Program
    {
        class RM57xBSPBuilder : BSPBuilder
        {
            const uint SRAMBase = 0x08000000;
            const uint FLASHBase = 0x00000000;

            public RM57xBSPBuilder(BSPDirectories dirs)
                : base(dirs, null, 5)
            {
                ShortName = "RM57x";
            }

            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                flashBase = FLASHBase;
                ramBase = SRAMBase;
            }

            public override bool OnFilePathTooLong(string pathInsidePackage)
            {
                return true;
            }

            public override MemoryLayoutAndSubstitutionRules GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                throw new NotSupportedException("RM57x BSP should reuse existing linker scripts instead of generating new ones");
            }
        }

        private static IEnumerable<MCUDefinitionWithPredicate> ParsePeripheralRegisters(string dir)
        {
            List<MCUDefinitionWithPredicate> RegistersPeriphs = new List<MCUDefinitionWithPredicate>();
            MCUDefinitionWithPredicate mcu_def = new MCUDefinitionWithPredicate
            {
                MCUName = "RM57L843ZWT",
                RegisterSets = null,
                MatchPredicate = null
            };

            RegistersPeriphs.Add(mcu_def);
            return RegistersPeriphs;
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: rm57x.exe <RM57x generated HAL directory>");
            string DirSDK = args[0];
            using (var bspBuilder = new RM57xBSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules", @"..\..\log")))
            {

                bool noPeripheralRegisters = args.Contains("/noperiph");
                bool noPack = args.Contains("/nopack");

                MCUFamilyBuilder famBuilder = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(Path.Combine(bspBuilder.Directories.RulesDir, "families\\rm57x.xml")));

                //string deviceDefinitionFile = @"DeviceDefinitions/CC_3220.xml";

                foreach (var name in new[] { "RM57L843ZWT" })
                {
                    famBuilder.MCUs.Add(new MCUBuilder
                    {
                        Core = CortexCore.R5F,
                        FlashSize = 4096 * 1024,
                        RAMSize = 512 * 1024,
                        Name = name,
                        //MCUDefinitionFile = deviceDefinitionFile,
                        StartupFile = null
                    });
                }


                List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
                List<MCUFamilyBuilder.CopiedSample> exampleDirs = new List<MCUFamilyBuilder.CopiedSample>();

                MCUFamilyBuilder commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));

                var famObj = famBuilder.GenerateFamilyObject(MCUFamilyBuilder.CoreSpecificFlags.All & ~MCUFamilyBuilder.CoreSpecificFlags.PrimaryMemory);
                List<string> projectFiles = new List<string>();
                commonPseudofamily.CopyFamilyFiles(ref famObj.CompilationFlags, projectFiles);

                famObj.AdditionalSourceFiles = famObj.AdditionalSourceFiles.Concat(projectFiles).ToArray();

                foreach (var fw in commonPseudofamily.GenerateFrameworkDefinitions())
                    frameworks.Add(fw);

                foreach (var sample in commonPseudofamily.CopySamples())
                    exampleDirs.Add(sample);

                if (!noPeripheralRegisters)
                    famBuilder.AttachPeripheralRegisters(ParsePeripheralRegisters(famBuilder.Definition.PrimaryHeaderDir));

                List<MCU> mcuDefinitions = new List<MCU>();
                foreach (var mcuDef in famBuilder.MCUs)
                {
                    var mcu = mcuDef.GenerateDefinition(famBuilder, bspBuilder, !noPeripheralRegisters, true);
                    mcuDefinitions.Add(mcu);
                }

                BoardSupportPackage bsp = new BoardSupportPackage
                {
                    PackageID = "com.sysprogs.arm.ti.rm57x",
                    PackageDescription = "TI RM57Lx Devices",
                    GNUTargetID = "arm-eabi",
                    GeneratedMakFileName = "rm57x.mak",
                    MCUFamilies = new[] { famObj },
                    SupportedMCUs = mcuDefinitions.ToArray(),
                    Frameworks = frameworks.ToArray(),
                    Examples = exampleDirs.Where(s => !s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                    TestExamples = exampleDirs.Where(s => s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                    FileConditions = bspBuilder.MatchedFileConditions.Values.ToArray(),
                    ConditionalFlags = commonPseudofamily.Definition.ConditionalFlags,
                    PackageVersion = "1.0"
                };
                bspBuilder.Save(bsp, !noPack);

                //StandaloneBSPValidator.Program.Main(new[] { "..\\..\\cc3220.validatejob", "f:\\bsptest" });
            }
        }
    }
}
