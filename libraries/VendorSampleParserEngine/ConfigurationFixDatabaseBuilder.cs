using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BSPEngine;
using BSPGenerationTools;

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

        public void BuildConfigurationFixDatabase()
        {
            ConfigurationFixDatabase result = new ConfigurationFixDatabase
            {
                ConfigurationTable = _ReverseConditionTable.ConditionTable,
                Frameworks = _ReverseConditionTable.Frameworks,
                SourceFiles = _ReverseConditionTable.FileTable,
            };

            for (int i = 0; i < _ReverseConditionTable.IncludeDirectoryTable.Count; i++)
            {
                string physicalDir = GetFullPath(_ReverseConditionTable.IncludeDirectoryTable[i].ObjectName);

                var headers = Directory.GetFiles(physicalDir, "*.h", SearchOption.AllDirectories);
                foreach (var hdr in headers)
                {
                    string relPath = hdr.Substring(physicalDir.Length).TrimStart('\\').Replace('\\', '/');

                    result.Headers.Add(new ConfigurationFixDatabase.SecondaryObjectEntry { ObjectName = relPath, PrimaryObjectIndex = i });
                }
            }

            BuildConfigurationFixSample();

            XmlTools.SaveObject(result, Path.Combine(_BSP.Directory, ConfigurationFixDatabase.FileName));
        }

        private void BuildConfigurationFixSample()
        {
            if (_ReverseConditionTable.ConfigurationFixSample == null)
                return;

            var sampleDir = GetFullPath(_ReverseConditionTable.ConfigurationFixSample.SamplePath);

            LoadedBSP.LoadedSample sampleObj = new LoadedBSP.LoadedSample
            {
                BSP = _BSP,
                Directory = sampleDir,
                Sample = XmlTools.LoadObject<EmbeddedProjectSample>(Path.Combine(sampleDir, "sample.xml"))
            };

            var mcu = _BSP.MCUs.First(m => m.ExpandedMCU.ID == _ReverseConditionTable.ConfigurationFixSample.MCUID);

            var result = StandaloneBSPValidator.Program.TestSingleSample(sampleObj, mcu, _TestDirectory, new StandaloneBSPValidator.TestedSample { }, null, null);
            if (result.Result != StandaloneBSPValidator.Program.TestBuildResult.Succeeded)
                throw new Exception("Failed to build synthetic sample for determining symbol-to-config map");
        }

        private string GetFullPath(string value)
        {
            return Path.GetFullPath(value.Replace("$$SYS:BSP_ROOT$$", _BSP.Directory));
        }
    }
}
