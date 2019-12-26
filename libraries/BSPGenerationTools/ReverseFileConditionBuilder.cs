using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BSPGenerationTools
{
    public class ReverseFileConditionBuilder
    {
        public readonly Handle RootHandle;

        public ReverseFileConditionWarning Warnings { get; private set; }

        public void FlagIncomplete(ReverseFileConditionWarning warning)
        {
            Warnings |= warning;
        }

        public ReverseFileConditionBuilder()
        {
            RootHandle = new Handle(this, null);
        }

        public class ConditionHandle
        {
            private ReverseFileConditionBuilder _Builder;
            private readonly Handle _Handle;
            private SysVarEntry[] _RequiredConfiguration;

            public ConditionHandle(ReverseFileConditionBuilder builder, Handle handle, SysVarEntry[] requiredConfiguration)
            {
                _Builder = builder;
                _Handle = handle;
                _RequiredConfiguration = requiredConfiguration;
            }

            public void AttachFile(string file) => _Handle.AttachFile(file, this);

            public ReverseConditionTable.Condition ToConditionRecord() => new ReverseConditionTable.Condition { RequestedConfiguration = _RequiredConfiguration };
        }

        public class Handle
        {
            private ReverseFileConditionBuilder _Builder;
            public readonly string FrameworkID;

            internal Dictionary<string, ConditionHandle> ConditionsPerFile = new Dictionary<string, ConditionHandle>();
            internal Dictionary<string, ConditionHandle> ConditionsPerMacro = new Dictionary<string, ConditionHandle>();
            internal Dictionary<string, string> FreeformMacros = new Dictionary<string, string>();
            internal Dictionary<string, string> MinimalConfiguration = new Dictionary<string, string>();
            internal List<string> IncludeDirs = new List<string>();

            public Handle(ReverseFileConditionBuilder reverseFileConditionBuilder, string id)
            {
                _Builder = reverseFileConditionBuilder;
                FrameworkID = id;
            }

            public void FlagIncomplete(ReverseFileConditionWarning warning) => _Builder.FlagIncomplete(warning);

            public ConditionHandle CreateSimpleCondition(string variable, string value)
            {
                var kv = new SysVarEntry { Key = variable, Value = value };
                return new ConditionHandle(_Builder, this, new[] { kv });
            }

            public void AttachFile(string encodedPath, ConditionHandle conditionHandle = null)
            {
                if (ConditionsPerFile.ContainsKey(encodedPath))
                    _Builder.FlagIncomplete(ReverseFileConditionWarning.MultipleConditionsPerFile);
                ConditionsPerFile[encodedPath] = conditionHandle;
            }

            public void AttachPreprocessorMacro(string macro, ConditionHandle conditionHandle = null)
            {
                if (ConditionsPerMacro.ContainsKey(macro))
                    _Builder.FlagIncomplete(ReverseFileConditionWarning.MultipleConditionsPerMacro);
                ConditionsPerMacro[macro] = conditionHandle;
            }

            public void AttachFreeformPreprocessorMacro(string macro, string macroName)
            {
                var macroRegex = Regex.Escape(macro).Replace("\\{0}", "(.*)");
                var regex = new Regex(macroRegex);
                var m = regex.Match(string.Format(macro, "TEST"));
                if (!m.Success || m.Groups[1].Value != "TEST")
                    throw new Exception("Invalid macro regex");

                FreeformMacros[macroRegex] = macroName;
            }

            public void AttachMinimalConfigurationValue(string key, string value)
            {
                MinimalConfiguration[key] = value;
            }

            internal void AttachIncludeDir(string mappedDir)
            {
                IncludeDirs.Add(mappedDir);
            }

            internal ReverseConditionTable.Framework ToFrameworkDefinition()
            {
                return new ReverseConditionTable.Framework
                {
                    ID = FrameworkID,
                    IncludeDirs = IncludeDirs.ToArray(),
                    MinimalConfiguration = MinimalConfiguration.Select(kv => new SysVarEntry { Key = kv.Key, Value = kv.Value }).ToArray()
                };
            }
        }

        Dictionary<string, Handle> _HandlesByFramework = new Dictionary<string, Handle>();

        public Handle GetHandleForFramework(EmbeddedFramework fw)
        {
            var id = fw.ClassID ?? fw.ID;
            if (!_HandlesByFramework.TryGetValue(id, out var handle))
                _HandlesByFramework[id] = handle = new Handle(this, id);
            return handle;
        }

        public Handle GetHandleForFramework(Framework fw)
        {
            var id = fw.ClassID ?? fw.ID;
            if (!_HandlesByFramework.TryGetValue(id, out var handle))
                _HandlesByFramework[id] = handle = new Handle(this, id);
            return handle;
        }

        public void SaveIfConsistent(string outputDir, PropertyDictionary2 renamedFileTable, bool throwIfInconsistent)
        {
            if (Warnings != ReverseFileConditionWarning.None)
            {
                if (throwIfInconsistent)
                    throw new Exception("Reverse file conditions are inconsistent! Please recheck the rules.");
                else
                    return;
            }

            Dictionary<ConditionHandle, int> conditionIndicies = new Dictionary<ConditionHandle, int>();

            var allFrameworkHandles = new[] { RootHandle }.Concat(_HandlesByFramework.Values).ToArray();
            ReverseConditionTable result = new ReverseConditionTable
            {
                Frameworks = allFrameworkHandles.Select(h => h.ToFrameworkDefinition()).ToArray(),
                RenamedFileTable = renamedFileTable,
            };

            for (int i = 0; i < allFrameworkHandles.Length; i++)
            {
                ConvertObjectConditions(allFrameworkHandles[i].ConditionsPerFile, result.ConditionTable, result.FileTable, conditionIndicies, i);
                ConvertObjectConditions(allFrameworkHandles[i].ConditionsPerMacro, result.ConditionTable, result.MacroTable, conditionIndicies, i);

                foreach(var kv in allFrameworkHandles[i].FreeformMacros)
                {
                    result.FreeFormMacros.Add(new ReverseConditionTable.FreeFormMacroEntry
                    {
                        Regex = kv.Key,
                        Value = kv.Value,
                        FrameworkIndex = i,
                    });
                }
            }

            using (var fs = File.Create(Path.Combine(outputDir, ReverseConditionListFileName + ".gz")))
            using (var gs = new GZipStream(fs, CompressionMode.Compress))
            {
                XmlTools.SaveObjectToStream(result, gs);
            }
        }

        private static void ConvertObjectConditions(Dictionary<string, ConditionHandle> conditionsToConvert, List<ReverseConditionTable.Condition> allConditions, List<ReverseConditionTable.ObjectEntry> result, Dictionary<ConditionHandle, int> conditionIndicies, int i)
        {
            foreach (var cond in conditionsToConvert)
            {
                int index = 0;
                if (cond.Value != null)
                {
                    if (!conditionIndicies.TryGetValue(cond.Value, out index))
                    {
                        conditionIndicies[cond.Value] = index = allConditions.Count + 1;
                        allConditions.Add(cond.Value.ToConditionRecord());
                    }
                }

                result.Add(new ReverseConditionTable.ObjectEntry { ObjectName = cond.Key, ConditionIndex = index, FrameworkIndex = i });
            }
        }

        public const string ReverseConditionListFileName = "ReverseFileConditions.xml";

    }

    public class ReverseConditionTable
    {
        public struct Condition
        {
            public SysVarEntry[] RequestedConfiguration;
        }

        public struct ObjectEntry
        {
            public string ObjectName;
            public int ConditionIndex;
            public int FrameworkIndex;
        }

        public struct FreeFormMacroEntry
        {
            public string Regex;
            public string Value;
            public int FrameworkIndex;
        }

        public class Framework
        {
            public string ID;
            public SysVarEntry[] MinimalConfiguration;
            public string[] IncludeDirs;
        }

        public List<Condition> ConditionTable = new List<Condition>();
        public List<ObjectEntry> FileTable = new List<ObjectEntry>();
        public List<ObjectEntry> MacroTable = new List<ObjectEntry>();
        public List<FreeFormMacroEntry> FreeFormMacros = new List<FreeFormMacroEntry>();
        public Framework[] Frameworks;
        public PropertyDictionary2 RenamedFileTable;
    }

    [Flags]
    public enum ReverseFileConditionWarning
    {
        None = 0,
        HasRegularConditions = 1,
        MultipleConditionsPerFile = 2,
        MultipleConditionsPerMacro = 4,
    }
}
