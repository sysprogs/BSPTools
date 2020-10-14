using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace STM32IDEProjectImporter
{
    public class SW4STM32ProjectParserBase
    {
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
                OnMultipleConfigurationsFound(projectFile);
                result = result.Take(1).ToList();
            }

            return result.ToArray();
        }

        protected virtual void OnMultipleConfigurationsFound(string projectFile) { }
        protected virtual void OnParseFailed(Exception ex, string sampleID, string projectFileDir, string warningText) { }
        protected virtual void AdjustMCUName(ref string mcu) { }
        protected virtual void ValidateFinalMCUName(string mcu) { }
        protected virtual void OnFileNotFound(string fullPath) { }
        protected virtual void OnVendorSampleParsed(VendorSample sample, CommonConfigurationOptions options) { }

        public List<VendorSample> ParseProjectFolder(string optionalProjectRootForLocatingHeaders,
            string topLevelDir,
            string boardName,
            List<string> extraIncludeDirs,
            ProjectSubtype subtype)
        {
            List<VendorSample> result = new List<VendorSample>();

            if (topLevelDir == null)
                topLevelDir = Path.GetDirectoryName(optionalProjectRootForLocatingHeaders);

            string[] cprojectFiles = Directory.GetFiles(optionalProjectRootForLocatingHeaders, ".cproject", SearchOption.AllDirectories);

            foreach (var projectFile in cprojectFiles)
            {
                var projectFileDir = Path.GetDirectoryName(projectFile);

                //E.g. convert "Applications\FatFs\FatFs_uSD\SW4STM32\STM32072B_EVAL" to "Applications\FatFs\FatFs_uSD"
                string virtualPath;
                if (cprojectFiles.Length > 1)
                    virtualPath = projectFileDir.Substring(topLevelDir.Length); //This sample has multiple project files (e.g. <...>\SW4STM32\Board1, <...>\SW4STM32\Board2).
                else
                    virtualPath = optionalProjectRootForLocatingHeaders.Substring(topLevelDir.Length);

                var virtualPathComponents = virtualPath.Trim('\\').Split('\\').Except(new[] { "SW4STM32", "Projects", "STM32CubeIDE" }, StringComparer.InvariantCultureIgnoreCase).ToArray();

                ParseSingleProject(optionalProjectRootForLocatingHeaders, projectFile, virtualPathComponents, boardName, extraIncludeDirs, subtype, result);
            }

            return result;
        }

        public void ParseSingleProject(string optionalProjectRootForLocatingHeaders, string projectFile, string[] virtualPathComponents, string boardName, List<string> extraIncludeDirs, ProjectSubtype subtype, List<VendorSample> result)
        {
            var projectFileDir = Path.GetDirectoryName(projectFile);

            if (virtualPathComponents == null)
                virtualPathComponents = new[] { "virtual", "sample", "path" };

            XmlDocument cproject = new XmlDocument();
            XmlDocument project = new XmlDocument();

            try
            {
                cproject.Load(projectFile);
                project.Load(Path.Combine(projectFileDir, ".project"));
            }
            catch (Exception ex)
            {
                OnParseFailed(ex, string.Join("-", virtualPathComponents), projectFileDir, "Failed to load project file");
                return;
            }

            ConfigurationWithContext[] configs;

            try
            {
                configs = DetectConfigurationContexts(cproject, projectFile);
            }
            catch (Exception ex)
            {
                OnParseFailed(ex, string.Join("-", virtualPathComponents), projectFileDir, "Failed to compute configuration contexts");
                return;
            }

            foreach (var cfg in configs)
            {
                string sampleID = null;

                try
                {
                    sampleID = string.Join("-", virtualPathComponents) + cfg.Context?.IDSuffix;

                    var sample = ParseSingleConfiguration(cproject, project, cfg.CConfiguration, optionalProjectRootForLocatingHeaders, Path.GetDirectoryName(projectFile),
                        boardName, cfg.Context, subtype);
                    sample.IncludeDirectories = sample.IncludeDirectories.Concat(extraIncludeDirs ?? new List<string>()).ToArray();
                    sample.VirtualPath = string.Join("\\", virtualPathComponents.Take(virtualPathComponents.Length - 1).ToArray());
                    sample.UserFriendlyName = virtualPathComponents.Last();
                    sample.InternalUniqueID = sampleID;

                    result.Add(sample);
                }
                catch (Exception ex)
                {
                    OnParseFailed(ex, sampleID, projectFileDir, "General error while parsing a sample");
                }
            }
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

        class SourceEntry
        {
            public string Name;

            public string RelativePath;

            public bool IsValid => !string.IsNullOrEmpty(Name);
            public SourceEntry(XmlElement e)
            {
                Name = e.GetAttribute("name");
                var flags = e.GetAttribute("flags");
                if (!flags.Contains("VALUE_WORKSPACE_PATH") || e.GetAttribute("kind") != "sourcePath")
                    throw new Exception("Don't know how to handle a source entry");
            }
        }

        public enum ProjectSubtype
        {
            Auto,
            SW4STM32,
            STM32CubeIDE,
        }

        public struct CommonConfigurationOptions
        {
            public string[] IncludeDirectories;
            public string[] PreprocessorMacros;
            public string BoardName;
            public string MCU;
            public List<ParsedSourceFile> SourceFiles;
            public string LinkerScript;
            public List<string> Libraries;
            public string LDFLAGS;
            public bool UseCMSE;
        }

        const string ToolchainConfigKey = "storageModule[@moduleId='cdtBuildSystem']/configuration/folderInfo/toolChain";
        const string SourceEntriesKey = "storageModule[@moduleId='cdtBuildSystem']/configuration/sourceEntries/entry";

        CommonConfigurationOptions ExtractSW4STM32Options(XmlDocument cproject, XmlDocument project, XmlElement cconfiguration, string cprojectDir)
        {
            var toolchainConfigNode = cconfiguration.SelectSingleNode(ToolchainConfigKey) as XmlNode ?? throw new Exception("Failed to locate the configuration node");
            CommonConfigurationOptions result = new CommonConfigurationOptions();

            var gccNode = toolchainConfigNode.SelectSingleNode("tool[starts-with(@id, 'fr.ac6.managedbuild.tool.gnu.cross.c.compiler')]") as XmlElement ?? throw new Exception("Missing gcc tool node");
            var linkerNode = toolchainConfigNode.SelectSingleNode("tool[starts-with(@id, 'fr.ac6.managedbuild.tool.gnu.cross.c.linker')]") as XmlElement ?? throw new Exception("Missing linker tool node");
            var cppLinkerNode = toolchainConfigNode.SelectSingleNode("tool[starts-with(@id, 'fr.ac6.managedbuild.tool.gnu.cross.cpp.linker')]") as XmlElement;

            result.IncludeDirectories = gccNode.LookupOptionValueAsList("gnu.c.compiler.option.include.paths")
                .Select(a => TranslatePath(cprojectDir, a, PathTranslationFlags.AddExtraComponentToBaseDir))
                .Where(d => d != null).ToArray();

            result.PreprocessorMacros = gccNode.LookupOptionValueAsList("gnu.c.compiler.option.preprocessor.def.symbols")
                .Select(a => a.Trim()).Where(a => a != "").ToArray();

            result.BoardName = toolchainConfigNode.LookupOptionValue("fr.ac6.managedbuild.option.gnu.cross.board", true);
            result.MCU = toolchainConfigNode.LookupOptionValue("fr.ac6.managedbuild.option.gnu.cross.mcu");
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

            var sourceFilters = cconfiguration.SelectNodes(SourceEntriesKey).OfType<XmlElement>().Select(e => new SourceFilterEntry(e)).Where(e => e.IsValid).ToArray();
            var relLinkerScript = linkerNode.LookupOptionValue("fr.ac6.managedbuild.tool.gnu.cross.c.linker.script");
            var linkerScript = TranslatePath(cprojectDir, relLinkerScript, PathTranslationFlags.AddExtraComponentToBaseDir);

            if (linkerScript != null)
                result.LinkerScript = Path.GetFullPath(linkerScript);

            result.LDFLAGS = linkerNode.LookupOptionValue("gnu.c.link.option.ldflags");
            result.SourceFiles = ParseSourceList(project, cprojectDir, sourceFilters);
            result.Libraries = libs;

            return result;
        }

        CommonConfigurationOptions ExtractSTM32CubeIDEOptions(XmlDocument cproject, XmlDocument project, XmlElement cconfiguration, string cprojectDir)
        {
            var toolchainConfigNode = cconfiguration.SelectSingleNode(ToolchainConfigKey) as XmlNode ?? throw new Exception("Failed to locate the configuration node");
            CommonConfigurationOptions result = new CommonConfigurationOptions();

            var gccNode = toolchainConfigNode.SelectSingleNode("tool[@superClass = 'com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.compiler']") as XmlElement ?? throw new Exception("Missing gcc tool node");
            var linkerNode = toolchainConfigNode.SelectSingleNode("tool[@superClass = 'com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.linker']") as XmlElement ?? throw new Exception("Missing linker tool node");
            var cppLinkerNode = toolchainConfigNode.SelectSingleNode("tool[@superClass = 'com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.cpp.linker']") as XmlElement;

            result.IncludeDirectories = gccNode.LookupOptionValueAsList("com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.compiler.option.includepaths")
                .Select(a => TranslatePath(cprojectDir, a, PathTranslationFlags.AddExtraComponentToBaseDir))
                .Where(d => d != null).ToArray();

            result.PreprocessorMacros = gccNode.LookupOptionValueAsList("com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.compiler.option.definedsymbols")
                .Select(a => a.Trim()).Where(a => a != "" && a != "DEBUG" && a != "RELEASE").ToArray();

            result.MCU = toolchainConfigNode.LookupOptionValue("com.st.stm32cube.ide.mcu.gnu.managedbuild.option.target_mcu");
            result.BoardName = toolchainConfigNode.LookupOptionValue("com.st.stm32cube.ide.mcu.gnu.managedbuild.option.target_board");

            var relLinkerScript = linkerNode.LookupOptionValue("com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.linker.option.script");
            var linkerScript = TranslatePath(cprojectDir, relLinkerScript, PathTranslationFlags.None);

            if (linkerScript != null)
                result.LinkerScript = Path.GetFullPath(linkerScript);

            result.LDFLAGS = linkerNode.LookupOptionValue("com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.linker.option.otherflags", true);
            result.UseCMSE = gccNode.LookupOptionValue("com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.compiler.option.mcmse", true) == "true";

            result.Libraries = new List<string>();

            List<SourceFilterEntry> sourceFilters = new List<SourceFilterEntry>();
            Dictionary<string, SourceEntry> sourceReferences = new Dictionary<string, SourceEntry>();
            foreach (var node in cconfiguration.SelectNodes(SourceEntriesKey).OfType<XmlElement>())
            {
                if (!string.IsNullOrEmpty(node.GetAttribute("excluding")))
                {
                    var entry = new SourceFilterEntry(node);
                    if (entry.IsValid)
                        sourceFilters.Add(entry);
                }
                else if (!string.IsNullOrEmpty(node.GetAttribute("name")))
                {
                    var entry = new SourceEntry(node);
                    if (entry.IsValid)
                        sourceReferences[entry.Name] = entry;
                }
            }

            var sources = ParseSourceList(project, cprojectDir, sourceFilters.ToArray(), sourceReferences)
                .Where(f => !f.FullPath.EndsWith(".ioc")).ToList();  //.ioc files have too long names that will exceed our path length limit

            result.SourceFiles = ExpandSourcePaths(sources);

            return result;
        }

        private List<ParsedSourceFile> ExpandSourcePaths(List<ParsedSourceFile> sources)
        {
            List<ParsedSourceFile> result = new List<ParsedSourceFile>();
            foreach (var src in sources)
            {
                try
                {
                    if (File.Exists(src.FullPath))
                        result.Add(src);
                    else if (Directory.Exists(src.FullPath))
                    {
                        var fullPath = Path.GetFullPath(src.FullPath);
                        foreach (var file in Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories))
                        {
                            var ext = Path.GetExtension(file).ToLower();
                            if (ext == ".c" || ext == ".cpp" || ext == ".cc" || ext == ".s")
                            {
                                string relPath = file.Substring(fullPath.Length).TrimStart('\\');
                                result.Add(new ParsedSourceFile { FullPath = file, VirtualPath = src.VirtualPath + "/" + relPath.Replace('\\', '/') });
                            }
                        }
                    }
                    else
                    {
                        //Nothing exists at this path
                    }
                }
                catch
                {
                }
            }
            return result;
        }

        private VendorSample ParseSingleConfiguration(XmlDocument cproject,
            XmlDocument project,
            XmlElement cconfiguration,
            string optionalProjectRootForLocatingHeaders,
            string cprojectFileDir,
            string boardName,
            MultiConfigurationContext multiConfigurationContext,
            ProjectSubtype subtype)
        {
            VendorSample result = new VendorSample
            {
                UserFriendlyName = (project.SelectSingleNode("projectDescription/name") as XmlElement)?.InnerText ?? throw new Exception("Failed to determine sample name"),
                NoImplicitCopy = true,
            };

            if (optionalProjectRootForLocatingHeaders == null)
                optionalProjectRootForLocatingHeaders = cprojectFileDir;

            CommonConfigurationOptions opts;
            if (subtype == ProjectSubtype.Auto)
            {
                var toolchainConfigNode = cconfiguration.SelectSingleNode(ToolchainConfigKey) as XmlNode ?? throw new Exception("Failed to locate the configuration node");
                if (toolchainConfigNode.SelectSingleNode("tool[starts-with(@id, 'fr.ac6.managedbuild.tool.gnu.cross.c.compiler')]") != null)
                    subtype = ProjectSubtype.SW4STM32;
                else if (toolchainConfigNode.SelectSingleNode("tool[@superClass = 'com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.compiler']") != null)
                    subtype = ProjectSubtype.STM32CubeIDE;
                else
                    throw new Exception("Failed to detect the project type");
            }

            if (subtype == ProjectSubtype.SW4STM32)
                opts = ExtractSW4STM32Options(cproject, project, cconfiguration, cprojectFileDir);
            else
                opts = ExtractSTM32CubeIDEOptions(cproject, project, cconfiguration, cprojectFileDir);

            var mcu = opts.MCU;

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

            AdjustMCUName(ref mcu);

            if (multiConfigurationContext is MultiConfigurationContext.MultiCore mc)
            {
                mcu += mc.DeviceSuffix;
                result.InternalUniqueID += mc.DeviceSuffix;
                result.UserFriendlyName += mc.UserFriendlyNameSuffix;
            }

            ValidateFinalMCUName(mcu);

            result.DeviceID = mcu;
            result.SourceFiles = opts.SourceFiles.Select(f => f.FullPath).Concat(opts.Libraries).Distinct().ToArray();
            result.IncludeDirectories = opts.IncludeDirectories;
            result.PreprocessorMacros = opts.PreprocessorMacros;
            result.BoardName = boardName ?? opts.BoardName;
            result.LinkerScript = opts.LinkerScript;
            OnVendorSampleParsed(result, opts);

            List<PropertyDictionary2.KeyValue> mcuConfig = new List<PropertyDictionary2.KeyValue>();

            if (opts.LDFLAGS?.Contains("rdimon.specs") == true)
            {
                mcuConfig.Add(new PropertyDictionary2.KeyValue { Key = "com.sysprogs.toolchainoptions.arm.libctype", Value = "--specs=rdimon.specs" });
            }

            if (opts.UseCMSE)
            {
                mcuConfig.Add(new PropertyDictionary2.KeyValue { Key = "com.sysprogs.bspoptions.cmse", Value = "-mcmse" });
            }

            try
            {
                if (result.SourceFiles.Select(f => Path.GetFileName(f)).FirstOrDefault(f => f.StartsWith("startup_", StringComparison.InvariantCultureIgnoreCase) && f.EndsWith(".s", StringComparison.InvariantCultureIgnoreCase)) != null)
                {
                    mcuConfig.Add(new PropertyDictionary2.KeyValue { Key = "com.sysprogs.mcuoptions.ignore_startup_file", Value = "1" });
                }
            }
            catch { }

            if (mcuConfig.Count > 0)
            {
                result.Configuration.MCUConfiguration = new PropertyDictionary2
                {
                    Entries = mcuConfig.ToArray()
                };
            }

            result.Path = Path.GetDirectoryName(optionalProjectRootForLocatingHeaders);

            HashSet<string> possibleIncludeDirs = new HashSet<string>();
            foreach (var src in result.SourceFiles)
            {
                int idx = src.IndexOf(@"\Src\", StringComparison.InvariantCultureIgnoreCase);
                if (idx == -1)
                    continue;
                string possibleInc = src.Substring(0, idx) + @"\Inc";
                possibleIncludeDirs.Add(possibleInc);
            }

            string possibleRoot = optionalProjectRootForLocatingHeaders;
            for (; ; )
            {
                string possibleIncDir = Path.Combine(possibleRoot, "Inc");
                if (Directory.Exists(possibleIncDir))
                {
                    possibleIncludeDirs.Add(possibleIncDir);
                    break;
                }

                var baseDir = Path.GetDirectoryName(possibleRoot);
                if (string.IsNullOrEmpty(baseDir) || baseDir == possibleRoot)
                    break;

                possibleRoot = baseDir;
            }

            List<string> headers = new List<string>();
            foreach (var possibleIncDir in possibleIncludeDirs)
            {
                if (Directory.Exists(possibleIncDir))
                    headers.AddRange(Directory.GetFiles(possibleIncDir, "*.h", SearchOption.AllDirectories));
            }

            result.HeaderFiles = headers.ToArray();
            return result;
        }

        Regex rgParentSyntax = new Regex("(\\$%7B|)PARENT-([0-9]+)-PROJECT_LOC(|..|%7D)/(.*)");

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

            string workspacePrefix = "${workspace_loc:/${ProjName}/";
            string workspacePrefix2 = "${workspace_loc:/";
            if (path.StartsWith(workspacePrefix))
                path = path.Substring(workspacePrefix.Length).TrimEnd('}');
            else if (path.StartsWith(workspacePrefix2))
            {
                string pathInsideWorkspace = path.Substring(workspacePrefix2.Length).Replace("${ProjName}", Path.GetFileName(baseDir)).TrimEnd('}');
                try
                {
                    string testedDir = baseDir;
                    for (; ;)
                    {
                        if (File.Exists(Path.Combine(testedDir, pathInsideWorkspace)))
                            return Path.GetFullPath(Path.Combine(testedDir, pathInsideWorkspace));
                        var parentDir = Path.GetDirectoryName(testedDir);
                        if (parentDir == null || parentDir.Length < 2 || parentDir == testedDir)
                            break;

                        testedDir = parentDir;
                    }
                }
                catch { }
            }

            var m = rgParentSyntax.Match(path);
            if (m.Success)
            {
                //e.g. PARENT-1-PROJECT_LOC/startup_stm32mp15xx.s => ../startup_stm32mp15xx.s
                path = string.Join("/", Enumerable.Range(0, int.Parse(m.Groups[2].Value)).Select(i => "..").ToArray()) + "/" + m.Groups[4].Value.TrimStart('/');
            }

            string fullPath;

            if (path == "")
                fullPath = baseDir; //This is sometimes used for include directories.
            else if ((flags & PathTranslationFlags.AddExtraComponentToBaseDir) != PathTranslationFlags.None)
                fullPath = Path.GetFullPath(Path.Combine(baseDir, Path.Combine("Build", path)));
            else
                fullPath = Path.GetFullPath(Path.Combine(baseDir, path));

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                OnFileNotFound(fullPath);
                if ((flags & PathTranslationFlags.ReturnEvenIfMissing) == PathTranslationFlags.None)
                    return null;
            }

            return fullPath;
        }

        public struct ParsedSourceFile
        {
            public string FullPath;
            public string VirtualPath;

            public override string ToString() => FullPath;
        }

        private List<ParsedSourceFile> ParseSourceList(XmlDocument project, string projectDir, SourceFilterEntry[] sourceFilters, Dictionary<string, SourceEntry> sourceReferences = null)
        {
            Regex rgStartup = new Regex("startup_stm.*\\.s");
            List<ParsedSourceFile> sources = new List<ParsedSourceFile>();
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

                var name = node.SelectSingleNode("name")?.InnerText;
                if (name != null && sourceReferences != null && sourceReferences.TryGetValue(name, out var sr))
                    sr.RelativePath = path;

                string virtualPath = name ?? Path.GetFileName(fullPath);
                sources.Add(new ParsedSourceFile { FullPath = Path.GetFullPath(fullPath), VirtualPath = virtualPath });
            }

            if (sourceReferences != null)
            {
                foreach (var sr in sourceReferences.Values)
                {
                    if (sr.RelativePath == null)
                    {
                        //There was no entry in linkedResources corresponding to this source specifier
                        string fullPath = TranslatePath(projectDir, sr.Name, PathTranslationFlags.None);
                        if (fullPath == null)
                            continue;

                        sources.Add(new ParsedSourceFile { FullPath = Path.GetFullPath(fullPath), VirtualPath = sr.Name });
                    }
                }
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
    }

    static class Extensions
    {
        public static string LookupOptionValue(this XmlNode element, string optionName, bool optional = false)
        {
            var attr = element.SelectSingleNode($"option[starts-with(@id, '{optionName}')]/@value") as XmlAttribute;
            if (!optional && attr == null)
                throw new Exception("Missing " + optionName);
            return attr?.Value;
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
