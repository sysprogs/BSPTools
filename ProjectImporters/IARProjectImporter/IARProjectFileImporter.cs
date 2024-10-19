using System;
using BSPEngine;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.IO;
using System.Text.RegularExpressions;

namespace IARProjectFileImporter
{
    public class IARProjectImporter : IExternalProjectImporter
    {
        public List<ImportedExternalProject.ImportedFile> GetFilesInGroup(XmlNode pGroup)
        {
            List<ImportedExternalProject.ImportedFile> result = new List<ImportedExternalProject.ImportedFile>();

            foreach (XmlNode fileNode in pGroup.SelectNodes("file"))
            {
                HashSet<string> excludedConfigurations = new HashSet<string>();
                foreach (XmlNode ndexc in fileNode.SelectNodes("excluded/configuration"))
                    excludedConfigurations.Add(ndexc.InnerText);

                List<ImportedExternalProject.FileConfiguration> configurations = new List<ImportedExternalProject.FileConfiguration>();

                foreach (XmlNode cfg in fileNode.SelectNodes("configuration"))
                {
                    var tCfg = ExtractInvariantBuildSettings(cfg);
                    ImportedExternalProject.FileConfiguration fileConfiguration = new ImportedExternalProject.FileConfiguration
                    {
                        ConfigurationName = cfg.SelectSingleNode("name").InnerText,
                        Settings = tCfg,
                    };

                    if (excludedConfigurations.Contains(fileConfiguration.ConfigurationName))
                    {
                        fileConfiguration.IsExcludedFromBuild = true;
                        excludedConfigurations.Remove(fileConfiguration.ConfigurationName);
                    }
                    else
                        fileConfiguration.IsExcludedFromBuild = false;

                    configurations.Add(fileConfiguration);
                }

                foreach (var ecxcfg in excludedConfigurations)
                {
                    configurations.Add(new ImportedExternalProject.FileConfiguration
                    {
                        ConfigurationName = ecxcfg,
                        Settings = null,
                        IsExcludedFromBuild = true,
                    });
                }

                var file = new ImportedExternalProject.ImportedFile
                {
                    FullPath = ExpandRelativePath(fileNode.SelectSingleNode("name").InnerText),
                    Configurations = configurations.ToList(),
                };

                if (file.FullPath == null)
                    continue;

                string extension = Path.GetExtension(file.FullPath);
                file.IsHeader = extension.StartsWith("h", StringComparison.InvariantCultureIgnoreCase);

                if (!_Settings.UseIARToolchain)
                {
                    //Try automatically replacing IAR-specific files with GCC-specific versions (this will only work if the directory structure stores them in 'IAR' and 'GCC' subdirectories respectively).
                    if (file.FullPath.IndexOf("\\IAR\\", StringComparison.InvariantCultureIgnoreCase) != -1)
                    {
                        var substitute = file.FullPath.ToLower().Replace("\\iar\\", "\\gcc\\");
                        if (File.Exists(substitute))
                            file.FullPath = substitute;
                    }

                    string nameOnly = Path.GetFileName(file.FullPath);
                    if (nameOnly.StartsWith("startup_", StringComparison.InvariantCultureIgnoreCase) && nameOnly.EndsWith(".s", StringComparison.InvariantCultureIgnoreCase))
                    {
                        //IAR startup files are not compatible with gcc and are not needed either as VisualGDB provides its own startup files.
                        continue;
                    }
                }

                result.Add(file);
            }

            return result;
        }

        public ImportedExternalProject.VirtualDirectory ConvertGroupToVirtualDirectoryRecursively(XmlNode projectOrGroup, string name)
        {
            ImportedExternalProject.VirtualDirectory result = new ImportedExternalProject.VirtualDirectory
            {
                Name = name,
                Files = GetFilesInGroup(projectOrGroup),
                Subdirectories = new List<ImportedExternalProject.VirtualDirectory>(),
            };

            foreach (XmlElement subGroup in projectOrGroup.SelectNodes("group"))
            {
                ImportedExternalProject.VirtualDirectory dir = ConvertGroupToVirtualDirectoryRecursively(subGroup, subGroup.SelectSingleNode("name").InnerText);
                result.Subdirectories.Add(dir);
            }

            return result;
        }

        public string ExpandRelativePath(string path)
        {
            if (path == null)
                return null;

            string tmpDir = ProjectDirectory;
            while (path.IndexOf(@"\..") > 0)
            {
                path = path.Remove(path.IndexOf(@"\.."), 3);
                tmpDir = Path.GetDirectoryName(tmpDir);
                if (tmpDir == null)
                    return null;
            }

            path = path.Replace("$PROJ_DIR$", tmpDir);

            return path;
        }

        public ImportedExternalProject.InvariantProjectBuildSettings ExtractInvariantBuildSettings(XmlNode pNode)
        {
            var st = new ImportedExternalProject.InvariantProjectBuildSettings();
            pNode = pNode.SelectSingleNode("settings[starts-with(name, 'ICC')]/data");
            if (pNode != null)
            {
                st.PreprocessorMacros = pNode.SelectNodes("option[name=\"CCDefines\"]/state").OfType<XmlElement>().Select(el => el.InnerText).ToArray();
                st.IncludeDirectories = pNode.SelectNodes("(option[name=\"CCIncludePath2\"]|option[name=\"newCCIncludePaths\"])/state").OfType<XmlElement>().Select(el => ExpandRelativePath(el.InnerText)).Where(d => !string.IsNullOrEmpty(d)).ToArray();
                st.GeneratePreprocessorOutput = pNode.SelectSingleNode("option[name=\"CCPreprocFile\"]/state")?.InnerText == "1" ? true : false;
            }
            return st;
        }

        public string ProjectDirectory;

        public string Name => "IAR";

        public string ImportCommandText => "Import an existing IAR Project";

        public string ProjectFileFilter => "IAR Project Files|*.ewp";
        public string HelpText => null;
        public string HelpURL => null;

        public string UniqueID => "com.sysprogs.project_importers.iar";

        object _SettingsControl;
        public object SettingsControl => _SettingsControl ??= new GUI.IARImporterSettingsControl();
        public object Settings
        {
            get => _Settings;
            set => _Settings = value as IARProjectImporterSettings;
        }

        IARProjectImporterSettings _Settings = new IARProjectImporterSettings();

        public ImportedExternalProject ParseEWPFile(string pFileEwp, IProjectImportService service)
        {
            ProjectDirectory = Path.GetDirectoryName(pFileEwp);

            ToolchainSubtype[] subtypes;
            if (_Settings.UseIARToolchain)
                subtypes = new ToolchainSubtype[] { ToolchainSubtype.IAR };
            else
                subtypes = new ToolchainSubtype[] { ToolchainSubtype.GCC };

            ImportedExternalProject result = new ImportedExternalProject { SupportedToolchainSubtypes = subtypes };
            XmlDocument doc = new XmlDocument();
            doc.Load(pFileEwp);
            string deviceName = "";
            List<ImportedExternalProject.ImportedConfiguration> allConfigurations = new List<ImportedExternalProject.ImportedConfiguration>();
            foreach (XmlElement prjNode in doc.SelectNodes("//project/configuration"))
            {
                ImportedExternalProject.ImportedConfiguration configuration = new ImportedExternalProject.ImportedConfiguration();
                configuration.Name = prjNode.SelectSingleNode($"name").InnerText ?? "NONAME_PRJ";
                allConfigurations.Add(configuration);

                configuration.Settings = ExtractInvariantBuildSettings(prjNode);

                string toolchain = prjNode.SelectSingleNode("toolchain/name")?.InnerText;

                string icfFile = ExpandRelativePath(prjNode.SelectSingleNode("settings/data/option[name=\"IlinkIcfFile\"]/state")?.InnerText);
                string overrideIcfFile = prjNode.SelectSingleNode("settings/data/option[name=\"IlinkIcfOverride\"]/state")?.InnerText;

                configuration.IsStaticLibrary = prjNode.SelectSingleNode("settings/data/option[name=\"GOutputBinary\"]/state")?.InnerText == "1";
                
                string thisDeviceName = prjNode.SelectSingleNode("(settings/data/option[name=\"OGChipSelectEditMenu\"]|settings/data/option[name=\"OGChipSelectMenu\"])/state")?.InnerText?.Split(' ', '\t')[0] ?? "";
                if (!string.IsNullOrEmpty(thisDeviceName))
                    deviceName = thisDeviceName;

                if (_Settings.UseIARToolchain && overrideIcfFile == "1" && !string.IsNullOrEmpty(icfFile))
                    configuration.Settings.LinkerScript = icfFile.Replace("$TOOLKIT_DIR$", "$(ToolchainDir)/" + toolchain);
            }

            if (doc.SelectSingleNode("/project/configuration/toolchain/name")?.InnerText == "ARM")
            {
                result.GNUTargetID = _Settings.UseIARToolchain ? "arm-none-eabi" : "arm-eabi";
            }

            if (allConfigurations.Count == 0)
                service.Logger.LogLine("Warning: No Configurations found in " + pFileEwp);

            result.RootDirectory = ConvertGroupToVirtualDirectoryRecursively(doc.SelectSingleNode("//project") ?? throw new Exception("Failed to locate root project node"), null);

            if (deviceName == "")
                service.Logger.LogLine($"Warning: {pFileEwp} does not specify the device name");

            result.Configurations = allConfigurations.ToArray();
            result.OriginalProjectFile = pFileEwp;

            result.DeviceNameMask = new Regex(".*" + deviceName.Replace('x', '.') + ".*", RegexOptions.IgnoreCase);
            if (result.Configurations.Length == 1)
                result.Configurations[0].Name = null;

            result.ReferencedFrameworks = new string[0];

            return result;
        }

        public ImportedExternalProject ImportProject(ProjectImportParameters parameters, IProjectImportService service)
        {
            return ParseEWPFile(parameters.ProjectFile, service);
        }
    }

    public class IARProjectImporterSettings
    {
        public bool UseIARToolchain { get; set; }
    }
}
