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
            public List<PropertyEntry> Entries = new List<PropertyEntry>();
            public Dictionary<string, string> IDToValueMapping = new Dictionary<string, string>();
        }

        struct UnknownProperty
        {
            public string PropertyName, ReferringFile;
        }

        readonly Dictionary<string, PropertyContext> _AllProperties = new Dictionary<string, PropertyContext>();
        readonly HashSet<string> _FixedValues = new HashSet<string>();
        readonly List<UnknownProperty> _UnknownProperties = new List<UnknownProperty>();

        readonly EnumTranslator _EnumTranslator = new EnumTranslator();

        static Regex rgPropertyReference = new Regex(@"\$\{([^${}]+)\}");

        public void TranslateModuleDescriptionFiles(EmbeddedFramework fw, XmlDocument xml, BSPReportWriter report)
        {
            var rgIncludeRA = new Regex("#include[ \t]+\"\\.\\.[./]+/(ra/.*)\"");
            List<GeneratedConfigurationFile> files = new List<GeneratedConfigurationFile>();
            List<GeneratedConfigurationFile> mergeableFragments = new List<GeneratedConfigurationFile>();
            List<PropertyGroup> propertyGroups = new List<PropertyGroup>();
            List<SysVarEntry> fixedValues = new List<SysVarEntry>();

            foreach (var cf in xml.DocumentElement.SelectElements("config"))
            {
                var fn = cf.GetStringAttribute("path") ?? throw new Exception("Undefined config file path");
                PropertyGroup[] pgs = TranslateModuleProperties(fw, cf, fixedValues);

                List<string> configLines = new List<string>();
                Dictionary<string, string> fixedProperties = new Dictionary<string, string>();

                string baseIndent = null;
                List<GeneratedConfigurationFile.Fragment> fragments = new List<GeneratedConfigurationFile.Fragment>();

                if (!TranslateSpecialConfigFile(cf, fragments))
                {
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
                        {
                            var vn = m.Groups[1].Value;
                            var value = $"$${vn}$$";

                            if (fixedProperties.TryGetValue(vn, out var fixedValue))
                            {
                                //The value was provided right here
                                value = fixedValue;
                            }
                            else if (vn.StartsWith("interface."))
                            {
                                //This is a special variable used to determine whether another module is referenced
                            }
                            else
                            {
                                _UnknownProperties.Add(new UnknownProperty { PropertyName = vn, ReferringFile = fn });
                            }

                            line = line.Substring(0, m.Index) + value + line.Substring(m.Index + m.Length);
                        }

                        configLines.Add(line);
                    }

                    fragments.Add(new GeneratedConfigurationFile.Fragment.BasicFragment { Lines = configLines.ToArray() });
                }

                if (StringComparer.InvariantCultureIgnoreCase.Compare(fn, "ra_cfg/fsp_cfg/bsp/board_cfg.h") == 0)
                {
                    /* Device-level header files require board_cfg.h unconditionally.
                     * Hence, we need to generate it even if no board framework was referenced.
                     * We implement it by saving the board-specific data to a mergable fragment,
                       and generating the actual config file unconditionally (see common_data.xml)*/
                    mergeableFragments.Add(new GeneratedConfigurationFile
                    {
                        Name = "com.renesas.ra.board_cfg",
                        Contents = fragments.ToArray(),
                        UndefinedVariableValue = "RA_NOT_DEFINED",
                    });
                }
                else
                {
                    files.Add(new GeneratedConfigurationFile
                    {
                        Name = "ra_cfg/" + fn,
                        Contents = fragments.ToArray(),
                        UndefinedVariableValue = "RA_NOT_DEFINED",
                    });
                }

                foreach (var pg in pgs)
                    if (pg.Properties.Count > 0)
                        propertyGroups.Add(pg);
            }

            if (xml.DocumentElement.SelectElements("pin[@config='config.bsp.pin']").SingleOrDefault() is XmlElement pinConfigFile)
            {
                List<GeneratedConfigurationFile.Fragment> fragments = new List<GeneratedConfigurationFile.Fragment>();
                if (!TranslateSpecialConfigFile(pinConfigFile, fragments, "declarations"))
                    throw new Exception("Could not find the template for the pin configuration file");

                files.Add(new GeneratedConfigurationFile
                {
                    Name = "ra_gen/pin_data.c",
                    Contents = fragments.ToArray(),
                });
            }
            

            if (xml.DocumentElement.SelectElements("board").SingleOrDefault()?.GetStringAttribute("device") is string dev && !fw.ID.EndsWith(".custom"))
            {
                //This is a board framework referencing a specific device. Refine the MCU filter.
                fw.MCUFilterRegex = dev;
            }

            TranslateClockSettings(xml, files, propertyGroups);
            _EnumTranslator.ProcessEnumDefinitions(xml, fixedValues);
            TranslateModuleConfiguration(fw.UserFriendlyName, xml, files, mergeableFragments, propertyGroups, fixedValues, "0");

            if (files.Count > 0)
                fw.GeneratedConfigurationFiles = files.ToArray();

            if (mergeableFragments.Count > 0)
                fw.GeneratedConfigurationFragments = mergeableFragments.ToArray();

            if (propertyGroups.Count > 0)
                fw.ConfigurableProperties = new PropertyList { PropertyGroups = propertyGroups };

            if (fixedValues.Count > 0)
                fw.AdditionalSystemVars = (fw.AdditionalSystemVars ?? new SysVarEntry[0]).Concat(fixedValues).ToArray();
        }

        void TranslateClockSettings(XmlDocument xml, List<GeneratedConfigurationFile> files, List<PropertyGroup> propertyGroups)
        {
            var pg = new PropertyGroup { Name = "Clock Configuration" };
            List<string> configLines = new List<string>();
            foreach (var cf in xml.DocumentElement.SelectElements("bsp/clock"))
            {
                foreach (var node in cf.SelectElements("node"))
                {
                    string id = node.GetAttribute("id");
                    string defaultValue = node.GetAttribute("default");
                    var options = node.SelectElements("option").ToArray();

                    var macro = options[0].TryGetStringAttribute("macroName");
                    if (macro == null)
                    {
                        //This option is not reflected in the config file. Still register it so the board packages overriding it won't trigger errors.
                        RegisterProperty(node, id, null);
                        continue;
                    }

                    PropertyEntry entry;
                    if (options.Length == 1 && options[0].GetStringAttribute("id") == "_edit")
                    {
                        //The 'enumerated' property is necessary to allow overriding the default value from other frameworks
                        entry = new PropertyEntry.Enumerated
                        {
                            AllowFreeEntry = true,
                            SuggestionList = new[]
                            {
                                new PropertyEntry.Enumerated.Suggestion
                                {
                                    InternalValue = defaultValue,
                                }
                            }
                        };
                        configLines.Add($"#define {macro} " + options[0].GetStringAttribute("macroValue").Replace("${value}", $"$${id}$$"));
                    }
                    else
                    {
                        if (options.GroupBy(o => o.GetStringAttribute("macroName") ?? "").Count() != 1)
                            throw new Exception($"Multiple options for {id} use different macro names");

                        int defaultValueIndex = Enumerable.Range(0, options.Length).First(i => options[i].GetStringAttribute("id") == defaultValue);
                        entry = new PropertyEntry.Enumerated
                        {
                            Description = id,
                            DefaultEntryIndex = defaultValueIndex,
                            SuggestionList = options.Select(o => new PropertyEntry.Enumerated.Suggestion
                            {
                                InternalValue = o.GetStringAttribute("macroValue"),
                                UserFriendlyName = o.GetStringAttribute("display"),
                            }).ToArray()
                        };

                        configLines.Add($"#define {macro} $${id}$$");
                    }

                    entry.UniqueID = id;
                    entry.Name = macro;
                    entry.Description = id;
                    pg.Properties.Add(entry);
                    RegisterProperty(node, id, entry, true);
                }
            }

            if (pg.Properties.Count > 0)
            {
                pg.Properties.InsertRange(0, new[]{
                    new PropertyEntry.Boolean { UniqueID = "board.clock.is_secure",   Name = "BSP_CFG_CLOCKS_SECURE",   ValueForTrue = "(1)", ValueForFalse = "(0)" },
                    new PropertyEntry.Boolean { UniqueID = "board.clock.is_override", Name = "BSP_CFG_CLOCKS_OVERRIDE", ValueForTrue = "(1)", ValueForFalse = "(0)" }
                });

                configLines.InsertRange(0, new[]
                {
                    "#define BSP_CFG_CLOCKS_SECURE $$board.clock.is_secure$$",
                    "#define BSP_CFG_CLOCKS_OVERRIDE $$board.clock.is_override$$",
                });

                AssignCommonPropertyGroupPrefix(pg);

                propertyGroups.Add(pg);
                files.Add(new GeneratedConfigurationFile
                {
                    Name = "ra_gen/bsp_clock_cfg.h",
                    Contents = new GeneratedConfigurationFile.Fragment[]
                    {
                        new GeneratedConfigurationFile.Fragment.BasicFragment
                        {
                            Lines = configLines.ToArray(),
                        }
                    }
                });
            }
        }


        class LocalPropertyContext
        {
            public List<string> BooleanSubproperties = new List<string>();

            public string FinalExpression => string.Join("", BooleanSubproperties.Select(p => $"$${p}$$"));
        }

        void TranslateModuleConfiguration(string moduleName,
                                          XmlDocument xml,
                                          List<GeneratedConfigurationFile> files,
                                          List<GeneratedConfigurationFile> fragments,
                                          List<PropertyGroup> propertyGroups,
                                          List<SysVarEntry> fixedValues,
                                          string instanceName)
        {
            PropertyGroup pg = new PropertyGroup { Name = moduleName + " - Module Configuration" };
            Dictionary<string, LocalPropertyContext> localProperties = new Dictionary<string, LocalPropertyContext>();

            foreach (var module in xml.DocumentElement.SelectElements("module"))
            {
                ModuleFlags flags = ModuleFlags.None;
                if (module.GetAttribute("common") == "1")
                    flags |= ModuleFlags.IsCommon;

                foreach (var prop in module.SelectElements("property"))
                {
                    var id = prop.GetStringAttribute("id");
                    if (prop.TryGetStringAttribute("bitmapPrefix") is string pfx)
                    {
                        //This is a special bitfield property that gets translated into multiple VisualGDB-level properties
                        if (prop.TryGetStringAttribute("default") is string def && def != "0U")
                            throw new Exception("Default values for bitfield properties are not supported");

                        List<string> allOptions = new List<string>();

                        var propName = prop.GetStringAttribute("display");

                        foreach (var option in prop.SelectElements("option"))
                        {
                            var oid = option.GetStringAttribute("id");
                            var ov = option.GetStringAttribute("value");
                            var od = option.GetStringAttribute("display");

                            allOptions.Add(oid);
                            pg.Properties.Add(new PropertyEntry.Boolean
                            {
                                UniqueID = oid,
                                Name = $"{propName} - {od}",
                                ValueForTrue = $"{pfx}{ov} | ",
                                ValueForFalse = ""
                            });
                        }

                        localProperties[id] = new LocalPropertyContext { BooleanSubproperties = allOptions };
                    }
                    else
                    {
                        localProperties[id] = null;
                        TranslateSingleProperty(prop, fixedValues, pg.Properties, PropertyTranslationFlags.None, instanceName);
                    }
                }

                TranslateModuleConfigurationFragment(module, "header", fragments, localProperties, moduleName, flags, instanceName);
                TranslateModuleConfigurationFragment(module, "includes", fragments, localProperties, moduleName, flags, instanceName);
                TranslateModuleConfigurationFragment(module, "declarations", fragments, localProperties, moduleName, flags, instanceName);
                TranslateModuleConfigurationFragment(module, "macros", fragments, localProperties, moduleName, flags, instanceName);
            }

            AssignCommonPropertyGroupPrefix(pg);

            if (pg.Properties.Count > 0)
            {
                propertyGroups.Add(pg);
            }
        }

        [Flags]
        enum ModuleFlags
        {
            None,
            IsCommon = 1,
        }

        void TranslateModuleConfigurationFragment(XmlElement module,
                                                  string type,
                                                  List<GeneratedConfigurationFile> files,
                                                  Dictionary<string, LocalPropertyContext> localProperties,
                                                  string referringFile,
                                                  ModuleFlags flags,
                                                  string instanceName)
        {
            if (module.SelectSingleNode(type) is XmlElement e)
            {
                List<string> configLines = new List<string>();
                string baseIndent = null;
                foreach (var rawLine in e.InnerText.Split('\n'))
                {
                    var line = rawLine.TrimEnd();
                    line = SubstituteInstanceName(line, instanceName);

                    if (line.Trim() == "" && configLines.Count == 0)
                        continue;

                    if (baseIndent == null)
                        baseIndent = line.Substring(0, line.Length - line.TrimStart(' ', '\t').Length);

                    if (line.StartsWith(baseIndent))
                        line = line.Substring(baseIndent.Length);

                    //Translate the ${var.name} references to $$var.name$$
                    var matches = rgPropertyReference.Matches(line);

                    foreach (var m in matches.OfType<Match>().OrderByDescending(m => m.Index))
                    {
                        var vn = m.Groups[1].Value;
                        int idx = vn.IndexOf("::");
                        if (idx != -1)
                            vn = vn.Substring(idx + 2);

                        var value = $"$${vn}$$";

                        if (!localProperties.TryGetValue(vn, out var pctx))
                            _UnknownProperties.Add(new UnknownProperty { PropertyName = vn, ReferringFile = referringFile });
                        else if (pctx != null)
                            value = pctx.FinalExpression;

                        line = line.Substring(0, m.Index) + value + line.Substring(m.Index + m.Length);
                    }

                    configLines.Add(line);
                }

                string prefix;
                if ((flags & ModuleFlags.IsCommon) != ModuleFlags.None)
                    prefix = CommonDataSnippetPrefix;
                else
                    prefix = HALDataSnippetPrefix;

                files.Add(new GeneratedConfigurationFile
                {
                    Name = prefix + type,
                    Contents = new GeneratedConfigurationFile.Fragment[] { new GeneratedConfigurationFile.Fragment.BasicFragment { Lines = configLines.ToArray() } }
                });
            }
        }

        public const string HALDataSnippetPrefix = "com.renesas.ra.snippet.hal.";
        public const string CommonDataSnippetPrefix = "com.renesas.ra.snippet.common.";

        private bool TranslateSpecialConfigFile(XmlElement cf, List<GeneratedConfigurationFile.Fragment> fragments, string contentNodeName = "content")
        {
            var id = cf.TryGetStringAttribute("id");
            var expectedTemplate = $@"..\..\Rules\KnownTemplates\{id}.txt";
            if (File.Exists(expectedTemplate))
            {
                var text = cf.SelectSingleNode(contentNodeName).InnerText;
                if (text != File.ReadAllText(expectedTemplate))
                    throw new Exception("Unexpected special template. Please update the logic below");

                var fragFile = Path.ChangeExtension(expectedTemplate, ".xml");
                fragments.AddRange(XmlTools.LoadObject<GeneratedConfigurationFile.Fragment[]>(fragFile));
                return true;
            }

            return false;
        }

        [Flags]
        enum PropertyTranslationFlags
        {
            None = 0,
            ComputeDefaultValuesForFlashID = 1,
            TreatZeroOptionsAsFixedValues = 2,
        }

        Regex rgIDCodeProperty = new Regex("config.bsp.common.id([1-4]|_fixed)");

        static string SubstituteInstanceName(string str, string inst) => inst == null ? str : str?.Replace("${_instance}", inst);

        PropertyEntry TranslateSingleProperty(XmlElement prop,
                                              List<SysVarEntry> fixedValues,
                                              List<PropertyEntry> properties,
                                              PropertyTranslationFlags flags = PropertyTranslationFlags.None,
                                              string instanceName = null)
        {
            string id = prop.GetStringAttribute("id") ?? throw new Exception("Missing property ID");
            var name = prop.TryGetStringAttribute("display");
            var defaultValue = prop.TryGetStringAttribute("default");

            if (defaultValue == null && (flags & PropertyTranslationFlags.ComputeDefaultValuesForFlashID) != PropertyTranslationFlags.None)
            {
                var m = rgIDCodeProperty.Match(id);
                if (m.Success)
                {
                    //The original BSP contains non-trivial JavaScript logic for computing the individual IDs (including the default one).
                    //Since VisualGDB does not have a JavaScript interpreter, we simply specify the default value explicitly, and allow the user to override them.
                    if (m.Groups[1].Length > 1)
                        defaultValue = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";
                    else
                        defaultValue = "FFFFFFFF";
                }
            }

            var options = prop.SelectElements("option").ToArray();
            PropertyEntry entry;
            if (prop.SelectSingleNode("select[@enum]/@enum")?.InnerText is string eid && eid != "")
            {
                entry = _EnumTranslator.CreatePendingEntryForEnum(eid);
            }
            else if (options.Length == 1 && options[0].GetStringAttribute("id") == defaultValue && fixedValues != null)
            {
                var val = options[0].GetStringAttribute("value");
                fixedValues.Add(new SysVarEntry { Key = id, Value = SubstituteInstanceName(val, instanceName) });
                _FixedValues.Add(id);
                return null;
            }
            else if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(defaultValue) && options.Length == 0
                && (flags & PropertyTranslationFlags.TreatZeroOptionsAsFixedValues) != PropertyTranslationFlags.None)
            {
                fixedValues.Add(new SysVarEntry { Key = id, Value = SubstituteInstanceName(defaultValue, instanceName) });
                _FixedValues.Add(id);
                return null;
            }
            else if (options.Length > 0)
            {
                entry = new PropertyEntry.Enumerated
                {
                    DefaultEntryIndex = Enumerable.Range(0, options.Length).FirstOrDefault(i => options[i].GetStringAttribute("id") == defaultValue),
                    SuggestionList = options.Select(o =>
                    new PropertyEntry.Enumerated.Suggestion
                    {
                        InternalValue = SubstituteInstanceName(o.GetAttribute("value") ?? throw new Exception("Missing option value"), instanceName),
                        UserFriendlyName = SubstituteInstanceName(o.GetAttribute("display") ?? throw new Exception("Missing option label"), instanceName),
                    }).ToArray()
                };
            }
            else
            {
                entry = new PropertyEntry.String
                {
                    DefaultValue = SubstituteInstanceName(defaultValue, instanceName),
                };
            }

            RegisterProperty(prop, id, entry);

            entry.UniqueID = id;
            if (string.IsNullOrEmpty(name))
                entry.Name = id.Split('.').LastOrDefault();
            else
                entry.Name = name;

            properties?.Add(entry);
            return entry;
        }

        PropertyGroup[] TranslateModuleProperties(EmbeddedFramework fw, XmlElement cf, List<SysVarEntry> fixedValues)
        {
            var pg = new PropertyGroup { Name = fw.UserFriendlyName };
            foreach (var prop in cf.SelectElements("property"))
            {
                TranslateSingleProperty(prop, fixedValues, pg.Properties, PropertyTranslationFlags.ComputeDefaultValuesForFlashID | PropertyTranslationFlags.TreatZeroOptionsAsFixedValues);
            }

            AssignCommonPropertyGroupPrefix(pg);

            if (pg.UniqueID == null)
                pg.Name = null;

            return SplitPropertyGroup(pg);
        }

        private static void AssignCommonPropertyGroupPrefix(PropertyGroup pg)
        {
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
        }

        private void RegisterProperty(XmlElement prop, string id, PropertyEntry entry, bool isClockEntry = false)
        {
            if (!_AllProperties.TryGetValue(id, out var ctx))
                _AllProperties[id] = ctx = new PropertyContext();

            if (entry != null)
                ctx.Entries.Add(entry);

            foreach (var option in prop.SelectElements("option"))
            {
                var valID = option.GetStringAttribute("id");
                string value;
                if (isClockEntry)
                {
                    if (valID == "_edit")
                        continue;

                    value = option.GetStringAttribute("macroValue");
                }
                else
                    value = option.GetAttribute("value");

                ctx.IDToValueMapping[valID] = value;
            }
        }

        /* Splits a single property group with properties like, e.g.:
         *   Property 1
         *   Subgroup | Property 2
         *   Subgroup | Property 3
         *  into multiple property groups, e.g.:
         *  [Group Name]
         *      Property 1
         *  [Group Name] - Subroup
         *      Property 2
         *      Property 3
         */
        PropertyGroup[] SplitPropertyGroup(PropertyGroup pg)
        {
            if (pg.Properties.Count == 0)
                return new PropertyGroup[0];

            var propertiesByPrefix = pg.Properties.GroupBy(p =>
            {
                int idx = p.Name.LastIndexOf('|');
                if (idx == -1)
                    return "";
                return p.Name.Substring(0, idx);
            }).ToDictionary(g => g.Key, g => g.ToArray());

            if (propertiesByPrefix.Count == 1)
                return new[] { pg };

            List<PropertyGroup> result = new List<PropertyGroup>();
            foreach (var g in propertiesByPrefix)
            {
                if (g.Key == "")
                    continue;

                var prefix = ComputeCommonPrefix(g.Value.Select(p => p.UniqueID));
                if (prefix != "")
                {
                    var newGroup = new PropertyGroup
                    {
                        Name = pg.Name + " - " + g.Key.Replace("|", " - "),
                        UniqueID = pg.UniqueID + prefix,
                        Properties = g.Value.ToList()
                    };

                    foreach (var p in newGroup.Properties)
                    {
                        p.Name = p.Name.Substring(g.Key.Length + 1);
                        p.UniqueID = p.UniqueID.Substring(prefix.Length);
                    }

                    result.Add(newGroup);
                }
            }

            var movedProperties = result.SelectMany(g => g.Properties).ToHashSet();
            pg.Properties.RemoveAll(movedProperties.Contains);
            if (pg.Properties.Count > 0)
                result.Insert(0, pg);

            return result.ToArray();
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
            foreach (var str in strings)
            {
                if (prefix == null)
                    prefix = str;
                else
                    prefix = GetCommonPart(str, prefix);
            }

            return prefix;
        }

        public void ProcessModuleConfiguration(EmbeddedFramework fw, XmlDocument xml, BSPReportWriter report)
        {
            _PendingConfigurationTranslations.Add(new PendingConfigurationTranslation { Framework = fw, Xml = xml });
        }


        struct PendingConfigurationTranslation
        {
            public EmbeddedFramework Framework;
            public XmlDocument Xml;
        }

        List<PendingConfigurationTranslation> _PendingConfigurationTranslations = new List<PendingConfigurationTranslation>();

        public void GenerateFrameworkDependentDefaultValues(BSPReportWriter report, Dictionary<string, PinConfigurationTranslator.DevicePinout> devicePinouts)
        {
            foreach (var cfg in _PendingConfigurationTranslations)
            {
                List<SysVarEntry> vars = new List<SysVarEntry>();
                List<GeneratedConfigurationFile> fragments = new List<GeneratedConfigurationFile>();
                TranslateDefaultValueOverrides(report, cfg, vars);
                var pinout = devicePinouts[cfg.Framework.MCUFilterRegex];   //If this doesn't yield any matches, check the assignment logic in TranslateModuleDescriptionFiles()

                PinConfigurationTranslator.TranslatePinConfigurationsFromBoardComponent(cfg.Xml, vars, fragments, pinout, report);

                if (vars.Count > 0)
                    cfg.Framework.AdditionalSystemVars = (cfg.Framework.AdditionalSystemVars ?? new SysVarEntry[0]).Concat(vars).ToArray();
                if (fragments.Count > 0)
                    cfg.Framework.GeneratedConfigurationFragments = (cfg.Framework.GeneratedConfigurationFragments ?? new GeneratedConfigurationFile[0]).Concat(fragments).ToArray();
            }

            foreach (var rec in _UnknownProperties)
            {
                if (!_AllProperties.ContainsKey(rec.PropertyName) && !_FixedValues.Contains(rec.PropertyName))
                {
                    report.ReportMergeableError("Unknown property name:", rec.PropertyName);
                }
            }

            _EnumTranslator.ExpandEnumReferences(report);
        }

        void TranslateDefaultValueOverrides(BSPReportWriter report, PendingConfigurationTranslation cfg, List<SysVarEntry> vars)
        {
            string frameworkID = cfg.Framework.ID;
            foreach (var prop in cfg.Xml.DocumentElement.SelectElements("raBspConfiguration/config/property"))
            {
                var id = prop.GetStringAttribute("id");
                var value = prop.GetStringAttribute("value");

                OverrideDefaultPropertyValue(report, vars, frameworkID, id, value);
            }

            foreach (var prop in cfg.Xml.DocumentElement.SelectElements("raClockConfiguration/node"))
            {
                var id = prop.GetStringAttribute("id");
                var value = prop.GetStringAttribute("option");
                if (value == "_edit")
                    value = prop.GetStringAttribute("mul");

                OverrideDefaultPropertyValue(report, vars, frameworkID, id, value, true);
            }
        }

        /* Allows frameworks to override default values of properties in other frameworks. This method creates a temporary variable, adjusts the original
         * property to use it for the computation of the default value, and updates the new framework to set the value of this variable. */
        private void OverrideDefaultPropertyValue(BSPReportWriter report, List<SysVarEntry> vars, string frameworkID, string id, string value, bool isClockProperty = false)
        {
            var propertyCtx = _AllProperties[id];

            foreach (var e in propertyCtx.Entries)
            {
                SysVarEntry entry = default;

                if (e is PropertyEntry.Enumerated ep)
                {
                    string defaultValueKey = "_default." + id;
                    string optionValue;

                    if (ep.AllowFreeEntry)
                    {
                        if (ep.SuggestionList.FirstOrDefault(s => s.InternalValue == value) == null)
                            ep.SuggestionList = ep.SuggestionList.Concat(new[] { new PropertyEntry.Enumerated.Suggestion { InternalValue = value } }).ToArray();
                        optionValue = value;
                    }
                    else
                        optionValue = propertyCtx.IDToValueMapping[value];

                    var newVarEntry = new SysVarEntry { Key = defaultValueKey, Value = optionValue };
                    entry ??= newVarEntry;

                    if (entry.Value != newVarEntry.Value)
                        throw new Exception("Inconsistent default values for " + id);

                    ep.DefaultEntryValue = $"$${defaultValueKey}$$";
                }
                else
                    report.ReportRawError($"The '{id}' property set by the {frameworkID} framework is not an enumerated property");

                if (entry != null)
                    vars.Add(entry);
            }
        }
    }
}
