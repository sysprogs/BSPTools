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

                _Builder.MatchedFileConditions.Add(new FileCondition()
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
            foreach(var fw in family.AdditionalFrameworks)
            {
                foreach(var job in fw.CopyJobs)
                {
                    if (job.VendorSpecificAttributes?.StartsWith("GenerateConditionsForSubdirs") == true)
                    {
                        GenerateConditionsForSubdirs(family, fw, job);
                    }
                }
            }

            GenerateBLEFrameworks(family);
        }

        private void GenerateConditionsForSubdirs(FamilyDefinition family, Framework fw, CopyJob job)
        {
            HashSet<string> explicitlyMentionedDirectories = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            foreach(var cond in job.SmartFileConditions ?? new string[0])
            {
                var def = SmartPropertyDefinition.Parse(cond, null);
                foreach(var item in def.Items)
                {
                    int idx = item.Key.IndexOf('\\');
                    if (idx != -1)
                        explicitlyMentionedDirectories.Add(item.Key.Substring(0, idx));
                }
            }

            List<string> generatedSmartFileConditions = new List<string>();

            foreach (var dir in Directory.GetDirectories(Path.Combine(Directories.InputDir, _Builder.ExpandVariables(job.SourceFolder))))
            {
                string name = Path.GetFileName(dir);
                if (explicitlyMentionedDirectories.Contains(name))
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
