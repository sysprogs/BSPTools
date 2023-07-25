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
    public class VendorSampleRelocator
    {
        public VendorSampleRelocator(ReverseConditionTable optionalConditionTableForFrameworkMapping = null)
        {
            if (optionalConditionTableForFrameworkMapping != null)
                _ConditionMatcher = new ReverseFileConditionMatcher(optionalConditionTableForFrameworkMapping);
        }

        readonly ReverseFileConditionMatcher _ConditionMatcher;

        public static void ValidateVendorSampleDependencies(ConstructedVendorSampleDirectory dir, string toolchainDir, string logFile)
        {
            using (var sw = File.CreateText(logFile))
            {
                foreach (var vs in dir.Samples)
                {
                    if (vs.AllDependencies == null)
                        continue;
                    var extraDeps = vs.AllDependencies.Except(vs.HeaderFiles ?? new string[0]).Except(vs.SourceFiles ?? new string[0]).Where(d => !d.StartsWith(toolchainDir, StringComparison.InvariantCultureIgnoreCase)).ToArray();

                    var knownDirs = vs.IncludeDirectories.Concat(vs.SourceFiles.Select(f => Path.GetDirectoryName(f)));

                    foreach (var dep in extraDeps)
                    {
                        if (knownDirs.FirstOrDefault(d => dep.Replace('\\', '/').StartsWith(d.Replace('\\', '/'), StringComparison.InvariantCultureIgnoreCase)) == null)
                        {
                            bool found = false;

                            foreach (var includeDir in vs.IncludeDirectories.Concat(vs.SourceFiles.Select(f => Path.GetDirectoryName(f))))
                            {
                                string baseDir = Path.GetDirectoryName(includeDir);
                                if (dep.Replace('\\', '/').StartsWith(baseDir.Replace('\\', '/'), StringComparison.InvariantCultureIgnoreCase))
                                {
                                    found = true;
                                    break;
                                }
                            }

                            if (!found)
                                sw.WriteLine("Unexpected dependency: " + dep);
                        }
                    }
                }
            }
        }

        protected const string SampleRootDirMarker = "$$SYS:VSAMPLE_DIR$$";

        public class PathMapper
        {
            protected readonly ConstructedVendorSampleDirectory _SampleDir;

            readonly Dictionary<string, string> _CopiedFiles;

            public PathMapper(ConstructedVendorSampleDirectory dir)
            {
                _SampleDir = dir;
                _CopiedFiles = CopiedFileMonitor.Load(dir.BSPDirectory, true);
            }

            //Returns null for toolchain-relative paths that need to be excluded
            public virtual string MapPath(string path)
            {
                if (string.IsNullOrEmpty(path) || (!Path.IsPathRooted(path) && !path.Contains("$$")))
                    return null;

                if (_CopiedFiles.Count > 0 && _CopiedFiles.TryGetValue(Path.GetFullPath(path), out var mapped))
                    return mapped;

                if (!path.Contains("$$"))
                    path = Path.GetFullPath(path).Replace('/', '\\');

                if (path.StartsWith(_SampleDir.ToolchainDirectory, StringComparison.InvariantCultureIgnoreCase))
                    return null;
                if (path.StartsWith(_SampleDir.BSPDirectory, StringComparison.InvariantCultureIgnoreCase))
                    return "$$SYS:BSP_ROOT$$/" + path.Substring(_SampleDir.BSPDirectory.Length + 1).Replace('\\', '/');
                if (path.StartsWith(_SampleDir.SourceDirectory, StringComparison.InvariantCultureIgnoreCase))
                    return SampleRootDirMarker + "/" + path.Substring(_SampleDir.SourceDirectory.Length + 1).Replace('\\', '/');

                throw new Exception("Don't know how to map " + path);
            }
        }

        public struct ParsedDependency
        {
            public string OriginalFile;
            public string MappedFile;

            public override string ToString()
            {
                return MappedFile;
            }
        }

        protected struct FileBasedConfigEntry
        {
            public readonly Regex Regex;
            public readonly string Format;

            public FileBasedConfigEntry(string regex, string format)
            {
                Regex = new Regex(regex, RegexOptions.IgnoreCase);
                Format = format;
            }
        }

        protected class AutoDetectedFramework
        {
            public Regex FileRegex;
            public Regex DisableTriggerRegex;   //Matching trigger will be disabled for files matching FileRegex and DisableTriggerRegex
            public Regex SkipFrameworkRegex;   //Framework will be skipped if any of these files are found
            public string FrameworkID;

            public Dictionary<string, string> Configuration = new Dictionary<string, string>();
            public FileBasedConfigEntry[] FileBasedConfig;

            public Regex UnsupportedDeviceRegex;

            public bool FindAndFilterOut<_Ty>(ref _Ty[] sources, Func<_Ty, string> conv = null)
            {
                if (sources == null)
                    return false;
                if (conv == null)
                    conv = t => t.ToString();

                int len = sources.Length;
                sources = sources.Where(s => !FileRegex.IsMatch(conv(s))).ToArray();
                return sources.Length != len;
            }

            public bool IsMatchingSourceFile(string fn)
            {
                if (!FileRegex.IsMatch(fn))
                    return false;

                if (DisableTriggerRegex?.IsMatch(fn) == true)
                    return false;

                return true;
            }
        }

        protected class PathMapping
        {
            public Regex OldPath;
            public string NewPath;

            public PathMapping(string oldPath, string newPath)
            {
                OldPath = new Regex(oldPath, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                NewPath = newPath;
            }

            internal void MapArray(ref string[] sources)
            {
                if (sources == null)
                    return;
                for (int i = 0; i < sources.Length; i++)
                {
                    var m = TryMap(sources[i]);
                    if (m != null)
                        sources[i] = m;
                }
            }

            internal string TryMap(string path)
            {
                var m = OldPath.Match(path);
                if (!m.Success)
                    return null;
                return string.Format(NewPath, m.Groups.OfType<object>().ToArray());
            }
        }

        protected AutoDetectedFramework[] AutoDetectedFrameworks;
        protected PathMapping[] AutoPathMappings;

        protected virtual VendorSampleConfiguration DetectKnownFrameworksAndFilterPaths(string deviceID, ref string[] sources, ref string[] headers, ref string[] includeDirs, ref string[] preprocessorMacros, ref ParsedDependency[] dependencies, VendorSampleConfiguration existingConfiguration)
        {
            List<AutoDetectedFramework> matchedFrameworks = new List<AutoDetectedFramework>();
            Dictionary<string, string> extraConfiguration = new Dictionary<string, string>();

            foreach (var fw in AutoDetectedFrameworks ?? new AutoDetectedFramework[0])
            {
                if (fw.UnsupportedDeviceRegex?.IsMatch(deviceID) == true)
                    continue;

                if (sources?.FirstOrDefault(fw.IsMatchingSourceFile) == null)
                    continue;

                if (fw.SkipFrameworkRegex != null && sources?.FirstOrDefault(s => fw.SkipFrameworkRegex.IsMatch(s)) != null)
                    continue;

                if (fw.FileBasedConfig != null)
                {
                    foreach(var e in fw.FileBasedConfig)
                    {
                        foreach(var fn in sources)
                        {
                            var m = e.Regex.Match(fn);
                            if (m.Success)
                            {
                                var kv = string.Format(e.Format, m.Groups.Cast<object>().ToArray());
                                int idx = kv.IndexOf('=');
                                if (idx == -1)
                                    extraConfiguration[kv] = "1";
                                else
                                    extraConfiguration[kv.Substring(0, idx)] = kv.Substring(idx + 1);
                            }
                        }
                    }
                }

                fw.FindAndFilterOut(ref sources);
                fw.FindAndFilterOut(ref headers);
                fw.FindAndFilterOut(ref includeDirs);
                fw.FindAndFilterOut(ref dependencies, d => d.MappedFile);

                matchedFrameworks.Add(fw);
            }

            foreach (var map in AutoPathMappings ?? new PathMapping[0])
            {
                map.MapArray(ref sources);
                map.MapArray(ref headers);
                map.MapArray(ref includeDirs);

                for (int i = 0; i < dependencies.Length; i++)
                    if (dependencies[i].MappedFile != null)
                    {
                        string mappedPath = map.TryMap(dependencies[i].MappedFile);
                        dependencies[i].MappedFile = mappedPath ?? dependencies[i].MappedFile;
                    }
            }

            dependencies = dependencies.Where(d => d.MappedFile != null).ToArray();

            HashSet<string> extraFrameworks = new HashSet<string>();

            foreach (var fw in matchedFrameworks)
                foreach (var kv in fw.Configuration)
                    extraConfiguration[kv.Key] = kv.Value;
            foreach (var kv in existingConfiguration.Configuration?.Entries ?? new PropertyDictionary2.KeyValue[0])
                extraConfiguration[kv.Key] = kv.Value;

            _ConditionMatcher?.DetectKnownFrameworksAndFilterPaths(ref sources, ref headers, ref includeDirs, ref preprocessorMacros, ref dependencies, extraFrameworks, extraConfiguration);

            return new VendorSampleConfiguration
            {
                Frameworks = matchedFrameworks.Select(f => f.FrameworkID).Concat(existingConfiguration.Frameworks ?? new string[0]).Concat(extraFrameworks).Distinct().ToArray(),
                Configuration = new PropertyDictionary2()
                {
                    Entries = extraConfiguration.Select(kv => new PropertyDictionary2.KeyValue { Key = kv.Key, Value = kv.Value}).ToArray()
                },
                MCUConfiguration = existingConfiguration.MCUConfiguration
            };
        }

        protected virtual void FilterPreprocessorMacros(ref string[] macros)
        {
        }

        protected virtual string BuildVirtualSamplePath(string originalPath) => null;
        protected virtual PathMapper CreatePathMapper(ConstructedVendorSampleDirectory dir) => null;

        static void MapPathList(ref string[] files, Func<string, string> mapping)
        {
            files = files?.Select(mapping)?.Where(f => f != null)?.ToArray();
        }

        static void TranslateVendorSamplePaths(VendorSample s, ref ParsedDependency[] deps, Func<string, string> mapping)
        {
            for (int i = 0; i < deps.Length; i++)
                deps[i].MappedFile = mapping(deps[i].MappedFile ?? deps[i].OriginalFile);

            deps = deps.Where(d => d.MappedFile != null).ToArray();

            MapPathList(ref s.HeaderFiles, mapping);
            MapPathList(ref s.IncludeDirectories, mapping);
            MapPathList(ref s.AuxiliaryLinkerScripts, mapping);
            MapPathList(ref s.SourceFiles, mapping);

            s.LinkerScript = mapping(s.LinkerScript);
            s.Path = mapping(s.Path);
        }

        public virtual Dictionary<string, string> InsertVendorSamplesIntoBSP(ConstructedVendorSampleDirectory dir, VendorSample[] sampleList, string bspDirectory, BSPReportWriter reportWriter, bool cleanCopy)
        {
            List<VendorSample> finalSamples = new List<VendorSample>();

            string outputDir = Path.Combine(bspDirectory, "VendorSamples");
            if (Directory.Exists(outputDir) && cleanCopy)
            {
                Console.WriteLine($"Deleting {outputDir}...");
                Directory.Delete(outputDir, true);
            }

            var mapper = CreatePathMapper(dir) ?? new PathMapper(dir);

            Dictionary<string, string> copiedFilesByTarget = new Dictionary<string, string>();
            Console.WriteLine("Processing sample list...");

            int shortPathIndex = 0;

            Dictionary<string, string> shortenedPaths = new Dictionary<string, string>();

            foreach (var s in sampleList)
            {
                if (s.AllDependencies == null)
                    continue;

                var deps = s.AllDependencies
                            .Concat(new[] { s.LinkerScript })
                            .Concat(s.SourceFiles)
                            .Concat(s.HeaderFiles ?? new string[0])
                            .Concat(s.AuxiliaryLinkerScripts ?? new string[0])
                            .Distinct()
                            .Select(d => new ParsedDependency { OriginalFile = d, MappedFile = d })
                            .ToArray();

                var rawPath = s.Path;

                //1. Translate absolute paths to the $$SYS:VSAMPLE_DIR$$ syntax. All files referenced here should be also also included in 'deps' in order to be copied.
                TranslateVendorSamplePaths(s, ref deps, mapper.MapPath);

                s.Configuration = DetectKnownFrameworksAndFilterPaths(s.DeviceID, ref s.SourceFiles, ref s.HeaderFiles, ref s.IncludeDirectories, ref s.PreprocessorMacros, ref deps, s.Configuration);
                FilterPreprocessorMacros(ref s.PreprocessorMacros);

                if (s.Path == null)
                    throw new Exception("Invalid sample path for " + s.UserFriendlyName);

                if (s.VirtualPath == null)
                    s.VirtualPath = BuildVirtualSamplePath(s.Path);

                if (s.LinkerScript != null)
                {
                    string prefix = s.Path.TrimEnd('/', '\\') + "/";
                    if (s.LinkerScript.StartsWith(prefix))
                        s.LinkerScript = s.LinkerScript.Substring(prefix.Length).TrimStart('/');
                    else if (s.LinkerScript.StartsWith("$$SYS:BSP_ROOT$$") || s.LinkerScript.StartsWith(SampleRootDirMarker))
                    {
                        //Nothing to do. VisualGDB will automatically expand this.
                    }
                    else
                    {
                        throw new Exception($"Unexpected linker script path {s.LinkerScript}. VisualGDB may not be able to expand it.");
                    }
                }

                if (deps.Any(dep => dep.MappedFile.StartsWith(s.Path) && IsPathTooLong(dep)))
                {
                    //Relocate the sample to a shorter path
                    string longPath = s.Path;
                    string shortPath = $"{SampleRootDirMarker}/_/{shortPathIndex++:d3}";

                    TranslateVendorSamplePaths(s, ref deps, path =>
                    {
                        if (shortenedPaths.TryGetValue(path.Replace('\\', '/'), out var result))
                            return result;

                        if (path.StartsWith(longPath))
                        {
                            result = shortPath + path.Substring(longPath.Length);
                            shortenedPaths[path.Replace('\\', '/')] = result;

                            return result;
                        }
                        return path;
                    });
                }

                foreach (var dep in deps)
                {
                    if (dep.MappedFile.StartsWith("$$SYS:BSP_ROOT$$/"))
                        continue;   //The file was already copied
                    
                    copiedFilesByTarget[dep.MappedFile.Replace(SampleRootDirMarker, outputDir)] = Path.GetFullPath(dep.OriginalFile);

                    if (IsPathTooLong(dep))
                        reportWriter.ReportMergeableError("Path too long", dep.MappedFile);
                }

                s.AllDependencies = deps.Select(d => d.MappedFile).ToArray();

                finalSamples.Add(s);
            }

            Console.Write($"Copying {copiedFilesByTarget.Count} files...      ");
            int filesProcessed = 0;
            var updateTime = DateTime.Now;
            foreach (var kv in copiedFilesByTarget)
            {
                if (!Directory.Exists(Path.GetDirectoryName(kv.Key)))
                    Directory.CreateDirectory(Path.GetDirectoryName(kv.Key));

                if (!File.Exists(kv.Key))
                {
                    File.Copy(kv.Value, kv.Key);
                    File.SetAttributes(kv.Key, File.GetAttributes(kv.Key) & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
                }

                filesProcessed++;

                if ((DateTime.Now - updateTime).TotalMilliseconds > 200)
                {
                    Console.Write($"\b\b\b\b\b[{filesProcessed * 100 / copiedFilesByTarget.Count:d2}%]");
                    updateTime = DateTime.Now;
                }
            }

            Console.WriteLine($"\b\b\b\b\bdone ");

            Console.WriteLine("Updating BSP...");
            VendorSampleDirectory finalDir = new VendorSampleDirectory { Samples = finalSamples.ToArray() };
            Directory.CreateDirectory(outputDir);

            using (var fs = File.Create(Path.Combine(outputDir, "VendorSamples.xml.gz")))
            using (var gs = new GZipStream(fs, CompressionMode.Compress))
            {
                XmlTools.SaveObjectToStream(finalDir, gs);
            }

            return copiedFilesByTarget;
        }

        static bool IsPathTooLong(ParsedDependency dep)
        {
            const int ReasonableVendorSampleDirPathLengthForUsers = 120;

            int estimatedTargetPathLength = ReasonableVendorSampleDirPathLengthForUsers + dep.MappedFile.Length - SampleRootDirMarker.Length;

            return estimatedTargetPathLength > 254;
        }
    }
}
