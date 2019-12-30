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
    public class NRFConfigFileParser : IConfigurationFileParser
    {
        public ConfigurationFileTemplate BuildConfigurationFileTemplate(string file)
        {
            Regex rgIfndef = new Regex("^#ifndef ([^ ]+)");
            Regex rgDefine = new Regex("^#define ([^ ]+)( )([^ ]+)$");

            Regex rgGroup = new Regex("^<h>[ \t]+(.*)");

            List<string> precedingComments = new List<string>();
            List<string> allProperties = new List<string>();
            string lastIfndef = null;

            PropertyList propertyList = new PropertyList { PropertyGroups = new List<PropertyGroup>() };
            PropertyGroup group = null;

            foreach (var line in File.ReadAllLines(file))
            {
                string previousLineIfndef = lastIfndef;
                lastIfndef = null;
                Match m;
                var trimmedLine = line.Trim();
                if (trimmedLine == "")
                    continue;

                if (trimmedLine.StartsWith("//"))
                {
                    string comment = trimmedLine.Substring(2).Trim();
                    m = rgGroup.Match(comment);
                    if (m.Success)
                    {
                        if (group != null && group.Properties.Count > 0)
                            propertyList.PropertyGroups.Add(group);

                        group = new PropertyGroup { Name = m.Groups[1].Value.Trim() };
                    }
                    else
                        precedingComments.Add(comment);
                }
                else if ((m = rgIfndef.Match(trimmedLine)).Success)
                    lastIfndef = m.Groups[1].Value;
                else if ((m = rgDefine.Match(trimmedLine)).Success)
                {
                    if (m.Groups[1].Value == previousLineIfndef)
                    {
                        if (group == null)
                            group = new PropertyGroup { Name = "Other Properties" };

                        var prop = ParseSingleProperty(m.Groups[1].Value, m.Groups[2].Value, precedingComments);
                        if (prop != null)
                        {
                            group.Properties.Add(prop);
                            allProperties.Add(prop.UniqueID);
                        }
                    }

                    precedingComments.Clear();
                }
                else
                    precedingComments.Clear();
            }

            if (group != null && group.Properties.Count > 0)
                propertyList.PropertyGroups.Add(group);

            return new ConfigurationFileTemplate
            {
                PropertyClasses = new ConfigurationFilePropertyClass[]
                {
                    new ConfigurationFilePropertyClass
                    {
                        NormalRegex = new SerializableRegularExpression(rgDefine.ToString()),
                        Template = "#define {0}{1}{2}",
                        Properties = allProperties.ToArray(),
                        NameIndex = 1,
                        IndentIndex = 2,
                        ValueIndex = 3,
                    }
                },
                TargetFileName = Path.GetFileName(file),
                PropertyList = propertyList,
            };
        }

        Regex rgMacroDescription = new Regex("<(.)> ([^ ]+)[ ]*-(.*)");
        Regex rgEnumValue = new Regex("<([0-9]+)=>[ \t]*(.*)");

        private PropertyEntry ParseSingleProperty(string name, string value, List<string> precedingComments)
        {
            var desc = precedingComments.Select(c => rgMacroDescription.Match(c)).FirstOrDefault(m => m.Success && m.Groups[1].Value != "i");

            string type = "o", text = name;

            if (desc != null && desc.Groups[2].Value == name)
            {
                type = desc.Groups[1].Value;
                text = desc.Groups[3].Value.Trim().TrimEnd('.') + $" ({name})";
            }

            PropertyEntry entry;

            switch (type)
            {
                case "q":
                case "e":
                    entry = new PropertyEntry.Boolean { ValueForTrue = "1", ValueForFalse = "0"};
                    break;
                case "o":
                    {
                        var enumValues = precedingComments
                            .Select(c => rgEnumValue.Match(c))
                            .Where(m => m.Success)
                            .Select(m => new PropertyEntry.Enumerated.Suggestion { InternalValue = m.Groups[1].Value, UserFriendlyName = m.Groups[2].Value })
                           .ToArray();

                        if (enumValues.Length > 0)
                            entry = new PropertyEntry.Enumerated { SuggestionList = enumValues };
                        else
                            entry = new PropertyEntry.String { };
                        break;
                    }
                case "s":
                    entry = new PropertyEntry.String { };
                    break;
                default:
                    return null;
            }


            entry.Name = text;
            entry.UniqueID = name;
            entry.Description = precedingComments.Select(c => rgMacroDescription.Match(c)).FirstOrDefault(m => m.Success && m.Groups[1].Value == "i")?.Groups[2].Value.Trim();

            return entry;
        }
    }
}
