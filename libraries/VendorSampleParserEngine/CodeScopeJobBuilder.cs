using BSPEngine;
using StandaloneBSPValidator;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VendorSampleParserEngine
{
    static class CodeScopeJobBuilder
    {
        public class FileContext
        {
            public readonly ModuleContext Module;
            public readonly string PathInModule, PathInSDK;

            public FileContext(ModuleContext mctx, string pathInModule, string pathInSDK)
            {
                Module = mctx;
                PathInModule = pathInModule;
                PathInSDK = pathInSDK;

                if (string.IsNullOrEmpty(pathInModule))
                    throw new ArgumentNullException(nameof(pathInModule));
            }

            public override string ToString() => $"[{Module}]\\{PathInModule}";
        }

        public class SDKContext
        {
            public string Family, Version;
            public readonly string PhysicalPath, RelativePath;
            Dictionary<string, ModuleContext> _Modules = new Dictionary<string, ModuleContext>();

            List<SampleContext> _Samples = new List<SampleContext>();

            public SDKContext(string family, string version, string sdkDir, string relativePath)
            {
                Family = family;
                Version = version;
                PhysicalPath = Path.Combine(sdkDir, relativePath);
                RelativePath = relativePath;
            }

            public ModuleContext ProvideModule(CodeScopeModuleMatchingRule rule, string physicalPath, string virtualPath)
            {
                if (!_Modules.TryGetValue(virtualPath, out var mc))
                    _Modules[virtualPath] = mc = new ModuleContext(new CodeScopeSampleJob.Module { PhysicalPath = physicalPath, VirtualPath = virtualPath, ID = Path.GetFileName(virtualPath) });
                return mc;
            }

            public CodeScopeSampleJob BuildJob(BaseFlagSetBuilder baseFlags)
            {
                List<CodeScopeSampleJob.Module> modules = new List<CodeScopeSampleJob.Module>();
                foreach (var m in _Modules.Values)
                {
                    m.AssignedIndex = modules.Count;
                    modules.Add(m.Definition);
                }

                var job = new CodeScopeSampleJob
                {
                    Name = Path.GetFileName(Family),
                    Version = Version,
                    RelativePath = RelativePath,
                    Modules = modules.ToArray(),
                    SampleProjects = _Samples.Select(s => s.Complete(this, baseFlags)).ToArray(),
                };

                foreach (var g in job.Modules.GroupBy(m => m.ID))
                    if (g.Count() > 1)
                        throw new Exception("Found multiple modules with ID of " + g.Key);

                foreach (var g in job.SampleProjects.GroupBy(m => m.Self.ID))
                    if (g.Count() > 1)
                        throw new Exception("Found multiple projects with ID of " + g.Key);

                return job;
            }

            public void AddSample(SampleContext sampleCtx)
            {
                _Samples.Add(sampleCtx);
            }

            public CodeScopeSampleJob.SimplifiedToolFlags SplitFlags(ToolFlags flags, string basePath, BaseFlagSetBuilder baseFlags)
            {
                var basicFlags = LoadedBSP.Combine(flags.COMMONFLAGS, flags.CFLAGS);

                return new CodeScopeSampleJob.SimplifiedToolFlags
                {
                    BaseFlagsSet = baseFlags.MapFlags(basicFlags),
                    IncludeDirectories = flags.IncludeDirectories.Select(d =>
                    {
                        if (d == ".")
                            return basePath;


                        if (d.StartsWith(PhysicalPath + "\\", StringComparison.InvariantCultureIgnoreCase))
                            return d.Substring(PhysicalPath.Length + 1);
                        else
                            return null;    //Directory coming from the MCU definition inside our BSP
                    }).Where(p => p != null).ToArray(),
                    PreprocessorMacros = flags.PreprocessorMacros
                };
            }
        }

        public class ModuleContext
        {
            public readonly CodeScopeSampleJob.Module Definition;
            public int AssignedIndex { get; set; } = -1;

            public bool HasAssignedIndex
            {
                get
                {
                    if (AssignedIndex >= 0)
                        return true;

                    return false;
                }
            }

            public ModuleContext(CodeScopeSampleJob.Module def)
            {
                Definition = def;
            }

            public override string ToString() => Definition.PhysicalPath;
        }

        public class SampleContext : ModuleContext
        {
            private readonly CodeScopeSample _Sample;
            public List<FileContext> Files = new List<FileContext>();
            public List<string> OutOfDirFiles = new List<string>();

            public SampleContext(CodeScopeSampleJob.Module def, CodeScopeSample sample)
                : base(def)
            {
                _Sample = sample;
            }

            public CodeScopeSampleJob.SampleProject Complete(SDKContext sdk, BaseFlagSetBuilder flagSet)
            {
                return new CodeScopeSampleJob.SampleProject
                {
                    Self = Definition,
                    Flags = sdk.SplitFlags(_Sample.Flags, Definition.PhysicalPath, flagSet),
                    SourceFiles = Files.Select(f => f.PathInSDK).ToArray(),
                    UsedModules = Files.Select(f => f.Module).Distinct().Where(m => m != this && m.HasAssignedIndex).Select(m => m.AssignedIndex).ToArray(),
                    ExternalFiles = OutOfDirFiles.Count > 0 ? OutOfDirFiles.ToArray() : null,
                };
            }

        }

        public static CodeScopeSampleJob[] ComputeJobs(string sdkDir,
            RawCodeScopeSampleList sampleList,
            ICodeScopeModuleLocator locator,
            BaseFlagSetBuilder baseFlags)
        {
            var allFiles = new Dictionary<string, FileContext>(StringComparer.InvariantCultureIgnoreCase);
            var sdkContexts = new Dictionary<string, SDKContext>();

            var sdkRules = locator.SDKMatchingRules;
            var moduleRules = locator.ModuleMatchingRules;
            var sampleRules = locator.SampleMatchingRules;

            foreach (var sample in sampleList.Samples)
            {
                SampleContext sampleCtx = null;
                if (!sample.VendorSample.Path.StartsWith(sdkDir + "\\"))
                    throw new Exception("Vendor sample is outside the SDK");

                var sdkContext = ProvideSDKContext(sdkContexts, sdkRules, sdkDir, sample.VendorSample.Path.Substring(sdkDir.Length + 1), out var samplePathInSDK) ?? throw new Exception("Could not find SDK for " + sample.VendorSample.Path);

                foreach (var rule in sampleRules)
                {
                    var m = rule.Regex.Match(samplePathInSDK);
                    if (m.Success)
                    {
                        var groups = m.Groups.OfType<Group>().Select(g => g.Value).ToArray();
                        var modulePath = string.Format(rule.ModulePath, groups);
                        var def = new CodeScopeSampleJob.Module
                        {
                            PhysicalPath = samplePathInSDK,
                            VirtualPath = modulePath,
                            ID = sample.VendorSample.InternalUniqueID
                        };

                        sampleCtx = new SampleContext(def, sample);
                        sdkContext.AddSample(sampleCtx);
                        break;
                    }
                }

                if (sampleCtx == null)
                    throw new Exception("No rule matches " + samplePathInSDK);

                foreach (var file in sample.SourceFiles)
                {
                    if (!allFiles.TryGetValue(file, out var fctx))
                    {
                        if (!file.StartsWith(sdkDir + "\\"))
                            continue;   //E.g. startup file

                        string relPath = file.Substring(sdkDir.Length + 1);

                        if (!relPath.StartsWith(sdkContext.RelativePath + "\\"))
                            throw new Exception(file + " is not from the same SDK");

                        string pathInSDK = relPath.Substring(sdkContext.RelativePath.Length + 1);

                        ModuleContext mctx = null;
                        string pathInModule = null;

                        if (pathInSDK.StartsWith(sampleCtx.Definition.PhysicalPath + "\\"))
                        {
                            mctx = sampleCtx;
                            pathInModule = pathInSDK.Substring(sampleCtx.Definition.PhysicalPath.Length + 1);
                        }
                        else
                        {
                            foreach (var rule in moduleRules)
                            {
                                var m = rule.Regex.Match(pathInSDK);
                                if (m.Success)
                                {
                                    var groups = m.Groups.OfType<Group>().Select(g => g.Value).ToArray();

                                    var modulePath = string.Format(rule.ModulePath, groups);
                                    pathInModule = groups.Last();
                                    mctx = sdkContext.ProvideModule(rule, pathInSDK.Substring(0, m.Groups[groups.Length - 1].Index).TrimEnd('\\'), modulePath);
                                    break;
                                }
                            }

                            if (mctx == null)
                            {
                                sampleCtx.OutOfDirFiles.Add(pathInSDK);
                                continue;
                            }
                        }

                        allFiles[file] = fctx = new FileContext(mctx, pathInModule, pathInSDK);
                    }

                    sampleCtx.Files.Add(fctx);
                }
            }

            return sdkContexts.Values.Select(c => c.BuildJob(baseFlags)).ToArray();
        }

        private static SDKContext ProvideSDKContext(Dictionary<string, SDKContext> sdkContexts,
            CodeScopeSDKMatchingRule[] sdkRules,
            string sdkDir,
            string relPath, out string pathInSDK)
        {
            foreach (var rule in sdkRules)
            {
                var m = rule.Regex.Match(relPath);
                if (m.Success)
                {
                    rule.MatchValidator?.Invoke(m);
                    var groups = m.Groups.OfType<Group>().Select(g => g.Value).ToArray();

                    var family = string.Format(rule.FamilyFormat, groups);
                    var version = string.Format(rule.VersionFormat, groups);
                    if (!sdkContexts.TryGetValue(family, out var sctx))
                        sdkContexts[family] = sctx = new SDKContext(family, version, sdkDir, relPath.Substring(0, m.Groups[m.Groups.Count - 1].Index).TrimEnd('\\'));
                    else if (sctx.Version != version)
                        throw new OverflowException("Mismatching SDK version");

                    pathInSDK = groups.Last();
                    return sctx;
                }
            }

            pathInSDK = null;
            return null;
        }
    }

    public struct CodeScopeSDKMatchingRule
    {
        public Regex Regex;
        public string FamilyFormat, VersionFormat;
        public Action<Match> MatchValidator;

        public CodeScopeSDKMatchingRule(string regex, string familyFormat, string versionFormat, Action<Match> matchValidator = null)
        {
            Regex = new Regex("^" + regex + @"\\(.*)$", RegexOptions.IgnoreCase);
            FamilyFormat = familyFormat;
            VersionFormat = versionFormat;
            MatchValidator = matchValidator;
        }

        public override string ToString() => Regex.ToString();
    }

    public struct CodeScopeModuleMatchingRule
    {
        public Regex Regex;
        public string ModulePath;

        public CodeScopeModuleMatchingRule(string regex, string modulePathFormat)
        {
            Regex = new Regex("^" + regex + @"\\(.*)$", RegexOptions.IgnoreCase);
            ModulePath = modulePathFormat;
        }

        public override string ToString() => Regex.ToString();
    }

    public interface ICodeScopeModuleLocator
    {
        CodeScopeSDKMatchingRule[] SDKMatchingRules { get; }
        CodeScopeModuleMatchingRule[] ModuleMatchingRules { get; }
        CodeScopeModuleMatchingRule[] SampleMatchingRules { get; }
    }

    public class BaseFlagSetBuilder
    {
        Dictionary<string, int> _BaseFlagSetDict = new Dictionary<string, int>();
        List<string> _BaseFlagSets = new List<string>();

        public int MapFlags(string basicFlags)
        {
            if (!_BaseFlagSetDict.TryGetValue(basicFlags, out var set))
            {
                _BaseFlagSetDict[basicFlags] = set = _BaseFlagSets.Count;
                _BaseFlagSets.Add(basicFlags);
            }

            return set;
        }

        public string[] Complete() => _BaseFlagSets.ToArray();
    }
}
