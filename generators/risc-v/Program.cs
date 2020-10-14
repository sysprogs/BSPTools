using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LinkerScriptGenerator;
using BSPEngine;
using System.IO;
using System.Text.RegularExpressions;

namespace risc_v
{
    class RISCVBSPBuilder : BSPBuilder
    {
        public RISCVBSPBuilder(BSPDirectories dirs)
            : base(dirs)
        {
        }

        public override void GetMemoryBases(out uint flashBase, out uint ramBase)
        {
            throw new NotImplementedException();
        }

        public override MemoryLayoutAndSubstitutionRules GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
        {
            throw new NotImplementedException();
        }

        public override bool OnFilePathTooLong(string pathInsidePackage)
        {
            return true;
        }

        public string WSLPathToBSPPath(string path)
        {
            if (!path.StartsWith("/mnt/"))
                throw new Exception("Unexpected LXSS path: " + path);

            var absPath = path[5] + ":" + path.Substring(6).Replace('/', '\\');
            var sourceDir = Path.GetFullPath(Directories.InputDir);

            if (!absPath.StartsWith(sourceDir, StringComparison.InvariantCultureIgnoreCase))
                throw new Exception($"Path is not inside {sourceDir}: {path}");

            return "$$SYS:BSP_ROOT$$/" + absPath.Substring(sourceDir.Length).TrimStart('\\').Replace('\\', '/');
        }
    }


    class Program
    {
        class MCUPredicateImpl
        {
            private MCU _Mcu;

            public MCUPredicateImpl(MCU mcu)
            {
                _Mcu = mcu;
            }

            internal bool Match(MCUBuilder builder) => builder.Name == _Mcu.ID;
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: risc-v.exe <freedom-e-sdk directory with build logs>");

            const string TargetVariable = "com.sysprogs.riscv.target";
            const string LinkerScriptVariant = "com.sysprogs.riscv.linkerscript";
            string linkerScriptTemplate = $"$$SYS:BSP_ROOT$$/bsp/$${TargetVariable}$$/metal.$${LinkerScriptVariant}$$.lds";
            const string FamilyName = "SIFIVE";

            using (var bspBuilder = new RISCVBSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules", @"..\..\logs")))
            {
                BoardSupportPackage bsp = new BoardSupportPackage
                {
                    PackageID = "com.sysprogs.arm.riscv.sifive",
                    PackageDescription = "SiFive Freedom E Devices",
                    GNUTargetID = "riscv64-unknown-elf",
                    RequiredToolchainID = "com.visualgdb.risc-v",
                    GeneratedMakFileName = "sifive.mak",
                    PackageVersion = "1.0",
                    MinimumEngineVersion = "5.4",

                    MCUFamilies = new[]
                    {
                        new MCUFamily
                        {
                            ID = FamilyName,
                            CompilationFlags = new ToolFlags
                            {
                                IncludeDirectories = new[]{$"$$SYS:BSP_ROOT$$/bsp/$${TargetVariable}$$/install/include" },
                                LinkerScript = linkerScriptTemplate,
                                AdditionalLibraries = new[]{"c", "gcc", "m"},
                            },

                            ConfigurableProperties = new PropertyList
                            {
                                PropertyGroups = new List<PropertyGroup>
                                {
                                    new PropertyGroup
                                    {
                                        Properties = new List<PropertyEntry>
                                        {
                                            new PropertyEntry.Enumerated
                                            {
                                                UniqueID = LinkerScriptVariant,
                                                Name = "Default Linker Script",
                                                SuggestionList = new []{ "default", "freertos","ramrodata", "scratchpad"}.Select(s => new PropertyEntry.Enumerated.Suggestion{InternalValue = s }).ToArray(),
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                };

                List<MCU> mcus = new List<MCU>();
                var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));
                List<MCUDefinitionWithPredicate> registers = new List<MCUDefinitionWithPredicate>();

                foreach (var bspDir in Directory.GetDirectories(Path.Combine(bspBuilder.Directories.InputDir, "bsp")))
                {
                    var target = Path.GetFileName(bspDir);
                    var logFile = Path.Combine(bspBuilder.Directories.InputDir, target + ".log");
                    if (!File.Exists(logFile))
                        throw new Exception($"Missing {logFile}. Please run _buildall.sh in the SDK directory using WSL.");

                    var parsedLog = BuildLogFileParser.ParseRISCVBuildLog(logFile);
                    if (parsedLog.LinkerScript == null)
                        throw new Exception("Unknown linker script");

                    var script = bspBuilder.WSLPathToBSPPath(parsedLog.LinkerScript).Replace('/', '\\');
                    if (StringComparer.InvariantCultureIgnoreCase.Compare(script, linkerScriptTemplate.Replace($"$${TargetVariable}$$", target).Replace($"$${LinkerScriptVariant}$$", "default").Replace('/', '\\')) != 0)
                        throw new Exception("Unexpected linker script: " + script);

                    var memories = LinkerScriptTools.ScanLinkerScriptForMemories(script.Replace("$$SYS:BSP_ROOT$$", bspBuilder.Directories.InputDir));

                    var mcu = new MCU
                    {
                        ID = target.ToUpper(),
                        UserFriendlyName = target.ToUpper(),
                        FamilyID = FamilyName,

                        MemoryMap = new AdvancedMemoryMap
                        {
                            Memories = memories,
                        },

                        CompilationFlags = new ToolFlags
                        {
                            IncludeDirectories = parsedLog.allIncludes.Where(inc => inc != ".").Select(bspBuilder.WSLPathToBSPPath).ToArray(),
                            PreprocessorMacros = parsedLog.allDefines.Where(kv => !kv.Key.StartsWith("PACKAGE")).Select(kv => $"{kv.Key}={kv.Value}").ToArray(),
                            COMMONFLAGS = string.Join(" ", parsedLog.allFlags),
                            LDFLAGS = string.Join(" ", parsedLog.AllLDFlags),
                        },
                        AdditionalSystemVars = new[]
                        {
                            new SysVarEntry
                            {
                                Key = TargetVariable,
                                Value = target,
                            }
                        },

                        RAMSize = (int)(memories.FirstOrDefault(m => m.Name == "ram")?.Size ?? 0),
                        FLASHSize = (int)(memories.FirstOrDefault(m => m.Name == "rom")?.Size ?? 0),
                        MCUDefinitionFile = $"DeviceDefinitions/{target.ToUpper()}.xml",
                    };

                    if (mcu.RAMSize < 0)
                        mcu.RAMSize = 0;

                    var parsedSVD = SVDParser.ParseSVDFile(Path.Combine(bspDir, "design.svd"), target.ToUpper());
                    parsedSVD.MatchPredicate = new MCUPredicateImpl(mcu).Match;
                    registers.Add(parsedSVD);

                    commonPseudofamily.MCUs.Add(new MCUBuilder { Name = mcu.ID });

                    mcus.Add(mcu);
                }

                commonPseudofamily.AttachPeripheralRegisters(registers);
                bsp.SupportedMCUs = mcus.ToArray();


                List<string> projectFiles = new List<string>();
                PropertyList unused = null;

                if (commonPseudofamily.Definition.CoreFramework != null)
                    foreach (var job in commonPseudofamily.Definition.CoreFramework.CopyJobs)
                        job.CopyAndBuildFlags(bspBuilder, projectFiles, null, ref unused, null);

                bsp.Frameworks = commonPseudofamily.GenerateFrameworkDefinitions().ToArray();

                var samples = commonPseudofamily.CopySamples(bsp.Frameworks).ToArray();
                bsp.Examples = samples.Select(s => s.RelativePath).ToArray();


                var mainFamily = bsp.MCUFamilies.First();

                if (mainFamily.AdditionalSourceFiles != null || mainFamily.AdditionalHeaderFiles != null || bsp.FileConditions != null)
                    throw new Exception("TODO: merge lists");

                mainFamily.AdditionalSourceFiles = projectFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).ToArray();
                mainFamily.AdditionalHeaderFiles = projectFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).ToArray();
                bsp.FileConditions = bspBuilder.MatchedFileConditions.Values.ToArray();

                XmlTools.SaveObject(bsp, Path.Combine(bspBuilder.BSPRoot, "BSP.XML"));
            }
        }
    }
}
