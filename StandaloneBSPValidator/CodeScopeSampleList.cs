using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StandaloneBSPValidator
{
    public class RawCodeScopeSampleList
    {
        public List<CodeScopeSample> Samples = new List<CodeScopeSample>();
    }

    public class CodeScopeSample
    {
        public VendorSample VendorSample;
        public string[] SourceFiles;
        public ToolFlags Flags;
        public string SuggestedBuildDirectory;
    }

    public class CodeScopeSampleJob
    {
        public struct Module
        {
            public string ID;
            public string PhysicalPath;
            public string VirtualPath;

            public override string ToString() => ID;
        }

        public struct SimplifiedToolFlags
        {
            public int BaseFlagsSet;
            public string[] IncludeDirectories;
            public string[] PreprocessorMacros;
        }

        public struct SampleProject
        {
            public Module Self;
            public string[] SourceFiles;
            public SimplifiedToolFlags Flags;
            public int[] UsedModules;
            public string[] ExternalFiles;

            public override string ToString() => Self.ToString();
        }

        public string Name, Version;
        public string RelativePath;
        public Module[] Modules;
        public SampleProject[] SampleProjects;
    }
}
