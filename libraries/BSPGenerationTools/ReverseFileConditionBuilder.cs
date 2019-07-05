using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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

            public ReverseConditionTable.Condition ToConditionRecord() => new ReverseConditionTable.Condition { FrameworkID = _Handle.FrameworkID, RequestedConfiguration = _RequiredConfiguration };
        }

        public class Handle
        {
            private ReverseFileConditionBuilder _Builder;
            public readonly string FrameworkID;

            internal Dictionary<string, ConditionHandle> ConditionsPerFile = new Dictionary<string, ConditionHandle>();

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
        }

        Dictionary<string, Handle> _HandlesByFramework = new Dictionary<string, Handle>();

        public Handle GetHandleForFramework(Framework fw)
        {
            var id = fw.ClassID ?? fw.ID;
            if (!_HandlesByFramework.TryGetValue(id, out var handle))
                _HandlesByFramework[id] = handle = new Handle(this, id);
            return handle;
        }

        public void SaveIfConsistent(string outputDir, bool throwIfInconsistent)
        {
            if (Warnings != ReverseFileConditionWarning.None)
            {
                if (throwIfInconsistent)
                    throw new Exception("Reverse file conditions are inconsistent! Please recheck the rules.");
                else
                    return;
            }

            List<ReverseConditionTable.Condition> conditions = new List<ReverseConditionTable.Condition>();
            List<ReverseConditionTable.FileEntry> files = new List<ReverseConditionTable.FileEntry>();

            Dictionary<ConditionHandle, int> conditionIndicies = new Dictionary<ConditionHandle, int>();

            foreach (var handle in _HandlesByFramework.Values.Concat(new[] { RootHandle}))
                foreach(var cond in handle.ConditionsPerFile)
                {
                    if (!conditionIndicies.TryGetValue(cond.Value, out var index))
                    {
                        conditionIndicies[cond.Value] = index = conditions.Count;
                        conditions.Add(cond.Value.ToConditionRecord());
                    }

                    files.Add(new ReverseConditionTable.FileEntry { Path = cond.Key, ConditionIndex = index });
                }

            XmlTools.SaveObject(new ReverseConditionTable { ConditionTable = conditions.ToArray(), FileTable = files.ToArray() }, Path.Combine(outputDir, ReverseConditionListFileName));
        }

        public const string ReverseConditionListFileName = "ReverseFileConditions.xml";

    }

    public class ReverseConditionTable
    {
        public struct Condition
        {
            public string FrameworkID;
            public SysVarEntry[] RequestedConfiguration;
        }

        public struct FileEntry
        {
            public string Path;
            public int ConditionIndex;
        }

        public Condition[] ConditionTable;
        public FileEntry[] FileTable;
    }

    [Flags]
    public enum ReverseFileConditionWarning
    {
        None = 0,
        HasRegularConditions = 1,
        MultipleConditionsPerFile = 2,
    }
}
