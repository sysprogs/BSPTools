﻿using BSPEngine;
using BSPEngine.Eclipse;
using Microsoft.SqlServer.Server;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;

namespace STM32ProjectImporter
{
    public class STM32CubeMXProjectImporter : IReconfigurableProjectImporter
    {
        public string Name => "STM32CubeMX";

        public string ImportCommandText => "Import an existing STM32CubeMX Project (GPDSC)";

        public string ProjectFileFilter => "GPDSC Files|*.gpdsc";
        public string HelpText => "Show a tutorial about importing STM32CubeMX projects";
        public string HelpURL => "https://visualgdb.com/tutorials/arm/stm32/cube/";

        public string UniqueID => "com.sysprogs.project_importers.stm32.cubemx";
        public object SettingsControl => null;
        public object Settings { get; set; }

        class STM32CubeExeTool : ProjectReconfigurationTool
        {
            public STM32CubeExeTool(IReconfigurableProjectImporter importer)
                : base(importer, "com.st.stm32cubemx", "STM32CubeMX", "STM32CubeMX Executable|STM32CubeMX.exe")
            {
            }

            public static string FindSTM32CubeMXExe(RegistryKey nonWow64HLKMKey)
            {
                var path = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\STM32CubeMX.exe")?.GetValue(null) as string;
                if (path != null && File.Exists(path))
                    return path;

                path = nonWow64HLKMKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\STM32CubeMX.exe")?.GetValue(null) as string;
                if (path != null && File.Exists(path))
                    return path;

                var command = nonWow64HLKMKey.OpenSubKey(@"SOFTWARE\Classes\iocFile\shell\open\command")?.GetValue(null) as string;
                if (command != null)
                {
                    string marker = "STM32CubeMX.exe";
                    int idx = command.IndexOf(marker, StringComparison.InvariantCultureIgnoreCase);
                    if (idx != -1)
                    {
                        path = command.Substring(0, idx + marker.Length).Trim(' ', '\t', '\"');
                        if (File.Exists(path))
                            return path;
                    }
                }

                return null;
            }

            public override string TryDetectLocation(RegistryKey nonWow64HLKMKey) => FindSTM32CubeMXExe(nonWow64HLKMKey);
        }

        class JDKJavaTool : ProjectReconfigurationTool
        {
            public JDKJavaTool(IReconfigurableProjectImporter importer)
                : base(importer, "com.java.jdk", "JDK", "Java Executable|java.exe")
            {
            }

            public override string TryDetectLocation(RegistryKey nonWow64HLKMKey)
            {
                var exe = STM32CubeExeTool.FindSTM32CubeMXExe(nonWow64HLKMKey);
                if (exe != null && File.Exists(exe))
                {
                    var javaExe = Path.Combine(Path.GetDirectoryName(exe), @"jre\bin\java.exe");
                    if (File.Exists(javaExe))
                        return javaExe;
                }

                var version = nonWow64HLKMKey.OpenSubKey(@"SOFTWARE\JavaSoft\JDK")?.GetValue("CurrentVersion") as string;
                if (version == null)
                    return version;

                var dir = nonWow64HLKMKey.OpenSubKey(@"SOFTWARE\JavaSoft\JDK\" + version)?.GetValue("JavaHome") as string;
                if (dir != null)
                {
                    var java = Path.Combine(dir, "bin\\java.exe");
                    if (File.Exists(java))
                        return java;
                }

                return null;
            }
        }

        readonly STM32CubeExeTool _STM32CubeMX;
        readonly JDKJavaTool _Java;

        public STM32CubeMXProjectImporter()
        {
            _STM32CubeMX = new STM32CubeExeTool(this);
            _Java = new JDKJavaTool(this);
        }

        public ProjectReconfigurationTool[] ReconfigurationTools => new ProjectReconfigurationTool[] { _STM32CubeMX, _Java };

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

        static void FixInvalidPathsRecursively(ImportedExternalProject.VirtualDirectory dir, string baseDir, ref Dictionary<string, string> allFilesUnderProjectDir)
        {
            foreach (var file in dir.Files)
            {
                if (!File.Exists(file.FullPath))
                {
                    if (allFilesUnderProjectDir == null)
                    {
                        allFilesUnderProjectDir = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
                        foreach (var fn in Directory.GetFiles(baseDir, "*", SearchOption.AllDirectories))
                        {
                            var key = Path.GetFileName(fn);
                            if (allFilesUnderProjectDir.ContainsKey(key))
                                allFilesUnderProjectDir[key] = null;
                            else
                                allFilesUnderProjectDir[key] = fn;
                        }
                    }

                    if (allFilesUnderProjectDir.TryGetValue(Path.GetFileName(file.FullPath), out var fullPath) && fullPath != null)
                        file.FullPath = fullPath;
                }
            }

            foreach (var subdir in dir.Subdirectories ?? new List<ImportedExternalProject.VirtualDirectory>())
                FixInvalidPathsRecursively(subdir, baseDir, ref allFilesUnderProjectDir);
        }


        //STM32CubeMX v5.3.0 does not reference some of the FreeRTOS-specific files and references an incorrect system file.
        //The method below detects and fixes this condition.
        static void ApplyFreeRTOSFixes(ImportedExternalProject.ConstructedVirtualDirectory dir, HashSet<string> includeDirs, ref PropertyDictionary2 mcuConfiguration)
        {
            List<FlatFileReference> allFiles = new List<FlatFileReference>();
            FillFlatFileCollectionRecursively(dir, allFiles);

            var fileListsByName = allFiles.GroupBy(f => Path.GetFileName(f.File.FullPath), StringComparer.InvariantCultureIgnoreCase).ToDictionary(g => g.Key, StringComparer.InvariantCultureIgnoreCase);

            if (!fileListsByName.TryGetValue("queue.c", out var queueFiles) || queueFiles.Count() == 0)
                return; //Could not find the FreeRTOS base directory

            var queueCFile = queueFiles.First();
            string baseDir = Path.GetFullPath(Path.GetDirectoryName(queueCFile.File.FullPath));

            string portFile = FindAndAddFileIfMissing(fileListsByName, "port.c", Path.Combine(baseDir, "portable"), queueCFile.Directory, includeDirs);
            FindAndAddFileIfMissing(fileListsByName, "cmsis_os.c", baseDir, queueCFile.Directory, includeDirs);

            foreach (var file in fileListsByName.SelectMany(g => g.Value))
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

        private static string FindAndAddFileIfMissing(Dictionary<string, IGrouping<string, FlatFileReference>> fileListsByName, string fileName, string baseDir, ImportedExternalProject.ConstructedVirtualDirectory directoryToAddFile, HashSet<string> includeDirs)
        {
            if (fileListsByName.TryGetValue(fileName, out var group))
                return group.FirstOrDefault().File?.FullPath;

            if (!Directory.Exists(baseDir))
                return null;

            string foundFile = Directory.GetFiles(baseDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (foundFile != null)
            {
                directoryToAddFile.AddFile(foundFile, false);
                includeDirs.Add(Path.GetDirectoryName(foundFile));
            }

            return foundFile;
        }

        struct ParsedCubeProject
        {
            public ImportedExternalProject.ConstructedVirtualDirectory RootDirectory;
            public string DeviceName, InternalDeviceName;
            public HashSet<string> IncludeDirectories;
            public HashSet<string> PreprocessorMacros;
            public PropertyDictionary2 MCUConfiguration;
            public string CFLAGS, LinkerScript;

            public HashSet<string> AllFiles;
        }

        ParsedCubeProject ParseProjectFile(string projectFile, bool importingReconfigurableProject)
        {
            XmlDocument xml = new XmlDocument();
            xml.Load(projectFile);

            ParsedCubeProject result = new ParsedCubeProject
            {
                RootDirectory = new ImportedExternalProject.ConstructedVirtualDirectory()
            };

            result.DeviceName = result.InternalDeviceName = (xml.SelectSingleNode("package/generators/generator/select/@Dname") as XmlAttribute)?.Value;
            if (result.DeviceName == null)
                throw new Exception("Failed to extract the device name from " + projectFile);

            result.DeviceName = result.DeviceName.TrimEnd('x');
            result.DeviceName = result.DeviceName.Substring(0, result.DeviceName.Length - 1);

            HashSet<string> allHeaderDirs = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            string baseDir = Path.GetDirectoryName(projectFile);
            ImportedExternalProject.ConstructedVirtualDirectory rootDir = new ImportedExternalProject.ConstructedVirtualDirectory();

            foreach (var file in xml.SelectNodes("package/generators/generator/project_files/file").OfType<XmlElement>())
            {
                string category = file.GetAttribute("category");
                string name = file.GetAttribute("name");

                if (category == "header")
                    allHeaderDirs.Add(Path.GetDirectoryName(name));

                result.RootDirectory.AddFile(Path.Combine(baseDir, name), category == "header");
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

                    if (category == "sourceAsm" && name.StartsWith("startup_", StringComparison.InvariantCultureIgnoreCase) && !importingReconfigurableProject)
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
                                result.RootDirectory.ProvideSudirectory(path).AddFile(fn, category == "header");
                            }
                        }
                    }
                    else
                        result.RootDirectory.ProvideSudirectory(path).AddFile(Path.Combine(baseDir, relativePath), category == "header");
                }
            }

            if (importingReconfigurableProject)
                result.PreprocessorMacros = new HashSet<string>();
            else
                result.PreprocessorMacros = new HashSet<string> { "$$com.sysprogs.bspoptions.primary_memory$$_layout", "$$com.sysprogs.stm32.hal_device_family$$" };

            result.IncludeDirectories = new HashSet<string>();

            foreach (var dir in allHeaderDirs)
                result.IncludeDirectories.Add(Path.GetFullPath(Path.Combine(baseDir, dir)));

            if (hasFreeRTOS)
            {
                result.PreprocessorMacros.Add("USE_FREERTOS");
                ApplyFreeRTOSFixes(result.RootDirectory, result.IncludeDirectories, ref result.MCUConfiguration);
            }

            Dictionary<string, string> temporaryExistingFileCollection = null;
            FixInvalidPathsRecursively(result.RootDirectory, baseDir, ref temporaryExistingFileCollection);

            result.AllFiles = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var f in result.RootDirectory.AllFilesRecursively)
                result.AllFiles.Add(Path.GetFullPath(f.FullPath));

            if (importingReconfigurableProject)
            {
                string makefile = Path.Combine(Path.GetDirectoryName(projectFile), "Makefile");
                if (File.Exists(makefile))
                {
                    AdjustImportedProjectFromMakefile(ref result, makefile);
                }
            }

            return result;
        }

        bool ExpandValues(ref string[] inputs, Dictionary<string, string[]> dict)
        {
            List<string> result = new List<string>();
            int expanded = 0;
            foreach (var t in inputs)
            {
                if (t.StartsWith("$(") && t.EndsWith(")"))
                {
                    expanded++;
                    if (dict.TryGetValue(t.Substring(2, t.Length - 3), out var foundValues))
                        result.AddRange(foundValues);
                }
                else
                    result.Add(t);
            }
            inputs = result.ToArray();
            return expanded > 0;
        }

        void AdjustImportedProjectFromMakefile(ref ParsedCubeProject parsedProject, string makefile)
        {
            var baseDir = Path.GetDirectoryName(makefile);

            Dictionary<string, string[]> listsByKey = ExtractListsFromSTM32CubeMXMakefile(makefile);

            if (listsByKey.TryGetValue("C_DEFS", out var values))
            {
                foreach (var v in values)
                    if (v.StartsWith("-D"))
                        parsedProject.PreprocessorMacros.Add(v.Substring(2));
            }

            if (listsByKey.TryGetValue("C_INCLUDES", out values))
            {
                foreach (var v in values)
                    if (v.StartsWith("-I"))
                        parsedProject.IncludeDirectories.Add(Path.GetFullPath(Path.Combine(baseDir, v.Substring(2))));
            }

            if (listsByKey.TryGetValue("LDSCRIPT", out values) && values.Length == 1)
            {
                parsedProject.LinkerScript = Path.GetFullPath(Path.Combine(baseDir, values[0]));
            }

            if (listsByKey.TryGetValue("MCU", out values))
            {
                for (int i = 0; i < 10; i++)
                {
                    if (!ExpandValues(ref values, listsByKey))
                        break;
                }

                parsedProject.CFLAGS = string.Join(" ", values);
            }

            if (listsByKey.TryGetValue("C_SOURCES", out values))
            {
                //GPDSC files generated by STM32CubeMX are often inaccurate and buggy, so we take the data from the Makefile instead
                parsedProject.AllFiles.RemoveWhere(f => f.EndsWith(".c", StringComparison.InvariantCultureIgnoreCase));

                foreach (var src in values)
                    parsedProject.AllFiles.Add(Path.GetFullPath(Path.Combine(baseDir, src)));
            }

            if (listsByKey.TryGetValue("ASM_SOURCES", out values))
            {
                parsedProject.AllFiles.RemoveWhere(f => f.EndsWith(".s", StringComparison.InvariantCultureIgnoreCase));

                foreach (var src in values)
                    parsedProject.AllFiles.Add(Path.GetFullPath(Path.Combine(baseDir, src)));
            }
        }

        private static Dictionary<string, string[]> ExtractListsFromSTM32CubeMXMakefile(string makefile)
        {
            Dictionary<string, string[]> listsByKey = new Dictionary<string, string[]>();
            Regex rgDefinition = new Regex(@"^([A-Za-z0-9_-]+) *= *(.*)$");
            List<string> builtList = null;
            string listKey = null;
            foreach (var line in File.ReadAllLines(makefile))
            {
                var trimmedLine = line.Trim();
                if (builtList != null)
                {
                    var token = trimmedLine.Trim('\\', ' ', '\t', '\r');
                    if (token != "")
                        builtList.Add(token);

                    if (!trimmedLine.EndsWith("\\"))
                    {
                        listsByKey[listKey] = builtList.ToArray();
                        builtList = null;
                    }
                }
                else
                {
                    var m = rgDefinition.Match(line);
                    if (m.Success)
                    {
                        listKey = m.Groups[1].Value;
                        builtList = new List<string>();

                        var value = m.Groups[2].Value;
                        var token = value.Trim('\\', ' ', '\t', '\r');
                        if (token != "")
                            builtList.AddRange(token.Split(' '));

                        if (!value.EndsWith("\\"))
                        {
                            listsByKey[listKey] = builtList.ToArray();
                            builtList = null;
                        }
                    }
                }
            }

            return listsByKey;
        }

        public ImportedExternalProject ImportProject(ProjectImportParameters parameters, IProjectImportService service)
        {
            var parsedProject = ParseProjectFile(parameters.ProjectFile, false);

            return new ImportedExternalProject
            {
                DeviceNameMask = new Regex(parsedProject.DeviceName.Replace("x", ".*") + ".*"),
                OriginalProjectFile = parameters.ProjectFile,
                RootDirectory = parsedProject.RootDirectory,
                GNUTargetID = "arm-eabi",
                ReferencedFrameworks = new string[0],   //Unless this is explicitly specified, VisualGDB will try to reference the default frameworks (STM32 HAL) that will conflict with the STM32CubeMX-generated files.

                MCUConfiguration = parsedProject.MCUConfiguration,

                Configurations = new[]
                {
                    new ImportedExternalProject.ImportedConfiguration
                    {
                        Settings = new ImportedExternalProject.InvariantProjectBuildSettings
                        {
                            IncludeDirectories = parsedProject.IncludeDirectories.ToArray(),
                            PreprocessorMacros = parsedProject.PreprocessorMacros.ToArray()
                        }
                    }
                }
            };
        }

        static string MakeBSPPath(string path, string baseDir)
        {
            if (path == null)
                return null;

            path = Path.GetFullPath(path);

            if (path.StartsWith(baseDir, StringComparison.InvariantCultureIgnoreCase))
                return "$$SYS:BSP_ROOT$$" + path.Substring(baseDir.Length).Replace('\\', '/');
            else
                return path.Replace('\\', '/');
        }

        static bool FilePathValidAndExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        public MCUDefinitionFromProject GenerateMCUDefinitionFromProject(ReconfigurableProjectImportParameters parameters, IProjectImportService service)
        {
            var dir = Path.GetDirectoryName(parameters.ProjectFile);

            if (EclipseProject.ExistsInDirectory(dir) || parameters.TargetSubdirectory != null)
            {
                string projectDir = dir;
                if (!string.IsNullOrEmpty(parameters.TargetSubdirectory))
                    projectDir = Path.Combine(dir, parameters.TargetSubdirectory);

                return GenerateMCUDefinitionFromSTM32CubeIDE(service, new EclipseProject(projectDir, service?.Logger), dir, MultipleConfigurationResolutionMode.Last);
            }
            else
                return GenerateMCUDefinitionFromGPDSC(service, Path.ChangeExtension(parameters.ProjectFile, ".gpdsc"));
        }

        MCUDefinitionFromProject GenerateMCUDefinitionFromGPDSC(IProjectImportService service, string gpdscFile)
        {
            var parsedProject = ParseProjectFile(gpdscFile, true);
            string baseDir = Path.GetFullPath(Path.GetDirectoryName(gpdscFile));

            var allExistingFiles = parsedProject.AllFiles.Where(FilePathValidAndExists).ToArray();

            var mcu = new MCU
            {
                ID = parsedProject.DeviceName,
                AdditionalSourceFiles = allExistingFiles.Where(f => f.EndsWith(".h", StringComparison.InvariantCultureIgnoreCase)).Select(f => MakeBSPPath(f, baseDir)).ToArray(),
                AdditionalHeaderFiles = allExistingFiles.Where(f => !f.EndsWith(".h", StringComparison.InvariantCultureIgnoreCase)).Select(f => MakeBSPPath(f, baseDir)).ToArray(),
                CompilationFlags = new ToolFlags
                {
                    IncludeDirectories = parsedProject.IncludeDirectories.Select(d => MakeBSPPath(d, baseDir)).ToArray(),
                    PreprocessorMacros = parsedProject.PreprocessorMacros.ToArray(),
                    COMMONFLAGS = parsedProject.CFLAGS,
                    LinkerScript = MakeBSPPath(parsedProject.LinkerScript, baseDir),
                }
            };

            return new MCUDefinitionFromProject
            {
                MCU = mcu,
                VirtualFolderStructure = parsedProject.RootDirectory,
            };
        }

        enum MultipleConfigurationResolutionMode
        {
            First,
            Last,
        }

        MCUDefinitionFromProject GenerateMCUDefinitionFromSTM32CubeIDE(IProjectImportService service, EclipseProject project, string baseDir, MultipleConfigurationResolutionMode mode)
        {
            EclipseProject.CConfiguration configuration;
            if (mode == MultipleConfigurationResolutionMode.First)
                configuration = project.NonReleaseConfigurationsIfAny.FirstOrDefault();
            else
                configuration = project.NonReleaseConfigurationsIfAny.LastOrDefault();

            if (configuration == null)
                throw new Exception($"{project.CProjectFile} does not contain any importable configurations");
            var options = SW4STM32ProjectParserBase.ExtractSTM32CubeIDEOptions(configuration);

            List<string> cflags = new List<string>();
            var tools = configuration.RequireTools(EclipseTool.None);

            XmlElement mcuNode = LoadFamilyList(service, out string xmlFile).SelectSingleNode($"Families/Family/SubFamily/Mcu[@RefName='{options.MCU}']") as XmlElement ?? throw new Exception($"Could not locate '{options.MCU}' in {xmlFile}");
            var core = mcuNode.SelectSingleNode("Core")?.InnerText ?? throw new Exception($"The definition for '{options.MCU}' does not specify the core");

            if (options.PreprocessorMacros?.Contains("CORE_CM4") == true)
                cflags.Add("-mcpu=cortex-m4");  //This project is targeting the Cortex-M4 core of a multi-core device
            else
                cflags.Add("-mcpu=" + TranslateCoreName(core));

            string fpuPrefix = "com.st.stm32cube.ide.mcu.gnu.managedbuild.option.fpu.value.";
            string fpuValue = tools.ReadOptionalValue("com.st.stm32cube.ide.mcu.gnu.managedbuild.option.fpu");
            if (!string.IsNullOrEmpty(fpuValue))
            {
                if (!fpuValue.StartsWith(fpuPrefix))
                    throw new Exception("Unknown FPU type: " + fpuValue);
                cflags.Add("-mfpu=" + fpuValue.Substring(fpuPrefix.Length));
            }

            string abiPrefix = "com.st.stm32cube.ide.mcu.gnu.managedbuild.option.floatabi.value.";
            string abiValue = tools.ReadOptionalValue("com.st.stm32cube.ide.mcu.gnu.managedbuild.option.floatabi");
            if (!string.IsNullOrEmpty(abiValue))
            {
                if (!abiValue.StartsWith(abiPrefix))
                    throw new Exception("Unknown FPU type: " + abiValue);
                cflags.Add("-m" + abiValue.Substring(abiPrefix.Length) + "-float");
            }

            var mcu = new MCU
            {
                ID = options.MCU,
                AdditionalSourceFiles = options.SourceFiles.Where(f => f.FullPath.EndsWith(".h", StringComparison.InvariantCultureIgnoreCase)).Select(f => MakeBSPPath(f.FullPath, baseDir)).ToArray(),
                AdditionalHeaderFiles = options.SourceFiles.Where(f => !f.FullPath.EndsWith(".h", StringComparison.InvariantCultureIgnoreCase)).Select(f => MakeBSPPath(f.FullPath, baseDir)).ToArray(),
                CompilationFlags = new ToolFlags
                {
                    IncludeDirectories = options.IncludeDirectories.Select(d => MakeBSPPath(d, baseDir)).ToArray(),
                    PreprocessorMacros = options.PreprocessorMacros,
                    COMMONFLAGS = string.Join(" ", cflags.ToArray()),
                    LinkerScript = MakeBSPPath(options.LinkerScript, baseDir),
                }
            };

            return new MCUDefinitionFromProject
            {
                MCU = mcu,
            };
        }

        string TranslateCoreName(string core)
        {
            const string prefix = "Arm Cortex-";
            if (!core.StartsWith(prefix, StringComparison.InvariantCultureIgnoreCase))
                throw new Exception("Unknown core: " + core);

            string shortCore = core.Substring(prefix.Length);
            return "cortex-" + shortCore.ToLower().Replace("+", "plus");
        }

        public ReconfigurationToolInvocation GetToolLaunchInfo(IProjectImportService service, ProjectReconfigurationContext context)
        {
            var javaExe = service.LocateTool(_Java);
            var stm32CubeMX = service.LocateTool(_STM32CubeMX);

            string args = "";
            string temporaryScriptFile = null;
            string text = "Launching STM32CubeMX...";
            string checkedFile = null;

            switch (context.Reason)
            {
                case ProjectReconfigurationReason.RegenerateFiles:
                    temporaryScriptFile = service.GetTemporaryFileName();

                    if (!File.Exists(Path.ChangeExtension(context.ProjectFile, ".gpdsc")))
                    {
                        //Use the new STM32CubeIDE-based workflow

                        File.WriteAllLines(temporaryScriptFile, new[]
                        {
                            "project toolchain \"STM32CubeIDE\"",
                            "project generateunderroot 1",
                            "project generate",
                            "exit",
                        });

                        checkedFile = Path.Combine(Path.GetDirectoryName(context.ProjectFile), ".project");
                    }
                    else
                    {
                        PatchIOCFileIfNeeded(context.ProjectFile, service.GUI);
                        File.WriteAllLines(temporaryScriptFile, new[]
                            {
                            "project toolchain \"Makefile\"",
                            "project generate",
                            "project toolchain \"Other Toolchains (GPDSC)\"",
                            "project generate",
                            "exit",
                            });

                        checkedFile = Path.ChangeExtension(context.ProjectFile, ".gpdsc");
                    }

                    args = $"\"{context.ProjectFile}\" -s \"{temporaryScriptFile}\"";
                    text = "Regenerating project...";
                    break;
                case ProjectReconfigurationReason.CreateNewProject:
                    temporaryScriptFile = service.GetTemporaryFileName();
                    File.WriteAllLines(temporaryScriptFile, new[]
                    {
                        $"load {context.DeviceID}",
                        "project name " + Path.GetFileNameWithoutExtension(context.ProjectFile),
                        $"project path \"{Path.GetDirectoryName(context.ProjectFile)}\"",
                        "project toolchain \"STM32CubeIDE\"",
                        "project generateunderroot 1",
                        "SetStructure Advanced",
                        $"config saveas \"{context.ProjectFile}\"",
                    });

                    args = $" -s \"{temporaryScriptFile}\"";
                    text = "Waiting for STM32CubeMX...";
                    break;
                case ProjectReconfigurationReason.EditConfigurationInteractively:
                    args = $"\"{context.ProjectFile}\"";
                    break;
                default:
                    throw new Exception("Invalid tool invocation mode");
            }

            var result = new ReconfigurationToolInvocation
            {
                CommandLine = new CommandLineToolLaunchInfo
                {
                    Command = javaExe,
                    Arguments = $"-jar \"{stm32CubeMX}\" {args}",
                    WorkingDirectory = Path.GetDirectoryName(context.ProjectFile),
                },

                Title = text,
                ExpectedOutputFile = checkedFile,
            };

            if (temporaryScriptFile != null)
                result.Finalizer = () => File.Delete(temporaryScriptFile);

            return result;
        }

        private void PatchIOCFileIfNeeded(string iocFile, IBasicGUIService gui)
        {
            try
            {
                if (!File.Exists(iocFile))
                    return;

                var lines = File.ReadAllLines(iocFile);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("ProjectManager.MainLocation="))
                    {
                        if (gui.Prompt($"{Path.GetFileName(iocFile)} overrides the location of the source directory. This may break project generation.\r\nDo you want to reset it to the default value?", MessageBoxIcon.Question))
                        {
                            lines[i] = "#" + lines[i];
                            File.WriteAllLines(iocFile, lines);
                        }

                        return;
                    }
                }
            }
            catch { }
        }

        XmlDocument LoadFamilyList(IProjectImportService service, out string xmlFile)
        {
            var stm32cubeMXExe = service.LocateTool(_STM32CubeMX);
            xmlFile = Path.Combine(Path.GetDirectoryName(stm32cubeMXExe), @"db\mcu\families.xml");

            var xml = new XmlDocument();
            xml.Load(xmlFile);
            return xml;
        }

        const string MCUIDAttribute = "RefName";

        public MCU[] LoadMCUList(IProjectImportService service)
        {
            List<MCU> result = new List<MCU>();

            var xml = LoadFamilyList(service, out _);

            foreach (var family in xml.DocumentElement.SelectNodes("Family").OfType<XmlElement>())
            {
                var familyName = family.GetAttribute("Name");
                foreach (var subfamily in family.SelectNodes("SubFamily").OfType<XmlElement>())
                {
                    var subfamilyName = subfamily.GetAttribute("Name");
                    foreach (var mcu in subfamily.SelectNodes("Mcu").OfType<XmlElement>())
                    {
                        var mcuName = mcu.GetAttribute("Name");
                        var refName = mcu.GetAttribute(MCUIDAttribute);
                        if (!string.IsNullOrEmpty(mcuName) && !string.IsNullOrEmpty(refName))
                        {
                            var mcuObject = new MCU
                            {
                                ID = refName,
                                //UserFriendlyName = mcuName,
                            };

                            int.TryParse(mcu.SelectSingleNode("Flash")?.InnerText, out mcuObject.FLASHSize);
                            int.TryParse(mcu.SelectSingleNode("Ram")?.InnerText, out mcuObject.RAMSize);

                            mcuObject.FLASHSize *= 1024;
                            mcuObject.RAMSize *= 1024;
                            mcuObject.HierarchicalPath = $"{familyName}\\{subfamilyName}";

                            result.Add(mcuObject);
                        }
                    }
                }
            }

            return result.ToArray();
        }

        public string[] LocateTargetSubdirectories(IProjectImportService service, ProjectReconfigurationContext context)
        {
            var baseDir = Path.GetDirectoryName(context.ProjectFile);
            if (File.Exists(Path.Combine(baseDir, ".project")) && !EclipseProject.ExistsInDirectory(baseDir))
            {
                List<string> result = new List<string>();
                foreach (var subdir in Directory.GetDirectories(baseDir))
                    if (EclipseProject.ExistsInDirectory(subdir))
                        result.Add(Path.GetFileName(subdir));

                if (result.Count > 0)
                    return result.ToArray();
            }

            return null;
        }
    }
}
