using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace nrf5x
{
    class Nrf5xRuleGenerator
    {
        private BSPBuilder _Builder;
        public BSPDirectories Directories => _Builder.Directories;

        public Nrf5xRuleGenerator(BSPBuilder nordicBSPBuilder)
        {
            _Builder = nordicBSPBuilder;
        }

        public void PatchGeneratedFrameworks(List<EmbeddedFramework> frameworks, List<ConditionalToolFlags> condFlags)
        {
            GenerateBoardProperty(frameworks);
            //ConvertFrameworkIncludesFromAutoConditionedSubfoldersToConditionalFlags(frameworks, condFlags);
        }

        void ConvertFrameworkIncludesFromAutoConditionedSubfoldersToConditionalFlags(List<EmbeddedFramework> frameworks, List<ConditionalToolFlags> condFlags)
        { 
            foreach (var fw in frameworks)
            {
                if (!_FrameworkConditions.TryGetValue(fw.ID, out var frameworkConditions))
                    continue;

                Dictionary<string, List<string>> conditionalIncludes = new Dictionary<string, List<string>>();
                var includeDirs = fw.AdditionalIncludeDirs.ToList();

                for (int i = 0; i < includeDirs.Count; i++)
                {
                    if (!includeDirs[i].StartsWith(frameworkConditions.TargetPath + "/"))
                        continue;

                    string firstComponent = includeDirs[i].Substring(frameworkConditions.TargetPath.Length).TrimStart('/');
                    int idx = firstComponent.IndexOf('/');
                    if (idx == -1)
                        continue;

                    firstComponent = firstComponent.Substring(0, idx);

                    if (frameworkConditions.ConditionalSubdirectories.Contains(firstComponent))
                    {
                        if (!conditionalIncludes.TryGetValue(firstComponent, out var list))
                            conditionalIncludes[firstComponent] = list = new List<string>();

                        list.Add(includeDirs[i]);
                        includeDirs.RemoveAt(i--);
                    }
                }

                foreach (var kv in conditionalIncludes)
                {
                    string prefix = frameworkConditions.TargetPath + "/" + kv.Key;
                    var fileCondition = _Builder.MatchedFileConditions.FirstOrDefault(kv2 => kv2.Key.StartsWith(prefix + "/")).Value;
                    if (fileCondition == null)
                        throw new Exception($"No file conditions start with '{prefix}'. Could not derive variable name for '{kv.Key}'.");

                    condFlags.Add(new ConditionalToolFlags
                    {
                        FlagCondition = new Condition.And
                        {
                            Arguments = new Condition[] {
                                new Condition.ReferencesFramework{FrameworkID = fw.ClassID ?? fw.ID},
                                fileCondition.ConditionToInclude,
                            }
                        },
                        Flags = new ToolFlags
                        {
                            IncludeDirectories = kv.Value.ToArray()
                        }
                    });
                }
            }

        }

        void GenerateBoardProperty(List<EmbeddedFramework> frameworks)
        {
            List<PropertyEntry.Enumerated.Suggestion> lstProp = new List<PropertyEntry.Enumerated.Suggestion>();
            var framework = frameworks.SingleOrDefault(fr => fr.ID.Equals("com.sysprogs.arm.nordic.nrf5x.boards"));
            var propertyGroup = framework.ConfigurableProperties.PropertyGroups.
                                            SingleOrDefault(pg => pg.UniqueID.Equals("com.sysprogs.bspoptions.nrf5x.board."));

            var rgBoardIfdef = new Regex("#(if|elif) defined\\(BOARD_([A-Z0-9a-z_]+)\\)");
            var rgInclude = new Regex("#include \"([^\"]+)\"");

            var lines = File.ReadAllLines(Path.Combine(Directories.OutputDir, @"nRF5x\components\boards\boards.h"));
            lstProp.Add(new PropertyEntry.Enumerated.Suggestion() { InternalValue = "", UserFriendlyName = "None" });

            var reverseConditions = _Builder.ReverseFileConditions?.GetHandleForFramework(framework);

            const string BoardTypeParameter = "com.sysprogs.bspoptions.nrf5x.board.type";

            for (int i = 0; i < lines.Length; i++)
            {
                var m = rgBoardIfdef.Match(lines[i]);
                if (!m.Success)
                    continue;

                string boardID = m.Groups[2].Value;
                string file = rgInclude.Match(lines[i + 1]).Groups[1].Value;

                _Builder.AddFileCondition(new FileCondition()
                {
                    ConditionToInclude = new Condition.Equals()
                    {
                        Expression = $"$${BoardTypeParameter}$$",
                        ExpectedValue = boardID,
                        IgnoreCase = false
                    },
                    FilePath = "nRF5x/components/boards/" + file
                });
                lstProp.Add(new PropertyEntry.Enumerated.Suggestion { InternalValue = boardID });

                reverseConditions?.AttachPreprocessorMacro("BOARD_" + boardID, reverseConditions?.CreateSimpleCondition(BoardTypeParameter, boardID));
            }
            //--ConfigurableProperties--

            propertyGroup.Properties.Add(new PropertyEntry.Enumerated
            {
                UniqueID = "type",
                Name = "Board Type",
                DefaultEntryIndex = Enumerable.Range(0, lstProp.Count).First(i => lstProp[i].InternalValue == "PCA10040"),
                DefaultEntryValue = "$$com.sysprogs.bspoptions.nrf5x.mcu.default_board$$",
                SuggestionList = lstProp.ToArray()
            });
        }

        public void GenerateRulesForFamily(FamilyDefinition family)
        {
            foreach (var fw in family.AdditionalFrameworks)
            {
                foreach (var job in fw.CopyJobs)
                {
                    string[] attrs = job.VendorSpecificAttributes?.Split('|');

                    if (attrs?.Contains("GenerateConditionsForPrebuiltLibraries") == true)
                        GenerateConditionsForPrebuiltLibraries(family, fw, job);
                    if (attrs?.Contains("GenerateConditionsForSubdirs") == true)
                        GenerateConditionsForSubdirs(family, fw, job);
                }
            }

            GenerateBLEFrameworks(family);
        }

        class PrebuiltLibraryCondition
        {
            public string Folder, InverseFolder;
            public string Variable;
            public string Value;

            public PrebuiltLibraryCondition(string folder, string variable, string value, string inverseFolder = null)
            {
                Folder = folder;
                Variable = variable;
                Value = value;
                InverseFolder = inverseFolder;
            }
        }

        struct ConstructedLibraryCondition
        {
            public string Library;
            public string Core;
            public string[] PathComponents;
        }

        private void GenerateConditionsForPrebuiltLibraries(FamilyDefinition family, Framework fw, CopyJob job)
        {
            var srcDir = Path.GetFullPath(_Builder.ExpandVariables(job.SourceFolder));
            var allLibraries = Directory.GetFiles(srcDir, "*.a", SearchOption.AllDirectories)
                .Select(l => l.Substring(srcDir.Length).TrimStart('\\')).ToArray();

            var conditions = new[]
            {
                new PrebuiltLibraryCondition("hard-float", "com.sysprogs.bspoptions.arm.floatmode", "-mfloat-abi=hard", "soft-float"),
                new PrebuiltLibraryCondition("no-interrupts", "com.sysprogs.bspoptions.nrf5x.interrupts", "no"),
                new PrebuiltLibraryCondition("short-wchar", "com.sysprogs.bspoptions.nrf5x.wchar", "-fshort-wchar"),
            };

            Dictionary<string, PrebuiltLibraryCondition> conditionsByFolder = new Dictionary<string, PrebuiltLibraryCondition>();
            foreach (var cond in conditions)
            {
                conditionsByFolder[cond.Folder] = cond;
                if (cond.InverseFolder != null)
                    conditionsByFolder[cond.InverseFolder] = cond;
            }

            foreach (var grp in allLibraries.GroupBy(l => l.Substring(0, l.IndexOf('\\')), StringComparer.InvariantCultureIgnoreCase))
            {
                HashSet<PrebuiltLibraryCondition> allConditionsInGroup = new HashSet<PrebuiltLibraryCondition>();
                List<ConstructedLibraryCondition> libs = new List<ConstructedLibraryCondition>();

                foreach (var lib in grp)
                {
                    List<string> components = lib.Split('\\').ToList();
                    int libComponent = components.IndexOf("lib");
                    if (libComponent < 0)
                        throw new Exception($"Cannot build a list of conditional folders. {lib} does not contain 'lib' in the path.");

                    components.RemoveRange(0, libComponent + 1);

                    string cortexPrefix = "cortex-";
                    if (!components[0].StartsWith(cortexPrefix))
                        throw new Exception($"Relative path to {lib} does not start with 'cortex-'");

                    string cortexCore = components[0].Substring(cortexPrefix.Length);

                    components.RemoveAt(0);
                    components.RemoveAt(components.Count - 1);

                    foreach (var cmp in components)
                    {
                        if (!conditionsByFolder.TryGetValue(cmp, out var cond))
                            throw new Exception($"Don't know how to map '{cmp}' to a prebuilt library condition");

                        allConditionsInGroup.Add(cond);
                    }

                    libs.Add(new ConstructedLibraryCondition { Library = $"$$SYS:BSP_ROOT$$/{family.FamilySubdirectory}/{job.TargetFolder}/" + lib.Replace('\\', '/'), PathComponents = components.ToArray(), Core = cortexCore });
                }

                foreach (var lib in libs)
                {
                    List<Condition> finalConditions = new List<Condition>();
                    finalConditions.Add(new Condition.Equals { Expression = "$$com.sysprogs.bspoptions.nrf5x.cortex$$", ExpectedValue = lib.Core });
                    foreach (var usedCond in allConditionsInGroup)
                    {
                        var eqCond = new Condition.Equals { Expression = $"$${usedCond.Variable}$$", ExpectedValue = usedCond.Value };
                        if (lib.PathComponents.Contains(usedCond.Folder))
                            finalConditions.Add(eqCond);
                        else
                            finalConditions.Add(new Condition.Not { Argument = eqCond });
                    }

                    FileCondition cond = new FileCondition
                    {
                        FilePath = lib.Library,
                        ConditionToInclude = new Condition.And
                        {
                            Arguments = finalConditions.ToArray()
                        }
                    };

                    _Builder.AddFileCondition(cond);
                }
            }
        }

        class GeneratedFrameworkConditions
        {
            public string TargetPath;
            public HashSet<string> ConditionalSubdirectories = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        }

        Dictionary<string, GeneratedFrameworkConditions> _FrameworkConditions = new Dictionary<string, GeneratedFrameworkConditions>();

        private void GenerateConditionsForSubdirs(FamilyDefinition family, Framework fw, CopyJob job)
        {
            HashSet<string> explicitlyMentionedDirectories = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            HashSet<string> excludedSubdirectories = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            foreach (var cond in job.SmartFileConditions ?? new string[0])
            {
                var def = SmartPropertyDefinition.Parse(cond, null);
                foreach (var item in def.Items)
                {
                    int idx = item.Key.IndexOf('\\');
                    if (idx != -1)
                        explicitlyMentionedDirectories.Add(item.Key.Substring(0, idx));
                }
            }

            Regex rgExcludedSubdir = new Regex(@"-([^\\]+)\\\*$");
            foreach (var cond in (job.FilesToCopy + ";" + job.ProjectInclusionMask).Split(';'))
            {
                var m = rgExcludedSubdir.Match(cond);
                if (m.Success)
                    excludedSubdirectories.Add(m.Groups[1].Value);
            }

            List<string> generatedSmartFileConditions = new List<string>();

            foreach (var dir in Directory.GetDirectories(_Builder.ExpandVariables(job.SourceFolder)))
            {
                string name = Path.GetFileName(dir);
                if (explicitlyMentionedDirectories.Contains(name))
                    continue;
                if (excludedSubdirectories.Contains(name))
                    continue;

                generatedSmartFileConditions.Add($"-{name}|{name}\\\\.*");

                if (!_FrameworkConditions.TryGetValue(fw.ID, out var conditions))
                    _FrameworkConditions[fw.ID] = conditions = new GeneratedFrameworkConditions { TargetPath = $"$$SYS:BSP_ROOT$$/{family.FamilySubdirectory}/{job.TargetFolder}" };

                conditions.ConditionalSubdirectories.Add(name);
            }

            job.SmartFileConditions = (job.SmartFileConditions ?? new string[0]).Concat(generatedSmartFileConditions).OrderBy(c => c.ToLower().TrimStart('-')).ToArray();
        }

        void GenerateBLEFrameworks(FamilyDefinition family)
        {
            List<Framework> bleFrameworks = new List<Framework>();
            string famBase = family.Name.Substring(0, 5).ToLower();

            HashSet<string> discoveredSubdirs = new HashSet<string>();
            string baseDir = Path.Combine(family.PrimaryHeaderDir, @"..\..\..\components\ble");
            foreach (var subdir in Directory.GetDirectories(baseDir))
                discoveredSubdirs.Add(Path.GetFileName(subdir));
            foreach (var subdir in Directory.GetDirectories(Path.Combine(baseDir, "ble_services")))
                discoveredSubdirs.Add(Path.Combine("ble_services", Path.GetFileName(subdir)));

            foreach (var name in new[] { "ble_services", "common", "peer_manager", "nrf_ble_gatt" })
                discoveredSubdirs.Remove(name);

            foreach (var dir in discoveredSubdirs)
            {
                string desc = FetchDescriptionFromDirectory(Path.Combine(baseDir, dir)) ?? throw new Exception("Failed to load description of " + dir);

                if (desc.StartsWith("BLE"))
                    desc = desc.Substring(3).Trim();

                string virtualFolderName = "BLE " + desc;
                desc = "Bluetooth LE - " + desc;

                string id = Path.GetFileName(dir);

                if (id.StartsWith("experimental_"))
                    id = id.Substring(13);

                if (!id.StartsWith("ble_") && !id.StartsWith("nrf_ble"))
                    id = "ble_" + id;

                /*if (dir.StartsWith("ble_services\\", StringComparison.CurrentCultureIgnoreCase))
                {
                    int idx = id.IndexOf("ble_");
                    if (idx == -1)
                        id = "ble_svc_" + id;
                    else
                        id = id.Insert(idx + 4, "svc_");
                }*/

                bleFrameworks.Add(new Framework
                {
                    Name = string.Format("{0} ({1})", desc, Path.GetFileName(dir)),
                    ID = "com.sysprogs.arm.nordic." + famBase + "." + id,
                    ClassID = "com.sysprogs.arm.nordic.nrfx." + id,
                    ProjectFolderName = virtualFolderName,
                    DefaultEnabled = false,
                    CopyJobs = new CopyJob[]
                    {
                        new CopyJob
                        {
                            SourceFolder = Path.Combine(baseDir, dir),
                            TargetFolder = @"components\ble\" + dir,
                            FilesToCopy = "*.c;*.h",
                        }
                    }
                });
            }

            family.AdditionalFrameworks = family.AdditionalFrameworks.Concat(bleFrameworks).OrderBy(fw => fw.Name).ToArray();
        }

        private string FetchDescriptionFromDirectory(string dir)
        {
            Regex rgDesc = new Regex(" \\* @defgroup [^ ]+ (.*)$");

            var headers = Directory.GetFiles(dir, "*.h", SearchOption.AllDirectories);
            string hdr = headers[0];
            if (headers.Length > 1)
            {
                string folderName = Path.GetFileName(dir);
                if (folderName.StartsWith("experimental_"))
                    folderName = folderName.Substring(13);

                hdr = headers.FirstOrDefault(h => Path.GetFileNameWithoutExtension(h) == folderName);
                if (hdr == null)
                    hdr = headers.FirstOrDefault(h => Path.GetFileNameWithoutExtension(h) == "nrf_" + folderName);
                if (hdr == null && folderName == "eddystone")
                    hdr = headers.FirstOrDefault(h => Path.GetFileNameWithoutExtension(h) == "nrf_ble_es");

                if (hdr == null)
                    throw new Exception("Don't know how to read description for " + dir);
            }

            foreach (var line in File.ReadAllLines(hdr))
            {
                var m = rgDesc.Match(line);
                if (m.Success)
                    return m.Groups[1].Value;
            }

            return null;
        }
    }
}
