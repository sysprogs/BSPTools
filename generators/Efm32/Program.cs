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

            protected override LinkerScriptTemplate GetTemplateForMCU(MCUBuilder mcu)
            {
                var template = base.GetTemplateForMCU(mcu).ShallowCopy();
                template.SymbolAliases = new[] { new SymbolAlias { Name = "__Vectors", Target = "g_pfnVectors" } };
                return template;
            }

            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                flashBase = FLASHBase;
                ramBase = SRAMBase;
            }

            public override MemoryLayoutAndSubstitutionRules GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                //No additional memory information available for this MCU. Build a basic memory layout from known RAM/FLASH sizes.
                MemoryLayout layout = new MemoryLayout();
                layout.Memories = new List<Memory>();
                layout.DeviceName = mcu.Name;
                string aFileName = family.BSP.Directories.InputDir + "\\platform\\Device\\SiliconLabs\\" + family.FamilyFilePrefix.Substring(0, family.FamilyFilePrefix.Length - 1) + "\\Include\\" + mcu + ".h";
                Match m;
                Regex rg = new Regex(@"(#define RAM_MEM_BASE[ \t]*.*0x)([0-9][U][L])+.*");
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

                return new MemoryLayoutAndSubstitutionRules(layout);
            }
        }

        enum enTypText { Text, MacroSection, MacroElseSection };

        static StartupFileGenerator.InterruptVector VectorLineHook(string[] lines, ref int lineIndex)
        {
            var line = lines[lineIndex];
            if (line.StartsWith("#ifdef"))
            {
                if (line == "#ifdef SL_APP_PROPERTIES")
                {
                    lineIndex += 4;
                    return new StartupFileGenerator.InterruptVector { Name = "sl_app_properties" };
                }

                else if (line == "#ifdef BOOTLOADER_ENABLE")
                {
                    lineIndex += 4;
                    return new StartupFileGenerator.InterruptVector { Name = "Bootloader_MainStageTable" };
                }

                throw new Exception("Unexpected conditional interrupt vector");
            }

            return null;
        }


        static StartupFileGenerator.InterruptVectorTable ParseStartupFiles(string startupFileName, MCUFamilyBuilder fam)
        {
            var vectors = StartupFileGenerator.ParseInterruptVectors(startupFileName,
                     @"const[ \t]+tVectorEntry[ \t]+__VECTOR_TABLE\[TOTAL_INTERRUPTS\][ \t]+__VECTOR_TABLE_ATTRIBUTE[ \t]+=[ \t]+\{",
                     @"[ \t]*\};",
                     @"\{[ \t]+([^ \t]+)[ \t]+\},[ \t]+/\*(.*)\*/",
                     @"([^ \t,]+)[,]?.*",
                     @"^([ \t]*/.*|#endif)",
                     null,
                     1,
                     2,
                     VectorLineHook,
                     true);

            //Fix the vector names from comments
            for (int i = 0; i < vectors.Length; i++)
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
                    if (vectors[i].Name == "Default_Handler")
                        vectors[i] = null;
                }

                if (vectors[i]?.Name is string s && s.IndexOfAny(new[] { '#', '/', '{' }) != -1)
                    throw new Exception("Invalid vector: " + vectors[i].Name);
            }

            if (vectors.Length < 16)
                throw new Exception("Too little vectors parsed");

            return new StartupFileGenerator.InterruptVectorTable
            {
                FileName = Path.ChangeExtension(Path.GetFileName(startupFileName), ".c"),
                MatchPredicate = null,
                Vectors = vectors.ToArray()
            };
        }

        private static IEnumerable<MCUDefinitionWithPredicate> ParsePeripheralRegisters(string dir, MCUFamilyBuilder fam)
        {
            List<MCUDefinitionWithPredicate> RegistersPeriphs = new List<MCUDefinitionWithPredicate>();
            Dictionary<string, HardwareRegisterSet[]> periphs = PeripheralRegisterGenerator.GenerateFamilyPeripheralRegistersEFM32(dir + "\\Include", fam.Definition.FamilySubdirectory, fam.MCUs.Select(m => m.Name).ToArray());

            foreach (var subfamily in periphs.Keys)
            {
                MCUDefinitionWithPredicate mcu_def = new MCUDefinitionWithPredicate { MCUName = subfamily, RegisterSets = periphs[subfamily], MatchPredicate = m => (subfamily == m.Name), };
                RegistersPeriphs.Add(mcu_def);
            }
            return RegistersPeriphs;
        }

        static void ValidateMCUNames(List<MCUBuilder> rawmcu_list)
        {
            foreach (var amcu in rawmcu_list)
            {
                var idx = amcu.Name.IndexOfAny(new[] { ' ', '-' });
                if (idx >= 0)
                    throw new Exception("MCU name should not contain '-' or ' '");
                if (amcu.Name.EndsWith("G"))
                    throw new Exception("MCU name should not end with 'G'");
            }
        }

        static bool IsMcuFull(MCUBuilder mcu)
        {
            if (mcu.Name != null && mcu.RAMSize != 0 && mcu.FlashSize != 0 && mcu.Core != CortexCore.Invalid)
                return true;
            else
                return false;
        }
        public static bool GetPropertyMCU(string str, string nameproperty, ref int property)
        {
            if (property != 0)
                return true;
            Match m = Regex.Match(str, $@"#define {nameproperty} [ \t(]*0x([0-9A-Fa-f]+)[UL\)]+.*");
            if (!m.Success)
                return false;

            property = int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
            return true;

        }

        public static MCUBuilder GetMcuFromFile(string fileinc)
        {
            var mcu = new MCUBuilder();
            foreach (var lnstr in File.ReadAllLines(fileinc))
            {
                Match m = Regex.Match(lnstr, @"[/* ]+Flash and SRAM limits for ([\w\d]+) [.]*");
                if (m.Success)
                    mcu.Name = m.Groups[1].Value;

                if (mcu.Name != null)
                {
                    GetPropertyMCU(lnstr, "FLASH_SIZE", ref mcu.FlashSize);
                    GetPropertyMCU(lnstr, "SRAM_SIZE", ref mcu.RAMSize);
                }

                if (mcu.Core == CortexCore.Invalid)
                {
                    m = Regex.Match(lnstr, @"#define __CM([\d\w]+)_REV[0-9xU /*<]+(Cortex-M[0-7+]+).*");
                    if (m.Success)
                        mcu.Core = BSPGeneratorTools.ParseCoreName(m.Groups[2].Value, out mcu.FPU);
                }

                if (IsMcuFull(mcu))
                    break;
            }

            if (mcu.Name == null)
                return null;

            if (!IsMcuFull(mcu))
                throw new Exception($"Failed to detect some parameters for '{mcu.Name}'");

            mcu.FPU = BSPGeneratorTools.GetDefaultFPU(mcu.Core);

            return mcu;
        }
        public static List<MCUBuilder> GetMCUsForFamily(string familyDir)
        {
            List<MCUBuilder> rawmcu_list = new List<MCUBuilder>();

            foreach (var flinc in Directory.GetFiles(familyDir, "*.h", SearchOption.AllDirectories))
            {
                if (!flinc.ToLower().Contains("include") || Path.GetFileName(flinc).Contains("_"))
                    continue;
                var MCU = GetMcuFromFile(flinc);

                if (MCU != null)
                    rawmcu_list.Add(MCU);
                else
                    Console.WriteLine($@"no mcu file {flinc}");
                //if (rawmcu_list.Count() == 20)
                //  break;

            }

            rawmcu_list.Sort((a, b) => a.Name.CompareTo(b.Name));
            return rawmcu_list;

        }
        public class StringIndexKeyComparer : IEqualityComparer<string>
        {
            /// <summary>
            /// Has a good distribution.
            /// </summary>
            const int _multiplier = 89;

            /// <summary>
            /// Whether the two strings are equal
            /// </summary>
            public bool Equals(string x, string y)
            {
                return x.Contains(y);
            }

            /// <summary>
            /// Return the hash code for this string.
            /// </summary>
            public int GetHashCode(string obj)
            {
                // Stores the result.
                int result = 0;


                return result;
            }
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: EFM32.exe <SLab SW package directory>");

            using (var bspBuilder = new SLabBSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules", @"..\..\logs")))
            {
                List<MCUFamilyBuilder> allFamilies = new List<MCUFamilyBuilder>();
                var ignoredFamilyNames = File.ReadAllLines(Path.Combine(bspBuilder.Directories.RulesDir, "rulesfamaly.txt"));

                string versionPrefix = "version=";
                var propertiesFile = Path.Combine(bspBuilder.Directories.InputDir, @"app\mcu_example\app_mcu.properties");
                if (!File.Exists(propertiesFile))
                    propertiesFile = Path.Combine(bspBuilder.Directories.InputDir, @".studio\efm32.properties");

                var innerVersion = File.ReadAllLines(propertiesFile).First(l => l.StartsWith(versionPrefix)).Substring(versionPrefix.Length).Trim();
                if (innerVersion.Count(c => c == '.') == 3 && innerVersion.EndsWith(".0"))
                    innerVersion = innerVersion.Substring(0, innerVersion.Length - 2);

                var outerVersion = File.ReadAllLines(Path.Combine(bspBuilder.Directories.InputDir, ".properties")).First(l => l.StartsWith(versionPrefix)).Substring(versionPrefix.Length).Trim();

                string DirDevices = Path.Combine(bspBuilder.Directories.InputDir, @"platform\Device\SiliconLabs");
                string[] allFamilySubdirectories = Directory.GetDirectories(DirDevices);
                Console.WriteLine("Enumerating devices...");
                foreach (var dir in allFamilySubdirectories)
                {
                    string familyName = Path.GetFileNameWithoutExtension(dir);

                    if (ignoredFamilyNames.FirstOrDefault(n => dir.Contains(n)) != null)
                        continue;

                    var devices = GetMCUsForFamily(dir);
                    Console.WriteLine($"    {familyName}: {devices.Count} devices");
                    ValidateMCUNames(devices);

                    if (devices.Where(d => d.RAMSize == 0 || d.FlashSize == 0).Count() > 0)
                        throw new Exception($"Some devices are RAM Size ({devices.Where(d => d.RAMSize == 0).Count()})  = 0 or FLASH Size({devices.Where(d => d.FlashSize == 0).Count()})  = 0 ");

                    if (devices.Count == 0)
                        throw new Exception("No devices for " + familyName);

                    string StartupFile = Directory.GetFiles(Path.Combine(DirDevices, familyName, @"Source"), "startup_*.c")[0].Replace(bspBuilder.Directories.InputDir, @"$$BSPGEN:INPUT_DIR$$");

                    var copyJob = new CopyJob()
                    {
                        FilesToCopy = "-*startup_*;*.h;*.c",
                        TargetFolder = "Devices",
                        ProjectInclusionMask = "*.c",
                        AutoIncludeMask = "*.h",
                        SourceFolder = DirDevices + "\\" + familyName
                    };

                    var fam = new MCUFamilyBuilder(bspBuilder, new FamilyDefinition()
                    {
                        Name = familyName,
                        FamilySubdirectory = familyName,
                        PrimaryHeaderDir = "$$BSPGEN:INPUT_DIR$$",
                        StartupFileDir = StartupFile,
                        CoreFramework = new Framework() { CopyJobs = new[] { copyJob } },
                        Subfamilies = new MCUClassifier[] { }.ToArray()
                    });

                    fam.MCUs.AddRange(devices);
                    allFamilies.Add(fam);
                }

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

                Console.WriteLine("Processing families...");

                int cnt = 0;

                foreach (var fam in allFamilies)
                {
                    Console.WriteLine($"    {fam.Definition.Name} ({++cnt}/{allFamilies.Count})...");
                    var rejectedMCUs = fam.RemoveUnsupportedMCUs();
                    if (rejectedMCUs.Length != 0)
                    {
                        Console.WriteLine("Unsupported {0} MCUs:", fam.Definition.Name);
                        foreach (var mcu in rejectedMCUs)
                            Console.WriteLine("\t{0}", mcu.Name);
                    }


                    fam.AttachStartupFiles(new[] { ParseStartupFiles(fam.Definition.StartupFileDir, fam) });

                    var famObj = fam.GenerateFamilyObject(true, true);

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
                    FileConditions = bspBuilder.MatchedFileConditions.Values.ToArray(),
                    PackageVersion = innerVersion,
                };

                File.WriteAllLines(Path.Combine(bspBuilder.Directories.OutputDir, "SDKVersions.txt"), new[]
                {
                    $"Gecko SDK version:      {outerVersion}",
                    $"Git tag:                https://github.com/SiliconLabs/gecko_sdk/releases/tag/v{outerVersion}",
                    $"32-Bit MCU SDK version: {innerVersion}"
                });

                Console.WriteLine("Saving BSP...");
                bspBuilder.Save(bsp, true);
            }
        }
    }
}
