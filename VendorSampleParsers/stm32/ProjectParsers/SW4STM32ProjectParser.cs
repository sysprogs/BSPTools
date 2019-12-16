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
        public static List<VendorSample> ParseProjectFolder(string projectDir, string topLevelDir, string boardDir, List<string> extraIncludeDirs)
        {
            List<VendorSample> result = new List<VendorSample>();

            string[] cprojectFiles = Directory.GetFiles(projectDir, ".cproject", SearchOption.AllDirectories);

            foreach (var projectFile in cprojectFiles)
            {
                var projectFileDir = Path.GetDirectoryName(projectFile);

                //E.g. convert "Applications\FatFs\FatFs_uSD\SW4STM32\STM32072B_EVAL" to "Applications\FatFs\FatFs_uSD"
                string virtualPath;
                if (cprojectFiles.Length > 1)
                    virtualPath = projectFileDir.Substring(topLevelDir.Length); //This sample has multiple project files (e.g. <...>\SW4STM32\Board1, <...>\SW4STM32\Board2).
                else
                    virtualPath = projectDir.Substring(topLevelDir.Length);

                var virtualPathComponents = virtualPath.Trim('\\').Split('\\').Except(new[] { "SW4STM32", "Projects" }, StringComparer.InvariantCultureIgnoreCase).ToArray();

                XmlDocument cproject = new XmlDocument();
                cproject.Load(projectFile);

                XmlDocument project = new XmlDocument();
                project.Load(Path.Combine(projectFileDir, ".project"));

                var errors = new ParseErrorCollection();
                try
                {
                    var sample = ParseSingleProject(cproject, project, projectDir, Path.GetDirectoryName(cprojectFiles[0]), topLevelDir, Path.GetFileName(boardDir), errors);
                    sample.IncludeDirectories = sample.IncludeDirectories.Concat(extraIncludeDirs).ToArray();
                    sample.VirtualPath = string.Join("\\", virtualPathComponents);
                    sample.UserFriendlyName = virtualPathComponents.Last();
                    sample.InternalUniqueID = string.Join("-", virtualPathComponents);

                    result.Add(sample);
                }
                catch (Exception ex)
                {
                    errors.AddError(ex.Message);
                }
            }

            return result;
        }

        class ParseErrorCollection
        {
            List<string> _Errors = new List<string>();

            public void AddError(string text)
            {
                _Errors.Add(text);
                Console.WriteLine(text);
            }
        }

        private static VendorSample ParseSingleProject(XmlDocument cproject, XmlDocument project, string sw4projectDir, string cprojectDir, string topLevelDir, string boardName, ParseErrorCollection errors)
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
                .Select(a => TranslatePath(cprojectDir, a, errors, PathTranslationFlags.AddExtraComponentToBaseDir))
                .Where(d => d != null).ToArray();

            result.PreprocessorMacros = gccNode.LookupOptionValueAsList("gnu.c.compiler.option.preprocessor.def.symbols")
                .Select(a => a).ToArray();

            result.BoardName = toolchainConfigNode.LookupOptionValue("fr.ac6.managedbuild.option.gnu.cross.board");
            string mcu = toolchainConfigNode.LookupOptionValue("fr.ac6.managedbuild.option.gnu.cross.mcu");
            List<string> libs = new List<string>();

            string[] libraryPaths = linkerNode.LookupOptionValueAsList("gnu.c.link.option.paths", true);
            foreach (var lib in linkerNode.LookupOptionValueAsList("gnu.c.link.option.libs", true))
            {
                if (!lib.StartsWith(":"))
                    throw new Exception("Unexpected library file format: " + lib);

                foreach (var libDir in libraryPaths)
                {
                    var fullPath = TranslatePath(cprojectDir, libDir, errors, PathTranslationFlags.AddExtraComponentToBaseDir);

                    string candidate = Path.Combine(fullPath, $"{lib.Substring(1)}");
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

            var relLinkerScript = linkerNode.LookupOptionValue("fr.ac6.managedbuild.tool.gnu.cross.c.linker.script");
            var linkerScript = TranslatePath(cprojectDir, relLinkerScript, errors, PathTranslationFlags.AddExtraComponentToBaseDir);

            if (linkerScript != null)
                result.LinkerScript = Path.GetFullPath(linkerScript);

            result.SourceFiles = ParseSourceList(project, cprojectDir, errors).Concat(libs).ToArray();
            result.Path = Path.GetDirectoryName(sw4projectDir);
            return result;
        }

        static Regex rgParentSyntax = new Regex("PARENT-([0-9]+)-PROJECT_LOC(|..)/(.*)");

        [Flags]
        enum PathTranslationFlags
        {
            None = 0,
            AddExtraComponentToBaseDir = 1,
            ReturnEvenIfMissing = 2,
        }

        static string TranslatePath(string baseDir, string path, ParseErrorCollection errors, PathTranslationFlags flags)
        {
            path = path.Trim('\"');

            var m = rgParentSyntax.Match(path);
            if (m.Success)
            {
                //e.g. PARENT-1-PROJECT_LOC/startup_stm32mp15xx.s => ../startup_stm32mp15xx.s
                path = string.Join("/", Enumerable.Range(0, int.Parse(m.Groups[1].Value)).Select(i => "..")) + "/" + m.Groups[3].Value.TrimStart('/');
            }

            string fullPath;

            if (path == "")
                fullPath = baseDir; //This is sometimes used for include directories.
            else if ((flags & PathTranslationFlags.AddExtraComponentToBaseDir) != PathTranslationFlags.None)
                fullPath = Path.GetFullPath(Path.Combine(baseDir, "Build", path));
            else
                fullPath = Path.GetFullPath(Path.Combine(baseDir, path));

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                errors.AddError("Missing " + fullPath);
                if ((flags & PathTranslationFlags.ReturnEvenIfMissing) == PathTranslationFlags.None)
                    return null;
            }

            return fullPath;
        }

        private static List<string> ParseSourceList(XmlDocument project, string projectDir, ParseErrorCollection errors)
        {
            Regex rgStartup = new Regex("startup_stm.*\\.s");
            List<string> sources = new List<string>();
            foreach (var node in project.SelectNodes("projectDescription/linkedResources/link").OfType<XmlElement>())
            {
                string path = node.SelectSingleNode("locationURI")?.InnerText ?? node.SelectSingleNode("location")?.InnerText ?? throw new Exception("Resource name unspecified");
                if (path == "")
                    continue;

                int type = int.Parse(node.SelectSingleNode("type")?.InnerText ?? throw new Exception("Resource type unspecified"));

                string fullPath = TranslatePath(projectDir, path, errors, PathTranslationFlags.None);
                if (fullPath == null)
                    continue;

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
