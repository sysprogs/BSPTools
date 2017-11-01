using BSPEngine;
using BSPGenerationTools;
using Microsoft.Win32;
using StandaloneBSPValidator;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace mbed
{
    public class TestInfo
    {
        public TestInfo(string Filename, int Passed, int Failed)
        {
            this.Filename = Filename;
            this.Passed = Passed;
            this.Failed = Failed;
        }
        public string Filename { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
    }

    class IntelHexParser
    {
        struct IntelHexRecord
        {
            public ushort Address;
            public byte RecordType;
            public byte[] Data;

            public static ushort SwapBytes(ushort x)
            {
                return (ushort)((ushort)((x & 0xff) << 8) | ((x >> 8) & 0xff));
            }

            internal static IntelHexRecord Parse(string line)
            {
                line = line.Trim();
                if (!line.StartsWith(":"))
                    throw new InvalidOperationException("Invalid Intel HEX line: " + line);

                byte[] parsedBytes = new byte[(line.Length - 1) / 2];
                for (int i = 0; i < parsedBytes.Length; i++)
                    parsedBytes[i] = byte.Parse(line.Substring(1 + i * 2, 2), System.Globalization.NumberStyles.HexNumber);

                byte byteCount = parsedBytes[0];

                //Warning: we do not verify the record size or the checksum!
                return new IntelHexRecord
                {
                    Address = SwapBytes(BitConverter.ToUInt16(parsedBytes, 1)),
                    RecordType = parsedBytes[3],
                    Data = parsedBytes.Skip(4).Take(byteCount).ToArray()
                };
            }
        }

        public static uint GetLoadAddress(string ihexFile)
        {
            using (var fs = File.OpenText(ihexFile))
            {
                var line0 = IntelHexRecord.Parse(fs.ReadLine());
                var line1 = IntelHexRecord.Parse(fs.ReadLine());
                uint segmentBase;
                if (line0.RecordType == 2)
                    segmentBase = IntelHexRecord.SwapBytes(BitConverter.ToUInt16(line0.Data, 0)) * 16U;
                else if (line0.RecordType == 4)
                    segmentBase = (uint)IntelHexRecord.SwapBytes(BitConverter.ToUInt16(line0.Data, 0)) << 16;
                else
                    throw new Exception($"{ihexFile} does not start with a record of type 2");

                return segmentBase + line1.Address;
            }
        }

        public struct ParsedIntelHexFile
        {
            public uint LoadAddress;
            public byte[] Data;
        }

        public static ParsedIntelHexFile Parse(string hexFile)
        {
            uint segmentBase = 0;
            List<byte> data = new List<byte>();
            uint? start = null;

            foreach (var line in File.ReadAllLines(hexFile))
            {
                var parsedLine = IntelHexRecord.Parse(line);

                if (parsedLine.RecordType == 2)
                    segmentBase = IntelHexRecord.SwapBytes(BitConverter.ToUInt16(parsedLine.Data, 0)) * 16U;
                else if (parsedLine.RecordType == 4)
                    segmentBase = (uint)IntelHexRecord.SwapBytes(BitConverter.ToUInt16(parsedLine.Data, 0)) << 16;
                else if (parsedLine.RecordType == 0)
                {
                    uint addr = parsedLine.Address + segmentBase;
                    if (!start.HasValue)
                        start = addr;

                    if (addr != (start.Value + data.Count))
                    {
                        int padding = (int)(addr - (start.Value + data.Count));
                        if (padding < 0 || padding > 4096)
                            throw new Exception("Unexpected gap in " + hexFile);
                        for (int i = 0; i < padding; i++)
                            data.Add(0);
                    }
                    data.AddRange(parsedLine.Data);

                }
                else if (parsedLine.RecordType == 1)
                    break;
                else
                    throw new Exception($"Unexpected record type {parsedLine.RecordType} in {hexFile}");
            }

            return new ParsedIntelHexFile { LoadAddress = start.Value, Data = data.ToArray() };
        }
    }

    class Program
    {
        static _Ty[] Intersect<_Ty>(IEnumerable<IEnumerable<_Ty>> allInputs)
        {
            _Ty[] result = null;

            foreach (var inp in allInputs)
            {
                if (result == null)
                    result = inp.ToArray();
                else
                    result = result.Intersect(inp).ToArray();
            }

            return result ?? new _Ty[0];
        }

        public static _Ty[] Union<_Ty>(IEnumerable<IEnumerable<_Ty>> allInputs, IEqualityComparer<_Ty> comparer = null)
        {
            _Ty[] result = null;

            foreach (var inp in allInputs)
            {
                if (result == null)
                    result = inp.ToArray();
                else
                    result = result.Union(inp, comparer).ToArray();
            }

            return result ?? new _Ty[0];
        }

        class ConditionalConfigAggregator
        {
            public Dictionary<string, ParsedTargetList.BuildConfiguration> AddedSettingsPerTargets = new Dictionary<string, ParsedTargetList.BuildConfiguration>();

            public ConditionalConfigAggregator(ParsedTargetList.DerivedConfiguration cfg)
            {
                if (cfg.Feature != null)
                {
                    ID = "com.sysprogs.arm.mbed.feature." + cfg.Feature;
                    Name = cfg.Feature + " Support";
                }
                else
                {
                    ID = "com.sysprogs.arm.mbed." + Path.GetFileName(cfg.Library);
                    Name = cfg.LibraryName ?? (Path.GetFileName(cfg.Library) + " Library");
                }
            }

            public readonly string ID, Name;
        }

        class PropertyComparerByID : IEqualityComparer<PropertyEntry>
        {
            public bool Equals(PropertyEntry x, PropertyEntry y)
            {
                return x.UniqueID == y.UniqueID;
            }

            public int GetHashCode(PropertyEntry obj)
            {
                return obj.UniqueID.GetHashCode();
            }
        }

        static void Main(string[] args)
        {
            var generator = new MbedBSPGenerator("5.6.3");

            string suffix = "r2";
            generator.UpdateGitAndRescanTargets();

            ParsedTargetList parsedTargets = XmlTools.LoadObject<ParsedTargetList>(Path.Combine(generator.outputDir, "mbed", "ParsedTargets.xml"));
            generator.PatchBuggyFiles();

            BoardSupportPackage bsp = new BoardSupportPackage
            {
                PackageID = "com.sysprogs.arm.mbed",
                PackageDescription = "ARM mbed",
                PackageVersion = generator.Version + suffix,
                GNUTargetID = "arm-eabi",
                GeneratedMakFileName = "mbed.mak",
                BSPSourceFolderName = "mbed Files"
            };

            MCUFamily commonFamily = new MCUFamily
            {
                ID = "MBED_CORE",
                AdditionalSourceFiles = generator.ConvertPaths(Intersect(parsedTargets.Targets.Select(t => t.BaseConfiguration.SourceFiles))),
                AdditionalHeaderFiles = generator.ConvertPaths(Intersect(parsedTargets.Targets.Select(t => t.BaseConfiguration.HeaderFiles))),
                SymbolsRequiredByLinkerScript = new[] { "__Vectors", "Stack_Size" },
                CompilationFlags = new ToolFlags
                {
                    IncludeDirectories = generator.ConvertPaths(Intersect(parsedTargets.Targets.Select(t => t.BaseConfiguration.IncludeDirectories))),
                    PreprocessorMacros = Intersect(parsedTargets.Targets.Select(t => t.BaseConfiguration.EffectivePreprocessorMacros))
                }
            };

            bsp.MCUFamilies = new[] { commonFamily };

            List<MCU> mcus = new List<MCU>();
            Dictionary<string, ConditionalConfigAggregator> libraryAndFeatureConfigs = new Dictionary<string, ConditionalConfigAggregator>();

            Console.WriteLine("Generating target definitions...");

            foreach (var target in parsedTargets.Targets)
            {
                if (string.IsNullOrEmpty(target.BaseConfiguration.LinkerScript))
                {
                    Console.WriteLine($"Skipping {target.ID}: no linker script defined");
                    continue;
                }

                var mcu = new MCU
                {
                    FamilyID = commonFamily.ID,
                    ID = target.ID,
                    AdditionalSourceFiles = generator.ConvertPaths(target.BaseConfiguration.SourceFiles),
                    AdditionalHeaderFiles = generator.ConvertPaths(target.BaseConfiguration.HeaderFiles),
                    CompilationFlags = new ToolFlags
                    {
                        IncludeDirectories = generator.ConvertPaths(target.BaseConfiguration.IncludeDirectories),
                        PreprocessorMacros = target.BaseConfiguration.EffectivePreprocessorMacros,
                        LinkerScript = generator.ConvertPaths(new[] { target.BaseConfiguration.LinkerScript })[0],
                        COMMONFLAGS = target.CFLAGS.Replace(';', ' '),
                    },
                    ConfigurableProperties = new PropertyList
                    {
                        PropertyGroups = new List<PropertyGroup>
                        {
                            new PropertyGroup
                            {
                                UniqueID = "com.sysprogs.mbed.",
                                Properties = target.BaseConfiguration.EffectiveConfigurableProperties.ToList()
                            }
                        }
                    }

                };

                generator.DetectAndApplyMemorySizes(mcu, target.BaseConfiguration.LinkerScript);

                if (mcu.CompilationFlags.COMMONFLAGS.Contains("-mfloat-abi"))
                {
                    string[] flags = mcu.CompilationFlags.COMMONFLAGS.Split(' ');
                    string defaultValue = flags.First(f => f.StartsWith("-mfloat-abi"));

                    var property = new PropertyEntry.Enumerated
                    {
                        Name = "Floating point support",
                        UniqueID = "floatmode",
                        SuggestionList = new PropertyEntry.Enumerated.Suggestion[]
                        {
                            new PropertyEntry.Enumerated.Suggestion{InternalValue = "-mfloat-abi=soft", UserFriendlyName = "Software"},
                            new PropertyEntry.Enumerated.Suggestion{InternalValue = "-mfloat-abi=hard", UserFriendlyName = "Hardware"},
                            new PropertyEntry.Enumerated.Suggestion{InternalValue = "-mfloat-abi=softfp", UserFriendlyName = "Hardware with Software interface"},
                            new PropertyEntry.Enumerated.Suggestion{InternalValue = "", UserFriendlyName = "Unspecified"},
                        },
                    };

                    property.DefaultEntryIndex = Enumerable.Range(0, property.SuggestionList.Length).First(i => property.SuggestionList[i].InternalValue == defaultValue);
                    flags[Array.IndexOf(flags, defaultValue)] = "$$" + mcu.ConfigurableProperties.PropertyGroups[0].UniqueID + property.UniqueID + "$$";
                    mcu.CompilationFlags.COMMONFLAGS = string.Join(" ", flags);
                    mcu.ConfigurableProperties.PropertyGroups[0].Properties.Add(property);
                }

                mcu.AdditionalSourceFiles = mcu.AdditionalSourceFiles.Except(commonFamily.AdditionalSourceFiles).ToArray();
                mcu.AdditionalHeaderFiles = mcu.AdditionalHeaderFiles.Except(commonFamily.AdditionalHeaderFiles).ToArray();
                mcu.CompilationFlags.IncludeDirectories = mcu.CompilationFlags.IncludeDirectories.Except(commonFamily.CompilationFlags.IncludeDirectories).ToArray();
                mcu.CompilationFlags.PreprocessorMacros = mcu.CompilationFlags.PreprocessorMacros.Except(commonFamily.CompilationFlags.PreprocessorMacros).ToArray();

                foreach (var cfg in target.DerivedConfigurations)
                {
                    cfg.MergeScatteredConfigurations();

                    ConditionalConfigAggregator agg;
                    if (!libraryAndFeatureConfigs.TryGetValue(cfg.CanonicalKey, out agg))
                        agg = libraryAndFeatureConfigs[cfg.CanonicalKey] = new ConditionalConfigAggregator(cfg);

                    agg.AddedSettingsPerTargets[target.ID] = cfg.Configuration.Subtract(target.BaseConfiguration, cfg.CanonicalKey, cfg.Library != null);
                }

                generator.ConvertSoftdevicesAndPatchTarget(mcu, target.BaseConfiguration.HexFiles);
                generator.CopyAndAttachRegisterDefinitions(mcu);
                mcus.Add(mcu);
            }

            bsp.SupportedMCUs = mcus.ToArray();
            List<FileCondition> fileConditions = new List<FileCondition>();
            List<ConditionalToolFlags> conditionalFlags = new List<ConditionalToolFlags>();
            List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
            Console.WriteLine("Merging library build settings...");

            foreach (var agg in libraryAndFeatureConfigs.Values)
            {
                EmbeddedFramework framework = new EmbeddedFramework
                {
                    ID = agg.ID,
                    UserFriendlyName = agg.Name,
                    AdditionalSourceFiles = generator.ConvertPaths(Union(agg.AddedSettingsPerTargets.Values.Select(t => t.SourceFiles))),
                    AdditionalHeaderFiles = generator.ConvertPaths(Union(agg.AddedSettingsPerTargets.Values.Select(t => t.HeaderFiles))),
                    AdditionalIncludeDirs = generator.ConvertPaths(Intersect(agg.AddedSettingsPerTargets.Values.Select(t => t.IncludeDirectories))),
                    AdditionalPreprocessorMacros = Intersect(agg.AddedSettingsPerTargets.Values.Select(t => t.EffectivePreprocessorMacros)),
                };

                var properties = Union(agg.AddedSettingsPerTargets.Values.Select(t => t.EffectiveConfigurableProperties), new PropertyComparerByID()).ToList();
                if (properties.Count > 0)
                    framework.ConfigurableProperties = new PropertyList
                    {
                        PropertyGroups = new List<PropertyGroup>
                        {
                            new PropertyGroup
                            {
                                UniqueID = "com.sysprogs.mbed.",
                                Properties =properties
                            }
                        }
                    };

                foreach (var file in framework.AdditionalSourceFiles.Concat(framework.AdditionalHeaderFiles))
                {
                    var targetsWhereIncluded = agg.AddedSettingsPerTargets
                        .Where(v => generator.ConvertPaths(v.Value.SourceFiles.Concat(v.Value.HeaderFiles)).Contains(file))
                        .Select(kv => kv.Key)
                        .ToArray();

                    if (targetsWhereIncluded.Length == agg.AddedSettingsPerTargets.Count)
                        continue;   //The file is included on all targets

                    fileConditions.Add(new FileCondition { FilePath = file, ConditionToInclude = new Condition.MatchesRegex { Expression = "$$SYS:MCU_ID$$", Regex = "^(" + string.Join("|", targetsWhereIncluded) + ")$" } });
                }


                foreach (var kv in agg.AddedSettingsPerTargets)
                {
                    var extraIncludeDirs = generator.ConvertPaths(kv.Value.IncludeDirectories).Except(framework.AdditionalIncludeDirs).ToArray();
                    var extraPreprocessorMacros = kv.Value.EffectivePreprocessorMacros.Except(framework.AdditionalPreprocessorMacros).ToArray();
                    if (extraIncludeDirs.Length == 0 && extraPreprocessorMacros.Length == 0)
                        continue;

                    ToolFlags flags = new ToolFlags();
                    if (extraIncludeDirs.Length > 0)
                        flags.IncludeDirectories = extraIncludeDirs;
                    if (extraPreprocessorMacros.Length > 0)
                        flags.PreprocessorMacros = extraPreprocessorMacros;

                    conditionalFlags.Add(new ConditionalToolFlags
                    {
                        Flags = flags,
                        FlagCondition = new Condition.And
                        {
                            Arguments = new Condition[]
                            {
                                new Condition.ReferencesFramework{FrameworkID = framework.ID},
                                new Condition.Equals{Expression = "$$SYS:MCU_ID$$", ExpectedValue = kv.Key}
                            }
                        }
                    });
                }

                frameworks.Add(framework);
            }

            bsp.FileConditions = fileConditions.ToArray();
            bsp.ConditionalFlags = conditionalFlags.ToArray();
            bsp.Frameworks = frameworks.ToArray();
            bsp.Examples = generator.DetectSampleDirs();

            generator.ProduceBSPArchive(bsp);

            bool performTests = true;
            if (performTests)
                RunTests(generator);
        }

        private static void RunTests(MbedBSPGenerator generator)
        {
            var testFiles = new TestInfo[] {
                new TestInfo("test_ledblink.xml", 0, 0),
                new TestInfo("test_usbcd.xml", 0, 0),
                new TestInfo("test_ledblink_rtos.xml", 0, 0),
            };

            foreach (var test in testFiles)
            {
                Console.WriteLine($"Testing {test.Filename}...");
                var job = XmlTools.LoadObject<TestJob>(Path.Combine(generator.dataDir, test.Filename));
                if (job.ToolchainPath.StartsWith("["))
                {
                    job.ToolchainPath = (string)Registry.CurrentUser.OpenSubKey(@"Software\Sysprogs\GNUToolchains").GetValue(job.ToolchainPath.Trim('[', ']'));
                    if (job.ToolchainPath == null)
                        throw new Exception("Cannot locate toolchain path from registry");
                }
                var toolchain = LoadedToolchain.Load(new ToolchainSource.Other(Environment.ExpandEnvironmentVariables(job.ToolchainPath)));
                var lbsp = LoadedBSP.Load(new BSPEngine.BSPSummary(Environment.ExpandEnvironmentVariables(Path.Combine(generator.outputDir, "mbed"))), toolchain);

                var r = StandaloneBSPValidator.Program.TestBSP(job, lbsp, Path.Combine(generator.outputDir, "TestResults"));
                test.Passed = r.Passed;
                test.Failed = r.Failed;
            }

            foreach (var test in testFiles)
            {
                Console.WriteLine("Results for the test: " + test.Filename);
                Console.WriteLine("Passed: " + test.Passed.ToString());
                Console.WriteLine("Failed: " + test.Failed.ToString());
                Console.WriteLine();
            }
        }
    }
}
