using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace renesas_ra_bsp_generator
{
    class ConfigurationFileTranslator
    {
        struct LinePredicate
        {
            public string Prefix, Suffix;

            public override string ToString() => $"{Prefix}<...>{Suffix}";

            public bool IsMatch(string line) => line.StartsWith(Prefix) && line.EndsWith(Suffix);

            public ConfigurationFilePropertyClass ToPropertyClass(params string[] propertyNames)
            {
                return new ConfigurationFilePropertyClass
                {
                    NormalRegex = new SerializableRegularExpression($"^{Regex.Escape(Prefix)}(.*){Regex.Escape(Suffix)}$"),
                    NameIndex = 0,
                    ValueIndex = 1,
                    IndentIndex = 0,
                    Properties = propertyNames,
                };
            }
        }

        public static void TranslateConfigurationFiles(EmbeddedFramework fw, XmlDocument xml, string outputDir, BSPReportWriter report)
        {
            var rgPropertyReference = new Regex(@"\$\{([^${}]+)\}");
            List<ConfigurationFileTemplate> templates = new List<ConfigurationFileTemplate>();

            foreach (var cf in xml.DocumentElement.SelectElements("config"))
            {
                var fn = cf.GetStringAttribute("path") ?? throw new Exception("Undefined config file path");
                PropertyGroup pg = TranslateModuleProperties(fw, cf);

                List<string> configLines = new List<string>();

                var propertiesByID = pg.Properties.ToDictionary(p => p.UniqueID);
                Dictionary<string, LinePredicate> linePredicatesByProperty = new Dictionary<string, LinePredicate>();

                string baseIndent = null;

                foreach (var rawLine in cf.SelectSingleNode("content").InnerText.Split('\n'))
                {
                    var line = rawLine.TrimEnd();
                    if (line.Trim() == "" && configLines.Count == 0)
                        continue;

                    if (baseIndent == null)
                        baseIndent = line.Substring(0, line.Length - line.TrimStart(' ', '\t').Length);

                    if (line.StartsWith(baseIndent))
                        line = line.Substring(baseIndent.Length);

                    var matches = rgPropertyReference.Matches(line);
                    if (matches.Count > 1)
                    {
                        report.ReportRawError($"{fn}: Line references multiple config variables: {line}");
                        continue;
                    }

                    if (matches.Count == 0)
                    {
                        configLines.Add(line);
                    }
                    else
                    {
                        var m = matches[0];
                        var vn = m.Groups[1].Value;
                        string effectiveValue;

                        //lines.Add(new ConfigLine.VariableReference { Prefix = line.Substring(0, m.Index), Suffix = line.Substring(m.Index + m.Length), VariableName = vn });
                        if (propertiesByID.TryGetValue(vn, out var pe))
                        {
                            //This module directly defines the property referenced in this line
                            effectiveValue = pe.GetDefaultValue();
                        }
                        else if (vn.StartsWith("interface."))
                        {
                            //This is a special variable used to determine whether another module is referenced
                            effectiveValue = "RA_NOT_DEFINED";  //TODO: create a virtual property
                        }
                        else
                        {
                            effectiveValue = "RA_NOT_DEFINED";
                            report.ReportRawError($"{fn}: Unknown property name: {vn}");
                        }

                        var predicate = new LinePredicate { Prefix = line.Substring(0, m.Index), Suffix = line.Substring(m.Index + m.Length) };
                        linePredicatesByProperty[vn] = predicate;
                        configLines.Add($"{predicate.Prefix}{effectiveValue}{predicate.Suffix}");
                    }
                }

                foreach (var pr in linePredicatesByProperty)
                {
                    var matchingLines = configLines.Where(pr.Value.IsMatch).ToArray();
                    if (matchingLines.Length != 1)
                        report.ReportRawError($"Ambiguous config lines: {matchingLines.Length} lines in {Path.GetFileName(fn)} match the {pr.Key} template");
                }

                var relPath = $"config/{fw.ID}/{Path.GetFileName(fn)}";
                var fullPath = Path.Combine(outputDir, relPath);
                if (File.Exists(fullPath))
                    throw new Exception($"{fullPath} already exists");

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

                configLines.Insert(0, "#pragma once");
                configLines.Insert(1, $"//This is a generated configuration file for the '{fw.UserFriendlyName}' framework ({fw.ID})");
                configLines.Insert(2, "");
                File.WriteAllLines(fullPath, configLines);

                templates.Add(new ConfigurationFileTemplate
                {
                    SourcePath = "$$SYS:BSP_ROOT$$/" + relPath,
                    TargetFileName = Path.GetFileName(fn),
                    PropertyClasses = linePredicatesByProperty.Select(kv => kv.Value.ToPropertyClass(kv.Key)).ToArray(),
                    PropertyList = new PropertyList
                    {
                        PropertyGroups = new List<PropertyGroup> { pg }
                    }
                });
            }

            if (templates.Count > 0)
                fw.ConfigurationFileTemplates = templates.ToArray();
        }

        private static PropertyGroup TranslateModuleProperties(EmbeddedFramework fw, XmlElement cf)
        {
            var pg = new PropertyGroup { Name = fw.UserFriendlyName };
            foreach (var prop in cf.SelectElements("property"))
            {
                string id = prop.GetStringAttribute("id") ?? throw new Exception("Missing property ID");
                var name = prop.TryGetStringAttribute("display");
                var defaultValue = prop.TryGetStringAttribute("default");

                var options = prop.SelectElements("option").ToArray();
                PropertyEntry entry;
                if (options.Length > 0)
                {
                    entry = new PropertyEntry.Enumerated
                    {
                        DefaultEntryIndex = Enumerable.Range(0, options.Length).FirstOrDefault(i => options[i].GetStringAttribute("id") == defaultValue),
                        SuggestionList = options.Select(o =>
                        new PropertyEntry.Enumerated.Suggestion
                        {
                            InternalValue = o.GetAttribute("value") ?? throw new Exception("Missing option value"),
                            UserFriendlyName = o.GetAttribute("display") ?? throw new Exception("Missing option label"),
                        }).ToArray()
                    };
                }
                else
                {
                    entry = new PropertyEntry.String
                    {
                        DefaultValue = defaultValue,
                    };
                }

                entry.UniqueID = id;
                entry.Name = name;

                pg.Properties.Add(entry);
            }

            return pg;
        }
    }
}
