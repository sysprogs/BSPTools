using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BSPEngine;

namespace BSPGenerationTools.ConfigFiles
{
    class FreeRTOSConfigParser : IConfigurationFileParser
    {
        public ConfigurationFileTemplate BuildConfigurationFileTemplate(string file)
        {
            Regex rgParameter = new Regex("#define config([^ ]+)([ \t]+)(\\( ?|)([a-zA-Z0-9_]+).*");

            PropertyGroup group = new PropertyGroup { Name = "FreeRTOS" };
            PropertyList propertyList = new PropertyList { PropertyGroups = new List<PropertyGroup> { group } };
            List<string> allProperties = new List<string>();

            foreach (var line in File.ReadAllLines(file))
            {
                var m = rgParameter.Match(line);
                if (m.Success)
                {
                    string name = m.Groups[1].Value;

                    if (name.StartsWith("USE_"))
                        group.Properties.Add(new PropertyEntry.Boolean { Name = name, UniqueID = name, ValueForTrue = "1", ValueForFalse = "0" });
                    else
                        group.Properties.Add(new PropertyEntry.String { Name = name, UniqueID = name });
                }
            }

            if (group != null && group.Properties.Count > 0)
                propertyList.PropertyGroups.Add(group);

            return new ConfigurationFileTemplate
            {
                PropertyClasses = new ConfigurationFilePropertyClass[]
                {
                    new ConfigurationFilePropertyClass
                    {
                        NormalRegex = new SerializableRegularExpression(rgParameter.ToString()),
                        Template = "#define {0}{1}{2}",
                        Properties = allProperties.ToArray(),
                        NameIndex = 1,
                        IndentIndex = 2,
                        ValueIndex = 4,
                    }
                },
                TargetFileName = Path.GetFileName(file),
                PropertyList = propertyList,
            };
        }
    }
}
