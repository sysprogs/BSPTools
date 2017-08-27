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
                    string name = file.GetAttribute("name");
                    if (name.EndsWith(@"\*") && category == "header")
                    {
                        allHeaderDirs.Add(name.Substring(0, name.Length -2));
                        continue;
                    }
                    string condition = file.GetAttribute("condition");
                    if (!string.IsNullOrEmpty(condition) && condition != "GCC Toolchain")
                        continue;   //This is a IAR-only or Keil-only file

                    if (category == "sourceAsm" && Path.GetFileName(name).StartsWith("startup_", StringComparison.InvariantCultureIgnoreCase))
                        continue;   //VisualGDB provides its own startup files for STM32 devices that are compatible with STM32CubeMX-generated files

                    if (category == "header")
                        allHeaderDirs.Add(Path.GetDirectoryName(name));

                    string path = group;
                    if (!string.IsNullOrEmpty(subGroup))
                        path += "/" + subGroup;

                    rootDir.ProvideSudirectory(path).AddFile(Path.Combine(baseDir, name), category == "header");
                }
            }

            List<string> macros = new List<string> { "$$com.sysprogs.bspoptions.primary_memory$$_layout", "$$com.sysprogs.stm32.hal_device_family$$" };
            if (hasFreeRTOS)
                macros.Add("USE_FREERTOS");

            deviceName = deviceName.TrimEnd('x');
            deviceName = deviceName.Substring(0, deviceName.Length - 1);

            return new ImportedExternalProject
            {
                DeviceNameMask = new Regex(deviceName.Replace("x", ".*") + ".*"),
                OriginalProjectFile = parameters.ProjectFile,
                RootDirectory = rootDir,
                GNUTargetID = "arm-eabi",
                ReferencedFrameworks = new string[0],   //Unless this is explicitly specified, VisualGDB will try to reference the default frameworks (STM32 HAL) that will conflict with the STM32CubeMX-generated files.

                Configurations = new[]
                {
                    new ImportedExternalProject.ImportedConfiguration
                    {
                        Settings = new ImportedExternalProject.InvariantProjectBuildSettings
                        {
                            IncludeDirectories = allHeaderDirs.Select(d=>Path.Combine(baseDir, d)).ToArray(),
                            PreprocessorMacros = macros.ToArray()
                        }
                    }
                }
            };
        }
    }
}
