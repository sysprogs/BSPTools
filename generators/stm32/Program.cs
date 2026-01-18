/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using System;
using System.Collections.Generic;
using System.Text;
using BSPEngine;
using System.IO;
using LinkerScriptGenerator;
using System.Xml.Serialization;
using BSPGenerationTools;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using BSPGenerationTools.Parsing;
using Microsoft.Win32;
using stm32_bsp_generator.Rulesets;
using System.Reflection;
using System.Net.Mime;

namespace stm32_bsp_generator
{
    class Program
    {
        public class STM32BSPBuilder : BSPBuilder
        {
            public readonly STM32SDKCollection SDKList;

            const int NO_DATA = -1;

            public override string GetMCUTypeMacro(MCUBuilder mcu)
            {
                var macro = base.GetMCUTypeMacro(mcu);
                return macro.Replace('-', '_');
            }


            //Different STM32 device subfamilies support different subsets of USB device classes, so we have to dynamically remove the unsupported ones.
            public override void PatchSmartFileConditions(ref string[] smartFileConditions, string expandedSourceFolder, string subdir, CopyJob copyJob)
            {
                if (copyJob.SourceFolder.EndsWith("STM32_USB_Device_Library"))
                {
                    for (int i = 0; i < smartFileConditions.Length; i++)
                    {
                        var condList = smartFileConditions[i].Split('\n').Select(s => s.Trim()).ToList();
                        for (int j = 0; j < condList.Count; j++)
                        {
                            int idx = condList[j].IndexOf("=>");
                            if (idx < 0)
                                continue;
                            string mask = condList[j].Substring(0, idx);
                            idx = mask.LastIndexOf('\\');
                            if (idx == -1)
                                continue;
                            string condSubdir = mask.Substring(0, idx);
                            if (!condSubdir.StartsWith("Class\\", StringComparison.InvariantCultureIgnoreCase))
                                continue;

                            if (!Directory.Exists(Path.Combine(expandedSourceFolder, condSubdir)))
                                condList.RemoveAt(j--);
                        }

                        smartFileConditions[i] = string.Join("\r\n", condList);
                    }
                }
            }

            static string DetectThreadXVersion(string sdkDir)
            {
                var fn = Path.Combine(sdkDir, @"Middlewares\ST\threadx\st_readme.txt");
                if (!File.Exists(fn))
                    return null;

                Regex rgVer = new Regex("### V([0-9\\.]+) \\([0-9\\-]+\\) ###");

                foreach (var line in File.ReadAllLines(fn))
                {
                    var m = rgVer.Match(line);
                    if (m.Success)
                        return m.Groups[1].Value;
                }

                return null;
            }

            public struct ImportedPackageVersions
            {
                public string ThreadX;
                public string BaseDir;
            }

            Dictionary<string, ImportedPackageVersions> _ImportedPackageVersions = new Dictionary<string, ImportedPackageVersions>(StringComparer.OrdinalIgnoreCase);

            public STM32BSPBuilder(BSPDirectories dirs, string cubeDir, BSPReportWriter commonReportWriter)
                : base(dirs, commonReportWriter: commonReportWriter)
            {
                STM32CubeDir = cubeDir;
                ShortName = "STM32";

                SDKList = XmlTools.LoadObject<STM32SDKCollection>(Path.Combine(dirs.InputDir, SDKFetcher.SDKListFileName));

                foreach (var sdk in SDKList.SDKs)
                {
                    var dir = Path.Combine(dirs.InputDir, sdk.FolderName);
                    if (Directory.Exists(dir))
                    {
                        var familyDir = Directory.GetDirectories(dir, "STM32Cube_FW_*").First();
                        _ImportedPackageVersions[sdk.Family] = new ImportedPackageVersions
                        {
                            BaseDir = familyDir,
                            ThreadX = DetectThreadXVersion(familyDir),
                        };

                        SystemVars[$"STM32:{sdk.Family.ToUpper()}_DIR"] = familyDir;
                    }
                }
            }

            public readonly string STM32CubeDir;

            public override MemoryLayoutAndSubstitutionRules GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                if (mcu is DeviceListProviders.CubeProvider.STM32MCUBuilder stMCU)
                {
                    return stMCU.ToMemoryLayout(family.BSP.Report);
                }

                throw new Exception($"{mcu.Name} is not provided by the STM32CubeMX-based MCU locator. Please ensure we get the actual memory map for this device, as guessing it from totals often yields wrong results.");


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


                return new MemoryLayoutAndSubstitutionRules(layout);
            }


            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                flashBase = FLASHBase;
                ramBase = SRAMBase;
            }

            /*
             *  As of February 2019, some of the STM32 software packages provide legacy versions of the regular HAL source files (e.g. stm32f1xx_hal_can.c) that are mutually exclusive
             *  with the regular versions of the same files. Instead of hardcoding the specific files for specific families, we detect it programmatically and generate the necessary
             *  rules on-the-fly in this function.
             */
            public void InsertLegacyHALRulesIfNecessary(FamilyDefinition fam, ReverseFileConditionBuilder reverseFileConditions)
            {
                var halFramework = fam.AdditionalFrameworks.FirstOrDefault(f => f.ClassID == "com.sysprogs.arm.stm32.hal") ?? throw new Exception(fam.Name + " defines no HAL framework");
                var primaryJob = halFramework.CopyJobs.First();
                string srcDir = primaryJob.SourceFolder + @"\Src";
                ExpandVariables(ref srcDir);
                if (!Directory.Exists(srcDir))
                    throw new Exception($"The first job for the HAL framework for {fam.Name} does not correspond to the normal source collection");

                var legacyDir = srcDir + @"\Legacy";
                if (Directory.Exists(legacyDir))
                {
                    var fileNames = Directory.GetFiles(legacyDir).Select(Path.GetFileName).ToArray();
                    string settingName = "com.sysprogs.stm32.legacy_hal_src";

                    List<string> moduleNames = new List<string>();

                    foreach (var file in fileNames)
                    {
                        if (!File.Exists(Path.Combine(legacyDir, file)))
                            throw new Exception($"{Path.Combine(legacyDir, file)} does not have a corresponding non-legacy file");

                        moduleNames.Add(Path.GetFileNameWithoutExtension(file.Substring(file.LastIndexOf('_') + 1)).ToUpper());
                        primaryJob.SimpleFileConditions = (primaryJob.SimpleFileConditions ?? new string[0]).Concat(new[]
                        {
                            $@"Src\\Legacy\\{file}: $${settingName}$$ == 1",
                            $@"Src\\{file}: $${settingName}$$ != 1",
                        }).ToArray();
                    }

                    if (halFramework.ConfigurableProperties == null)
                        halFramework.ConfigurableProperties = new PropertyList { PropertyGroups = new List<PropertyGroup> { new PropertyGroup() } };

                    halFramework.ConfigurableProperties.PropertyGroups[0].Properties.Add(new PropertyEntry.Enumerated
                    {
                        UniqueID = settingName,
                        Name = "HAL Driver Sources for " + string.Join(" ,", moduleNames),
                        SuggestionList = new PropertyEntry.Enumerated.Suggestion[]
                        {
                            new PropertyEntry.Enumerated.Suggestion{InternalValue = "", UserFriendlyName = "Default"},
                            new PropertyEntry.Enumerated.Suggestion{InternalValue = "1", UserFriendlyName = "Legacy"}
                        }
                    });
                }

                Regex rgLegacyDefineCheck = new Regex(@"#if( defined|def)[ \(]+(USE_LEGACY|USE_HAL_LEGACY)");
                string legacyDefineName = null;
                foreach (var line in File.ReadAllLines(Directory.GetFiles(srcDir + @"\..\Inc", "stm32*hal_def.h")[0]))
                {
                    var m = rgLegacyDefineCheck.Match(line);
                    if (m.Success)
                    {
                        legacyDefineName = m.Groups[2].Value;
                    }
                }

                if (legacyDefineName != null)
                {
                    if (halFramework.ConfigurableProperties == null)
                        halFramework.ConfigurableProperties = new PropertyList { PropertyGroups = new List<PropertyGroup> { new PropertyGroup() } };

                    halFramework.ConfigurableProperties.PropertyGroups[0].Properties.Add(new PropertyEntry.Boolean
                    {
                        UniqueID = "com.sysprogs.bspoptions.stm32.hal_legacy",
                        Name = "Support legacy HAL API",
                        ValueForTrue = legacyDefineName,
                        DefaultValue = true,
                    });

                    halFramework.CopyJobs[0].PreprocessorMacros += ";$$com.sysprogs.bspoptions.stm32.hal_legacy$$";
                    halFramework.CopyJobs[0].PreprocessorMacros = halFramework.CopyJobs[0].PreprocessorMacros.Trim(';');

                    reverseFileConditions?.GetHandleForFramework(halFramework)?.AttachMinimalConfigurationValue("com.sysprogs.bspoptions.stm32.hal_legacy", "");
                }
            }

            public ImportedPackageVersions LookupImportedPackageVersions(STM32SDKCollection.SDK sdk)
            {
                return _ImportedPackageVersions[sdk.Family];
            }
        }

        static string GetSubfamilyDefine(STM32Ruleset ruleset, MCUBuilder builder)
        {
            if (ruleset == STM32Ruleset.BlueNRG_LP)
                return "BLUENRG_LP";
            return (builder as DeviceListProviders.CubeProvider.STM32MCUBuilder)?.MCU.Details.Define ?? throw new Exception("Unknown primary macro for " + builder.Name);
        }

        class MCUMatcher
        {
            private string _Subfamily;
            private bool _IsOnlyFile;

            public MCUMatcher(string subfamily, bool v)
            {
                _Subfamily = subfamily;
                _IsOnlyFile = v;
            }

            internal bool Match(MCUBuilder mcu)
            {
                if (_IsOnlyFile)
                    return true;

                var startupFiles = (mcu as DeviceListProviders.CubeProvider.STM32MCUBuilder)?.MCU.Details.StartupFiles ?? throw new Exception("Missing startup file reference");

                if (startupFiles.Length == 1)
                    return StringComparer.InvariantCultureIgnoreCase.Compare(startupFiles[0].NameOnly, _Subfamily) == 0;
                else
                    throw new NotImplementedException("TODO: match per-core startup files");
            }
        }

        static IEnumerable<StartupFileGenerator.InterruptVectorTable> ParseStartupFiles(string dir, MCUFamilyBuilder fam, STM32Ruleset ruleset)
        {
            var allFiles = Directory.GetFiles(dir);

            foreach (var fn in allFiles)
            {
                string name = Path.GetFileName(fn);
                string expandedFile = fn + ".expanded";

                yield return new StartupFileGenerator.InterruptVectorTable
                {
                    FileName = Path.ChangeExtension(Path.GetFileName(fn), ".c"),
                    MatchPredicate = new MCUMatcher(name, allFiles.Length == 1).Match,
                    IsFallbackFile = (ruleset == STM32Ruleset.STM32MP1 && fn.EndsWith("startup_stm32mp15xx.s")),
                    Vectors = StartupFileGenerator.ParseInterruptVectors(File.Exists(expandedFile) ? expandedFile : fn,
                        tableStart: "g_pfnVectors:",
                        tableEnd: @"/\*{10,999}|^[^/\*]+\*/$",
                        vectorLineA: @"^[ \t]+\.word[ \t]+([^ \t/]+)",
                        vectorLineB: null,
                        ignoredLine: @"^[ \t]+/\*|[ \t]+stm32.*|[ \t]+STM32.*|// External Interrupts|^[ \t]*.size[ \t]+g_pfnVectors",
                        macroDef: ".equ[ \t]+([^ \t]+),[ \t]+(0x[0-9a-fA-F]+)",
                        nameGroup: 1,
                        commentGroup: 2)
                };
            }
        }

        private static IEnumerable<MCUDefinitionWithPredicate> ParsePeripheralRegisters(string dir, MCUFamilyBuilder fam, string specificDevice, ParseReportWriter writer, STM32Ruleset ruleset)
        {
            List<MCUDefinitionWithPredicate> result = new List<MCUDefinitionWithPredicate>();

            string addedSubfamilySuffix = "";
            var match = Regex.Match(fam.Definition.Name, @"_m(\d+)$", RegexOptions.IgnoreCase);
            if (match.Success)
                addedSubfamilySuffix = $"_m{match.Groups[1].Value}";

            Console.Write("Parsing {0} registers using the new parsing logic...", fam.Definition.Name);
            foreach (var fn in Directory.GetFiles(dir, "*.h"))
            {
                string subfamily = Path.GetFileNameWithoutExtension(fn);
                var subfamilyMatch = Regex.Match(subfamily, @"(stm32[^_]+)(|_.*)", RegexOptions.IgnoreCase);
                if (!subfamilyMatch.Success)
                    continue;

                string subfamilyForMatching = subfamilyMatch.Groups[1].Value;

                if (specificDevice != null && subfamily != specificDevice)
                    continue;

                var r = new MCUDefinitionWithPredicate
                {
                    MCUName = subfamily + addedSubfamilySuffix,
                    RegisterSets = PeripheralRegisterGenerator2.GeneratePeripheralRegisterDefinitionsFromHeaderFile(fn, fam.MCUs[0].Core, writer),
                    MatchPredicate = m => StringComparer.InvariantCultureIgnoreCase.Compare(GetSubfamilyDefine(ruleset, m), subfamilyForMatching) == 0,
                };

                result.Add(r);
            }

            Console.WriteLine("done");
            return result;
        }

        class SamplePrioritizer
        {
            List<KeyValuePair<Regex, int>> _Rules = new List<KeyValuePair<Regex, int>>();
            public SamplePrioritizer(string rulesFile)
            {
                int index = 1;
                if (File.Exists(rulesFile))
                    foreach (var line in File.ReadAllLines(rulesFile))
                        _Rules.Add(new KeyValuePair<Regex, int>(new Regex(line, RegexOptions.IgnoreCase), index++));
            }

            public int GetScore(string path)
            {
                string fn = Path.GetFileName(path);
                foreach (var rule in _Rules)
                    if (rule.Key.IsMatch(fn))
                        return rule.Value;
                return 1000;
            }

            public int Prioritize(string left, string right)
            {
                int sc1 = GetScore(left), sc2 = GetScore(right);
                if (sc1 != sc2)
                    return sc1 - sc2;
                return StringComparer.InvariantCultureIgnoreCase.Compare(left, right);
            }

        }

        class OuterSTM32BSPGenerator
        {
            private string[] _Args;
            private string _SDKRoot;
            private string _CubeRoot;
            private string _LogDir;
            private string _ExplicitSource;
            private string _RulesetName;
            private STM32Ruleset _Ruleset;
            private HashSet<string> _ExistingUnspecializedDevices;
            private string _UnspecDeviceFile;
            private readonly STM32Directories _STM32Directories;
            private readonly MCUBuilder[] _Devices;

            public OuterSTM32BSPGenerator(string[] args)
            {
                _Args = args;

                var regKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Sysprogs\BSPGenerators\STM32");
                _SDKRoot = regKey.GetValue("SDKRoot") as string ?? throw new Exception("Please specify STM32 SDK root via registry");
                _CubeRoot = regKey.GetValue("CubeMXRoot") as string ?? throw new Exception("Please specify STM32CubeMX location via registry");
                _RulesetName = args.FirstOrDefault(a => a.StartsWith("/rules:"))?.Substring(7) ?? STM32Ruleset.Classic.ToString();
                _STM32Directories = new STM32Directories(_CubeRoot, @"..\..\rules\" + _RulesetName);

                if (args.Contains("/fetch"))
                {
                    // This will load the latest SDK list from the STM32CubeMX directory and will fetch/unpack them to our SDKRoot directory.
                    // Before running this, ensure the STM32CubeMX has the up‑to‑date SDK definitions (using the 'check for update' function), as
                    // otherwise the BSP generator will fetch the old versions.

                    SDKFetcher.FetchLatestSDKs(_SDKRoot, _CubeRoot);
                }

                _ExplicitSource = args.FirstOrDefault(a => a.StartsWith("/source:"))?.Substring(8);

                _Ruleset = Enum.GetValues(typeof(STM32Ruleset))
                    .OfType<STM32Ruleset>()
                    .First(v => StringComparer.InvariantCultureIgnoreCase.Compare(v.ToString(), _RulesetName.Replace('-', '_')) == 0);

                _LogDir = @"..\..\Logs\" + _RulesetName;
                _ExistingUnspecializedDevices = new HashSet<string>();
                _UnspecDeviceFile = @"..\..\rules\UnspecializedDevices.txt";

                foreach (var fn in File.ReadAllLines(_UnspecDeviceFile))
                    _ExistingUnspecializedDevices.Add(fn);

                var provider = new DeviceListProviders.CubeProvider(_ExistingUnspecializedDevices);

                List<MCUBuilder> devices;
                if (_Ruleset == STM32Ruleset.BlueNRG_LP)
                    devices = new List<MCUBuilder> { new BlueNRGFamilyBuilder.BlueNRGMCUBuilder() };
                else
                {
                    devices = provider.LoadDeviceList(_STM32Directories);
                    var incompleteDevices = devices.Where(d => d.FlashSize == 0 && !d.Name.StartsWith("STM32MP") && !d.Name.StartsWith("STM32N6")).ToArray();
                    if (incompleteDevices.Length > 0)
                        throw new Exception($"{incompleteDevices.Length} devices have FLASH Size = 0 ");
                }

                _Devices = devices.ToArray();
            }

            void DoRun(string subsetName = null, BSPReportWriter existingReportWriter = null)
            {
                var dirSuffix = "";
                if (subsetName != null)
                    dirSuffix = @"\" + subsetName;

                var addedFrameworks = new HashSet<string>();

                using (var bspBuilder = new STM32BSPBuilder(new BSPDirectories(_SDKRoot, @"..\..\Output\" + _RulesetName + dirSuffix, _STM32Directories.RulesDir, @"..\..\Logs\" + _RulesetName + dirSuffix), _CubeRoot, existingReportWriter))
                using (var wr = new ParseReportWriter(Path.Combine(_LogDir, $"registers-{subsetName}.log")))
                {
                    if (_ExplicitSource != null)
                        bspBuilder.SystemVars["BSPGEN:INPUT_DIR"] = _ExplicitSource;

                    List<MCUFamilyBuilder> allFamilies = new List<MCUFamilyBuilder>();
                    string extraFrameworksFile = Path.Combine(bspBuilder.Directories.RulesDir, "FrameworkTemplates.xml");
                    if (!File.Exists(extraFrameworksFile) && File.Exists(Path.ChangeExtension(extraFrameworksFile, ".txt")))
                        extraFrameworksFile = Path.Combine(bspBuilder.Directories.RulesDir, File.ReadAllText(Path.ChangeExtension(extraFrameworksFile, ".txt")));

                    Dictionary<string, STM32SDKCollection.SDK> sdksByVariable = bspBuilder.SDKList.SDKs.ToDictionary(s => $"$$STM32:{s.Family}_DIR$$");
                    List<STM32SDKCollection.SDK> referencedSDKs = new List<STM32SDKCollection.SDK>();

                    string latestThreadX = "0.0";
                    var comparer = new SimpleVersionComparer();

                    string[] familyFiles;
                    if (subsetName != null)
                        familyFiles = Directory.GetFiles(bspBuilder.Directories.RulesDir + @"\families", "*.xml").Where(f => Path.GetFileNameWithoutExtension(f).StartsWith(subsetName)).ToArray();
                    else
                        familyFiles = Directory.GetFiles(bspBuilder.Directories.RulesDir + @"\families", "*.xml");

                    foreach (var fn in familyFiles)
                    {
                        var fam = XmlTools.LoadObject<FamilyDefinition>(fn);

                        if (File.Exists(extraFrameworksFile))
                        {
                            int idx = fam.PrimaryHeaderDir.IndexOf('\\');
                            string baseDir = fam.PrimaryHeaderDir.Substring(0, idx);
                            if (!baseDir.StartsWith("$$STM32:"))
                                baseDir = _ExplicitSource ?? throw new Exception("Invalid base directory. Please recheck the family definition.");

                            string baseFamName = fam.Name;
                            if (baseFamName.EndsWith("_M4"))
                                baseFamName = baseFamName.Substring(0, baseFamName.Length - 3);

                            var dict = new Dictionary<string, string>
                            {
                                { "STM32:FAMILY_EX"  , fam.Name },
                                { "STM32:FAMILY"     , baseFamName },
                                { "STM32:FAMILY_L"   , baseFamName.ToLower() },
                                { "STM32:FAMILY_DIR" , baseDir },
                            };

                            if (_Ruleset != STM32Ruleset.BlueNRG_LP)
                            {
                                var sdk = sdksByVariable[baseDir];
                                referencedSDKs.Add(sdk);

                                var versions = bspBuilder.LookupImportedPackageVersions(sdk);
                                if (comparer.Compare(latestThreadX, versions.ThreadX ?? "0.0") < 0)
                                {
                                    latestThreadX = versions.ThreadX;
                                    bspBuilder.SystemVars["STM32:LATEST_THREADX_SDK_DIR"] = versions.BaseDir;
                                }
                            }

                            if (!string.IsNullOrEmpty(fam.FamilySubdirectory))
                                dict["STM32:TARGET_FAMILY_DIR"] = "$$SYS:BSP_ROOT$$/" + fam.FamilySubdirectory;
                            else
                                dict["STM32:TARGET_FAMILY_DIR"] = "$$SYS:BSP_ROOT$$";

                            var extraFrameworkFamily = XmlTools.LoadObject<FamilyDefinition>(extraFrameworksFile);

                            //USB host/device libraries are not always compatible between different device families. Hence we need to ship separate per-family copies of those.
                            var expandedExtraFrameworks = extraFrameworkFamily.AdditionalFrameworks.Select(fw =>
                            {
                                fw.ID = VariableHelper.ExpandVariables(fw.ID, dict);
                                fw.Name = VariableHelper.ExpandVariables(fw.Name, dict);
                                fw.RequiredFrameworks = ExpandVariables(fw.RequiredFrameworks, dict);
                                fw.IncompatibleFrameworks = ExpandVariables(fw.IncompatibleFrameworks, dict);
                                foreach (var job in fw.CopyJobs)
                                {
                                    job.SourceFolder = VariableHelper.ExpandVariables(job.SourceFolder, dict);
                                    job.TargetFolder = VariableHelper.ExpandVariables(job.TargetFolder, dict);
                                    job.AdditionalIncludeDirs = VariableHelper.ExpandVariables(job.AdditionalIncludeDirs, dict);
                                }

                                if (fw.ConfigFiles != null)
                                {
                                    foreach (var file in fw.ConfigFiles)
                                    {
                                        file.Path = VariableHelper.ExpandVariables(file.Path, dict);
                                        file.FinalName = VariableHelper.ExpandVariables(file.FinalName, dict);
                                        file.TargetPathForInsertingIntoProject = VariableHelper.ExpandVariables(file.TargetPathForInsertingIntoProject, dict);
                                        file.TestableHeaderFiles = file.TestableHeaderFiles?.Select(f => VariableHelper.ExpandVariables(f, dict))?.ToArray();
                                    }
                                }

                                return fw;
                            });

                            //Furthermore, some families do not include a USB host peripheral and hence do not contain a USB Host library. We need to skip it automatically.
                            var extraFrameworksWithoutMissingFolders = expandedExtraFrameworks.Where(fw => fw.CopyJobs.Count(j =>
                            {
                                string expandedJobSourceDir = j.SourceFolder;
                                bspBuilder.ExpandVariables(ref expandedJobSourceDir);
                                if (!Directory.Exists(expandedJobSourceDir))
                                    return true;

                                return false;
                            }) == 0);

                            extraFrameworksWithoutMissingFolders = extraFrameworksWithoutMissingFolders.Where(fw => !addedFrameworks.Contains(fw.ID)).ToArray();

                            foreach (var fw in extraFrameworksWithoutMissingFolders)
                            {
                                addedFrameworks.Add(fw.ID);

                                foreach (var job in fw.CopyJobs)
                                {
                                    if (job.SmartPropertyGroup?.StartsWith("com.sysprogs.bspoptions.stm32.usb.") == true)
                                    {
                                        var physicalDir = bspBuilder.ExpandVariables(job.SourceFolder);
                                        job.SmartFileConditions = job.SmartFileConditions.Where(c => Directory.Exists(Path.Combine(physicalDir, c.Split('|')[1].TrimEnd('*', '\\', '.')))).ToArray();
                                        if (job.SmartFileConditions.Length < 6)
                                            throw new Exception("Too little USB class conditions after filtering");
                                    }
                                }
                            }

                            fam.AdditionalFrameworks = (fam.AdditionalFrameworks ?? new Framework[0]).Concat(extraFrameworksWithoutMissingFolders).ToArray();
                        }

                        if (_Ruleset != STM32Ruleset.BlueNRG_LP)
                            bspBuilder.InsertLegacyHALRulesIfNecessary(fam, bspBuilder.ReverseFileConditions);
                        switch (_Ruleset)
                        {
                            case STM32Ruleset.STM32WB:
                                allFamilies.Add(new STM32WBFamilyBuilder(bspBuilder, fam));
                                break;
                            case STM32Ruleset.STM32MP1:
                                allFamilies.Add(new STM32MP1FamilyBuilder(bspBuilder, fam));
                                break;
                            case STM32Ruleset.BlueNRG_LP:
                                allFamilies.Add(new BlueNRGFamilyBuilder(bspBuilder, fam));
                                break;
                            case STM32Ruleset.STM32WL:
                                allFamilies.Add(new STM32WLFamilyBuilder(bspBuilder, fam));
                                break;
                            case STM32Ruleset.STM32H7RS:
                                allFamilies.Add(new STM32H7RSFamilyBuilder(bspBuilder, fam));
                                break;
                            case STM32Ruleset.Classic:
                            default:
                                allFamilies.Add(new STM32ClassicFamilyBuilder(bspBuilder, fam));
                                break;
                        }
                    }

                    BSPGeneratorTools.AssignMCUsToFamilies(_Devices, allFamilies);

                    List<MCUFamily> familyDefinitions = new List<MCUFamily>();
                    List<MCU> mcuDefinitions = new List<MCU>();
                    List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
                    List<MCUFamilyBuilder.CopiedSample> exampleDirs = new List<MCUFamilyBuilder.CopiedSample>();

                    bool noPeripheralRegisters = _Args.Contains("/noperiph");
                    bool noAutoFixes = _Args.Contains("/nofixes");
                    string specificDeviceForDebuggingPeripheralRegisterGenerator = _Args.FirstOrDefault(a => a.StartsWith("/periph:"))?.Substring(8);

                    var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));
                    HashSet<string> familySpecificFrameworkIDs = new HashSet<string>();

                    List<ConditionalToolFlags> allConditionalToolFlags = new List<ConditionalToolFlags>();

                    foreach (var fam in allFamilies)
                    {
                        fam.RemoveUnsupportedMCUs();

                        fam.AttachStartupFiles(ParseStartupFiles(fam.Definition.StartupFileDir, fam, _Ruleset));
                        if (!noPeripheralRegisters)
                            fam.AttachPeripheralRegisters(ParsePeripheralRegisters(fam.Definition.PrimaryHeaderDir, fam, specificDeviceForDebuggingPeripheralRegisterGenerator, wr, _Ruleset),
                                throwIfNotFound: specificDeviceForDebuggingPeripheralRegisterGenerator == null);

                        familyDefinitions.Add(fam.GenerateFamilyObject(MCUFamilyBuilder.CoreSpecificFlags.All, true));
                        fam.GenerateLinkerScripts(false);

                        foreach (var mcu in fam.MCUs)
                        {
                            var builtMCU = mcu.GenerateDefinition(fam, bspBuilder, !noPeripheralRegisters && specificDeviceForDebuggingPeripheralRegisterGenerator == null);
                            if (builtMCU.ID == (mcu as DeviceListProviders.CubeProvider.STM32MCUBuilder).MCU.RPN)
                                _ExistingUnspecializedDevices.Add(builtMCU.ID);
                            mcuDefinitions.Add(builtMCU);
                        }

                        foreach (var fw in fam.GenerateFrameworkDefinitions())
                        {
                            familySpecificFrameworkIDs.Add(fw.ID);
                            frameworks.Add(fw);
                        }

                        foreach (var sample in fam.CopySamples())
                            exampleDirs.Add(sample);

                        if (fam.Definition.ConditionalFlags != null)
                            allConditionalToolFlags.AddRange(fam.Definition.ConditionalFlags);
                    }

                    foreach (var fw in commonPseudofamily.GenerateFrameworkDefinitions(familySpecificFrameworkIDs))
                    {
                        frameworks.Add(fw);

                        if (fw.ID == "com.sysprogs.arm.stm32.threadx")
                        {
                            const string secureModeOptionID = "secure_domain";
                            fw.ConfigurableProperties.PropertyGroups[0].Properties.Add(new PropertyEntry.Boolean { Name = "Run in secure domain (TrustZone CPUs)", ValueForTrue = "1", UniqueID = secureModeOptionID });

                            foreach (var f in fw.AdditionalSourceFiles)
                            {
                                if (f.Contains("thread_secure_stack.c"))
                                {
                                    var cond = new Condition.Equals { Expression = $"$${fw.ConfigurableProperties.PropertyGroups[0].UniqueID}{secureModeOptionID}$$", ExpectedValue = "1" };

                                    if (!bspBuilder.MatchedFileConditions.TryGetValue(f, out var condRec))
                                        bspBuilder.AddFileCondition(new FileCondition { FilePath = f, ConditionToInclude = cond });
                                    else
                                    {
                                        condRec.ConditionToInclude = new Condition.And
                                        {
                                            Arguments = new Condition[]
                                            {
                                                condRec.ConditionToInclude,
                                                cond,
                                            }
                                        };
                                    }
                                }
                            }
                        }
                    }

                    foreach (var sample in commonPseudofamily.CopySamples(null, allFamilies.Where(f => f.Definition.AdditionalSystemVars != null).SelectMany(f => f.Definition.AdditionalSystemVars)))
                        exampleDirs.Add(sample);

                    var prioritizer = new SamplePrioritizer(Path.Combine(bspBuilder.Directories.RulesDir, "SamplePriorities.txt"));
                    exampleDirs.Sort((a, b) => prioritizer.Prioritize(a.RelativePath, b.RelativePath));

                    var bsp = XmlTools.LoadObject<BoardSupportPackage>(Path.Combine(bspBuilder.Directories.RulesDir, "BSPTemplate.xml"));

                    frameworks.Sort((a, b) => StringComparer.InvariantCultureIgnoreCase.Compare(a.UserFriendlyName, b.UserFriendlyName));

                    bsp.MCUFamilies = familyDefinitions.ToArray();
                    bsp.SupportedMCUs = mcuDefinitions.ToArray();
                    bsp.Frameworks = frameworks.ToArray();
                    bsp.Examples = exampleDirs.Where(s => !s.IsTestProjectSample).Select(s => s.RelativePath).ToArray();
                    bsp.TestExamples = exampleDirs.Where(s => s.IsTestProjectSample).Select(s => s.RelativePath).ToArray();
                    bsp.FileConditions = bspBuilder.MatchedFileConditions.Values.ToArray();
                    bsp.InitializationCodeInsertionPoints = commonPseudofamily.Definition.InitializationCodeInsertionPoints;
                    bsp.ConditionalFlags = allConditionalToolFlags.ToArray();

                    bspBuilder.SDKList.SDKs = referencedSDKs.Distinct().ToArray();
                    if (_Ruleset == STM32Ruleset.STM32H7RS || subsetName != null)
                        bsp.PackageVersion = bspBuilder.SDKList.SDKs[0].Version;
                    else
                        bsp.PackageVersion = bspBuilder.SDKList.BSPVersion;

                    if (subsetName != null)
                    {
                        bsp.ReplacesBSP = bsp.PackageID;
                        bsp.PackageID += "." + subsetName.ToLower();
                        bsp.PackageDescription = bsp.PackageDescription.Replace("$$STM32:SUBSET$$", subsetName.ToUpper());
                    }

                    XmlTools.SaveObject(bspBuilder.SDKList, Path.Combine(bspBuilder.BSPRoot, "SDKVersions.xml"));

                    bspBuilder.ValidateBSP(bsp);

                    if (!noAutoFixes)
                        bspBuilder.ComputeAutofixHintsForConfigurationFiles(bsp);

                    bspBuilder.ReverseFileConditions.SaveIfConsistent(bspBuilder.Directories.OutputDir, bspBuilder.ExportRenamedFileTable(), _Ruleset == STM32Ruleset.STM32WB);

                    File.Copy(@"..\..\stm32_compat.h", Path.Combine(bspBuilder.BSPRoot, "stm32_compat.h"), true);
                    Console.WriteLine("Saving BSP...");
                    bspBuilder.Save(bsp, false);
                }
            }

            public void Run()
            {
                using (var reportWriter = new BSPReportWriter(_LogDir))
                {
                    if (_Ruleset == STM32Ruleset.Classic)
                    {
                        //Families like stm32h7 and stm32h7m4 should go into the same final SDK
                        var subsets = Directory.GetFiles(_STM32Directories.RulesDir + @"\families", "*.xml").Select(fn => Path.GetFileNameWithoutExtension(fn).Substring(0, 7)).Distinct().ToArray();

                        foreach (var subset in subsets)
                            DoRun(subset, reportWriter);
                    }
                    else
                        DoRun(null, reportWriter);


                    if (_Ruleset == STM32Ruleset.Classic)
                    {
                        foreach (var r in _Devices.Where(d => d.AssignedFamily == null))
                        {
                            if (r.Name.StartsWith("STM32MP1") || r.Name.StartsWith("STM32MP2") || r.Name.StartsWith("STM32GBK") || r.Name.StartsWith("STM32WB") || r.Name.StartsWith("STM32WL"))
                                continue;
                            if (r.Name.StartsWith("STM32H7R") || r.Name.StartsWith("STM32H7S"))
                                continue;   //Separate BSP
                            if (r.Name.StartsWith("STM32N6"))
                                continue;   //Separate BSP

                            reportWriter.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, $"Could not find the family for {r.Name.Substring(0, 7)} MCU(s)", r.Name, true);
                        }
                    }

                }

                File.WriteAllLines(_UnspecDeviceFile, _ExistingUnspecializedDevices.OrderBy(x => x).ToArray());
            }

        }

        //Usage: stm32.exe /rules:{Classic|STM32WB|STM32MP1|stm32h7rs} [/fetch] [/noperiph] [/nofixes]
        static void Main(string[] args)
        {
            new OuterSTM32BSPGenerator(args).Run();
        }

        private static string[] ExpandVariables(string[] strings, Dictionary<string, string> dict)
        {
            return strings?.Select(s => VariableHelper.ExpandVariables(s, dict))?.ToArray();
        }

        static void CompareMCULists(string MCUList1, string MCUList2)
        {
            List<string> list1 = new List<string>();
            foreach (var line in File.ReadAllLines(MCUList1))
            {
                string[] items = line.Split(',');
                if (!items[0].StartsWith("STM32"))
                    continue;
                if (items[0].StartsWith("STM32TS60"))
                    continue;

                string mcuName = items[0];
                list1.Add(mcuName);
            }

            List<string> list2 = new List<string>();
            foreach (var line in File.ReadAllLines(MCUList2))
            {
                string[] items = line.Split(',');
                if (!items[0].StartsWith("STM32"))
                    continue;
                if (items[0].StartsWith("STM32TS60"))
                    continue;

                string mcuName = items[0];
                list2.Add(mcuName);
            }

            Console.WriteLine("Items missing from second list:");
            foreach (var line in list1)
            {
                if (!list2.Contains(line))
                    Console.WriteLine(line);

            }

            Console.WriteLine("Items added to second list:");
            foreach (var line in list2)
            {
                if (!list1.Contains(line))
                    Console.WriteLine(line);
            }
        }

        static void MergeOldAndNewMCULists(string oldMCUList, string newMCUList, string mergedMCUList)
        {
            string[] oldheaders = null;
            string[] newheaders = null;

            Dictionary<string, string> mergedDict = new Dictionary<string, string>();

            // Add all the entries from the newer list to the dictionary
            foreach (var line in File.ReadAllLines(newMCUList, Encoding.UTF8))
            {
                if (newheaders == null)
                {
                    newheaders = line.Split(new char[] { ',' });
                    continue;
                }
                // Assume the first entry is the mcu name!
                int index = line.IndexOf(',');
                mergedDict.Add(line.Substring(0, index), line.Substring(index + 1));
            }

            // Add the entries that are in the old list but not in the new list to the dictionary
            foreach (var line in File.ReadAllLines(oldMCUList, Encoding.UTF8))
            {
                if (oldheaders == null)
                {
                    oldheaders = line.Split(new char[] { ',' });
                    continue;
                }
                // Assume the first entry is the mcu name!
                int index = line.IndexOf(',');
                if (!mergedDict.ContainsKey(line.Substring(0, index)))
                {
                    string fitted_line = FitOldLineToNewHeaderOrder(line, oldheaders, newheaders);
                    mergedDict.Add(fitted_line.Substring(0, index), fitted_line.Substring(index + 1));
                }
            }

            List<string> mergedList = new List<string>();
            foreach (var key in mergedDict.Keys)
            {
                mergedList.Add(key + "," + mergedDict[key]);
            }
            mergedList.Sort();
            mergedList.Insert(0, string.Join(",", newheaders));

            File.WriteAllLines(mergedMCUList, mergedList.ToArray(), Encoding.UTF8);
        }

        static string FitOldLineToNewHeaderOrder(string line, string[] oldHeaders, string[] newHeaders)
        {
            List<string> built_line = new List<string>();
            string[] split_line = line.Split(new char[] { ',' });

            foreach (var col in newHeaders)
            {
                int index = -1;
                for (int i = 0; i < oldHeaders.Length; i++)
                {
                    if (oldHeaders[i].ToLower() == col.ToLower())
                    {
                        index = i;
                        break;
                    }
                }

                if (index >= 0)
                    built_line.Add(split_line[index]);
                else
                    built_line.Add("");
            }

            return string.Join(",", built_line);
        }

        static Memory AddMemory(MemoryLayout layout, string name, MemoryType type, string[] data, int baseIndex, bool required)
        {
            if (data[baseIndex] == "")
            {
                if (required)
                    throw new Exception(name + " not defined!");
                return null;
            }

            layout.Memories.Add(new Memory
            {
                Name = name,
                Access = MemoryAccess.Undefined,
                Type = type,
                Start = ParseHex(data[baseIndex]),
                Size = uint.Parse(data[baseIndex + 1]) * 1024
            });

            return layout.Memories[layout.Memories.Count - 1];
        }

        public static uint ParseHex(string text)
        {
            text = text.Replace(" ", "");
            if (text.StartsWith("0x"))
                return uint.Parse(text.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier);
            else
                return uint.Parse(text);
        }

        static int ParseInt(string str)
        {
            if (str == "" || str == "-")
                return 0;
            return int.Parse(str);
        }

        const uint FLASHBase = 0x08000000, SRAMBase = 0x20000000;

        static void CopyDirectoryRecursive(string sourceDirectory, string destinationDirectory)
        {
            sourceDirectory = sourceDirectory.TrimEnd('/', '\\');
            destinationDirectory = destinationDirectory.TrimEnd('/', '\\');

            foreach (var file in Directory.GetFiles(sourceDirectory))
            {
                string relPath = file.Substring(sourceDirectory.Length + 1);
                string dest = Path.Combine(destinationDirectory, relPath);
                File.Copy(file, dest, true);
                File.SetAttributes(dest, File.GetAttributes(dest) & ~FileAttributes.ReadOnly);
            }

            foreach (var dir in Directory.GetDirectories(sourceDirectory))
            {
                string relPath = dir.Substring(sourceDirectory.Length + 1);
                string newDir = Path.Combine(destinationDirectory, relPath);
                if (!Directory.Exists(newDir))
                    Directory.CreateDirectory(newDir);
                CopyDirectoryRecursive(dir, newDir);
            }
        }

    }

    public enum STM32Ruleset
    {
        Classic,
        STM32WB,
        STM32MP1,
        BlueNRG_LP,
        STM32WL,
        STM32H7RS,
    }


}
