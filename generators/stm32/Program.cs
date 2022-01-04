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

            public STM32BSPBuilder(BSPDirectories dirs, string cubeDir)
                : base(dirs)
            {
                STM32CubeDir = cubeDir;
                ShortName = "STM32";

                SDKList = XmlTools.LoadObject<STM32SDKCollection>(Path.Combine(dirs.InputDir, SDKFetcher.SDKListFileName));

                foreach (var sdk in SDKList.SDKs)
                {
                    SystemVars[$"STM32:{sdk.Family.ToUpper()}_DIR"] = Directory.GetDirectories(Path.Combine(dirs.InputDir, sdk.FolderName), "STM32Cube_FW_*").First();
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
        }

        static string GetSubfamilyDefine(STM32Ruleset ruleset, MCUBuilder builder)
        {
            if (ruleset == STM32Ruleset.BlueNRG_LP)
                return "BLUENRG_LP";
            return (builder as DeviceListProviders.CubeProvider.STM32MCUBuilder)?.MCU.Define ?? throw new Exception("Unknown primary macro for " + builder.Name);
        }

        static IEnumerable<StartupFileGenerator.InterruptVectorTable> ParseStartupFiles(string dir, MCUFamilyBuilder fam, STM32Ruleset ruleset)
        {
            var allFiles = Directory.GetFiles(dir);

            foreach (var fn in allFiles)
            {
                string subfamily = Path.GetFileNameWithoutExtension(fn);
                if (!subfamily.StartsWith("startup_"))
                    continue;
                subfamily = subfamily.Substring(8);

                if (subfamily.EndsWith("_cm4"))
                    subfamily = subfamily.Substring(0, subfamily.Length - 4);

                yield return new StartupFileGenerator.InterruptVectorTable
                {
                    FileName = Path.ChangeExtension(Path.GetFileName(fn), ".c"),
                    MatchPredicate = m => (allFiles.Length == 1) || StringComparer.InvariantCultureIgnoreCase.Compare(GetSubfamilyDefine(ruleset, m), subfamily) == 0,
                    Vectors = StartupFileGenerator.ParseInterruptVectors(fn,
                        tableStart: "g_pfnVectors:",
                        tableEnd: @"/\*{10,999}|^[^/\*]+\*/
                $",
                        vectorLineA: @"^[ \t]+\.word[ \t]+([^ ]+)",
                        vectorLineB: null,
                        ignoredLine: @"^[ \t]+/\*|[ \t]+stm32.*|[ \t]+STM32.*|// External Interrupts",
                        macroDef: ".equ[ \t]+([^ \t]+),[ \t]+(0x[0-9a-fA-F]+)",
                        nameGroup: 1,
                        commentGroup: 2)
                };
            }
        }

        private static IEnumerable<MCUDefinitionWithPredicate> ParsePeripheralRegisters(string dir, MCUFamilyBuilder fam, string specificDevice, ParseReportWriter writer, STM32Ruleset ruleset)
        {
            List<MCUDefinitionWithPredicate> result = new List<MCUDefinitionWithPredicate>();
            string subfamilySuffix = "";
            if (fam.Definition.Name.EndsWith("_M4"))
                subfamilySuffix += "_m4";

            Console.Write("Parsing {0} registers using the new parsing logic...", fam.Definition.Name);
            foreach (var fn in Directory.GetFiles(dir, "*.h"))
            {
                string subfamily = Path.GetFileNameWithoutExtension(fn);
                string subfamilyForMatching = subfamily;
                if (subfamily.Length != 11 && subfamily.Length != 12)
                {
                    if (subfamily.EndsWith("_cm4", StringComparison.InvariantCultureIgnoreCase))
                    {
                        //This is a header file for the Cortex-M4 core of an STM32MP1 device
                        subfamilyForMatching = subfamily.Substring(0, subfamily.Length - 4);
                    }
                    else
                        continue;
                }

                if (specificDevice != null && subfamily != specificDevice)
                    continue;

                var r = new MCUDefinitionWithPredicate
                {
                    MCUName = subfamily + subfamilySuffix,
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

        //Usage: stm32.exe /rules:{Classic|STM32WB|STM32MP1} [/fetch] [/noperiph] [/nofixes]
        static void Main(string[] args)
        {
            var regKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Sysprogs\BSPGenerators\STM32");
            var sdkRoot = regKey.GetValue("SDKRoot") as string ?? throw new Exception("Please specify STM32 SDK root via registry");
            var cubeRoot = regKey.GetValue("CubeMXRoot") as string ?? throw new Exception("Please specify STM32CubeMX location via registry");

            if (args.Contains("/fetch"))
            {
                //This will load the latest SDK list from the STM32CubeMX directory and will fetch/unpack them to our SDKRoot directory.
                //Before running this, ensure the STM32CubeMX has the up-to-date SDK definitions (using the 'check for update' function), as
                //otherwise the BSP generator will fetch the old versions.

                SDKFetcher.FetchLatestSDKs(sdkRoot, cubeRoot);
            }

            string explicitSource = args.FirstOrDefault(a => a.StartsWith("/source:"))?.Substring(8);

            string rulesetName = args.FirstOrDefault(a => a.StartsWith("/rules:"))?.Substring(7) ?? STM32Ruleset.Classic.ToString();
            STM32Ruleset ruleset = Enum.GetValues(typeof(STM32Ruleset))
                .OfType<STM32Ruleset>()
                .First(v => StringComparer.InvariantCultureIgnoreCase.Compare(v.ToString(), rulesetName.Replace('-', '_')) == 0);

            ///If the MCU list format changes again, create a new implementation of the IDeviceListProvider interface, switch to using it, but keep the old one for reference & easy comparison.
            IDeviceListProvider provider = new DeviceListProviders.CubeProvider();

            using (var bspBuilder = new STM32BSPBuilder(new BSPDirectories(sdkRoot, @"..\..\Output\" + rulesetName, @"..\..\rules\" + rulesetName, @"..\..\Logs\" + rulesetName), cubeRoot))
            using (var wr = new ParseReportWriter(Path.Combine(bspBuilder.Directories.LogDir, "registers.log")))
            {
                if (explicitSource != null)
                    bspBuilder.SystemVars["BSPGEN:INPUT_DIR"] = explicitSource;

                List<MCUBuilder> devices;
                if (ruleset == STM32Ruleset.BlueNRG_LP)
                    devices = new List<MCUBuilder> { new BlueNRGFamilyBuilder.BlueNRGMCUBuilder() };
                else
                {
                    devices = provider.LoadDeviceList(bspBuilder);
                    if (devices.Where(d => d.FlashSize == 0 && !d.Name.StartsWith("STM32MP1")).Count() > 0)
                        throw new Exception($"Some deviceshave FLASH Size({devices.Where(d => d.FlashSize == 0).Count()})  = 0 ");
                }

                List<MCUFamilyBuilder> allFamilies = new List<MCUFamilyBuilder>();
                string extraFrameworksFile = Path.Combine(bspBuilder.Directories.RulesDir, "FrameworkTemplates.xml");
                if (!File.Exists(extraFrameworksFile) && File.Exists(Path.ChangeExtension(extraFrameworksFile, ".txt")))
                    extraFrameworksFile = Path.Combine(bspBuilder.Directories.RulesDir, File.ReadAllText(Path.ChangeExtension(extraFrameworksFile, ".txt")));

                Dictionary<string, STM32SDKCollection.SDK> sdksByVariable = bspBuilder.SDKList.SDKs.ToDictionary(s => $"$$STM32:{s.Family}_DIR$$");
                List<STM32SDKCollection.SDK> referencedSDKs = new List<STM32SDKCollection.SDK>();

                foreach (var fn in Directory.GetFiles(bspBuilder.Directories.RulesDir + @"\families", "*.xml"))
                {
                    var fam = XmlTools.LoadObject<FamilyDefinition>(fn);

                    if (File.Exists(extraFrameworksFile))
                    {
                        int idx = fam.PrimaryHeaderDir.IndexOf('\\');
                        string baseDir = fam.PrimaryHeaderDir.Substring(0, idx);
                        if (!baseDir.StartsWith("$$STM32:"))
                            baseDir = explicitSource ?? throw new Exception("Invalid base directory. Please recheck the family definition.");

                        string baseFamName = fam.Name;
                        if (baseFamName.EndsWith("_M4"))
                            baseFamName = baseFamName.Substring(0, baseFamName.Length - 3);

                        if (ruleset != STM32Ruleset.BlueNRG_LP)
                            referencedSDKs.Add(sdksByVariable[baseDir]);

                        var dict = new Dictionary<string, string>
                        {
                            { "STM32:FAMILY_EX"  , fam.Name },
                            { "STM32:FAMILY"     , baseFamName },
                            { "STM32:FAMILY_L"   , baseFamName.ToLower() },
                            { "STM32:FAMILY_DIR" , baseDir },
                        };

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

                        fam.AdditionalFrameworks = (fam.AdditionalFrameworks ?? new Framework[0]).Concat(extraFrameworksWithoutMissingFolders).ToArray();
                    }

                    if (ruleset != STM32Ruleset.BlueNRG_LP)
                        bspBuilder.InsertLegacyHALRulesIfNecessary(fam, bspBuilder.ReverseFileConditions);
                    switch (ruleset)
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
                        case STM32Ruleset.Classic:
                        default:
                            allFamilies.Add(new STM32ClassicFamilyBuilder(bspBuilder, fam));
                            break;
                    }
                }

                var rejects = BSPGeneratorTools.AssignMCUsToFamilies(devices, allFamilies);

                if (ruleset == STM32Ruleset.Classic)
                {
                    foreach (var r in rejects)
                    {
                        if (r.Name.StartsWith("STM32MP1") || r.Name.StartsWith("STM32GBK") || r.Name.StartsWith("STM32WB") || r.Name.StartsWith("STM32WL"))
                            continue;

                        bspBuilder.Report.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, $"Could not find the family for {r.Name.Substring(0, 7)} MCU(s)", r.Name, true);
                    }
                }

                List<MCUFamily> familyDefinitions = new List<MCUFamily>();
                List<MCU> mcuDefinitions = new List<MCU>();
                List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
                List<MCUFamilyBuilder.CopiedSample> exampleDirs = new List<MCUFamilyBuilder.CopiedSample>();

                bool noPeripheralRegisters = args.Contains("/noperiph");
                bool noAutoFixes = args.Contains("/nofixes");
                string specificDeviceForDebuggingPeripheralRegisterGenerator = args.FirstOrDefault(a => a.StartsWith("/periph:"))?.Substring(8);

                var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));
                HashSet<string> familySpecificFrameworkIDs = new HashSet<string>();

                List<ConditionalToolFlags> allConditionalToolFlags = new List<ConditionalToolFlags>();

                foreach (var fam in allFamilies)
                {
                    fam.RemoveUnsupportedMCUs();

                    fam.AttachStartupFiles(ParseStartupFiles(fam.Definition.StartupFileDir, fam, ruleset));
                    if (!noPeripheralRegisters)
                        fam.AttachPeripheralRegisters(ParsePeripheralRegisters(fam.Definition.PrimaryHeaderDir, fam, specificDeviceForDebuggingPeripheralRegisterGenerator, wr, ruleset),
                            throwIfNotFound: specificDeviceForDebuggingPeripheralRegisterGenerator == null);

                    familyDefinitions.Add(fam.GenerateFamilyObject(MCUFamilyBuilder.CoreSpecificFlags.All, true));
                    fam.GenerateLinkerScripts(false);

                    foreach (var mcu in fam.MCUs)
                        mcuDefinitions.Add(mcu.GenerateDefinition(fam, bspBuilder, !noPeripheralRegisters && specificDeviceForDebuggingPeripheralRegisterGenerator == null));

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
                    frameworks.Add(fw);

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
                bsp.PackageVersion = bspBuilder.SDKList.BSPVersion;
                bsp.FileConditions = bspBuilder.MatchedFileConditions.Values.ToArray();
                bsp.InitializationCodeInsertionPoints = commonPseudofamily.Definition.InitializationCodeInsertionPoints;
                bsp.ConditionalFlags = allConditionalToolFlags.ToArray();

                bspBuilder.SDKList.SDKs = referencedSDKs.Distinct().ToArray();
                XmlTools.SaveObject(bspBuilder.SDKList, Path.Combine(bspBuilder.BSPRoot, "SDKVersions.xml"));

                bspBuilder.ValidateBSP(bsp);

                if (!noAutoFixes)
                    bspBuilder.ComputeAutofixHintsForConfigurationFiles(bsp);

                bspBuilder.ReverseFileConditions.SaveIfConsistent(bspBuilder.Directories.OutputDir, bspBuilder.ExportRenamedFileTable(), ruleset == STM32Ruleset.STM32WB);

                File.Copy(@"..\..\stm32_compat.h", Path.Combine(bspBuilder.BSPRoot, "stm32_compat.h"), true);
                Console.WriteLine("Saving BSP...");
                bspBuilder.Save(bsp, false);
            }
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
    }


}
