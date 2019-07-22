using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace STM32CubeMXImporter
{
    public class STM32CubeMXProjectImporter : IExternalProjectImporter
    {
        public string Name => "STM32CubeMX";

        public string ImportCommandText => "Import an existing STM32CubeMX Project (GPDSC)";

        public string ProjectFileFilter => "GPDSC Files|*.gpdsc";
        public string HelpText => "Show a tutorial about importing STM32CubeMX projects";
        public string HelpURL => "https://visualgdb.com/tutorials/arm/stm32/cube/";

        public string UniqueID => "com.sysprogs.project_importers.stm32.cubemx";
        public object SettingsControl => null;
        public object Settings { get; set; }


        struct FlatFileReference
        {
            public ImportedExternalProject.ConstructedVirtualDirectory Directory;
            public ImportedExternalProject.ImportedFile File;
        }

        static void FillFlatFileCollectionRecursively(ImportedExternalProject.ConstructedVirtualDirectory dir, List<FlatFileReference> fileList)
        {
            foreach (var file in dir.Files ?? new List<ImportedExternalProject.ImportedFile>())
                fileList.Add(new FlatFileReference { Directory = dir, File = file });
            foreach (var subdir in (dir.Subdirectories ?? new List<ImportedExternalProject.VirtualDirectory>()).OfType<ImportedExternalProject.ConstructedVirtualDirectory>())
                FillFlatFileCollectionRecursively(subdir, fileList);
        }

        //STM32CubeMX v5.3.0 does not reference some of the FreeRTOS-specific files and references an incorrect system file.
        //The method below detects and fixes this condition.
        static void ApplyFreeRTOSFixes(ImportedExternalProject.ConstructedVirtualDirectory dir, ref string[] includeDirs, ref PropertyDictionary2 mcuConfiguration)
        {
            List<FlatFileReference> allFiles = new List<FlatFileReference>();
            FillFlatFileCollectionRecursively(dir, allFiles);

            var fileListsByName = allFiles.GroupBy(f => Path.GetFileName(f.File.FullPath), StringComparer.InvariantCultureIgnoreCase).ToDictionary(g => g.Key, StringComparer.InvariantCultureIgnoreCase);

            if (!fileListsByName.TryGetValue("queue.c", out var queueFiles) || queueFiles.Count() == 0)
                return; //Could not find the FreeRTOS base directory

            var queueCFile = queueFiles.First();
            string baseDir = Path.GetFullPath(Path.GetDirectoryName(queueCFile.File.FullPath));

            string portFile = FindAndAddFileIfMissing(fileListsByName, "port.c", Path.Combine(baseDir, "portable"), queueCFile.Directory, ref includeDirs);
            FindAndAddFileIfMissing(fileListsByName, "cmsis_os.c", baseDir, queueCFile.Directory, ref includeDirs);

            foreach(var file in fileListsByName.SelectMany(g=>g.Value))
            {
                if (Path.GetFileName(file.File.FullPath).StartsWith("system_stm32", StringComparison.InvariantCultureIgnoreCase) && !File.Exists(file.File.FullPath))
                {
                    //Found an incorrectly referenced system file (typically Source\Templates\system_stm32f7xx.c). Replace it with the real path.

                    string foundReplacement = null;

                    foreach (var f2 in fileListsByName.SelectMany(g => g.Value))
                    {
                        string candidatePath = Path.Combine(Path.GetDirectoryName(f2.File.FullPath), Path.GetFileName(file.File.FullPath));
                        if (File.Exists(candidatePath))
                        {
                            foundReplacement = candidatePath;
                            break;
                        }
                    }

                    if (foundReplacement != null)
                    {
                        file.File.FullPath = foundReplacement;
                    }
                }
            }

            if (portFile != null)
            {
                string relPath = portFile.Substring(baseDir.Length);
                if (relPath.Contains("ARM_CM7") || relPath.Contains("ARM_CM4F"))
                {
                    if (mcuConfiguration == null)
                        mcuConfiguration = new PropertyDictionary2();
                    if (mcuConfiguration.Entries == null)
                        mcuConfiguration.Entries = new PropertyDictionary2.KeyValue[0];

                    mcuConfiguration.Entries = mcuConfiguration.Entries.Concat(new[] { new PropertyDictionary2.KeyValue { Key = "com.sysprogs.bspoptions.arm.floatmode", Value = "-mfloat-abi=hard" } }).ToArray();
                }
            }
        }

        private static string FindAndAddFileIfMissing(Dictionary<string, IGrouping<string, FlatFileReference>> fileListsByName, string fileName, string baseDir, ImportedExternalProject.ConstructedVirtualDirectory directoryToAddFile, ref string[] includeDirs)
        {
            if (fileListsByName.TryGetValue(fileName, out var group))
                return group.FirstOrDefault().File?.FullPath;

            if (!Directory.Exists(baseDir))
                return null;

            string foundFile = Directory.GetFiles(baseDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (foundFile != null)
            {
                directoryToAddFile.AddFile(foundFile, false);
                includeDirs = includeDirs.Concat(new[] { Path.GetDirectoryName(foundFile) }).ToArray();
            }

            return foundFile;
        }

        public ImportedExternalProject ImportProject(ProjectImportParameters parameters, IProjectImportService service)
        {
            XmlDocument xml = new XmlDocument();
            xml.Load(parameters.ProjectFile);

            string deviceName = (xml.SelectSingleNode("package/generators/generator/select/@Dname") as XmlAttribute)?.Value;
            if (deviceName == null)
                throw new Exception("Failed to extract the device name from " + deviceName);

            HashSet<string> allHeaderDirs = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            string baseDir = Path.GetDirectoryName(parameters.ProjectFile);
            ImportedExternalProject.ConstructedVirtualDirectory rootDir = new ImportedExternalProject.ConstructedVirtualDirectory();

            foreach (var file in xml.SelectNodes("package/generators/generator/project_files/file").OfType<XmlElement>())
            {
                string category = file.GetAttribute("category");
                string name = file.GetAttribute("name");

                if (category == "header")
                    allHeaderDirs.Add(Path.GetDirectoryName(name));

                rootDir.AddFile(Path.Combine(baseDir, name), category == "header");
            }

            bool hasFreeRTOS = false;

            foreach (var component in xml.SelectNodes("package/components/component").OfType<XmlElement>())
            {
                string group = component.GetAttribute("Cgroup");
                string subGroup = component.GetAttribute("Csub");
                if (subGroup == "FREERTOS")
                    hasFreeRTOS = true;
                foreach (var file in component.SelectNodes("files/file").OfType<XmlElement>())
                {
                    string category = file.GetAttribute("category");
                    string relativePath = file.GetAttribute("name");

                    string condition = file.GetAttribute("condition");
                    if (!string.IsNullOrEmpty(condition))
                    {
                        if (condition == "FreeRTOS")
                        {
                            if (!hasFreeRTOS)
                                continue;
                        }
                        else if (condition != "GCC Toolchain")
                            continue;   //This is a IAR-only or Keil-only file
                    }

                    int idx = relativePath.LastIndexOfAny(new[] { '\\', '/' });
                    string name, dir;
                    if (idx == -1)
                    {
                        name = relativePath;
                        dir = "";
                    }
                    else
                    {
                        name = relativePath.Substring(idx + 1);
                        dir = relativePath.Substring(0, idx);
                    }

                    if (category == "sourceAsm" && name.StartsWith("startup_", StringComparison.InvariantCultureIgnoreCase))
                        continue;   //VisualGDB provides its own startup files for STM32 devices that are compatible with STM32CubeMX-generated files

                    if (category == "header" && dir != "")
                        allHeaderDirs.Add(dir);

                    string path = group;
                    if (!string.IsNullOrEmpty(subGroup))
                        path += "/" + subGroup;

                    if (relativePath.Contains("*"))
                    {
                        string physicalDir = Path.Combine(baseDir, dir);
                        if (Directory.Exists(physicalDir))
                        {
                            foreach (var fn in Directory.GetFiles(physicalDir, name))
                            {
                                rootDir.ProvideSudirectory(path).AddFile(fn, category == "header");
                            }
                        }
                    }
                    else
                        rootDir.ProvideSudirectory(path).AddFile(Path.Combine(baseDir, relativePath), category == "header");
                }
            }

            List<string> macros = new List<string> { "$$com.sysprogs.bspoptions.primary_memory$$_layout", "$$com.sysprogs.stm32.hal_device_family$$" };
            string[] includeDirs = allHeaderDirs.Select(d => Path.Combine(baseDir, d)).ToArray();

            PropertyDictionary2 mcuConfiguration = null;

            if (hasFreeRTOS)
            {
                macros.Add("USE_FREERTOS");
                ApplyFreeRTOSFixes(rootDir, ref includeDirs, ref mcuConfiguration);
            }

            deviceName = deviceName.TrimEnd('x');
            deviceName = deviceName.Substring(0, deviceName.Length - 1);

            return new ImportedExternalProject
            {
                DeviceNameMask = new Regex(deviceName.Replace("x", ".*") + ".*"),
                OriginalProjectFile = parameters.ProjectFile,
                RootDirectory = rootDir,
                GNUTargetID = "arm-eabi",
                ReferencedFrameworks = new string[0],   //Unless this is explicitly specified, VisualGDB will try to reference the default frameworks (STM32 HAL) that will conflict with the STM32CubeMX-generated files.

                MCUConfiguration = mcuConfiguration,

                Configurations = new[]
                {
                    new ImportedExternalProject.ImportedConfiguration
                    {
                        Settings = new ImportedExternalProject.InvariantProjectBuildSettings
                        {
                            IncludeDirectories = includeDirs,
                            PreprocessorMacros = macros.ToArray()
                        }
                    }
                }
            };
        }
    }
}
