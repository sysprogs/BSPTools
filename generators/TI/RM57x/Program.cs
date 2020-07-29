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
using System.Text;
using System.Xml;

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

        private static IEnumerable<MCUDefinitionWithPredicate> ParsePeripheralRegisters(string dir, string deviceName)
        {
            List<MCUDefinitionWithPredicate> RegistersPeriphs = new List<MCUDefinitionWithPredicate>();
            MCUDefinitionWithPredicate mcu_def = new MCUDefinitionWithPredicate
            {
                MCUName = deviceName,
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
                var xml = new XmlDocument();
                xml.Load(Path.Combine(bspBuilder.Directories.InputDir, "NonRTOS\\NonRTOS.hcg"));

                var deviceID = xml.DocumentElement.SelectSingleNode("DEVICE/device")?.InnerText ?? throw new Exception("Failed to extract the device ID");
                var familyID = xml.DocumentElement.SelectSingleNode("DEVICE/family")?.InnerText ?? throw new Exception("Failed to extract the family ID");

                CortexCore core;
                bool isBigEndian = false;
                bool isThumb = true;

                switch (deviceID)
                {
                    case "RM57L843ZWT":
                        core = CortexCore.R5;
                        break;
                    case "TMS570LS1224PGE":
                        core = CortexCore.R4;
                        isBigEndian = true;
                        isThumb = false;
                        break;
                    default:
                        throw new Exception($"Unknown ARM Cortex core for {deviceID}. Please update the logic above.");
                }

                bool noPeripheralRegisters = args.Contains("/noperiph");
                bool noPack = args.Contains("/nopack");

                var nonRTOSDir = Path.GetFullPath(Path.Combine(bspBuilder.Directories.InputDir, "NonRTOS"));
                var linkerScriptPath = Directory.GetFiles(nonRTOSDir, "*.ld", SearchOption.AllDirectories)[0];

                MCUFamilyBuilder famBuilder = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(Path.Combine(bspBuilder.Directories.RulesDir, "families\\rm57x.xml")));
                famBuilder.Definition.Name = familyID;

                if (isBigEndian)
                    famBuilder.Definition.CompilationFlags.COMMONFLAGS += " -mbig-endian -mbe32";

                //string deviceDefinitionFile = @"DeviceDefinitions/CC_3220.xml";

                var memories = LinkerScriptTools.ScanLinkerScriptForMemories(linkerScriptPath);

                famBuilder.MCUs.Add(new MCUBuilder
                {
                    Core = core,
                    FPU = FPUType.DP,
                    FlashSize = (int)memories.First(m => m.Name == "FLASH").Size,
                    RAMSize = (int)memories.First(m => m.Name == "RAM").Size,
                    Name = deviceID,
                    //MCUDefinitionFile = deviceDefinitionFile,
                    StartupFile = null
                });

                List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
                List<MCUFamilyBuilder.CopiedSample> exampleDirs = new List<MCUFamilyBuilder.CopiedSample>();

                MCUFamilyBuilder commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));

                var famObj = famBuilder.GenerateFamilyObject(MCUFamilyBuilder.CoreSpecificFlags.All & ~MCUFamilyBuilder.CoreSpecificFlags.PrimaryMemory);
                List<string> projectFiles = new List<string>();
                commonPseudofamily.CopyFamilyFiles(ref famObj.CompilationFlags, projectFiles);

                famObj.AdditionalSourceFiles = famObj.AdditionalSourceFiles.Concat(projectFiles).ToArray();

                if (!Directory.Exists(Path.Combine(bspBuilder.Directories.InputDir, "FreeRTOS")))
                {
                    Console.WriteLine("Missing FreeRTOS directory. Skipping FreeRTOS framework and project sample...");
                    commonPseudofamily.Definition.AdditionalFrameworks = commonPseudofamily.Definition.AdditionalFrameworks.Where(f => !f.ID.Contains("freertos")).ToArray();
                    commonPseudofamily.Definition.SmartSamples = commonPseudofamily.Definition.SmartSamples.Where(f => f.EmbeddedSample.Name.IndexOf("freertos", StringComparison.InvariantCultureIgnoreCase) == -1).ToArray();
                }

                foreach (var fw in commonPseudofamily.GenerateFrameworkDefinitions())
                    frameworks.Add(fw);

                foreach (var sample in commonPseudofamily.CopySamples())
                    exampleDirs.Add(sample);

                if (!noPeripheralRegisters)
                    famBuilder.AttachPeripheralRegisters(ParsePeripheralRegisters(famBuilder.Definition.PrimaryHeaderDir, deviceID));

                if (!isThumb)
                    famObj.CompilationFlags.COMMONFLAGS = famObj.CompilationFlags.COMMONFLAGS.Replace("-mthumb", "-marm");

                List<MCU> mcuDefinitions = new List<MCU>();
                foreach (var mcuDef in famBuilder.MCUs)
                {
                    var mcu = mcuDef.GenerateDefinition(famBuilder, bspBuilder, !noPeripheralRegisters, true);

                    mcu.AdditionalSystemVars = (mcu.AdditionalSystemVars ?? new SysVarEntry[0]).Concat(new[]
                    {
                        new SysVarEntry{ Key = "com.sysprogs.linker_script", Value = linkerScriptPath.Substring(nonRTOSDir.Length + 1).Replace('\\', '/')},
                        CreateHeaderFileVariable(nonRTOSDir, "common.h"),
                        CreateHeaderFileVariable(nonRTOSDir, "gio.h"),
                        CreateHeaderFileVariable(nonRTOSDir, "het.h"),
                    }).ToArray();

                    mcuDefinitions.Add(mcu);
                }

                ApplyKnownPatches(bspBuilder.Directories.OutputDir);

                BoardSupportPackage bsp = new BoardSupportPackage
                {
                    PackageID = "com.sysprogs.arm.ti." + deviceID,
                    PackageDescription = $"TI {deviceID} Device",
                    GNUTargetID = "arm-eabi",
                    GeneratedMakFileName = familyID + ".mak",
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

        static SysVarEntry CreateHeaderFileVariable(string baseDir, string fileName)
        {
            var matchedFile = Directory.GetFiles(baseDir, "*.h", SearchOption.AllDirectories).Where(f =>
            {
                var nameOnly = Path.GetFileName(f);
                return StringComparer.InvariantCultureIgnoreCase.Compare(nameOnly, fileName) == 0 || nameOnly.EndsWith("_" + fileName, StringComparison.InvariantCultureIgnoreCase);
            }).OrderBy(f => f.Length).First();

            return new SysVarEntry { Key = "com.sysprogs.halcogen." + fileName, Value = Path.GetFileName(matchedFile) };
        }

        private static void ApplyKnownPatches(string dir)
        {
            foreach (var file in Directory.GetFiles(dir, "HL_sys_startup.c", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file).ToList();
                int idx = -1;
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i].Contains("esmGroup3Notification(esmREG,esmREG->SR1[2]);"))
                    {
                        idx = i;
                        break;
                    }

                if (idx == -1)
                    throw new Exception("Could not find the startup notification error line");

                lines.Insert(idx, "#if !DEBUG");
                lines.Insert(idx + 2, "#endif");
                File.WriteAllLines(file, lines);
            }
        }
    }
}
