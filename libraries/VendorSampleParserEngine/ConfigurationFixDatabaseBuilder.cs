using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BSPEngine;
using BSPGenerationTools;
using StandaloneBSPValidator;

namespace VendorSampleParserEngine
{
    public class ConfigurationFixDatabaseBuilder
    {
        LoadedBSP _BSP;
        private readonly string _TestDirectory;
        ReverseConditionTable _ReverseConditionTable;

        public ConfigurationFixDatabaseBuilder(LoadedBSP bsp, string testDirectory, ReverseConditionTable reverseConditionTable)
        {
            _BSP = bsp;
            _TestDirectory = testDirectory;
            _ReverseConditionTable = reverseConditionTable;
        }

        public void BuildConfigurationFixDatabase(BSPReportWriter reportWriter)
        {
            ConfigurationFixDatabase result = new ConfigurationFixDatabase
            {
                ConfigurationTable = _ReverseConditionTable.ConditionTable,
                Frameworks = _ReverseConditionTable.Frameworks,
                SourceFiles = _ReverseConditionTable.FileTable,
                IncludeDirectories = _ReverseConditionTable.IncludeDirectoryTable,
            };

            var file = Path.Combine(_BSP.Directory, ConfigurationFixDatabase.FileName);
            if (File.Exists(file))
                result.ConfigurationFileEntries = XmlTools.LoadObject<ConfigurationFixDatabase>(file).ConfigurationFileEntries;

            for (int i = 0; i < _ReverseConditionTable.IncludeDirectoryTable.Count; i++)
            {
                string physicalDir = GetFullPath(_ReverseConditionTable.IncludeDirectoryTable[i].ObjectName);
                if (physicalDir.Contains("$$"))
                    continue;

                var headers = Directory.GetFiles(physicalDir, "*.h", SearchOption.AllDirectories);
                foreach (var hdr in headers)
                {
                    string relPath = hdr.Substring(physicalDir.Length).TrimStart('\\').Replace('\\', '/');

                    result.Headers.Add(new ConfigurationFixDatabase.SecondaryObjectEntry { ObjectName = relPath, PrimaryObjectIndex = i });
                }
            }

            result.Symbols = ComputeSymbolToFileMap(reportWriter);

            XmlTools.SaveObject(result, file);
        }

        class ConstructedConfiguration
        {
            HashSet<int> _Frameworks = new HashSet<int>();
            Dictionary<string, string> _Config = new Dictionary<string, string>();
            private readonly ReverseConditionTable _Table;

            public ConstructedConfiguration(ReverseConditionTable table)
            {
                _Table = table;
            }

            public bool TryMerge(ConfigurationFixDatabase.ObjectEntry entry)
            {
                if (entry.OneBasedConfigurationFragmentIndex > 0)
                {
                    var fragment = _Table.ConditionTable[entry.OneBasedConfigurationFragmentIndex - 1];

                    foreach (var kv in fragment.RequestedConfiguration)
                    {
                        if (_Config.TryGetValue(kv.Key, out var tmp) && tmp != kv.Value)
                            return false;   //Incompatible configuration detected
                    }

                    foreach (var kv in fragment.RequestedConfiguration)
                        _Config[kv.Key] = kv.Value;
                }

                if (entry.OneBasedFrameworkIndex > 0)
                {
                    _Frameworks.Add(entry.OneBasedFrameworkIndex);
                }

                return true;
            }

            public TestedSample ToSampleJobObject()
            {
                Dictionary<string, string> config = new Dictionary<string, string>(_Config);
                List<string> frameworkIDs = new List<string>();

                foreach (var idx in _Frameworks)
                {
                    var fw = _Table.Frameworks[idx - 1];
                    frameworkIDs.Add(fw.ID);
                    foreach (var kv in fw.MinimalConfiguration ?? new SysVarEntry[0])
                    {
                        if (!config.ContainsKey(kv.Key))
                            config[kv.Key] = kv.Value;
                    }
                }

                return new TestedSample
                {
                    FrameworkConfiguration = new PropertyDictionary2(config),
                    AdditionalFrameworks = frameworkIDs.ToArray()
                };
            }
        }

        private List<ConfigurationFixDatabase.SecondaryObjectEntry> ComputeSymbolToFileMap(BSPReportWriter reportWriter)
        {
            var result = new HashSet<ConfigurationFixDatabase.SecondaryObjectEntry>();
            if (_ReverseConditionTable.ConfigurationFixSamples == null)
                return result.ToList();

            Dictionary<string, bool> fileBuildStatus = new Dictionary<string, bool>();

            foreach (var sample in _ReverseConditionTable.ConfigurationFixSamples)
            {
                var sampleDir = GetFullPath(sample.SamplePath);

                LoadedBSP.LoadedSample sampleObj = new LoadedBSP.LoadedSample
                {
                    BSP = _BSP,
                    Directory = sampleDir,
                    Sample = XmlTools.LoadObject<EmbeddedProjectSample>(Path.Combine(sampleDir, "sample.xml"))
                };

                var mcu = _BSP.MCUs.First(m => m.ExpandedMCU.ID == sample.MCUID);
                List<int> queue = Enumerable.Range(0, _ReverseConditionTable.FileTable.Count).ToList();

                while (queue.Count > 0)
                {
                    Console.WriteLine($"Analyzing {sampleObj.Sample.Name} ({result.Count} symbols mapped, {queue.Count} files left)...");
                    List<int> rejects = new List<int>();
                    List<int> handledFiles = new List<int>();
                    ConstructedConfiguration cfg = new ConstructedConfiguration(_ReverseConditionTable);

                    foreach (var i in queue)
                    {
                        var file = _ReverseConditionTable.FileTable[i];
                        string ext = Path.GetExtension(file.ObjectName).ToLower();
                        if (ext != ".c" && ext != ".cpp")
                            continue;

                        if (cfg.TryMerge(file))
                            handledFiles.Add(i);
                        else
                            rejects.Add(i);
                    }

                    var buildResult = BSPValidator.TestSingleSample(sampleObj, mcu, _TestDirectory, cfg.ToSampleJobObject(), null, null, BSPValidationFlags.KeepDirectoryAfterSuccessfulTest | BSPValidationFlags.ContinuePastCompilationErrors);
                    BuildSymbolTableFromBuildResults(handledFiles, result, fileBuildStatus);

                    if (rejects.Count == queue.Count)
                        break;

                    queue = rejects;
                }
            }

            foreach (var kv in fileBuildStatus)
                if (!kv.Value)
                    reportWriter.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, "Could not obtain symbol list provided by the following files", kv.Key, false);

            return result.ToList();
        }

        private void BuildSymbolTableFromBuildResults(IEnumerable<int> fileIndicies, HashSet<ConfigurationFixDatabase.SecondaryObjectEntry> result, Dictionary<string, bool> fileBuildStatus)
        {
            Regex rgDefinedSymbol = new Regex("[0-9a-fA-F]+[ \t]+([^ \t]+)[ \t]+([^ \t]+)[ \t]+([^ \t]+)[ \t]+([^ \t]+)[ \t]+([^ \t]+)$");

            foreach (var i in fileIndicies)
            {
                var nameBase = Path.GetFileNameWithoutExtension(_ReverseConditionTable.FileTable[i].ObjectName);
                if (!File.Exists(Path.Combine(_TestDirectory, nameBase + ".o")))
                {
                    if (!fileBuildStatus.ContainsKey(_ReverseConditionTable.FileTable[i].ObjectName))
                        fileBuildStatus[_ReverseConditionTable.FileTable[i].ObjectName] = false;
                    continue;
                }

                fileBuildStatus[_ReverseConditionTable.FileTable[i].ObjectName] = true;

                var objdump = _BSP.Toolchain.MakeToolName("objdump");
                var proc = new Process();
                proc.StartInfo.FileName = "cmd.exe";
                proc.StartInfo.Arguments = $"/c {objdump} -t {nameBase}.o > {nameBase}.lst";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.WorkingDirectory = _TestDirectory;
                proc.Start();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    throw new Exception("Failed to obtain symbol list for " + nameBase);

                foreach (var line in File.ReadAllLines(Path.Combine(_TestDirectory, nameBase + ".lst")))
                {
                    var m = rgDefinedSymbol.Match(line);
                    if (m.Success)
                    {
                        var scope = m.Groups[1].Value;
                        var name = m.Groups[5].Value;
                        if (scope == "g")
                        {
                            result.Add(new ConfigurationFixDatabase.SecondaryObjectEntry { ObjectName = name, PrimaryObjectIndex = i });
                        }
                    }
                }
            }
        }

        private string GetFullPath(string value)
        {
            return Path.GetFullPath(value.Replace("$$SYS:BSP_ROOT$$", _BSP.Directory));
        }
    }
}
