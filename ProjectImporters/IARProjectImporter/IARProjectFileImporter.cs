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
                    FullPath = ExpandPath(fileNode.SelectSingleNode("name").InnerText ?? ""),
                    Configurations = configurations.ToList(),
                };

                string extension = Path.GetExtension(file.FullPath);
                file.IsHeader = extension.StartsWith("h", StringComparison.InvariantCultureIgnoreCase);

                //Try automatically replacing IAR-specific files with GCC-specific versions (this will only work if the directory structure stores them in 'IAR' and 'GCC' subdirectories respectively).
                if (file.FullPath.IndexOf("\\IAR\\", StringComparison.InvariantCultureIgnoreCase) != -1)
                {
                    var substitute = file.FullPath.ToLower().Replace("\\iar\\", "\\gcc\\");
                    if (File.Exists(substitute))
                        file.FullPath = substitute;
                }

                result.Add(file);
            }

            return result;
        }

        public List<ImportedExternalProject.VirtualDirectory> ConvertGroupToVirtualDirectoryRecursively(XmlNode pGroup)
        {
            List<ImportedExternalProject.VirtualDirectory> result = new List<ImportedExternalProject.VirtualDirectory>();

            var xmlNodeList = pGroup.SelectNodes("group");
            if (xmlNodeList != null)
                foreach (XmlElement CntGroup in xmlNodeList)
                {
                    ImportedExternalProject.VirtualDirectory aOutGroup = new ImportedExternalProject.VirtualDirectory();
                    aOutGroup.Files = GetFilesInGroup(CntGroup);
                    string name = CntGroup.SelectSingleNode("name").InnerText;
                    aOutGroup.Name = name;
                    aOutGroup.Subdirectories = ConvertGroupToVirtualDirectoryRecursively(CntGroup);
                    result.Add(aOutGroup);
                }
            return result;
        }

        public string ExpandPath(string pPath)
        {
            string aDirFile = PRJ_DIR;
            while (pPath.IndexOf(@"\..") > 0)
            {
                pPath = pPath.Remove(pPath.IndexOf(@"\.."), 3);
                aDirFile = aDirFile.Remove(aDirFile.LastIndexOf(@"\"));
            }

            pPath = pPath.Replace("$PROJ_DIR$", aDirFile);

            return pPath;
        }

        public ImportedExternalProject.InvariantProjectBuildSettings ExtractInvariantBuildSettings(XmlNode pNode)
        {
            var st = new ImportedExternalProject.InvariantProjectBuildSettings();
            pNode = pNode.SelectSingleNode("settings[name=\"ICCARM\"]/data");
            if (pNode != null)
            {
                st.PreprocessorMacros = pNode.SelectNodes("option[name=\"CCDefines\"]/state").OfType<XmlElement>().Select(el => el.InnerText).ToArray();
                st.IncludeDirectories = pNode.SelectNodes("option[name=\"CCIncludePath2\"]/state").OfType<XmlElement>().Select(el => ExpandPath(el.InnerText)).ToArray();
                st.GeneratePreprocessorOutput = pNode.SelectSingleNode("option[name=\"CCPreprocFile\"]/state")?.InnerText == "1" ? true : false;
            }
            return st;
        }

        public static string PRJ_DIR;

        public string Name => "IAR";

        public string ImportCommandText => "Import an existing IAR Project";

        public string ProjectFileFilter => "IAR Project Files|*.ewp";
        public string HelpText => null;
        public string HelpURL => null;

        public string UniqueID => "com.sysprogs.project_importers.iar";

        public ImportedExternalProject ParseEIPFile(string pFileEwp, IProjectImportService service)
        {
            PRJ_DIR = Path.GetDirectoryName(pFileEwp);
            ImportedExternalProject result = new ImportedExternalProject();
            XmlDocument doc = new XmlDocument();
            doc.Load(pFileEwp);
            string deviceName = "";
            List<ImportedExternalProject.ImportedConfiguration> allConfigurations = new List<ImportedExternalProject.ImportedConfiguration>();
            foreach (XmlElement prjNode in doc.SelectNodes("//project/configuration"))
            {
                ImportedExternalProject.ImportedConfiguration configuration = new ImportedExternalProject.ImportedConfiguration();
                configuration.Name = prjNode.SelectSingleNode($"name").InnerText ?? "NONAME_PRJ";
                allConfigurations.Add(configuration);

                var tPrjSetting = ExtractInvariantBuildSettings(prjNode);

                //string aIcfFile = ExpandPath(prjNode.SelectSingleNode("settings/data/option[name=\"IlinkIcfFile\"]/state").InnerText);
                deviceName = prjNode.SelectSingleNode("settings/data/option[name=\"OGChipSelectEditMenu\"]/state")?.InnerText?.Split(' ', '\t')[0] ?? "";
                //tPrjSetting.LinkerScript = aIcfFile;

                configuration.Settings = tPrjSetting;
            }

            if (allConfigurations.Count == 0)
                service.Logger.LogLine("Warning: No Configurations found in " + pFileEwp);

            ImportedExternalProject.VirtualDirectory RootDirectory = new ImportedExternalProject.VirtualDirectory
            {
                Name = "Root",
                Files = null,
                Subdirectories = ConvertGroupToVirtualDirectoryRecursively(doc.SelectSingleNode("//project"))
            };
            result.RootDirectory = RootDirectory;

            if (deviceName == "")
                service.Logger.LogLine($"Warning: {pFileEwp} does not specify the device name");

            result.Configurations = allConfigurations.ToArray();
            result.OriginalProjectFile = pFileEwp;
            result.DeviceNameMask = new Regex(".*" + deviceName + ".*");
            if (result.Configurations.Length == 1)
                result.Configurations[0].Name = null;

            result.ReferencedFrameworks = new string[0];

            return result;
        }

        public ImportedExternalProject ImportProject(ProjectImportParameters parameters, IProjectImportService service)
        {
            return ParseEIPFile(parameters.ProjectFile, service);
        }
    }
}
