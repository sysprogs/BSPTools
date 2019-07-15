using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace GeneratorSampleStm32.ProjectParsers
{
    class SW4STM32ProjectParser
    {
        public static List<VendorSample> ParseProjectFolder(string projectDir, string topLevelDir, string boardName, List<string> extraIncludeDirs)
        {
            List<VendorSample> result = new List<VendorSample>();

            string[] cprojectFiles = Directory.GetFiles(projectDir, ".cproject", SearchOption.AllDirectories);
            if (cprojectFiles.Length != 1)
                throw new Exception($"Found {cprojectFiles.Length} .cproject files in {projectDir}");

            XmlDocument cproject = new XmlDocument();
            cproject.Load(cprojectFiles[0]);

            XmlDocument project = new XmlDocument();
            project.Load(Path.Combine(Path.GetDirectoryName(cprojectFiles[0]), ".project"));

            var sample = ParseSingleProject(cproject, project, projectDir, Path.GetDirectoryName(cprojectFiles[0]), topLevelDir, boardName);
            sample.IncludeDirectories = sample.IncludeDirectories.Concat(extraIncludeDirs).ToArray();
            result.Add(sample);

            return result;
        }

        private static VendorSample ParseSingleProject(XmlDocument cproject, XmlDocument project, string sw4projectDir, string cprojectDir, string topLevelDir, string boardName)
        {
            VendorSample result = new VendorSample
            {
                UserFriendlyName = (project.SelectSingleNode("projectDescription/name") as XmlElement)?.InnerText ?? throw new Exception("Failed to determine sample name")
            };

            const string ToolchainConfigKey = "cproject/storageModule[@moduleId='org.eclipse.cdt.core.settings']/cconfiguration/storageModule[@moduleId='cdtBuildSystem']/configuration/folderInfo/toolChain";
            var toolchainConfigNode = cproject.SelectSingleNode(ToolchainConfigKey) as XmlNode ?? throw new Exception("Failed to locate the configuration node");

            var gccNode = toolchainConfigNode.SelectSingleNode("tool[starts-with(@id, 'fr.ac6.managedbuild.tool.gnu.cross.c.compiler')]") as XmlElement ?? throw new Exception("Missing gcc tool node");
            var linkerNode = toolchainConfigNode.SelectSingleNode("tool[starts-with(@id, 'fr.ac6.managedbuild.tool.gnu.cross.c.linker')]") as XmlElement ?? throw new Exception("Missing linker tool node");

            result.IncludeDirectories = gccNode.LookupOptionValueAsList("gnu.c.compiler.option.include.paths")
                .Select(a => Path.GetFullPath(Path.Combine(cprojectDir, "Build", a))).ToArray();

            result.PreprocessorMacros = gccNode.LookupOptionValueAsList("gnu.c.compiler.option.preprocessor.def.symbols")
                .Select(a => a).ToArray();

            result.BoardName = toolchainConfigNode.LookupOptionValue("fr.ac6.managedbuild.option.gnu.cross.board");
            string mcu = toolchainConfigNode.LookupOptionValue("fr.ac6.managedbuild.option.gnu.cross.mcu");
            List<string> libs = new List<string>();

            string[] libraryPaths = linkerNode.LookupOptionValueAsList("gnu.c.link.option.paths", true);
            foreach(var lib in linkerNode.LookupOptionValueAsList("gnu.c.link.option.libs", true))
            {
                if (!lib.StartsWith(":"))
                    throw new Exception("Unexpected library file format: " + lib);

                foreach(var libDir in libraryPaths)
                {
                    string candidate = Path.Combine(cprojectDir, "Build", libDir, $"{lib.Substring(1)}");
                    if (File.Exists(candidate))
                    {
                        libs.Add(Path.GetFullPath(candidate));
                        break;
                    }
                }

            }

            if (mcu.EndsWith("x"))
            {
                if (mcu.StartsWith("STM32MP1"))
                    mcu = mcu.Substring(0, mcu.Length - 3) + "_M4";
                else
                    mcu = mcu.Remove(mcu.Length - 2, 2);
            }

            result.DeviceID = mcu;

            var linkerScript = Path.Combine(cprojectDir, "Build", linkerNode.LookupOptionValue("fr.ac6.managedbuild.tool.gnu.cross.c.linker.script"));
            if (!File.Exists(linkerScript))
                throw new Exception("Missing " + linkerScript);

            result.LinkerScript = Path.GetFullPath(linkerScript);

            result.SourceFiles = ParseSourceList(project, cprojectDir).Concat(libs).ToArray();
            result.Path = Path.GetDirectoryName(sw4projectDir);
            return result;
        }

        private static List<string> ParseSourceList(XmlDocument project, string projectDir)
        {
            Regex rgParentSyntax = new Regex("PARENT-([0-9]+)-PROJECT_LOC/(.*)");
            Regex rgStartup = new Regex("startup_stm.*\\.s");
            List<string> sources = new List<string>();
            foreach (var node in project.SelectNodes("projectDescription/linkedResources/link").OfType<XmlElement>())
            {
                int type = int.Parse(node.SelectSingleNode("type")?.InnerText ?? throw new Exception("Resource type unspecified"));
                string path = node.SelectSingleNode("locationURI")?.InnerText ?? throw new Exception("Resource name unspecified");

                var m = rgParentSyntax.Match(path);
                if (!m.Success)
                    throw new Exception("Invalid path: " + path);

                //e.g. PARENT-1-PROJECT_LOC/startup_stm32mp15xx.s => ../startup_stm32mp15xx.s
                string relPath = string.Join("/", Enumerable.Range(0, int.Parse(m.Groups[1].Value)).Select(i => "..")) + "/" + m.Groups[2].Value;
                string fullPath = Path.Combine(projectDir, relPath);

                if (!File.Exists(fullPath))
                {
                    Console.WriteLine("Missing " + fullPath);
                    continue;
                }

                if (rgStartup.IsMatch(fullPath))
                    continue;   //Our BSP already provides a startup file

                sources.Add(Path.GetFullPath(fullPath));
            }

            return sources;
        }
    }

    static class Extensions
    {
        public static string LookupOptionValue(this XmlNode element, string optionName)
        {
            return (element.SelectSingleNode($"option[starts-with(@id, '{optionName}')]/@value") as XmlAttribute ?? throw new Exception("Missing " + optionName)).Value;
        }

        public static string[] LookupOptionValueAsList(this XmlNode element, string optionName, bool isOptional = false)
        {
            string[] result = element.SelectNodes($"option[starts-with(@id, '{optionName}')]/listOptionValue/@value").OfType<XmlAttribute>().Select(a => a.Value).ToArray();
            if (result.Length == 0 && !isOptional)
                throw new Exception("No values found for " + optionName);
            return result;
        }
    }
}
