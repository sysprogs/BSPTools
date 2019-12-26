using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static BSPEngine.ConfigurationFixDatabase;

namespace BSPGenerationTools
{
    class ReverseFileConditionMatcher
    {
        public class RequestedConfiguration
        {
            public FrameworkReference Framework;
            public SysVarEntry[] Configuration;
        }

        public struct FreeFormMacro
        {
            public Regex Regex;
            public string VariableName;
            public FrameworkReference Framework;
        }

        readonly Dictionary<string, RequestedConfiguration> _ConfigurationByFile;
        readonly Dictionary<string, RequestedConfiguration> _ConfigurationByIncludeDir;
        readonly Dictionary<string, RequestedConfiguration> _ConfigurationByMacro;

        readonly List<FreeFormMacro> _FreeFormMacros = new List<FreeFormMacro>();

        public ReverseFileConditionMatcher(ReverseConditionTable table)
        {
            var fileDict = PropertyDictionary2.ReadPropertyDictionary(table.RenamedFileTable);

            _ConfigurationByFile = TranslateObjectList(table, table.FileTable, fileDict);
            _ConfigurationByMacro = TranslateObjectList(table, table.MacroTable);
            _ConfigurationByIncludeDir = TranslateObjectList(table, table.IncludeDirectoryTable);

            foreach (var macro in table.FreeFormMacros)
            {
                _FreeFormMacros.Add(new FreeFormMacro
                {
                    Framework = table.Frameworks[macro.FrameworkIndex],
                    Regex = new Regex(macro.Regex),
                    VariableName = macro.Value
                });
            }
        }

        static Dictionary<string, RequestedConfiguration> TranslateObjectList(ReverseConditionTable table, IEnumerable<ObjectEntry> list, Dictionary<string, string> optionalDictionary = null)
        {
            Dictionary<string, RequestedConfiguration> result = new Dictionary<string, RequestedConfiguration>();
            foreach (var entry in list)
            {
                var cfg = new RequestedConfiguration { Framework = table.Frameworks[entry.OneBasedFrameworkIndex - 1] };
                if (entry.OneBasedConfigurationFragmentIndex != 0)
                    cfg.Configuration = table.ConditionTable[entry.OneBasedConfigurationFragmentIndex - 1].RequestedConfiguration;

                string key = entry.ObjectName;
                if (optionalDictionary != null && optionalDictionary.TryGetValue(key, out var val))
                    key = val;

                result[key] = cfg;
            }

            return result;
        }

        [Flags]
        enum MatchingFlags
        {
            None = 0,
            FoundItems = 1,
            FoundInconsistencies = 2,
        }

        enum ObjectMatchingMode
        {
            MatchAndUpdateConfiguration = 1,
            MatchIfConfigurationMatches = 2,
        }

        public void DetectKnownFrameworksAndFilterPaths(ref string[] sources, ref string[] headers, ref string[] includeDirs, ref string[] preprocesorMacros, ref VendorSampleRelocator.ParsedDependency[] dependencies, HashSet<string> frameworks, Dictionary<string, string> requestedConfiguration)
        {
            var sourceList = sources?.ToList() ?? new List<string>();
            var headerList = headers?.ToList() ?? new List<string>();
            var includeList = includeDirs?.ToList() ?? new List<string>();
            var macroList = preprocesorMacros?.ToList() ?? new List<string>();

            var dependencyList = dependencies.Select(d => d.MappedFile).ToList();

            MatchingFlags flags = MatchingFlags.None;

            var frameworkObjects = new HashSet<FrameworkReference>();

            LocateAndRemoveMatchingEntries(sourceList, _ConfigurationByFile, requestedConfiguration, frameworkObjects, ref flags);
            LocateAndRemoveMatchingEntries(macroList, _ConfigurationByMacro, requestedConfiguration, frameworkObjects, ref flags);

            LocateAndRemoveMatchingEntries(headerList, _ConfigurationByFile, requestedConfiguration, frameworkObjects, ref flags, ObjectMatchingMode.MatchIfConfigurationMatches);
            LocateAndRemoveMatchingEntries(dependencyList, _ConfigurationByFile, requestedConfiguration, frameworkObjects, ref flags, ObjectMatchingMode.MatchIfConfigurationMatches);

            LocateAndRemoveMatchingEntries(includeList, _ConfigurationByIncludeDir, requestedConfiguration, frameworkObjects, ref flags, ObjectMatchingMode.MatchIfConfigurationMatches);

            for (int i = 0; i < macroList.Count; i++)
            {
                bool match = false;
                foreach (var rule in _FreeFormMacros)
                {
                    var m = rule.Regex.Match(macroList[i]);
                    if (m.Success)
                    {
                        if (requestedConfiguration.TryGetValue(rule.VariableName, out string oldValue) && oldValue != m.Groups[1].Value)
                        {
                            flags |= MatchingFlags.FoundInconsistencies;
                            continue;
                        }

                        requestedConfiguration[rule.VariableName] = m.Groups[1].Value;
                        match = true;
                        break;
                    }
                }

                if (match)
                    macroList.RemoveAt(i--);
            }

            if (((flags & MatchingFlags.FoundItems) != MatchingFlags.None) &&
                ((flags & MatchingFlags.FoundInconsistencies) == MatchingFlags.None))
            {
                //No inconsistencies found. We can attach the discovered frameworks to the sample.
                sources = sourceList.ToArray();
                headers = headerList.ToArray();
                preprocesorMacros = macroList.ToArray();
                includeDirs = includeList.ToArray();

                foreach (var fwObj in frameworkObjects)
                {
                    frameworks.Add(fwObj.ID);
                    foreach (var kv in fwObj.MinimalConfiguration ?? new SysVarEntry[0])
                    {
                        //Unless a configuration value was explicitly detected from the macros/files, set it to 'disabled' (that might be different from the GUI default).
                        //This ensures that each vendor sample excludes the conditional items that were present in the original vendor sample, even if they are enabled by default.
                        if (!requestedConfiguration.ContainsKey(kv.Key))
                            requestedConfiguration[kv.Key] = kv.Value;
                    }
                }

                var dependencySet = new HashSet<string>();
                foreach (var dep in dependencyList)
                    dependencySet.Add(dep);

                dependencies = dependencies.Where(d => dependencySet.Contains(d.MappedFile)).ToArray();
            }
        }

        static void LocateAndRemoveMatchingEntries(List<string> items,
            Dictionary<string, RequestedConfiguration> rules,
            Dictionary<string, string> requestedConfiguration,
            HashSet<FrameworkReference> frameworks,
            ref MatchingFlags flags,
            ObjectMatchingMode mode = ObjectMatchingMode.MatchAndUpdateConfiguration)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (!rules.TryGetValue(items[i], out var rule))
                    continue;

                flags |= MatchingFlags.FoundItems;

                if (rule.Framework != null)
                {
                    if (mode == ObjectMatchingMode.MatchIfConfigurationMatches)
                    {
                        //We are matching the secondary objects (e.g. headers). 
                        //If the framework (and configuration) providing them is already pulled, just remove the file from the list.
                        //If not, don't remove it and don't pull the framework.
                        if (!frameworks.Contains(rule.Framework))
                            continue;
                    }
                    else
                        frameworks.Add(rule.Framework);
                }

                bool skip = false;

                foreach (var entry in rule.Configuration ?? new SysVarEntry[0])
                {
                    if (mode == ObjectMatchingMode.MatchIfConfigurationMatches)
                    {
                        if (!requestedConfiguration.TryGetValue(entry.Key, out string oldValue) || oldValue != entry.Value)
                        {
                            //This is a secondary object (e.g. header file) and it won't be normally pulled by the current configuration.
                            //Keep an explicit reference to it.
                            skip = true;
                        }
                    }
                    else
                    {
                        if (requestedConfiguration.TryGetValue(entry.Key, out string oldValue) && oldValue != entry.Value)
                        {
                            flags |= MatchingFlags.FoundInconsistencies;
                            continue;
                        }

                        requestedConfiguration[entry.Key] = entry.Value;
                    }
                }

                if (!skip)
                    items.RemoveAt(i--);
            }
        }
    }
}
