using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace KeilProjectImporter
{
    public class KeilProjectImporter : IExternalProjectImporter
    {
        public string Name => "Keil";

        public string ImportCommandText => "Import an existing Keil Project";

        public string ProjectFileFilter => "Keil Project Files|*.uvprojx";
        public string HelpText => null;
        public string HelpURL => null;

        public string UniqueID => "com.sysprogs.project_importers.keil";

        object _SettingsControl;
        public object SettingsControl => _SettingsControl ??= new GUI.KeilImporterSettingsControl();
        public object Settings
        {
            get => _Settings;
            set => _Settings = value as KeilProjectImporterSettings;
        }

        KeilProjectImporterSettings _Settings = new KeilProjectImporterSettings();

        string TryAdjustPath(string baseDir, string path, IProjectImportService service)
        {
            try
            {
                path = path.Trim('\"');
                var finalPath = Path.GetFullPath(Path.Combine(baseDir, path));

                if (!_Settings.UseKeilToolchain)
                {
                    //Try automatically replacing IAR-specific files with GCC-specific versions (this will only work if the directory structure stores them in 'IAR' and 'GCC' subdirectories respectively).
                    if (finalPath.IndexOf("\\RVDS\\", StringComparison.InvariantCultureIgnoreCase) != -1)
                    {
                        var substitute = finalPath.ToLower().Replace("\\rvds\\", "\\gcc\\");
                        if (File.Exists(substitute) || Directory.Exists(substitute))
                            finalPath = substitute;
                    }
                    if (finalPath.EndsWith(".lib", StringComparison.InvariantCultureIgnoreCase) && finalPath.IndexOf("_Keil", StringComparison.InvariantCultureIgnoreCase) != -1)
                    {
                        string dir = Path.GetDirectoryName(finalPath);
                        string fn = Path.GetFileName(finalPath);

                        var substitute = Path.Combine(dir, Path.ChangeExtension(fn.ToLower().Replace("_keil", "_gcc"), ".a"));
                        if (File.Exists(substitute))
                            finalPath = substitute;
                    }
                }
                return finalPath;
            }
            catch (Exception ex)
            {
                service.Logger.LogException(ex, $"Invalid path: {path}, base directory = {baseDir}");
                return null;
            }
        }

        public ImportedExternalProject ImportProject(ProjectImportParameters parameters, IProjectImportService service)
        {
            XmlDocument xml = new XmlDocument();
            xml.Load(parameters.ProjectFile);

            var target = xml.SelectSingleNode("Project/Targets/Target") as XmlElement;
            if (target == null)
                throw new Exception("Failed to locate the target node in " + parameters.ProjectFile);

            string deviceName = (target.SelectSingleNode("TargetOption/TargetCommonOption/Device") as XmlElement)?.InnerText;
            if (deviceName == null)
                throw new Exception("Failed to extract the device name from " + parameters.ProjectFile);

            if (deviceName.EndsWith("x"))
            {
                deviceName = deviceName.TrimEnd('x');
                deviceName = deviceName.Substring(0, deviceName.Length - 1);
            }

            string baseDir = Path.GetDirectoryName(parameters.ProjectFile);
            ImportedExternalProject.ConstructedVirtualDirectory rootDir = new ImportedExternalProject.ConstructedVirtualDirectory();

            foreach (var group in target.SelectNodes("Groups/Group").OfType<XmlElement>())
            {
                string virtualPath = group.SelectSingleNode("GroupName")?.InnerText;
                if (string.IsNullOrEmpty(virtualPath))
                    continue;

                var subdir = rootDir.ProvideSudirectory(virtualPath);
                foreach (var file in group.SelectNodes("Files/File").OfType<XmlElement>())
                {
                    string path = file.SelectSingleNode("FilePath")?.InnerText;
                    string type = file.SelectSingleNode("FileType")?.InnerText;
                    if (type == "2" && !_Settings.UseKeilToolchain)
                    {
                        //This is an assembly file. Keil uses a different assembly syntax than GCC, so we cannot include this file into the project.
                        //The end user will need to include a GCC-specific replacement manually (unless this is the startup file, in which case VisualGDB
                        //automatically includes a GCC-compatible replacement).
                        continue;
                    }
                    if (string.IsNullOrEmpty(path))
                        continue;

                    var adjustedPath = TryAdjustPath(baseDir, path, service);
                    subdir.AddFile(adjustedPath, type == "5");
                }
            }

            List<string> macros = new List<string>();
            if (!_Settings.UseKeilToolchain)
                macros.Add("$$com.sysprogs.bspoptions.primary_memory$$_layout");

            List<string> includeDirs = new List<string>();

            var optionsNode = target.SelectSingleNode("TargetOption/TargetArmAds/Cads/VariousControls");
            if (optionsNode != null)
            {
                macros.AddRange((optionsNode.SelectSingleNode("Define")?.InnerText ?? "").Split(',').Select(m => m.Trim()).Where(m => m != ""));
                includeDirs.AddRange((optionsNode.SelectSingleNode("IncludePath")?.InnerText ?? "")
                    .Split(';')
                    .Select(p => TryAdjustPath(baseDir, p.Trim(), service))
                    .Where(p => p != null));
            }

            optionsNode = target.SelectSingleNode("TargetOption/TargetArmAds/LDads");
            string linkerScript = null;
            if (optionsNode != null)
            {
                string scatterFile = optionsNode.SelectSingleNode("ScatterFile")?.InnerText;
                if (!string.IsNullOrEmpty(scatterFile) && _Settings.UseKeilToolchain)
                    linkerScript = TryAdjustPath(baseDir, scatterFile.Trim(), service);
            }

            ToolchainSubtype[] subtypes;
            if (_Settings.UseKeilToolchain)
                subtypes = new ToolchainSubtype[] { ToolchainSubtype.ARMCC, ToolchainSubtype.ARMClang };
            else
                subtypes = new ToolchainSubtype[] { ToolchainSubtype.GCC };

            return new ImportedExternalProject
            {
                DeviceNameMask = new Regex(deviceName.Replace("x", ".*") + ".*"),
                OriginalProjectFile = parameters.ProjectFile,
                RootDirectory = rootDir,
                GNUTargetID = _Settings.UseKeilToolchain ? "arm-none-eabi" : "arm-eabi",
                ReferencedFrameworks = new string[0],   //Unless this is explicitly specified, VisualGDB will try to reference the default frameworks (STM32 HAL) that will conflict with the STM32CubeMX-generated files.

                Configurations = new[]
                {
                    new ImportedExternalProject.ImportedConfiguration
                    {
                        Settings = new ImportedExternalProject.InvariantProjectBuildSettings
                        {
                            IncludeDirectories = includeDirs.ToArray(),
                            PreprocessorMacros = macros.ToArray(),
                            LinkerScript = linkerScript,
                        }
                    }
                },

                SupportedToolchainSubtypes = subtypes,
            };
        }
    }

    public class KeilProjectImporterSettings
    {
        public bool UseKeilToolchain { get; set; }
    }
}
