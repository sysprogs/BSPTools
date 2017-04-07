using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace mbed
{
    public class ParsedTargetList
    {
        public struct RawConfigurationVariable
        {
            public string ID;
            public string DefaultValue;
            public string Description;
            public string MacroName;
            public string IsBool;

            public bool IsFixed => string.IsNullOrEmpty(Description);

            public override string ToString()
            {
                return ID;
            }

            static Regex rgOneOf = new Regex("One of ([^\\.]+)\\.");

            static bool IsValidIdentifierChar(char ch)
            {
                if ((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || ch == '_')
                    return true;
                return false;
            }

            static bool IsValidIdentifier(string str)
            {
                foreach (var ch in str)
                    if (!IsValidIdentifierChar(ch))
                        return false;
                return true;
            }

            public PropertyEntry ToPropertyEntry()
            {
                if (IsBool == "True")
                {
                    if (DefaultValue != "1" && DefaultValue != "0")
                        throw new Exception("Invalid default value for a boolean property");
                    return new PropertyEntry.Boolean { UniqueID = ID, Name = ID, DefaultValue = DefaultValue == "1", Description = Description, ValueForTrue = "1", ValueForFalse = "0" };
                }

                if (!string.IsNullOrEmpty(Description))
                {
                    var m = rgOneOf.Match(Description);
                    if (m.Success)
                    {
                        var options = m.Groups[1].Value.Split(',').Select(v => v.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                        int defaultIdx = Array.IndexOf(options, DefaultValue);
                        if (defaultIdx < 0)
                            throw new Exception("Default value is not a part of suggestion list");
                        if (options.FirstOrDefault(s=>!IsValidIdentifier(s)) == null)
                        {
                            //All options are valid identifiers
                            return new PropertyEntry.Enumerated
                            {
                                UniqueID = ID,
                                Name = ID,
                                SuggestionList = options.Select(s=>new PropertyEntry.Enumerated.Suggestion { InternalValue = s}).ToArray(),
                                DefaultEntryIndex = defaultIdx
                            };
                        }
                    }
                }

                return new PropertyEntry.String { UniqueID = ID, Name = ID, DefaultValue = DefaultValue, Description = Description };
            }
        }

        public class BuildConfiguration
        {
            public string[] SourceFiles;
            public string[] HeaderFiles;
            public string[] IncludeDirectories;
            public string[] NormalPreprocessorMacros;
            public string[] HexFiles;
            public string LinkerScript;
            public RawConfigurationVariable[] ConfigurationVariables;

            public string[] EffectivePreprocessorMacros
            {
                get
                {
                    return NormalPreprocessorMacros
                        .Concat(ConfigurationVariables.Where(c => c.IsFixed).Select(c => $"{c.MacroName}={c.DefaultValue}"))
                        .Concat(ConfigurationVariables.Where(c => !c.IsFixed).Select(c => $"{c.MacroName}=$$com.sysprogs.mbed.{c.ID}$$"))
                        .ToArray();
                }
            }

            public PropertyEntry[] EffectiveConfigurableProperties => ConfigurationVariables.Where(c => !c.IsFixed).Select(c => c.ToPropertyEntry()).ToArray();

            static string[] SubtractOrThrow(string[] left, string[] right, bool throwIfBaseValuesAreNotPresent, string hint)
            {
                if (throwIfBaseValuesAreNotPresent && right.Except(left).Count() > 0)
                    throw new Exception($"Conditional {hint} are not derived from base settings");
                return left.Except(right).ToArray();
            }

            public BuildConfiguration Subtract(BuildConfiguration baseConfiguration, string hint, bool throwIfBaseValuesAreNotPresent)
            {
                BuildConfiguration result = new BuildConfiguration
                {
                    SourceFiles = SubtractOrThrow(SourceFiles, baseConfiguration.SourceFiles, false, "Source files for " + hint),
                    HeaderFiles = SubtractOrThrow(HeaderFiles, baseConfiguration.HeaderFiles, false, "Header files for " + hint),
                    IncludeDirectories = SubtractOrThrow(IncludeDirectories, baseConfiguration.IncludeDirectories, false, "Include directories for " + hint),
                    NormalPreprocessorMacros = SubtractOrThrow(NormalPreprocessorMacros, baseConfiguration.NormalPreprocessorMacros, throwIfBaseValuesAreNotPresent, "Preprocessor macros for " + hint),
                };

                var removedKeys = baseConfiguration.ConfigurationVariables.Select(v => v.ID).Except(ConfigurationVariables.Select(v => v.ID));
                if (removedKeys.Count() > 0)
                    throw new Exception($"Configuration variables for {hint} are not derived from base settings");

                var originalKeys = baseConfiguration.ConfigurationVariables.ToDictionary(v => v.ID, v => v);
                result.ConfigurationVariables = ConfigurationVariables.Where(v => !originalKeys.ContainsKey(v.ID)).ToArray();

                return result;
            }
        }

        public class CfgEntryComparerByID : IEqualityComparer<RawConfigurationVariable>
        {
            public bool Equals(RawConfigurationVariable x, RawConfigurationVariable y)
            {
                return x.ID == y.ID;
            }

            public int GetHashCode(RawConfigurationVariable obj)
            {
                return obj.ID.GetHashCode();
            }
        }

        public class DerivedConfiguration
        {
            public string Library;
            public string LibraryName;
            public string Feature;

            public string CanonicalKey => Library != null ? "L:" + Library : "F:" + Feature;

            public BuildConfiguration Configuration;

            public BuildConfiguration[] ConfigurationsToMerge;

            public void MergeScatteredConfigurations()
            {
                if (Configuration != null || ConfigurationsToMerge == null)
                    return;
                if (ConfigurationsToMerge.Length < 2)
                    Configuration = ConfigurationsToMerge[0];
                else
                    Configuration = new BuildConfiguration
                    {
                        SourceFiles = Program.Union(ConfigurationsToMerge.Select(c => c.SourceFiles)),
                        HeaderFiles = Program.Union(ConfigurationsToMerge.Select(c => c.HeaderFiles)),
                        NormalPreprocessorMacros = Program.Union(ConfigurationsToMerge.Select(c => c.NormalPreprocessorMacros)),
                        IncludeDirectories = Program.Union(ConfigurationsToMerge.Select(c => c.IncludeDirectories)),
                        ConfigurationVariables = Program.Union(ConfigurationsToMerge.Select(c => c.ConfigurationVariables), new CfgEntryComparerByID()),
                    };
            }
        }

        public class Target
        {
            public string ID;
            public string Features; //Separated by ';'
            public BuildConfiguration BaseConfiguration;
            public DerivedConfiguration[] DerivedConfigurations;
            public string CFLAGS;

            public override string ToString()
            {
                return ID;
            }
        }

        public Target[] Targets;
    }
}
