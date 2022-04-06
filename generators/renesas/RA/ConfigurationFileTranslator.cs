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
        class PropertyContext
        {
            public PropertyEntry Entry;
            public XmlElement OriginalElement;
        }

        readonly Dictionary<string, PropertyContext> _AllProperties = new Dictionary<string, PropertyContext>();

        public void TranslateConfigurationFiles(EmbeddedFramework fw, XmlDocument xml, BSPReportWriter report)
        {
            var rgPropertyReference = new Regex(@"\$\{([^${}]+)\}");
            var rgIncludeRA = new Regex("#include[ \t]+\"\\.\\.[./]+/(ra/.*)\"");
            List<GeneratedConfigurationFile> files = new List<GeneratedConfigurationFile>();
            List<PropertyGroup> propertyGroups = new List<PropertyGroup>();

            foreach (var cf in xml.DocumentElement.SelectElements("config"))
            {
                var fn = cf.GetStringAttribute("path") ?? throw new Exception("Undefined config file path");
                PropertyGroup pg = TranslateModuleProperties(fw, cf);

                List<string> configLines = new List<string>();

                var propertiesByID = pg.Properties.ToDictionary(p => p.UniqueID);

                string baseIndent = null;
                List<GeneratedConfigurationFile.Fragment> fragments = new List<GeneratedConfigurationFile.Fragment>();

                foreach (var rawLine in cf.SelectSingleNode("content").InnerText.Split('\n'))
                {
                    var line = rawLine.TrimEnd();
                    if (line.Trim() == "" && configLines.Count == 0)
                        continue;

                    if (baseIndent == null)
                        baseIndent = line.Substring(0, line.Length - line.TrimStart(' ', '\t').Length);

                    if (line.StartsWith(baseIndent))
                        line = line.Substring(baseIndent.Length);

                    if (line.StartsWith("#include"))
                    {
                        var m = rgIncludeRA.Match(line);
                        if (m.Success)
                        {
                            //Replace the self-relative path with a path relative to $$SYS:BSP_ROOT$$.
                            line = $"#include <{m.Groups[1]}>";
                        }
                    }

                    //Translate the ${var.name} references to $$var.name$$
                    var matches = rgPropertyReference.Matches(line);
                    foreach (var m in matches.OfType<Match>().OrderByDescending(m => m.Index))
                        line = line.Substring(0, m.Index) + $"$${m.Groups[1].Value}$$" + line.Substring(m.Index + m.Length);

                    foreach(var m in matches.OfType<Match>())
                    {
                        var vn = m.Groups[1].Value;

                        if (propertiesByID.TryGetValue(vn, out var pe))
                        {
                            //This module directly defines the property referenced in this line
                        }
                        else if (vn.StartsWith("interface."))
                        {
                            //This is a special variable used to determine whether another module is referenced
                        }
                        else
                        {
                            report.ReportRawError($"{fn}: Unknown property name: {vn}");
                        }
                    }

                    configLines.Add(line);
                }

                fragments.Add(new GeneratedConfigurationFile.Fragment.BasicFragment { Lines = configLines.ToArray() });

                files.Add(new GeneratedConfigurationFile
                {
                    RelativePath = "ra_cfg/" + fn,
                    Contents = fragments.ToArray(),
                    UndefinedVariableValue = "RA_NOT_DEFINED",
                });

                if (pg.Properties.Count > 0)
                    propertyGroups.Add(pg);
            }

            if (files.Count > 0)
                fw.GeneratedConfigurationFiles = files.ToArray();

            if (propertyGroups.Count > 0)
                fw.ConfigurableProperties = new PropertyList { PropertyGroups = propertyGroups };
        }

        PropertyGroup TranslateModuleProperties(EmbeddedFramework fw, XmlElement cf)
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

                _AllProperties[id] = new PropertyContext { Entry = entry, OriginalElement = prop };

                entry.UniqueID = id;
                entry.Name = name;

                pg.Properties.Add(entry);
            }

            string prefix = ComputeCommonPrefix(pg.Properties.Select(p => p.UniqueID ?? ""));
            if (!string.IsNullOrEmpty(prefix))
            {
                int idx = prefix.LastIndexOf('.');
                if (idx != -1)
                {
                    pg.UniqueID = prefix.Substring(0, idx + 1);
                    foreach (var prop in pg.Properties)
                        prop.UniqueID = prop.UniqueID.Substring(idx + 1);
                }
            }

            if (pg.UniqueID == null)
                pg.Name = null;

            return pg;
        }

        static string GetCommonPart(string x, string y)
        {
            int len = Math.Min(x.Length, y.Length);
            for (int i = 0; i < len; i++)
                if (x[i] != y[i])
                    return x.Substring(0, i);
            return x.Substring(0, len);
        }

        static string ComputeCommonPrefix(IEnumerable<string> strings)
        {
            string prefix = null;
            foreach(var str in strings)
            {
                if (prefix == null)
                    prefix = str;
                else
                    prefix = GetCommonPart(str, prefix);
            }

            return prefix;
        }

        public void TranslateModuleConfiguration(EmbeddedFramework fw, XmlDocument xml, BSPReportWriter report)
        {
            foreach(var prop in xml.DocumentElement.SelectElements("raBspConfiguration/config/property"))
            {
                var id = prop.GetStringAttribute("id");
                var value = prop.GetStringAttribute("value");

                var propertyCtx = _AllProperties[id];
                if (propertyCtx.Entry is PropertyEntry.Enumerated ep)
                {
                    string defaultValueKey = "_default." + id;
                    var option = propertyCtx.OriginalElement.SelectSingleNode($"option[@id='{value}']") as XmlElement ?? throw new Exception("Failed to locate the option element for " + value);

                    var optionValue = option.GetStringAttribute("value");
                    fw.AdditionalSystemVars = (fw.AdditionalSystemVars ?? new SysVarEntry[0]).Concat(new[] { new SysVarEntry { Key = defaultValueKey , Value = optionValue } }).ToArray();
                    ep.DefaultEntryValue = $"$${defaultValueKey}$$";
                }
                else
                    throw new Exception($"The '{id}' property set by the {fw.ID} framework is not an enumerated property");
            }
        }
    }
}
