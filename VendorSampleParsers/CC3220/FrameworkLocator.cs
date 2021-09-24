using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CC3220VendorSampleParser
{
    class FrameworkLocator
    {
        private string[] _AllFilesInSDK;
        FamilyDefinition _FamilyDefinition;
        private Framework[] _AllFrameworks;

        HashSet<string> _UnresolvedLibraries = new HashSet<string>();

        public FrameworkLocator(string sdkDir, string rulesDir)
        {
            var freeRTOSPrefix = Path.Combine(sdkDir, "FreeRTOSv");
            _AllFilesInSDK = Directory.GetFiles(sdkDir, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(freeRTOSPrefix)).Select(f => f.Substring(sdkDir.Length + 1).Replace('\\', '/')).ToArray();
            _FamilyDefinition = XmlTools.LoadObject<FamilyDefinition>(Path.Combine(rulesDir, "CommonFiles.xml"));

            _AllFrameworks = _FamilyDefinition.AdditionalFrameworks.Concat(_FamilyDefinition.AdditionalFrameworkTemplates.SelectMany(t => t.Expand())).ToArray();

#if UNUSED
            var allFiles = File.ReadAllLines(@"e:\temp\libs.txt");
            var unresolvedLibs = allFiles.Where(f => DoLookupFrameworkIDForLibraryFile(f) == null).ToArray();
            Console.WriteLine($"Found {unresolvedLibs.Length} unresolved libraries:");
            foreach (var lib in unresolvedLibs)
                Console.WriteLine("\t" + lib);
#endif
        }

        HashSet<string> _AllLibraryFiles = new HashSet<string>();

        public string LocateFrameworkForLibraryFile(string libraryFile)
        {
            if (libraryFile.StartsWith("-l:"))
                libraryFile = libraryFile.Substring(3);

            var matchingFile = _AllFilesInSDK.FirstOrDefault(f => f.EndsWith(libraryFile, StringComparison.InvariantCultureIgnoreCase));
            if (matchingFile == null)
            {
                if (libraryFile.EndsWith("ota.a"))
                {
                    //The file is referenced, but not present in the SDK.
                    matchingFile = @"source/ti/net/ota/ota.a";
                }
            }

            if (matchingFile == null)
            {
                //If this triggers for freertos.lib, restart the generator again (the file list is cached before the FreeRTOS library is built)
                throw new Exception($"Could not find {libraryFile} in the CC3220 SDK");
            }

            return DoLookupFrameworkIDForLibraryFile(matchingFile);
        }

        string DoLookupFrameworkIDForLibraryFile(string file)
        {
            string bestMatch = null;
            int bestMatchScore = 0;

            foreach(var fixedFramework in _AllFrameworks)
            {
                foreach(var job in fixedFramework.CopyJobs)
                {
                    var source = job.SourceFolder.Replace("$$BSPGEN:INPUT_DIR$$\\", "");
                    if (file.StartsWith(source.Replace('\\', '/'), StringComparison.InvariantCultureIgnoreCase))
                    {
                        //Some subdirectories of the "source/ti/net" framework are wrapped as separate frameworks. Don't resolve to the "net" framework for them.
                        if (source.Length > bestMatchScore)
                        {
                            bestMatchScore = source.Length;
                            bestMatch = fixedFramework.ID;
                        }
                    }
                }
            }

            if (bestMatch == null)
                _UnresolvedLibraries.Add(file);

            return bestMatch;
        }

        public void ThrowIfUnresolvedLibrariesFound()
        {
            if (_UnresolvedLibraries.Count > 0)
            {
#if UNUSED
                File.WriteAllLines(@"e:\temp\libs.txt", _AllLibraryFiles.ToArray());
#endif
                throw new Exception($"Could not locate frameworks corresponding to {_UnresolvedLibraries.Count} libraries");
            }
        }
    }
}
