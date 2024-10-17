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

    public struct CodeScopeModuleSummary
    {
        public CodeScopeModuleType ModuleType;
        public string UserFriendlyName;
        public string VirtualPath;
        public string DeviceName;
        public string ReasonablyUniqueName;     //Used for cookies
    }


    public class CodeScopeSampleJob
    {
        public struct Module
        {
            public string PhysicalPath;
            public CodeScopeModuleSummary Summary;

            public override string ToString() => Summary.UserFriendlyName;
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
        public bool ForceCSemantics;
        public Module[] Modules;
        public SampleProject[] SampleProjects;
    }

    public enum CodeScopeModuleType //Higher value means higher priority when picking the "owning module" for a function
    {
        Unknown,
        Example,
        Library,
        Driver,
        Core,
    }
}
