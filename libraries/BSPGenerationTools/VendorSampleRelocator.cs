using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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

        const string SampleRootDirMarker = "$$SYS:VSAMPLE_DIR$$";

        class PathMapper
        {
            private ConstructedVendorSampleDirectory _SampleDir;

            public PathMapper(ConstructedVendorSampleDirectory dir)
            {
                _SampleDir = dir;
            }

            //Returns null for toolchain-relative paths that need to be excluded
            public string MapPath(string path)
            {
                if (!Path.IsPathRooted(path))
                    return null;
                path = Path.GetFullPath(path).Replace('/', '\\');
                if (path.StartsWith(_SampleDir.ToolchainDirectory, StringComparison.InvariantCultureIgnoreCase))
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

        public void InsertVendorSamplesIntoBSP(ConstructedVendorSampleDirectory dir, string bspDirectory)
        {
            List<BSPEngine.VendorSample> finalSamples = new List<BSPEngine.VendorSample>();

            string outputDir = Path.Combine(bspDirectory, "VendorSamples");
            if (Directory.Exists(outputDir))
            {
                Console.WriteLine($"Deleting {outputDir}...");
                Directory.Delete(outputDir, true);
            }

            var mapper = new PathMapper(dir);
            Dictionary<string, string> copiedFiles = new Dictionary<string, string>();
            Console.WriteLine("Processing sample list...");

            foreach(var s in dir.Samples)
            {
                if (s.AllDependencies == null)
                    continue;

                mapper.MapPathList(ref s.SourceFiles);
                mapper.MapPathList(ref s.HeaderFiles);
                mapper.MapPathList(ref s.IncludeDirectories);

                foreach (var dep in s.AllDependencies)
                {
                    var mappedPath = mapper.MapPath(dep);
                    if (mappedPath == null)
                        continue;

                    copiedFiles[dep] = mappedPath.Replace(SampleRootDirMarker, outputDir);
                }

                mapper.MapPathList(ref s.AllDependencies);
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
            XmlTools.SaveObject(finalDir, Path.Combine(outputDir, "VendorSamples.xml"));
        }
    }
}
