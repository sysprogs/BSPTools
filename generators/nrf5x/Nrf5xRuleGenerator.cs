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

        public void GenerateBoardProperty(List<EmbeddedFramework> prFrBoard)
        {
            List<PropertyEntry.Enumerated.Suggestion> lstProp = new List<PropertyEntry.Enumerated.Suggestion>();
            var propertyGroup = prFrBoard.SingleOrDefault(fr => fr.ID.Equals("com.sysprogs.arm.nordic.nrf5x.boards")).
                                        ConfigurableProperties.PropertyGroups.
                                            SingleOrDefault(pg => pg.UniqueID.Equals("com.sysprogs.bspoptions.nrf5x.board."));

            var rgBoardIfdef = new Regex("#(if|elif) defined\\(BOARD_([A-Z0-9a-z_]+)\\)");
            var rgInclude = new Regex("#include \"([^\"]+)\"");

            var lines = File.ReadAllLines(Path.Combine(Directories.OutputDir, @"nRF5x\components\boards\boards.h"));
            lstProp.Add(new PropertyEntry.Enumerated.Suggestion() { InternalValue = "", UserFriendlyName = "None" });

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
                        Expression = "$$com.sysprogs.bspoptions.nrf5x.board.type$$",
                        ExpectedValue = boardID,
                        IgnoreCase = false
                    },
                    FilePath = "nRF5x/components/boards/" + file
                });
                lstProp.Add(new PropertyEntry.Enumerated.Suggestion() { InternalValue = boardID });
            }
            //--ConfigurableProperties--

            propertyGroup.Properties.Add(new PropertyEntry.Enumerated
            {
                UniqueID = "type",
                Name = "Board Type",
                DefaultEntryIndex = Enumerable.Range(0, lstProp.Count).First(i => lstProp[i].InternalValue == "PCA10040"),
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
            foreach(var cond in job.FilesToCopy.Split(';'))
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
            }

            job.SmartFileConditions = (job.SmartFileConditions ?? new string[0]).Concat(generatedSmartFileConditions).OrderBy(c => c.ToLower().TrimStart('-')).ToArray();
        }

        void GenerateBLEFrameworks(FamilyDefinition family)
        {
            List<Framework> bleFrameworks = new List<Framework>();
            string famBase = family.Name.Substring(0, 5).ToLower();

            foreach (var line in File.ReadAllLines(Directories.RulesDir + @"\BLEFrameworks.txt"))
            {
                int idx = line.IndexOf('|');
                string dir = line.Substring(0, idx);
                string desc = line.Substring(idx + 1);

                string id = Path.GetFileName(dir);
                if (!id.StartsWith("ble_"))
                    id = "ble_" + id;

                if (dir.StartsWith("services\\", StringComparison.CurrentCultureIgnoreCase))
                    id = "ble_svc_" + id.Substring(4);

                bleFrameworks.Add(new Framework
                {
                    Name = string.Format("Bluetooth LE - {0} ({1})", desc, Path.GetFileName(dir)),
                    ID = "com.sysprogs.arm.nordic." + famBase + "." + id,
                    ClassID = "com.sysprogs.arm.nordic.nrfx." + id,
                    ProjectFolderName = "BLE " + desc,
                    DefaultEnabled = false,
                    CopyJobs = new CopyJob[]
                    {
                        new CopyJob
                        {
                            SourceFolder = family.PrimaryHeaderDir + @"\..\..\..\components\ble\" + dir,
                            TargetFolder = dir,
                            FilesToCopy = "*.c;*.h",
                        }
                    }
                });
            }

            family.AdditionalFrameworks = family.AdditionalFrameworks.Concat(bleFrameworks).ToArray();
        }
    }
}
