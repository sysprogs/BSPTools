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
        string _BSPDirectory;
        ReverseConditionTable _ReverseConditionTable;

        public ConfigurationFixDatabaseBuilder(string bSPDirectory, ReverseConditionTable reverseConditionTable)
        {
            _BSPDirectory = bSPDirectory;
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

            XmlTools.SaveObject(result, Path.Combine(_BSPDirectory, ConfigurationFixDatabase.FileName));
        }

        private string GetFullPath(string value)
        {
            return Path.GetFullPath(value.Replace("$$SYS:BSP_ROOT$$", _BSPDirectory));
        }
    }
}
