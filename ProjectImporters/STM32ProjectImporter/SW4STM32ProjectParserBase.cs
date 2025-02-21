using BSPEngine;
using BSPEngine.Eclipse;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;

namespace STM32ProjectImporter
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

            public class Other : MultiConfigurationContext
            {
                public string Suffix;
                public override string IDSuffix => Suffix;
            }
        }

        struct ConfigurationWithContext
        {
            public EclipseProject.CConfiguration CConfiguration;
            public MultiConfigurationContext Context;
        }

        ConfigurationWithContext[] DetectConfigurationContexts(EclipseProject project, ProjectSubtype subtype)
        {
            if (project.Configurations.Length == 0)
                throw new Exception("No 'cconfiguration' nodes found");

            if (project.Configurations.Length > 1)
            {
                if (subtype == ProjectSubtype.WiSEStudio)
                    return new[] { new ConfigurationWithContext { CConfiguration = project.Configurations[0] } };
            }

            List<ConfigurationWithContext> result = new List<ConfigurationWithContext>();
            var nonReleaseConfigs = project.NonReleaseConfigurationsIfAny;
            if (nonReleaseConfigs.Length > 0)
            {
                var cfgs = nonReleaseConfigs.OrderBy(c => c.ID.Length).ToArray();
                var lastID = nonReleaseConfigs.Last().ID;
                if (nonReleaseConfigs.Any(c => lastID.StartsWith(c.ID + ".")))
                    nonReleaseConfigs = new[] { nonReleaseConfigs.Last() };
            }

            foreach (var cconfiguration in nonReleaseConfigs)
            {
                MultiConfigurationContext mctx = null;
                if (nonReleaseConfigs.Length > 1)
                {
                    if (nonReleaseConfigs.Length > 3)
                        throw new Exception("Unexpected configuration count for " + project.CProjectFile + ":" + string.Join(", ", nonReleaseConfigs.Select(c => c.ToString()).ToArray()));

                    string artifactName = cconfiguration.ArtifactName;
                    if (artifactName.EndsWith("_CM4"))
                        mctx = new MultiConfigurationContext.MultiCore { DeviceSuffix = "_M4", UserFriendlyNameSuffix = " (Cortex-M4 Core)" };
                    else if (artifactName.EndsWith("_CM7"))
                        mctx = new MultiConfigurationContext.MultiCore { DeviceSuffix = "", UserFriendlyNameSuffix = " (Cortex-M7 Core)" };

                    if (mctx == null && cconfiguration.Name.Length > 6 && cconfiguration.Name.EndsWith("Debug"))
                    {
                        var name = cconfiguration.Name.Substring(0, cconfiguration.Name.Length - 6);
                        if (name.StartsWith(project.Name, StringComparison.InvariantCultureIgnoreCase))
                            name = name.Substring(project.Name.Length).TrimStart('_', '-');

                        mctx = new MultiConfigurationContext.Other { Suffix = "_" + name };
                    }

                    if (mctx == null)
                        throw new Exception("Don't know how to interpret the difference between multiple configurations for a project. Please review it manually.");
                }

                result.Add(new ConfigurationWithContext { CConfiguration = cconfiguration, Context = mctx });
            }

            if (result.Select(c => c.Context?.IDSuffix ?? "").Distinct().Count() != result.Count)
            {
                OnMultipleConfigurationsFound(project.CProjectFile);
                result = result.Take(1).ToList();
            }

            return result.ToArray();
        }

        protected virtual void OnMultipleConfigurationsFound(string projectFile) { }
        protected virtual void OnParseFailed(Exception ex, string sampleID, string projectFileDir, string warningText) { }
        protected virtual void AdjustMCUName(ref string mcu) { }
        protected virtual void ValidateFinalMCUName(ref string mcu) { }
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

                var virtualPathComponents = virtualPath.Trim('\\').Split('\\').Except(new[] { "SW4STM32", "Projects", "STM32CubeIDE", "WiSE-Studio" }, StringComparer.InvariantCultureIgnoreCase).ToArray();

                ParseSingleProject(optionalProjectRootForLocatingHeaders, projectFile, virtualPathComponents, boardName, extraIncludeDirs, subtype, result);
            }

            return result;
        }

        public void ParseSingleProject(string optionalProjectRootForLocatingHeaders, string projectFile, string[] virtualPathComponents, string boardName, List<string> extraIncludeDirs, ProjectSubtype subtype, List<VendorSample> result)
        {
            var projectFileDir = Path.GetDirectoryName(projectFile);

            if (virtualPathComponents == null)
                virtualPathComponents = new[] { "virtual", "sample", "path" };

            EclipseProject project;

            try
            {
                project = new EclipseProject(Path.GetDirectoryName(projectFile));

                project.FileNotFound += Project_FileNotFound;
            }
            catch (Exception ex)
            {
                OnParseFailed(ex, string.Join("-", virtualPathComponents), projectFileDir, "Failed to load project file");
                return;
            }

            ConfigurationWithContext[] configs;

            try
            {
                configs = DetectConfigurationContexts(project, subtype);
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

                    var sample = ParseSingleConfiguration(cfg.CConfiguration, optionalProjectRootForLocatingHeaders, Path.GetDirectoryName(projectFile),
                        boardName, cfg.Context, subtype);
                    sample.IncludeDirectories = sample.IncludeDirectories.Concat(extraIncludeDirs ?? new List<string>()).ToArray();
                    sample.VirtualPath = string.Join("\\", virtualPathComponents.Take(virtualPathComponents.Length - 1).ToArray());
                    sample.UserFriendlyName = virtualPathComponents.Last();
                    sample.InternalUniqueID = sampleID;

                    if (sample.InternalUniqueID.EndsWith("-NonSecure") && cfg.CConfiguration.OptionalToolchain.Linker.ReadOptionalList("com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.linker.option.additionalobjs") is string[] lst && lst.Length > 0)
                        sample.RelatedSamples = new[] { new VendorSampleReference { Type = VendorSampleReferenceType.OutgoingReference, ID = sample.InternalUniqueID.Substring(0, sample.InternalUniqueID.Length - 10) + "-Secure" } };
                    else if (sample.InternalUniqueID.EndsWith("-Secure") && sample.CMSEImportLibraryName != null)
                        sample.RelatedSamples = new[] { new VendorSampleReference { Type = VendorSampleReferenceType.IncomingReference, ID = sample.InternalUniqueID.Substring(0, sample.InternalUniqueID.Length - 7) + "-NonSecure" } };

                    result.Add(sample);
                }
                catch (Exception ex)
                {
                    OnParseFailed(ex, sampleID, projectFileDir, "General error while parsing a sample");
                }
            }
        }

        private void Project_FileNotFound(ref string missingFilePath) => OnFileNotFound(missingFilePath);

        public enum ProjectSubtype
        {
            Auto,
            SW4STM32,
            STM32CubeIDE,
            WiSEStudio,
        }

        public struct CommonConfigurationOptions
        {
            public string[] IncludeDirectories;
            public string[] PreprocessorMacros;
            public string BoardName;
            public string MCU;
            public List<EclipseProject.ParsedSourceFile> SourceFiles;
            public string LinkerScript;
            public List<string> Libraries;
            public string LDFLAGS;
            public bool UseCMSE;

            public string[] LibrarySearchDirs;  //Will not be directly mapped to LDFLAGS unless they contain linker scripts
        }

        CommonConfigurationOptions ExtractSW4STM32Options(EclipseProject.CConfiguration configuration)
        {
            var tools = configuration.RequireTools(EclipseTool.CCompiler | EclipseTool.CLinker);

            return new CommonConfigurationOptions
            {
                IncludeDirectories = tools.Compiler.ReadList("gnu.c.compiler.option.include.paths", PathTranslationFlags.AddExtraComponentToBaseDir),
                PreprocessorMacros = tools.Compiler.ReadList("gnu.c.compiler.option.preprocessor.def.symbols"),

                BoardName = tools.ReadOptionalValue("fr.ac6.managedbuild.option.gnu.cross.board"),
                MCU = tools.ReadValue("fr.ac6.managedbuild.option.gnu.cross.mcu"),
                Libraries = tools.Linker.ResolveLibraries("gnu.c.link.option.libs", "gnu.c.link.option.paths"),

                LinkerScript = tools.Linker.ReadOptionalValue("fr.ac6.managedbuild.tool.gnu.cross.c.linker.script", PathTranslationFlags.AddExtraComponentToBaseDir),

                LDFLAGS = tools.Linker.ReadValue("gnu.c.link.option.ldflags"),
                SourceFiles = configuration.ParseSourceList(ShouldIncludeSourceFile, true)
            };
        }

        static Regex rgStartup = new Regex("startup_stm.*\\.s");

        static bool ShouldIncludeSourceFile(string fullPath, int resourceType)
        {
            if (fullPath.EndsWith(".ioc", StringComparison.InvariantCultureIgnoreCase))
                return false; //.ioc files have too long names that will exceed our path length limit

            if (rgStartup.IsMatch(fullPath))
                return false;   //Our BSP already provides a startup file

            if (StringComparer.InvariantCultureIgnoreCase.Compare(Path.GetFileName(fullPath), "system_BlueNRG_LP.c") == 0)
                return false;   //This is the startup file on BlueNRG

            return true;
        }

        public static CommonConfigurationOptions ExtractSTM32CubeIDEOptions(EclipseProject.CConfiguration configuration, Dictionary<string, HashSet<string>> libraryDirCache = null)
        {
            var tools = configuration.RequireTools(EclipseTool.CCompiler | EclipseTool.CLinker | EclipseTool.CPPLinker);

            const string cflagsKey = "com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.linker.option.otherflags";
            var ldflags = tools.Linker.ReadOptionalValue(cflagsKey)?.Trim();
            if (string.IsNullOrEmpty(ldflags))
                ldflags = string.Join(" ", tools.Linker.ReadOptionalList(cflagsKey) ?? new string[0]);

            const string LibraryDirsKey = "com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.linker.option.directories";

            return new CommonConfigurationOptions
            {
                IncludeDirectories = tools.Compiler.ReadList("com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.compiler.option.includepaths", PathTranslationFlags.AddExtraComponentToBaseDir),
                PreprocessorMacros = tools.Compiler.ReadList("com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.compiler.option.definedsymbols", "DEBUG", "RELEASE"),

                MCU = tools.ReadValue("com.st.stm32cube.ide.mcu.gnu.managedbuild.option.target_mcu"),
                BoardName = tools.ReadValue("com.st.stm32cube.ide.mcu.gnu.managedbuild.option.target_board"),

                LinkerScript = tools.Linker.ReadOptionalValue("com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.linker.option.script", PathTranslationFlags.None) ??
                               tools.CPPLinker.ReadOptionalValue("com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.cpp.linker.option.script", PathTranslationFlags.None),

                LDFLAGS = ldflags,
                UseCMSE = tools.Compiler.ReadOptionalValue("com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.compiler.option.mcmse") == "true",

                Libraries = tools.Linker.ResolveLibraries("com.st.stm32cube.ide.mcu.gnu.managedbuild.tool.c.linker.option.libraries", LibraryDirsKey, libraryDirCache),
                SourceFiles = configuration.ParseSourceList(ShouldIncludeSourceFile, true),
                LibrarySearchDirs = tools.Linker.ReadOptionalList(LibraryDirsKey, PathTranslationFlags.AddExtraComponentToBaseDir)
            };
        }

        Dictionary<string, HashSet<string>> _LibraryDirCache = new Dictionary<string, HashSet<string>>();

        CommonConfigurationOptions ExtractWiSEOptions(EclipseProject.CConfiguration configuration)
        {
            var tools = configuration.RequireTools(EclipseTool.CCompiler | EclipseTool.CLinker | EclipseTool.CPPLinker);

            return new CommonConfigurationOptions
            {
                IncludeDirectories = tools.Compiler.ReadList("fr.ac6.ide.wise.managedbuild.option.c.compiler.include.paths", PathTranslationFlags.AddExtraComponentToBaseDir),
                PreprocessorMacros = tools.Compiler.ReadList("fr.ac6.ide.wise.managedbuild.option.c.compiler.defs", "DEBUG", "RELEASE"),

                MCU = "BlueNRG-LP",
                BoardName = "STEVAL-IDB011V1",

                LinkerScript = tools.Linker.ReadOptionalValue("fr.ac6.ide.wise.managedbuild.option.c.linker.script", PathTranslationFlags.AddExtraComponentToBaseDir) ??
                               tools.CPPLinker.ReadOptionalValue("fr.ac6.ide.wise.managedbuild.option.cpp.linker.script", PathTranslationFlags.AddExtraComponentToBaseDir),

                Libraries = tools.Linker.ResolveLibraries("fr.ac6.ide.wise.managedbuild.option.c.linker.libs", "fr.ac6.ide.wise.managedbuild.option.c.linker.paths", _LibraryDirCache),

                SourceFiles = configuration.ParseSourceList(ShouldIncludeSourceFile, true)
            };
        }

        private VendorSample ParseSingleConfiguration(EclipseProject.CConfiguration configuration,
            string optionalProjectRootForLocatingHeaders,
            string cprojectFileDir,
            string boardName,
            MultiConfigurationContext multiConfigurationContext,
            ProjectSubtype subtype)
        {
            VendorSample result = new VendorSample
            {
                UserFriendlyName = configuration.Project.Name ?? throw new Exception("Failed to determine sample name"),
                NoImplicitCopy = true,
            };

            if (optionalProjectRootForLocatingHeaders == null)
                optionalProjectRootForLocatingHeaders = cprojectFileDir;

            CommonConfigurationOptions opts;
            if (subtype == ProjectSubtype.Auto)
            {
                var toolchainType = configuration.OptionalToolchain?.SuperClass ?? "";
                if (toolchainType.StartsWith("fr.ac6.managedbuild.toolchain.gnu.cross"))
                    subtype = ProjectSubtype.SW4STM32;
                else if (toolchainType.StartsWith("com.st.stm32cube.ide.mcu.gnu.managedbuild"))
                    subtype = ProjectSubtype.STM32CubeIDE;
                else
                    throw new Exception("Failed to detect the project type");
            }

            if (subtype == ProjectSubtype.SW4STM32)
                opts = ExtractSW4STM32Options(configuration);
            else if (subtype == ProjectSubtype.WiSEStudio)
                opts = ExtractWiSEOptions(configuration);
            else
                opts = ExtractSTM32CubeIDEOptions(configuration, _LibraryDirCache);

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
            else if (multiConfigurationContext is MultiConfigurationContext.Other oth)
            {
                result.InternalUniqueID += oth.Suffix;
                result.UserFriendlyName += oth.Suffix;
            }

            ValidateFinalMCUName(ref mcu);

            result.DeviceID = mcu;
            result.SourceFiles = opts.SourceFiles.Select(f => FixAssemblyFileExtension(f.FullPath)).Concat(opts.Libraries).Distinct().ToArray();
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
                result.CMSEImportLibraryName = "secure_nsclib.o";
            }

            List<string> linkerScripts = new List<string>();

            if (!string.IsNullOrEmpty(opts.LDFLAGS))
            {
                var flags = opts.LDFLAGS.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                for (int i = 0; i < flags.Length; i++)
                {
                    string path;
                    if (flags[i] == "-L" && i < (flags.Length - 1))
                        path = flags[++i];
                    else if (flags[i].StartsWith("-L") && flags[i].Length > 2)
                        path = flags[i].Substring(2);
                    else
                        continue;

                    path = configuration.Project.TranslatePath(path, PathTranslationFlags.AddExtraComponentToBaseDir);
                    if (path != null)
                        linkerScripts.AddRange(Directory.GetFiles(path, "*.ld"));
                }
            }

            foreach (var dir in opts.LibrarySearchDirs ?? new string[0])
            {
                if (Directory.Exists(dir))
                    linkerScripts.AddRange(Directory.GetFiles(dir, "*.ld"));
            }

            if (linkerScripts.Count > 0)
                result.AuxiliaryLinkerScripts = linkerScripts.ToArray();

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

        static string FixAssemblyFileExtension(string fullPath)
        {
            if (fullPath.EndsWith(".s"))
            {
                var newPath = fullPath.Substring(0, fullPath.Length - 1) + "S";
                File.Move(fullPath, newPath);
                return newPath;
            }

            return fullPath;
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

        public static HashSet<_Ty> ToHashSet<_Ty>(this IEnumerable<_Ty> coll, IEqualityComparer<_Ty> comparer = null)
        {
            var dict = comparer == null ? new HashSet<_Ty>() : new HashSet<_Ty>(comparer);
            if (coll != null)
                foreach (var item in coll)
                    dict.Add(item);
            return dict;
        }
    }
}
