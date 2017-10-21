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
        public static void ValidateVendorSampleDependencies(ConstructedVendorSampleDirectory dir, string toolchainDir)
        {
            using (var sw = File.CreateText(@"e:\temp\0.txt"))
            {
                foreach (var vs in dir.Samples)
                {
                    if (vs.AllDependencies == null)
                        continue;
                    var extraDeps = vs.AllDependencies.Except(vs.HeaderFiles ?? new string[0]).Except(vs.SourceFiles ?? new string[0]).Where(d => !d.StartsWith(toolchainDir, StringComparison.InvariantCultureIgnoreCase)).ToArray();

                    var knownDirs = vs.IncludeDirectories.Concat(vs.SourceFiles.Select(f=>Path.GetDirectoryName(f)));

                    foreach (var dep in extraDeps)
                    {
                        if (knownDirs.FirstOrDefault(d => dep.Replace('\\', '/').StartsWith(d.Replace('\\', '/'), StringComparison.InvariantCultureIgnoreCase)) == null)
                        {
                            bool found = false;

                            foreach(var includeDir in vs.IncludeDirectories.Concat(vs.SourceFiles.Select(f=>Path.GetDirectoryName(f))))
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
                if (!Path.IsPathRooted(path))
                    return null;
                path = Path.GetFullPath(path).Replace('/', '\\');
                if (path.StartsWith(_SampleDir.ToolchainDirectory, StringComparison.InvariantCultureIgnoreCase))
                    return null;
                if (path.StartsWith(_SampleDir.BSPDirectory, StringComparison.InvariantCultureIgnoreCase))
                    return null;
                if (path.StartsWith(_SampleDir.SourceDirectory, StringComparison.InvariantCultureIgnoreCase))
                    return SampleRootDirMarker + "/" + path.Substring(_SampleDir.SourceDirectory.Length + 1).Replace('\\', '/');

                throw new Exception("Don't know how to map " + path);
            }

            public void MapPathList(ref string[] files)
            {
                files = files?.Select(MapPath)?.Where(f => f != null)?.ToArray();
            }
        }

        protected struct ParsedDependency
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

        protected virtual VendorSampleConfiguration DetectKnownFrameworksAndFilterPaths(ref string[] sources, ref string[] headers, ref string[] includeDirs, ref ParsedDependency[] dependencies, PropertyDictionary2 mcuConfiguration)
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

            foreach(var map in AutoPathMappings)
            {
                map.MapArray(ref sources);
                map.MapArray(ref headers);
                map.MapArray(ref includeDirs);

                for (int i = 0; i < dependencies.Length; i++)
                    if (dependencies[i].MappedFile != null && map.TryMap(dependencies[i].MappedFile) != null)
                        dependencies[i].MappedFile = null;
            }

            dependencies = dependencies.Where(d => d.MappedFile != null).ToArray();

            return new VendorSampleConfiguration
            {
                Frameworks = matchedFrameworks.Select(f => f.FrameworkID).Distinct().ToArray(),
                Configuration = new PropertyDictionary2 { Entries = matchedFrameworks.SelectMany(f => f.Configuration).Select(kv => new PropertyDictionary2.KeyValue { Key = kv.Key, Value = kv.Value }).ToArray() },
                MCUConfiguration = mcuConfiguration
            };
        }

        protected virtual void FilterPreprocessorMacros(ref string[] macros)
        {
        }

        protected virtual string BuildVirtualSamplePath(string originalPath) => null;


        public void InsertVendorSamplesIntoBSP(ConstructedVendorSampleDirectory dir, string bspDirectory, PathMapper mapper = null)
        {
            List<VendorSample> finalSamples = new List<VendorSample>();

            string outputDir = Path.Combine(bspDirectory, "VendorSamples");
            if (Directory.Exists(outputDir))
            {
                Console.WriteLine($"Deleting {outputDir}...");
                Directory.Delete(outputDir, true);
            }

            if (mapper == null)
                mapper = new PathMapper(dir);
            Dictionary<string, string> copiedFiles = new Dictionary<string, string>();
            Console.WriteLine("Processing sample list...");

            foreach(var s in dir.Samples)
            {
                if (s.AllDependencies == null)
                    continue;
                var deps = s.AllDependencies.Concat(s.SourceFiles).Distinct().Select(d => new ParsedDependency { OriginalFile = d, MappedFile = mapper.MapPath(d) }).Where(d => d.MappedFile != null).ToArray();

                mapper.MapPathList(ref s.HeaderFiles);
                mapper.MapPathList(ref s.IncludeDirectories);
                mapper.MapPathList(ref s.SourceFiles);

                s.Configuration = DetectKnownFrameworksAndFilterPaths(ref s.SourceFiles, ref s.HeaderFiles, ref s.IncludeDirectories, ref deps, s.Configuration.MCUConfiguration);
                FilterPreprocessorMacros(ref s.PreprocessorMacros);

                foreach (var dep in deps)
                    copiedFiles[dep.OriginalFile] = dep.MappedFile.Replace(SampleRootDirMarker, outputDir);

                s.AllDependencies = deps.Select(d => d.MappedFile).ToArray();

                s.Path = mapper.MapPath(s.Path);
                s.VirtualPath = BuildVirtualSamplePath(s.Path);
                finalSamples.Add(s);
            }

            Console.WriteLine($"Copying {copiedFiles.Count} files...");
            foreach(var kv in copiedFiles)
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
