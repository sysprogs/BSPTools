using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace rs14100
{
    class Program
    {
        class RS14100BSPBuilder : BSPBuilder
        {
            const uint SRAMBase = 0x00000000;
            const uint FLASHBase = 0x08012000;

            public RS14100BSPBuilder(BSPDirectories dirs)
                : base(dirs)
            {
                ShortName = "RS14100";
                SkipHiddenFiles = true;
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
                throw new NotSupportedException("RS14100 BSP should reuse existing linker scripts instead of generating new ones");
            }
        }

        private static IEnumerable<MCUDefinitionWithPredicate> ParsePeripheralRegisters(string dir)
        {
            List<MCUDefinitionWithPredicate> RegistersPeriphs = new List<MCUDefinitionWithPredicate>();
            MCUDefinitionWithPredicate mcu_def = new MCUDefinitionWithPredicate
            {
                MCUName = "RS14100",
                //RegisterSets = PeripheralRegisterGenerator.GeneratePeripheralRegisters(dir),
                MatchPredicate = null
            };

            RegistersPeriphs.Add(mcu_def);
            return RegistersPeriphs;
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: rs14100.exe <rs14100 SW package directory>");
            string DirSDK = args[0];
            using (var bspBuilder = new RS14100BSPBuilder(BSPDirectories.MakeDefault(args)))
            {
                bool noPeripheralRegisters = args.Contains("/noperiph");
                bool noPack = args.Contains("/nopack");

                MCUFamilyBuilder famBuilder = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(Path.Combine(bspBuilder.Directories.RulesDir, "rs14100.xml")));

                string deviceDefinitionFile = @"DeviceDefinitions/RS14100.xml";

                foreach (var name in new[] { "RS14100" })
                {
                    famBuilder.MCUs.Add(new MCUBuilder
                    {
                        Core = CortexCore.M4,
                        FlashSize = 0x000EE000,
                        RAMSize = 0x00030000,
                        Name = name,
                        //MCUDefinitionFile = deviceDefinitionFile,
                        LinkerScriptPath = $"$$SYS:BSP_ROOT$$/DeviceDefinition/arm-gcc-link.ld",
                        StartupFile = "$$SYS:BSP_ROOT$$/DeviceDefinition/startup_RS1xxxx.c"
                    });
                }


                List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
                List<MCUFamilyBuilder.CopiedSample> exampleDirs = new List<MCUFamilyBuilder.CopiedSample>();

                var famObj = famBuilder.GenerateFamilyObject(MCUFamilyBuilder.CoreSpecificFlags.All & ~MCUFamilyBuilder.CoreSpecificFlags.PrimaryMemory);
                List<string> projectFiles = new List<string>();

                if (!noPeripheralRegisters)
                    famBuilder.AttachPeripheralRegisters(ParsePeripheralRegisters(famBuilder.Definition.PrimaryHeaderDir));

                foreach (var fw in famBuilder.GenerateFrameworkDefinitions())
                    frameworks.Add(fw);

                foreach (var sample in famBuilder.CopySamples())
                    exampleDirs.Add(sample);

                List<MCU> mcuDefinitions = new List<MCU>();
                foreach (var mcuDef in famBuilder.MCUs)
                {
                    var mcu = mcuDef.GenerateDefinition(famBuilder, bspBuilder, !noPeripheralRegisters, true);
                    mcuDefinitions.Add(mcu);
                }

                BoardSupportPackage bsp = new BoardSupportPackage
                {
                    PackageID = "com.sysprogs.arm.rs14100",
                    PackageDescription = "Redpine RS14100 Devices",
                    GNUTargetID = "arm-eabi",
                    GeneratedMakFileName = "rs14100.mak",
                    MCUFamilies = new[] { famObj },
                    SupportedMCUs = mcuDefinitions.ToArray(),
                    Frameworks = frameworks.ToArray(),
                    Examples = exampleDirs.Where(s => !s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                    TestExamples = exampleDirs.Where(s => s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                    FileConditions = bspBuilder.MatchedFileConditions.Values.ToArray(),
                    PackageVersion = "1.1.3"
                };

                bspBuilder.ValidateBSP(bsp);
                bspBuilder.Save(bsp, !noPack, false);

                //StandaloneBSPValidator.Program.RunJob( "..\\..\\rs14100.validatejob", "f:\\bsptest");
            }
        }
    }
}
