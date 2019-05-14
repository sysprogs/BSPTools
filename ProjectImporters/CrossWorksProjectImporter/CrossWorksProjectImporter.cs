using System;
using BSPEngine;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CrossWorksProjectFileImporter
{
    public class CrossWorksProjectImporter : IExternalProjectImporter
    {
        public string Name => "CrossWorks";

        public string ImportCommandText => "Import an existing CrossWorks Project";

        public string ProjectFileFilter => "CrossWorks Project Files|*.hzp";
        public string HelpText => null;
        public string HelpURL => null;

        public string UniqueID => "com.sysprogs.project_importers.crossworks";

        public object SettingsControl => null;
        public object Settings { get; set; }

        struct SettingsFromBuiltProject
        {
            public string[] AdditionalInputs;
            public string LinkerScript;
        }

        SettingsFromBuiltProject RetrieveSettingsFromBuiltProject(ProjectImportParameters parameters, IProjectImportService service)
        {
            string[] linkerScripts, indFiles;

            for (; ; )
            {
                linkerScripts = Directory.GetFiles(Path.GetDirectoryName(parameters.ProjectFile), Path.GetFileNameWithoutExtension(parameters.ProjectFile) + ".ld", SearchOption.AllDirectories);
                if (linkerScripts.Length == 0)
                    linkerScripts = Directory.GetFiles(Path.GetDirectoryName(parameters.ProjectFile), "*.ld", SearchOption.AllDirectories);
                if (linkerScripts.Length != 0 || service.Context != ProjectImporterInvocationContext.Wizard)
                    break;
                switch (service.GUI.AbortRetryIgnore("Could not find the generated linker script under the project directory. Please try building the project with CrossWorks first."))
                {
                    case System.Windows.Forms.DialogResult.Abort:
                        throw new OperationCanceledException();
                    case System.Windows.Forms.DialogResult.Retry:
                        continue;
                    default:
                        break;
                }
                break;
            }

            for (; ; )
            {
                indFiles = Directory.GetFiles(Path.GetDirectoryName(parameters.ProjectFile), Path.GetFileNameWithoutExtension(parameters.ProjectFile) + ".ind", SearchOption.AllDirectories);
                if (indFiles.Length == 0)
                    indFiles = Directory.GetFiles(Path.GetDirectoryName(parameters.ProjectFile), "*.ind", SearchOption.AllDirectories);

                if (indFiles.Length != 0 || service.Context != ProjectImporterInvocationContext.Wizard)
                    break;
                switch (service.GUI.AbortRetryIgnore("Could not find the generated input list (.ind) under the project directory. Please try building the project with CrossWorks first."))
                {
                    case System.Windows.Forms.DialogResult.Abort:
                        throw new OperationCanceledException();
                    case System.Windows.Forms.DialogResult.Retry:
                        continue;
                    default:
                        break;
                }
                break;
            }

            SettingsFromBuiltProject result = new SettingsFromBuiltProject();
            result.LinkerScript = linkerScripts.FirstOrDefault();
            List<string> inputs = new List<string>();
            foreach(var fn in indFiles)
            {
                try
                {
                    foreach(var rawLine in File.ReadAllLines(fn))
                    {
                        string line = rawLine.Trim(' ', '\t', '\"');
                        try
                        {
                            if (Path.IsPathRooted(line) && File.Exists(line))
                                inputs.Add(line);
                        }
                        catch { }
                    }
                }
                catch(Exception ex)
                {
                    service.Logger.LogException(ex, "failed to load " + fn);
                }
            }

            result.AdditionalInputs = inputs.ToArray();
            return result;
        }

        public ImportedExternalProject ImportProject(ProjectImportParameters parameters, IProjectImportService service)
        {
            Dictionary<string, string> systemDirectories = new Dictionary<string, string>();

            var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Rowley Associates Limited\CrossWorks for ARM\Installer");
            service.Logger.LogLine("Detecting CrossWorks directory...");
            string crossWorksDir = null;
            if (key != null)
            {
                foreach (var kn in key.GetSubKeyNames().OrderByDescending(k => k))
                {
                    service.Logger.LogLine($"Checking {kn}...");

                    using (var subkey = key.OpenSubKey(kn + @"\OrganizationDefaults"))
                    {
                        var destDir = subkey?.GetValue("DestDir") as string;
                        if (destDir != null && Directory.Exists(destDir))
                        {
                            service.Logger.LogLine($"Found {destDir}");
                            systemDirectories["StudioDir"] = crossWorksDir = destDir;
                            break;
                        }
                    }
                }
            }

            service.Logger.LogLine("Detecting CrossWorks AppData directory...");

            var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Rowley Associates Limited\CrossWorks for ARM");
            foreach (var subdir in Directory.GetDirectories(appDataDir).OrderByDescending(d => d))
            {
                string packagesDir = Path.Combine(subdir, "packages");
                service.Logger.LogLine($"Trying {subdir}...");
                if (Directory.Exists(packagesDir))
                {
                    service.Logger.LogLine($"Found {packagesDir}");
                    systemDirectories["TargetsDir"] = packagesDir + @"\targets";
                    systemDirectories["PackagesDir"] = packagesDir;
                    break;
                }
            }

            systemDirectories["ProjectDir"] = Path.GetDirectoryName(parameters.ProjectFile);
            systemDirectories["LibExt"] = "";

            XmlDocument xml = new XmlDocument();
            xml.Load(parameters.ProjectFile);

            var project = xml.DocumentElement.SelectSingleNode("project") as XmlElement ?? throw new Exception("Could not find the project element");
            var commonConfig = project.SelectSingleNode("configuration[@Name='Common']") as XmlElement ?? throw new Exception("Could not find the common configuration element");

            string deviceName = commonConfig.GetAttribute("Target");
            if (string.IsNullOrEmpty(deviceName))
                throw new Exception("Target name is unspecified");

            string[] macros = commonConfig.GetAttribute("c_preprocessor_definitions")?.Split(';');
            string[] includeDirs = (commonConfig.GetAttribute("c_system_include_directories") + ";" + commonConfig.GetAttribute("c_user_include_directories"))?.Split(';');
            if (crossWorksDir != null)
                includeDirs = includeDirs.Concat(new[] { crossWorksDir + "/include" }).ToArray();

            string[] additionalLinkerInputs = commonConfig.GetAttribute("linker_additional_files")?.Split(';');

            macros = macros.Concat(new[] { "__CROSSWORKS_ARM", "STARTUP_FROM_RESET" }).ToArray();

            string frequency = commonConfig.GetAttribute("oscillator_frequency");
            if (frequency?.EndsWith("MHz") == true && int.TryParse(frequency.Substring(0, frequency.Length - 3), out int frequencyInMhz))
                macros = macros.Concat(new[] { "OSCILLATOR_CLOCK_FREQUENCY=" + (frequencyInMhz * 1000000) }).ToArray();

            ImportedExternalProject.ConstructedVirtualDirectory rootDir = new ImportedExternalProject.ConstructedVirtualDirectory();

            var expander = service.CreateVariableExpander(systemDirectories, VariableExpansionSyntax.Makefile);

            SettingsFromBuiltProject extraSettings = RetrieveSettingsFromBuiltProject(parameters, service);
            if (extraSettings.AdditionalInputs != null)
                foreach (var input in extraSettings.AdditionalInputs)
                    rootDir.AddFile(input, false);

            foreach (var lib in additionalLinkerInputs)
            {
                var mappedPath = ExpandVariables(lib, expander, service);
                if (!string.IsNullOrEmpty(mappedPath))
                    rootDir.AddFile(mappedPath, false);
            }

            ImportProjectFolderRecursively(project, rootDir, parameters, expander, service);

            return new ImportedExternalProject
            {
                DeviceNameMask = new Regex(deviceName.Replace("x", ".*") + ".*"),
                OriginalProjectFile = parameters.ProjectFile,
                RootDirectory = rootDir,
                GNUTargetID = "arm-eabi",
                ReferencedFrameworks = new string[0],   //Unless this is explicitly specified, VisualGDB will try to reference the default frameworks (STM32 HAL) that will conflict with the STM32CubeMX-generated files.
                MCUConfiguration = new PropertyDictionary2 { Entries = new[] { new PropertyDictionary2.KeyValue { Key = "com.sysprogs.mcuoptions.ignore_startup_file", Value = "1" } } },

                Configurations = new[]
                {
                    new ImportedExternalProject.ImportedConfiguration
                    {
                        Settings = new ImportedExternalProject.InvariantProjectBuildSettings
                        {
                            IncludeDirectories = includeDirs?.Select(d => ExpandVariables(d, expander, service))?.Where(d => !string.IsNullOrEmpty(d))?.ToArray(),
                            PreprocessorMacros = macros,
                            ExtraLDFLAGS = new[]{ "-nostdlib -Wl,-u_vectors -Wl,-ereset_handler " },
                            LinkerScript = extraSettings.LinkerScript,
                        },
                    }
                }
            };
        }

        private string ExpandVariables(string path, IVariableExpander expander, IProjectImportService service)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            path = expander.ExpandVariables(path);

            if (path.Contains("$("))
                service.Logger.LogLine("Warning: could not expand CrossWorks-specific path: " + path);

            return path;
        }

        private void ImportProjectFolderRecursively(XmlElement projectOrFolder,
            ImportedExternalProject.ConstructedVirtualDirectory constructedDir,
            ProjectImportParameters parameters,
            IVariableExpander expander,
            IProjectImportService service)
        {
            foreach (var el in projectOrFolder.ChildNodes.OfType<XmlElement>())
            {
                if (el.Name == "file")
                {
                    string relPath = ExpandVariables(el.GetAttribute("file_name"), expander, service);
                    if (!string.IsNullOrEmpty(relPath) && !relPath.EndsWith("$(DeviceVectorsFile)"))
                    {
                        string fullPath = Path.Combine(Path.GetDirectoryName(parameters.ProjectFile), relPath);
                        constructedDir.AddFile(fullPath, relPath.EndsWith(".h", StringComparison.InvariantCultureIgnoreCase));
                    }
                }
                else if (el.Name == "folder")
                {
                    string name = el.GetAttribute("Name");
                    if (string.IsNullOrEmpty(name))
                        name = "Subfolder";

                    if (name == "Source Files" || name == "Header Files")
                    {
                        //Visual Studio already provides filters for source/header files, so we don't need to specify them explicitly
                        ImportProjectFolderRecursively(el, constructedDir, parameters, expander, service);
                    }
                    else
                        ImportProjectFolderRecursively(el, constructedDir.ProvideSudirectory(name), parameters, expander, service);
                }
            }
        }
    }
}
