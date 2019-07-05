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

        public static void ValidateVendorSampleDependencies(ConstructedVendorSampleDirectory dir, string toolchainDir)
        {
            using (var sw = File.CreateText(@"e:\temp\0.txt"))
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

            public PathMapper(ConstructedVendorSampleDirectory dir)
            {
                _SampleDir = dir;
            }

            //Returns null for toolchain-relative paths that need to be excluded
            public virtual string MapPath(string path)
            {
                if (string.IsNullOrEmpty(path) || (!Path.IsPathRooted(path) && !path.Contains("$$")))
                    return null;

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

            public void MapPathList(ref string[] files)
            {
                files = files?.Select(MapPath)?.Where(f => f != null)?.ToArray();
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

        protected class AutoDetectedFramework
        {
            public Regex FileRegex;
            public Regex DisableTriggerRegex;   //Matching trigger will be disabled for files matching FileRegex and DisableTriggerRegex
            public string FrameworkID;

            public Dictionary<string, string> Configuration;

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

        protected virtual VendorSampleConfiguration DetectKnownFrameworksAndFilterPaths(ref string[] sources, ref string[] headers, ref string[] includeDirs, ref string[] preprocessorMacros, ref ParsedDependency[] dependencies, VendorSampleConfiguration existingConfiguration)
        {
            List<AutoDetectedFramework> matchedFrameworks = new List<AutoDetectedFramework>();

            foreach (var fw in AutoDetectedFrameworks)
            {
                if (sources?.FirstOrDefault(s => fw.FileRegex.IsMatch(s) && !fw.DisableTriggerRegex.IsMatch(s)) != null)
                {
                    fw.FindAndFilterOut(ref sources);
                    fw.FindAndFilterOut(ref headers);
                    fw.FindAndFilterOut(ref includeDirs);
                    fw.FindAndFilterOut(ref dependencies, d => d.MappedFile);

                    matchedFrameworks.Add(fw);
                }
            }

            foreach (var map in AutoPathMappings)
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
            Dictionary<string, string> extraConfiguration = new Dictionary<string, string>();

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


        public void InsertVendorSamplesIntoBSP(ConstructedVendorSampleDirectory dir, VendorSample[] sampleList, string bspDirectory)
        {
            List<VendorSample> finalSamples = new List<VendorSample>();

            string outputDir = Path.Combine(bspDirectory, "VendorSamples");
            if (Directory.Exists(outputDir))
            {
                Console.WriteLine($"Deleting {outputDir}...");
                Directory.Delete(outputDir, true);
            }

            var mapper = CreatePathMapper(dir) ?? new PathMapper(dir);

            Dictionary<string, string> copiedFiles = new Dictionary<string, string>();
            Console.WriteLine("Processing sample list...");

            List<string> tooLongPaths = new List<string>();

            foreach (var s in sampleList)
            {
                if (s.AllDependencies == null)
                    continue;

                var deps = s.AllDependencies
                            .Concat(new[] { s.LinkerScript })
                            .Concat(s.SourceFiles)
                            .Distinct()
                            .Select(d => new ParsedDependency { OriginalFile = d, MappedFile = mapper.MapPath(d) })
                            .Where(d => d.MappedFile != null)
                            .ToArray();

                mapper.MapPathList(ref s.HeaderFiles);
                mapper.MapPathList(ref s.IncludeDirectories);
                mapper.MapPathList(ref s.SourceFiles);

                s.LinkerScript = mapper.MapPath(s.LinkerScript);

                s.Configuration = DetectKnownFrameworksAndFilterPaths(ref s.SourceFiles, ref s.HeaderFiles, ref s.IncludeDirectories, ref s.PreprocessorMacros, ref deps, s.Configuration);
                FilterPreprocessorMacros(ref s.PreprocessorMacros);

                const int ReasonableVendorSampleDirPathLengthForUsers = 120;

                foreach (var dep in deps)
                {
                    if (dep.MappedFile.StartsWith("$$SYS:BSP_ROOT$$/"))
                        continue;   //The file was already copied
                    copiedFiles[dep.OriginalFile] = dep.MappedFile.Replace(SampleRootDirMarker, outputDir);

                    int estimatedTargetPathLength = ReasonableVendorSampleDirPathLengthForUsers + dep.MappedFile.Length - SampleRootDirMarker.Length;
                    if (estimatedTargetPathLength > 254)
                        tooLongPaths.Add(dep.MappedFile);
                }

                s.AllDependencies = deps.Select(d => d.MappedFile).ToArray();

                var rawPath = s.Path;
                s.Path = mapper.MapPath(rawPath);
                if (s.Path == null)
                    throw new Exception("Invalid sample path for " + s.UserFriendlyName);

                s.VirtualPath = BuildVirtualSamplePath(s.Path);

                if (s.LinkerScript != null)
                {
                    string prefix = s.Path.TrimEnd('/', '\\') + "/";
                    if (s.LinkerScript.StartsWith(prefix))
                        s.LinkerScript = s.LinkerScript.Substring(prefix.Length).TrimStart('/');
                    else if (s.LinkerScript.StartsWith("$$SYS:BSP_ROOT$$") || s.LinkerScript.StartsWith("$$SYS:VSAMPLE_DIR$$"))
                    {
                        //Nothing to do. VisualGDB will automatically expand this.
                    }
                    else
                    {
                        throw new Exception($"Unexpected linker script path {s.LinkerScript}. VisualGDB may not be able to expand it.");
                    }
                }

                finalSamples.Add(s);
            }

            if (tooLongPaths.Count > 0)
            {
                throw new Exception($"Found {tooLongPaths.Count} files with excessively long paths. Please update MapPath() in the BSP-specific path mapper to shorten the target paths.");
            }

            Console.WriteLine($"Copying {copiedFiles.Count} files...");
            foreach (var kv in copiedFiles)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(kv.Value));
                if (!File.Exists(kv.Value))
                    File.Copy(kv.Key, kv.Value);
                File.SetAttributes(kv.Value, File.GetAttributes(kv.Value) & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
            }

            Console.WriteLine("Updating BSP...");
            VendorSampleDirectory finalDir = new VendorSampleDirectory { Samples = finalSamples.ToArray() };
            Directory.CreateDirectory(outputDir);

            using (var fs = File.Create(Path.Combine(outputDir, "VendorSamples.xml.gz")))
            using (var gs = new GZipStream(fs, CompressionMode.Compress))
            {
                XmlTools.SaveObjectToStream(finalDir, gs);
            }
        }
    }
}
