using BSPEngine;
using BSPGenerationTools;
using BSPGenerationTools.Parsing;
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
    class SW4STM32ProjectParser : IDisposable
    {
        private MCU[] _SupportedMCUs;
        HashSet<string> _SupportedMCUNames = new HashSet<string>();
        BSPReportWriter _Report;

        public SW4STM32ProjectParser(string reportDir, MCU[] supportedMCUs)
        {
            _Report = new BSPReportWriter(reportDir, "ParseReport.txt");
            _SupportedMCUs = supportedMCUs;
            foreach (var mcu in _SupportedMCUs)
                _SupportedMCUNames.Add(mcu.ID);
        }

        abstract class MultiConfigurationContext
        {
            public abstract string IDSuffix { get; }

            public class MultiCore : MultiConfigurationContext
            {
                public string DeviceSuffix;
                public string UserFriendlyNameSuffix;

                public override string IDSuffix => DeviceSuffix;
            }
        }

        struct ConfigurationWithContext
        {
            public XmlElement CConfiguration;
            public MultiConfigurationContext Context;
        }

        ConfigurationWithContext[] DetectConfigurationContexts(XmlDocument cproject, string projectFile)
        {
            var cconfigurationNodes = cproject.SelectNodes("cproject/storageModule[@moduleId='org.eclipse.cdt.core.settings']/cconfiguration").OfType<XmlElement>().ToArray();
            if (cconfigurationNodes.Length == 0)
                throw new Exception("No 'cconfiguration' nodes found");

            if (cconfigurationNodes.Length > 1)
            {
                var nonReleaseNodes = cconfigurationNodes.Where(n => !n.GetAttribute("id").Contains(".release.")).ToArray();
                if (nonReleaseNodes.Length > 0)
                    cconfigurationNodes = nonReleaseNodes;
            }

            List<ConfigurationWithContext> result = new List<ConfigurationWithContext>();
            foreach (var cconfiguration in cconfigurationNodes)
            {
                MultiConfigurationContext mctx = null;
                if (cconfigurationNodes.Length > 1)
                {
                    if (cconfigurationNodes.Length != 2)
                        throw new Exception("Unexpected configuration count for " + projectFile);

                    string artifactName = cconfiguration.SelectSingleNode("storageModule[@moduleId='cdtBuildSystem']/configuration/@artifactName")?.Value;
                    if (artifactName.EndsWith("_CM4"))
                        mctx = new MultiConfigurationContext.MultiCore { DeviceSuffix = "_M4", UserFriendlyNameSuffix = " (Cortex-M4 Core)" };
                    else if (artifactName.EndsWith("_CM7"))
                        mctx = new MultiConfigurationContext.MultiCore { DeviceSuffix = "", UserFriendlyNameSuffix = " (Cortex-M7 Core)" };
                    else
                        throw new Exception("Don't know how to interpret the difference between multiple configurations for a project. Please review it manually.");

                }

                result.Add(new ConfigurationWithContext { CConfiguration = cconfiguration, Context = mctx });
            }

            if (result.Select(c => c.Context?.IDSuffix ?? "").Distinct().Count() != result.Count)
            {
                _Report.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, "Found multiple configurations with the same ID", projectFile, false);
                result = result.Take(1).ToList();
            }

            return result.ToArray();
        }


        public List<VendorSample> ParseProjectFolder(string projectDir, string topLevelDir, string boardDir, List<string> extraIncludeDirs)
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

                var configs = DetectConfigurationContexts(cproject, projectFile);

                foreach (var cfg in configs)
                {
                    try
                    {
                        var sample = ParseSingleProject(cproject, project, cfg.CConfiguration, projectDir, Path.GetDirectoryName(cprojectFiles[0]), topLevelDir, Path.GetFileName(boardDir), cfg.Context);
                        sample.IncludeDirectories = sample.IncludeDirectories.Concat(extraIncludeDirs).ToArray();
                        sample.VirtualPath = string.Join("\\", virtualPathComponents.Take(virtualPathComponents.Length - 1));
                        sample.UserFriendlyName = virtualPathComponents.Last();
                        sample.InternalUniqueID = string.Join("-", virtualPathComponents) + cfg.Context?.IDSuffix;

                        result.Add(sample);
                    }
                    catch (Exception ex)
                    {
                        _Report.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, "General error while parsing a sample", ex.Message, false);
                    }
                }
            }

            return result;
        }

        struct SourceFilterEntry
        {
            public string[] Prefixes;

            public bool IsValid => Prefixes != null && Prefixes.Length > 0;

            public SourceFilterEntry(XmlElement e)
            {
                var excl = e.GetAttribute("excluding");
                var flags = e.GetAttribute("flags");
                if (!flags.Contains("VALUE_WORKSPACE_PATH") || e.GetAttribute("kind") != "sourcePath")
                    throw new Exception("Don't know how to handle a source entry");

                Prefixes = excl.Split('|').Where(p => p != "").ToArray();
            }

            public bool MatchesVirtualPath(string virtualPath)
            {
                foreach (var pfx in Prefixes)
                    if (virtualPath.StartsWith(pfx))
                        return true;
                return false;
            }
        }

        private VendorSample ParseSingleProject(XmlDocument cproject, XmlDocument project,
            XmlElement cconfiguration, string sw4projectDir,
            string cprojectDir, string topLevelDir, string boardName,
            MultiConfigurationContext multiConfigurationContext)
        {
            VendorSample result = new VendorSample
            {
                UserFriendlyName = (project.SelectSingleNode("projectDescription/name") as XmlElement)?.InnerText ?? throw new Exception("Failed to determine sample name"),
                NoImplicitCopy = true,
            };

            const string ToolchainConfigKey = "storageModule[@moduleId='cdtBuildSystem']/configuration/folderInfo/toolChain";
            const string SourceEntriesKey = "storageModule[@moduleId='cdtBuildSystem']/configuration/sourceEntries/entry";
            var toolchainConfigNode = cconfiguration.SelectSingleNode(ToolchainConfigKey) as XmlNode ?? throw new Exception("Failed to locate the configuration node");

            var gccNode = toolchainConfigNode.SelectSingleNode("tool[starts-with(@id, 'fr.ac6.managedbuild.tool.gnu.cross.c.compiler')]") as XmlElement ?? throw new Exception("Missing gcc tool node");
            var linkerNode = toolchainConfigNode.SelectSingleNode("tool[starts-with(@id, 'fr.ac6.managedbuild.tool.gnu.cross.c.linker')]") as XmlElement ?? throw new Exception("Missing linker tool node");
            var cppLinkerNode = toolchainConfigNode.SelectSingleNode("tool[starts-with(@id, 'fr.ac6.managedbuild.tool.gnu.cross.cpp.linker')]") as XmlElement;

            result.IncludeDirectories = gccNode.LookupOptionValueAsList("gnu.c.compiler.option.include.paths")
                .Select(a => TranslatePath(cprojectDir, a, PathTranslationFlags.AddExtraComponentToBaseDir))
                .Where(d => d != null).ToArray();

            result.PreprocessorMacros = gccNode.LookupOptionValueAsList("gnu.c.compiler.option.preprocessor.def.symbols")
                .Select(a => a.Trim()).Where(a => a != "").ToArray();

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
                    var fullPath = TranslatePath(cprojectDir, libDir, PathTranslationFlags.AddExtraComponentToBaseDir);

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
            else if (mcu.EndsWith("xP"))
            {
                mcu = mcu.Remove(mcu.Length - 3, 3);
            }

            if (multiConfigurationContext is MultiConfigurationContext.MultiCore mc)
            {
                mcu += mc.DeviceSuffix;
                result.InternalUniqueID += mc.DeviceSuffix;
                result.UserFriendlyName += mc.UserFriendlyNameSuffix;
            }

            if (!_SupportedMCUNames.Contains(mcu))
            {
                _Report.ReportMergeableError("Invalid MCU", mcu);
            }

            result.DeviceID = mcu;

            var sourceFilters = cconfiguration.SelectNodes(SourceEntriesKey).OfType<XmlElement>().Select(e => new SourceFilterEntry(e)).Where(e => e.IsValid).ToArray();

            var relLinkerScript = linkerNode.LookupOptionValue("fr.ac6.managedbuild.tool.gnu.cross.c.linker.script");
            var linkerScript = TranslatePath(cprojectDir, relLinkerScript, PathTranslationFlags.AddExtraComponentToBaseDir);

            string ldflags = linkerNode.LookupOptionValue("gnu.c.link.option.ldflags");

            if (ldflags?.Contains("rdimon.specs") == true)
            {
                result.Configuration.MCUConfiguration = new PropertyDictionary2
                {
                    Entries = new PropertyDictionary2.KeyValue[]
                    {
                        new PropertyDictionary2.KeyValue{Key = "com.sysprogs.toolchainoptions.arm.libctype", Value = "--specs=rdimon.specs"}
                    }
                };
            }

            if (linkerScript != null)
                result.LinkerScript = Path.GetFullPath(linkerScript);

            result.SourceFiles = ParseSourceList(project, cprojectDir, sourceFilters).Concat(libs).Distinct().ToArray();
            result.Path = Path.GetDirectoryName(sw4projectDir);

            string possibleRoot = sw4projectDir;
            for(; ;)
            {
                string possibleIncDir = Path.Combine(possibleRoot, "Inc");
                if (Directory.Exists(possibleIncDir))
                {
                    result.HeaderFiles = Directory.GetFiles(possibleIncDir, "*.h", SearchOption.AllDirectories);
                    break;
                }

                var baseDir = Path.GetDirectoryName(possibleRoot);
                if (baseDir == "" || baseDir == possibleRoot)
                    break;

                possibleRoot = baseDir;
            }

            return result;
        }

        Regex rgParentSyntax = new Regex("PARENT-([0-9]+)-PROJECT_LOC(|..)/(.*)");

        [Flags]
        enum PathTranslationFlags
        {
            None = 0,
            AddExtraComponentToBaseDir = 1,
            ReturnEvenIfMissing = 2,
        }

        string TranslatePath(string baseDir, string path, PathTranslationFlags flags)
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
                _Report.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, "Missing file/directory", fullPath, false);
                if ((flags & PathTranslationFlags.ReturnEvenIfMissing) == PathTranslationFlags.None)
                    return null;
            }

            return fullPath;
        }

        private List<string> ParseSourceList(XmlDocument project, string projectDir, SourceFilterEntry[] sourceFilters)
        {
            Regex rgStartup = new Regex("startup_stm.*\\.s");
            List<string> sources = new List<string>();
            foreach (var node in project.SelectNodes("projectDescription/linkedResources/link").OfType<XmlElement>())
            {
                string path = node.SelectSingleNode("locationURI")?.InnerText ?? node.SelectSingleNode("location")?.InnerText ?? throw new Exception("Resource name unspecified");
                if (path == "")
                    continue;

                int type = int.Parse(node.SelectSingleNode("type")?.InnerText ?? throw new Exception("Resource type unspecified"));

                string fullPath = TranslatePath(projectDir, path, PathTranslationFlags.None);
                if (fullPath == null)
                    continue;

                if (rgStartup.IsMatch(fullPath))
                    continue;   //Our BSP already provides a startup file

                if (sourceFilters != null)
                {
                    if (ShouldSkipNode(node, sourceFilters))
                        continue;
                }

                sources.Add(Path.GetFullPath(fullPath));
            }

            return sources;
        }

        private bool ShouldSkipNode(XmlElement node, SourceFilterEntry[] sourceFilters)
        {
            var virtualPath = node.SelectSingleNode("name")?.InnerText;
            if (string.IsNullOrEmpty(virtualPath))
                return false;

            foreach (var sf in sourceFilters)
            {
                if (sf.MatchesVirtualPath(virtualPath))
                    return true;
            }

            return false;
        }

        public void Dispose()
        {
            _Report.Dispose();
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
