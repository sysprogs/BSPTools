/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using BSPEngine;
using LinkerScriptGenerator;
using System.Xml.Serialization;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Diagnostics;
using Microsoft.Win32;

namespace BSPGenerationTools
{
    public enum CortexCore
    {
        Invalid,
        M0,
        M0Plus,
        M3,
        M33,
        M4,
        M7,
        A7,
        R4,
        R5,
        NonARM,
    }

    public enum FPUType
    {
        None,
        SP,
        DP
    }

    public class BSPSummary
    {
        public class MCU
        {
            public string Name;
            public string UserFriendlyName;
            public int FLASHSize;
            public int RAMSize;
        }

        public List<MCU> MCUs = new List<MCU>();
        public string BSPID;
        public string BSPName;
        public string BSPVersion;
        public string MinimumEngineVersion;
        public string FileName;
    }

    public class MCUBuilder
    {
        public string Name;
        public int FlashSize;
        public int RAMSize;
        public CortexCore Core;
        public FPUType FPU;

        public string LinkerScriptPath;
        public string StartupFile;
        public string MCUDefinitionFile;
        public MemoryLayout AttachedMemoryLayout;

        public override bool Equals(Object obj)
        {
            //MCUBuilder m1 = (MCUBuilder)obj;
            if (Name == ((MCUBuilder)obj).Name)
                return true;
            else
                return false;
        }

        public override string ToString()
        {
            return Name;
        }

        public virtual MCU GenerateDefinition(MCUFamilyBuilder fam, BSPBuilder bspBuilder, bool requirePeripheralRegisters, bool allowIncompleteDefinition = false, MCUFamilyBuilder.CoreSpecificFlags flagsToAdd = MCUFamilyBuilder.CoreSpecificFlags.All)
        {
            if (!allowIncompleteDefinition && string.IsNullOrEmpty(LinkerScriptPath))
                throw new Exception("Linker script not defined for " + Name);
            if (!allowIncompleteDefinition && string.IsNullOrEmpty(StartupFile))
                throw new Exception("Startup file not defined for " + Name);
            if (string.IsNullOrEmpty(MCUDefinitionFile) && requirePeripheralRegisters)
                throw new Exception("Peripheral register definition not found for " + Name);

            var mcu = new MCU
            {
                ID = Name,
                FamilyID = fam.Definition.Name,
                FLASHSize = FlashSize,
                RAMSize = RAMSize,
                HierarchicalPath = $@"{bspBuilder.ShortName}\{fam.Definition.Name}",
                CompilationFlags = new ToolFlags
                {
                    PreprocessorMacros = new string[] { bspBuilder.GetMCUTypeMacro(this) },
                    LinkerScript = LinkerScriptPath,
                },
                AdditionalSourceFiles = new string[] { StartupFile }.Where(s => !string.IsNullOrEmpty(s)).ToArray(),
                MCUDefinitionFile = MCUDefinitionFile,
            };

            if (fam.Definition.HasMixedCores)
                MCUFamilyBuilder.AddCoreSpecificFlags(flagsToAdd, mcu, Core, FPU);
            else if (fam.Definition.HasMixedFPUs)
                MCUFamilyBuilder.AddFPUTypeFlag(mcu, Core, FPU);

            List<SysVarEntry> sysVars = new List<SysVarEntry>();
            foreach (var classifier in fam.Definition.Subfamilies ?? new MCUClassifier[0])
            {
                string category = classifier.TryMatchMCUName(Name);
                if (category == null)
                {
                    if (classifier.Required)
                        throw new Exception("Cannot detect subfamily for " + Name);
                }

                sysVars.Add(new SysVarEntry { Key = classifier.VariableName, Value = category });
            }

            if (sysVars.Count > 0)
                mcu.AdditionalSystemVars = sysVars.ToArray();

            if ((AttachedMemoryLayout?.Memories?.Count ?? 0) > 0)
            {
                var flash = AttachedMemoryLayout.Memories.FirstOrDefault(m => m.Type == MemoryType.FLASH);
                var ram = AttachedMemoryLayout.Memories.FirstOrDefault(m => m.Type == MemoryType.RAM);

                if (flash != null)
                {
                    mcu.FLASHBase = flash.Start;
                    mcu.FLASHSize = (int)flash.Size;
                }
                else
                {
                    Console.WriteLine($"Warning: could not find default FLASH for {mcu.ID}");
                }

                if (ram != null)
                {
                    mcu.RAMBase = ram.Start;
                    mcu.RAMSize = (int)ram.Size;
                }
                else
                {
                    Console.WriteLine($"Warning: could not find default RAM for {mcu.ID}");
                }

                mcu.MemoryMap = AttachedMemoryLayout.ToMemoryMap();
            }
            else
                bspBuilder.GetMemoryBases(out mcu.FLASHBase, out mcu.RAMBase);

            if (fam.Definition.ConfigFiles != null)
            {
                mcu.ConfigurationFileTemplates = fam.Definition.ConfigFiles
                    .Where(cf => cf.SeparateConfigsForEachMCU)
                    .Select(cf => bspBuilder.ParseConfigurationFile(cf, fam, null, mcu.AdditionalSystemVars))
                    .ToArray();
            }

            return mcu;
        }
    }

    public struct BSPDirectories
    {
        public readonly string InputDir, OutputDir, RulesDir, LogDir;

        public BSPDirectories(string inputDir, string outputDir, string rulesDir, string logDir)
        {
            InputDir = inputDir;
            OutputDir = outputDir;
            RulesDir = rulesDir;
            LogDir = logDir;
        }

        public static BSPDirectories MakeDefault(string[] args) => new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules", @"..\..\logs");
    }

    public class CopiedFileMonitor
    {
        readonly Dictionary<string, string> _AllCopiedFiles = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

        public void RememberFileMapping(string absSource, string absTarget, string encodedPath)
        {
            _AllCopiedFiles[Path.GetFullPath(absSource)] = encodedPath;
        }

        public const string MetadataSubdir = ".bspgen";
        public const string MappingFileName = "CopiedFiles.txt";

        public void SaveMapping(string bspDir)
        {
            Directory.CreateDirectory(Path.Combine(bspDir, MetadataSubdir));
            using (var sw = new StreamWriter(Path.Combine(bspDir, MetadataSubdir, MappingFileName)))
            {
                foreach (var kv in _AllCopiedFiles)
                    sw.WriteLine($"{kv.Key} => {kv.Value}");
            }
        }

        public static Dictionary<string, string> Load(string bspDir, bool deriveDirectoryMappings)
        {
            Dictionary<string, string> copiedFiles = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

            var fn = Path.Combine(bspDir, MetadataSubdir, MappingFileName);
            if (File.Exists(fn))
                foreach (var line in File.ReadAllLines(fn))
                {
                    int idx = line.IndexOf("=>");
                    if (idx == -1)
                        continue;

                    string realPath = line.Substring(0, idx).Trim(), bspPath = line.Substring(idx + 2).Trim();
                    copiedFiles[realPath] = bspPath;
                }

            if (deriveDirectoryMappings)
            {
                foreach (var kv in copiedFiles.ToArray())
                {
                    int idx = kv.Value.LastIndexOf('/');
                    if (idx == -1)
                        throw new Exception("Could not find the directory name for " + kv.Value);
                    copiedFiles[Path.GetDirectoryName(kv.Key)] = kv.Value.Substring(0, idx);
                }
            }

            return copiedFiles;
        }
    }

    public abstract class BSPBuilder : IDisposable
    {
        public readonly ReverseFileConditionBuilder ReverseFileConditions = new ReverseFileConditionBuilder();
        public readonly CopiedFileMonitor CopiedFileMonitor = new CopiedFileMonitor();

        public readonly BSPReportWriter Report;

        public LinkerScriptTemplate LDSTemplate;
        public readonly string BSPRoot;
        public string ShortName;
        public readonly Dictionary<string, string> SystemVars = new Dictionary<string, string>();

        public Dictionary<string, FileCondition> MatchedFileConditions = new Dictionary<string, FileCondition>();

        public Dictionary<string, string> RenamedFileTable = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        public string OnValueForSmartBooleanProperties = "1";   //Can be overridden to keep backward compatibility
        public readonly BSPDirectories Directories;

        public bool SkipHiddenFiles { get; protected set; }

        internal HashSet<string> StartupFilesWithAutoGeneratedConditions = new HashSet<string>();

        public void AddFileCondition(FileCondition cond)
        {
            if (MatchedFileConditions.TryGetValue(cond.FilePath, out var oldCond))
                MatchedFileConditions[cond.FilePath] = new FileCondition { FilePath = cond.FilePath, ConditionToInclude = new Condition.And { Arguments = new[] { oldCond.ConditionToInclude, cond.ConditionToInclude } } };
            else
                MatchedFileConditions[cond.FilePath] = cond;
        }

        public BSPBuilder(BSPDirectories dirs, string linkerScriptTemplate = null, int linkerScriptLevel = 4)
        {
            Report = new BSPReportWriter(dirs.LogDir);

            if (linkerScriptTemplate == null)
            {
                for (int i = 0; i < linkerScriptLevel; i++)
                    linkerScriptTemplate += @"..\";
                linkerScriptTemplate += @"GenericARM.ldsx";
            }

            Directories = dirs;
            SystemVars["BSPGEN:INPUT_DIR"] = dirs.InputDir;
            SystemVars["BSPGEN:RULES_DIR"] = dirs.RulesDir;
            if (linkerScriptTemplate != null && linkerScriptLevel >= 0)
                LDSTemplate = XmlTools.LoadObject<LinkerScriptTemplate>(linkerScriptTemplate);
            BSPRoot = dirs.OutputDir;
            if (Directory.Exists(dirs.OutputDir))
            {
                Console.Write("Deleting {0}...", dirs.OutputDir);
                DeleteDirectoryWithRetries(dirs.OutputDir);
                Console.WriteLine(" done");
            }
            Directory.CreateDirectory(dirs.OutputDir);
        }

        public PropertyDictionary2 ExportRenamedFileTable()
        {
            List<PropertyDictionary2.KeyValue> result = new List<PropertyDictionary2.KeyValue>();
            foreach (var kv in RenamedFileTable)
            {
                if (!kv.Key.StartsWith(Directories.OutputDir))
                    throw new Exception("Unexpected renamed file");

                string relativePath = kv.Key.Substring(Directories.OutputDir.Length).TrimStart('\\', '/').Replace('\\', '/');
                int idx = relativePath.LastIndexOf('/');
                string relativeDir = relativePath.Substring(0, idx);

                result.Add(new PropertyDictionary2.KeyValue { Value = "$$SYS:BSP_ROOT$$/" + relativePath, Key = $"$$SYS:BSP_ROOT$$/{relativeDir}/{kv.Value}" });
            }

            return new PropertyDictionary2 { Entries = result.ToArray() };
        }

        void DeleteDirectoryWithRetries(string dir)
        {
            for (int retry = 0; ; retry++)
            {
                try
                {
                    Directory.Delete(dir, true);
                    return;
                }
                catch
                {
                    try
                    {
                        if (Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length == 0)
                            return;
                    }
                    catch
                    {
                    }

                    if (retry >= 5)
                        throw;

                    Thread.Sleep(200);
                }
            }
        }

        public static void SaveBSP(BoardSupportPackage bsp, string dir, bool produceBSPArchive)
        {
            XmlTools.SaveObject(bsp, Path.Combine(dir, "BSP.XML"));

            string archiveName = string.Format("{0}-{1}.vgdbxbsp", bsp.PackageID.Split('.').Last(), bsp.PackageVersion);

            if (produceBSPArchive)
                TarPacker.PackDirectoryToTGZ(dir, Path.Combine(dir, archiveName), fn => Path.GetExtension(fn).ToLower() != ".vgdbxbsp", subdir => subdir.ToLower() != ".bspgen");

            BSPSummary lst = new BSPSummary
            {
                BSPName = bsp.PackageDescription,
                BSPID = bsp.PackageID,
                BSPVersion = bsp.PackageVersion,
                MinimumEngineVersion = bsp.MinimumEngineVersion,
                FileName = archiveName,
            };

            foreach (var mcu in bsp.SupportedMCUs)
                lst.MCUs.Add(new BSPSummary.MCU { Name = mcu.ID, FLASHSize = mcu.FLASHSize, RAMSize = mcu.RAMSize, UserFriendlyName = mcu.UserFriendlyName });

            XmlTools.SaveObject(lst, Path.Combine(dir, Path.ChangeExtension(archiveName, ".xml")));
        }

        public void Save(BoardSupportPackage bsp, bool produceBSPArchive, bool addFixedStackHeapFramework = true)
        {
            if (addFixedStackHeapFramework)
            {
                string dir = Path.Combine(Directories.OutputDir, "StackAndHeap");
                Directory.CreateDirectory(dir);
                File.Copy(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "StackAndHeap.c"), Path.Combine(dir, "StackAndHeap.c"));
                var framework = XmlTools.LoadObject<EmbeddedFramework>(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "StackAndHeap.xml"));
                bsp.Frameworks = LoadedBSP.Combine(bsp.Frameworks, new[] { framework });
            }

            SaveBSP(bsp, BSPRoot, produceBSPArchive);
        }


        public abstract MemoryLayoutAndSubstitutionRules GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family);

        public abstract void GetMemoryBases(out uint flashBase, out uint ramBase);

        protected virtual LinkerScriptTemplate GetTemplateForMCU(MCUBuilder mcu)
        {
            return LDSTemplate;
        }

        public virtual void GenerateLinkerScriptsAndUpdateMCU(string ldsDirectory,
            string familyFilePrefix,
            MCUBuilder mcu,
            MemoryLayoutAndSubstitutionRules layout,
            string generalizedName)
        {
            using (var gen = new LdsFileGenerator(GetTemplateForMCU(mcu), layout.Layout))
            {
                using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_flash.lds")))
                    gen.GenerateLdsFile(sw, null);

                using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_sram.lds")))
                    gen.GenerateLdsFile(sw, layout.MemorySubstitutionsForRAMScript ?? gen.CreateDefaultFLASHToRAMSubstitutionMap());

                mcu.LinkerScriptPath = string.Format("$$SYS:BSP_ROOT$$/{0}LinkerScripts/{1}_$${2}$$.lds", familyFilePrefix, generalizedName, MCUFamilyBuilder.PrimaryMemoryOptionName);
            }
        }

        public virtual bool OnFilePathTooLong(string pathInsidePackage)
        {
            throw new Exception("File path too long: " + pathInsidePackage);
        }

        public void ExpandVariables(ref string value)
        {
            if (value != null && value.Contains("$$"))
                value = VariableHelper.ExpandVariables(value, SystemVars);
        }

        public string ExpandVariables(string value)
        {
            ExpandVariables(ref value);
            return value;
        }

        internal void ExpandAdditionalVariables(ref string strSources, SysVarEntry[] AddVariables)
        {
            if (AddVariables != null && strSources.Contains("$$"))
                foreach (var entry in AddVariables)
                    strSources = strSources.Replace("$$" + entry.Key + "$$", entry.Value);
        }

        public virtual string GetMCUTypeMacro(MCUBuilder mcu)
        {
            return mcu.Name;
        }

        public void ValidateBSP(BoardSupportPackage bsp)
        {
            int devicesWithZeroRAM = bsp.SupportedMCUs.Count(dev => dev.RAMSize == 0);
            if (devicesWithZeroRAM > 0)
                throw new Exception($"Found {devicesWithZeroRAM} devices with RAMSize = 0. Please fix the list.");

            HashSet<string> reportedClassIDs = new HashSet<string>();

            foreach (var dev in bsp.SupportedMCUs)
            {
                if (dev.MemoryMap != null)
                {
                    bool foundMainFLASH = false;

                    foreach (var mem in dev.MemoryMap.Memories)
                    {
                        if ((mem.Flags & MCUMemoryFlags.IsDefaultFLASH) == MCUMemoryFlags.IsDefaultFLASH)
                            foundMainFLASH = true;
                    }

                    if (!foundMainFLASH && dev.FamilyID != "STM32MP1")
                        throw new Exception($"Memory map for {dev.ID} does not contain a FLASH memory");
                }

                foreach (var fw in bsp.Frameworks.Where(fw => fw.IsCompatibleWithMCU(dev.ID)).GroupBy(fw => fw.ClassID).Where(g => g.Key != null))
                {
                    if (fw.Count() > 1 && !reportedClassIDs.Contains(fw.Key))
                    {
                        Report.ReportRawError($"{fw.Key} ClassID corresponds to more than 1 framework on {dev.ID}:");
                        foreach (var fwObj in fw)
                            Report.ReportRawError($"    {fwObj.ID}");
                        Report.ReportRawError($"This will break referencing those frameworks via VisualGDB Project Properties. Specify MCUFilterRegex for those frameworks to ensure only 1 is compatible with each device.");
                        Report.ReportRawError("");

                        reportedClassIDs.Add(fw.Key);
                    }
                }
            }

            Dictionary<string, string> usedFoldersToCompatibleIDs = new Dictionary<string, string>();
            foreach (var fw in bsp.Frameworks ?? new EmbeddedFramework[0])
            {
                var id = fw.ClassID ?? fw.ID;
                if (usedFoldersToCompatibleIDs.TryGetValue(fw.ProjectFolderName, out var tmp) && tmp != id)
                {
                    Report.ReportRawError($"'{fw.ProjectFolderName}' is used by both {id} and {tmp}. This will break builds in Visual Studio.");
                }
                usedFoldersToCompatibleIDs[fw.ProjectFolderName] = id;

                if (fw.ConfigurationFileTemplates != null)
                {
                    foreach (var ft in fw.ConfigurationFileTemplates)
                    {
                        var path = ExpandVariables(ft.SourcePath).Replace("$$SYS:BSP_ROOT$$", Directories.OutputDir);
                        if (!File.Exists(path))
                            Report.ReportMergeableError("Missing configuration template", ft.SourcePath);
                    }
                }
            }

            var allSources = bsp.MCUFamilies.SelectMany(f => TranslateFileList(f.AdditionalSourceFiles, f.ID))
                .Concat(bsp.SupportedMCUs.SelectMany(m => TranslateFileList(m.AdditionalSourceFiles, m.ID)))
                .Concat(bsp.Frameworks.SelectMany(fw => TranslateFileList(fw.AdditionalSourceFiles, fw.MCUFilterRegex?.ToString())))
                .Distinct();

            var sourcesByName = allSources.Where(s => s.File.EndsWith(".c", StringComparison.InvariantCultureIgnoreCase) || s.File.EndsWith(".cpp", StringComparison.InvariantCultureIgnoreCase))
                .GroupBy(f => Path.GetFileName(f.File), StringComparer.InvariantCultureIgnoreCase);

            foreach (var grp in sourcesByName)
            {
                if (grp.Count() > 1)
                {
                    if (!AreFilesMutuallyExclusive(bsp, grp.ToArray()))
                    {
                        Report.ReportRawError($"Found multiple files called '{grp.Key}':");
                        foreach (var fn in grp)
                            Report.ReportRawError("  " + fn);
                    }
                }
            }
        }

        public virtual void PatchSmartFileConditions(ref string[] smartFileConditions, string expandedSourceFolder, string subdir, CopyJob copyJob)
        {
        }

        private IEnumerable<FileWithContext> TranslateFileList(string[] files, string deviceSpecificCondition)
        {
            if (files == null)
                return new FileWithContext[0];

            return files.Select(f => new FileWithContext { File = f, DeviceSpecificCondition = deviceSpecificCondition });
        }

        struct FileWithContext
        {
            public string File;
            public string DeviceSpecificCondition;

            public override string ToString() => File;
        }

        public class PerDeviceFileList
        {
            public List<Condition> Conditions = new List<Condition>();
        }


        private bool AreFilesMutuallyExclusive(BoardSupportPackage bsp, FileWithContext[] files)
        {
            Dictionary<string, PerDeviceFileList> conditionsByDevice = new Dictionary<string, PerDeviceFileList>();
            foreach (var file in files)
            {
                var cond = bsp.FileConditions.FirstOrDefault(c => c.FilePath == file.File);

                if (!conditionsByDevice.TryGetValue(file.DeviceSpecificCondition ?? "", out var lst))
                    conditionsByDevice[file.DeviceSpecificCondition ?? ""] = lst = new PerDeviceFileList();

                lst.Conditions.Add(cond?.ConditionToInclude);
            }

            foreach (var kv in conditionsByDevice)
            {
                if (kv.Value.Conditions.Count == 1 && !string.IsNullOrEmpty(kv.Key))
                    continue;  //Only 1 file instance found for this device type.

                var conditionsByEqualsExpression = kv.Value.Conditions.GroupBy(ExtractRelevantVariable).ToArray();
                if (conditionsByEqualsExpression.Length == 1 && conditionsByEqualsExpression[0].Key != null)
                    continue;  //All conditions refer to the same variable. They are likely mutually exclusive.

                Debugger.Break();   //Most likely, we are not accounting for some special case. Investigate it.
                return false;
            }

            return true;
        }

        private string ExtractRelevantVariable(Condition cond)
        {
            if (cond is Condition.Not cn)
                return ExtractRelevantVariable(cn.Argument);
            else if (cond is Condition.Equals ce)
                return ce.Expression;
            else if (cond is Condition.MatchesRegex cm)
                return cm.Expression;
            else
                return null;
        }

        public void Dispose()
        {
            Report.Dispose();
            CopiedFileMonitor.SaveMapping(Directories.OutputDir);
        }

        struct QueuedConfigurationFileTemplate
        {
            public ConfigurationFileTemplateEx Template;
            public MCUFamilyBuilder Family;
            public EmbeddedFramework OptionalFramework;

            public QueuedConfigurationFileTemplate(ConfigurationFileTemplateEx result, MCUFamilyBuilder family, EmbeddedFramework optionalFramework)
            {
                Template = result;
                Family = family;
                OptionalFramework = optionalFramework;
            }
        }

        List<QueuedConfigurationFileTemplate> _ProcessedConfigurationFileTemplates = new List<QueuedConfigurationFileTemplate>();

        public ConfigurationFileTemplate ParseConfigurationFile(ConfigFileDefinition cf, MCUFamilyBuilder family, EmbeddedFramework optionalFramework, SysVarEntry[] additionalSystemVars = null)
        {
            string sourceFile = ExpandVariables(cf.Path);
            sourceFile = VariableHelper.ExpandVariables(sourceFile, additionalSystemVars?.ToDictionary(kv => kv.Key, kv => kv.Value));

            var parserClass = new[] { Assembly.GetExecutingAssembly(), GetType().Assembly }.Select(a => a.GetType(cf.ParserClass, false)).FirstOrDefault(p => p != null);
            var parser = parserClass?.GetConstructor(new Type[0])?.Invoke(new object[0]) as IConfigurationFileParser;

            if (parser == null)
                throw new Exception("Failed to instantiate " + cf.ParserClass);

            if (!File.Exists(sourceFile))
                throw new Exception("Missing " + sourceFile);

            var result = parser.BuildConfigurationFileTemplate(sourceFile, cf);
            var template = result.Template;

            if (cf.FinalName != null && template.TargetFileName != null)
                template.TargetFileName = cf.FinalName;

            if (cf.TargetPathForInsertingIntoProject != null)
            {
                template.SourcePath = cf.TargetPathForInsertingIntoProject;
            }

            if (result.TestableHeaderFiles != null && result.TestableParameters != null)
            {
                result.Template = template;
                _ProcessedConfigurationFileTemplates.Add(new QueuedConfigurationFileTemplate(result, family, optionalFramework));
            }
            return template;
        }



        public void ComputeAutofixHintsForConfigurationFiles(BoardSupportPackage bsp)
        {
            var temporaryDir = Path.Combine(Path.GetTempPath(), "BSPAutofix");

            var clangExe = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Sysprogs\BSPGenerators")?.GetValue("clang") as string;
            if (clangExe == null || !File.Exists(clangExe))
                return;

            var compatFlags = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Sysprogs\BSPGenerators")?.GetValue("clang-compatflags") as string;

            var bspRoot = Path.GetFullPath(Directories.OutputDir);

            List<ConfigurationFixDatabase.ConfigurationFileEntry> autofixEntries = new List<ConfigurationFixDatabase.ConfigurationFileEntry>();

            foreach (var cf in _ProcessedConfigurationFileTemplates)
            {
                var matchingFamily = bsp.MCUFamilies.First(f => f.ID == cf.Family.Definition.Name);
                var firstMCU = bsp.SupportedMCUs.First(m => m.FamilyID == matchingFamily.ID);

                if (Directory.Exists(temporaryDir))
                    Directory.Delete(temporaryDir, true);
                Thread.Sleep(500);
                Directory.CreateDirectory(temporaryDir);

                var templateFile = cf.Template.Template.SourcePath;
                templateFile = cf.Family.BSP.ExpandVariables(templateFile);
                var configFile = Path.Combine(temporaryDir, cf.Template.Template.TargetFileName);
                Console.WriteLine($"Analyzing {cf.Template.Template.TargetFileName}...");

                File.Copy(templateFile.Replace("$$SYS:BSP_ROOT$$", bspRoot), configFile);

                var finalFlags = firstMCU.CompilationFlags.Merge(matchingFamily.CompilationFlags);
                if (cf.OptionalFramework != null)
                {
                    finalFlags.IncludeDirectories = finalFlags.IncludeDirectories.Concat(cf.OptionalFramework.AdditionalIncludeDirs ?? new string[0]).ToArray();
                    finalFlags.PreprocessorMacros = finalFlags.PreprocessorMacros.Concat(cf.OptionalFramework.AdditionalPreprocessorMacros ?? new string[0]).ToArray();
                }

                string rspFile = Path.Combine(temporaryDir, "test.rsp");
                var flagsAsArray = finalFlags.GetEffectiveCFLAGS(false).Split(' ');

                var dict = new Dictionary<string, string>();
                dict["SYS:BSP_ROOT"] = bspRoot;
                foreach (var kv in firstMCU.AdditionalSystemVars ?? new SysVarEntry[0])
                    dict[kv.Key] = kv.Value;

                foreach (var kv in matchingFamily.ConfigurableProperties?.GetDefaultPropertyValues() ?? new Dictionary<string, string>())
                    dict[kv.Key] = kv.Value;

                foreach (var kv in firstMCU.ConfigurableProperties?.GetDefaultPropertyValues() ?? new Dictionary<string, string>())
                    dict[kv.Key] = kv.Value;

                foreach (var kv in cf.OptionalFramework?.ConfigurableProperties?.GetDefaultPropertyValues() ?? new Dictionary<string, string>())
                    dict[kv.Key] = kv.Value;

                string flagsAsString = string.Join(" ", flagsAsArray.Select(f => VariableHelper.ExpandVariables(f, dict).Replace('\\', '/')).Where(f => !f.StartsWith("-m")));

                File.WriteAllText(rspFile, flagsAsString + " " + compatFlags);

                string testFile = Path.Combine(temporaryDir, "test.c");
                File.WriteAllLines(testFile, cf.Template.TestableHeaderFiles.Select(hf => $"#include <{hf}>"));

                using (var analyzer = new ConfigFileAnalyzer(clangExe, temporaryDir, testFile, rspFile, configFile, cf.Template))
                {
                    var initialSymbols = analyzer.BuildGlobalSymbolList();

                    foreach (var p in cf.Template.TestableParameters)
                    {
                        Console.Write($"  {p.Name}...");
                        analyzer.SetParameterValue(p.Name, p.DisabledValue, true);
                        var symbols = analyzer.BuildGlobalSymbolList();
                        analyzer.SetParameterValue(p.Name, p.EnabledValue, false);

                        var newSymbols = initialSymbols.Except(symbols).ToArray();

                        Console.WriteLine($" => {newSymbols.Length} symbols");

                        if (newSymbols.Length > 0)
                        {
                            autofixEntries.Add(new ConfigurationFixDatabase.ConfigurationFileEntry
                            {
                                File = cf.Template.Template.TargetFileName,
                                Name = p.Name,
                                Value = p.EnabledValue,
                                ProvidedSymbols = newSymbols
                            });
                        }
                    }

                }
            }

            if (autofixEntries.Count > 0)
            {
                XmlTools.SaveObject(new ConfigurationFixDatabase { ConfigurationFileEntries = autofixEntries.ToArray() }, Path.Combine(bspRoot, ConfigurationFixDatabase.FileName));
            }
        }
    }

    public class MCUFamilyBuilder
    {
        public BSPBuilder BSP;
        public readonly string FamilyFilePrefix;

        public MCUFamilyBuilder(BSPBuilder bspBuilder, FamilyDefinition definition)
        {
            bspBuilder.ExpandVariables(ref definition.PrimaryHeaderDir);
            bspBuilder.ExpandVariables(ref definition.StartupFileDir);

            if (definition.SmartSamples != null)
                foreach (var simple in definition.SmartSamples)
                {
                    for (int count = 0; count < simple.AdditionalSources?.Count(); count++)
                    {
                        string addSource = simple.AdditionalSources[count]; ;
                        bspBuilder.ExpandAdditionalVariables(ref simple.AdditionalSources[count], definition.AdditionalSystemVars);
                    }
                }

            BSP = bspBuilder;
            Definition = definition;
            if (string.IsNullOrEmpty(definition.FamilySubdirectory))
                FamilyFilePrefix = "";
            else
                FamilyFilePrefix = definition.FamilySubdirectory + "/";
        }

        public List<MCUBuilder> MCUs = new List<MCUBuilder>();

        public const string PrimaryMemoryOptionName = "com.sysprogs.bspoptions.primary_memory";
        public const string SecureModeOptionName = "com.sysprogs.bspoptions.cmse";
        public readonly FamilyDefinition Definition;

        const string IgnoreStartupFileProperty = "com.sysprogs.mcuoptions.ignore_startup_file";

        public MCUFamily GenerateFamilyObject(bool defineConfigurationVariables, bool allowExcludingStartupFiles = false) => GenerateFamilyObject(defineConfigurationVariables ? CoreSpecificFlags.All : CoreSpecificFlags.None, allowExcludingStartupFiles);

        public virtual MCUFamily GenerateFamilyObject(CoreSpecificFlags flagsToGenerate, bool allowExcludingStartupFiles = false)
        {
            var family = new MCUFamily { ID = Definition.Name };

            if (!Definition.HasMixedCores)
            {
                if (MCUs.Count == 0)
                    throw new Exception("No MCUs found for " + Definition.Name);

                var core = MCUs[0].Core;
                var fpu = MCUs[0].FPU;

                foreach (var mcu in MCUs)
                {
                    if (mcu.Core != core)
                        throw new Exception("Different MCUs within " + Definition.Name + " have different core types");
                    if (!Definition.HasMixedFPUs && mcu.FPU != fpu)
                        throw new Exception("Different MCUs within " + Definition.Name + " have different FPUs");
                }

                AddCoreSpecificFlags(flagsToGenerate, family, core, Definition.HasMixedFPUs ? default(FPUType?) : fpu);
            }

            family.CompilationFlags = family.CompilationFlags.Merge(Definition.CompilationFlags);

            List<string> projectFiles = new List<string>();
            CopyFamilyFiles(ref family.CompilationFlags, projectFiles);

            if (Definition.AdditionalSourceFiles != null)
                projectFiles.AddRange(Definition.AdditionalSourceFiles);

            family.AdditionalSourceFiles = projectFiles.Where(f => !IsHeaderFile(f) && !f.EndsWith(".a")).ToArray();
            family.AdditionalHeaderFiles = projectFiles.Where(f => IsHeaderFile(f)).ToArray();

            family.AdditionalSystemVars = LoadedBSP.Combine(family.AdditionalSystemVars, Definition.AdditionalSystemVars);

            if (Definition.CoreFramework?.ConfigurableProperties != null)
            {
                if (Definition.ConfigurableProperties == null)
                    Definition.ConfigurableProperties = Definition.CoreFramework.ConfigurableProperties;
                else
                    Definition.ConfigurableProperties.Import(Definition.CoreFramework.ConfigurableProperties);
            }

            if (Definition.ConfigurableProperties != null || allowExcludingStartupFiles)
            {
                if (family.ConfigurableProperties == null)
                    family.ConfigurableProperties = new PropertyList();

                if (Definition.ConfigurableProperties != null)
                    family.ConfigurableProperties.Import(Definition.ConfigurableProperties);

                if (allowExcludingStartupFiles && MCUs != null)
                {
                    family.ConfigurableProperties.Import(new PropertyList
                    {
                        PropertyGroups = new List<PropertyGroup>()
                    {
                        new PropertyGroup
                        {
                            Properties = new List<PropertyEntry>
                            {
                                new PropertyEntry.Boolean
                                {
                                    DefaultValue = false,
                                    ValueForTrue = "1",
                                    Name = "Exclude the startup file from project",
                                    UniqueID = IgnoreStartupFileProperty,
                                }
                            }
                        }
                    }
                    });

                    foreach (var mcu in MCUs)
                        if (mcu.StartupFile != null && !BSP.StartupFilesWithAutoGeneratedConditions.Contains(mcu.StartupFile))
                        {
                            BSP.StartupFilesWithAutoGeneratedConditions.Add(mcu.StartupFile);
                            BSP.AddFileCondition(new FileCondition { FilePath = mcu.StartupFile, ConditionToInclude = new Condition.Not { Argument = new Condition.Equals { Expression = $"$${IgnoreStartupFileProperty}$$", ExpectedValue = "1" } } });
                        }
                }
            }


            return family;
        }

        [Flags]
        public enum CoreSpecificFlags
        {
            None = 0,
            FPU = 0x01,
            PrimaryMemory = 0x02,
            SecureMode = 0x04,
            ConciseFPUMacro = 0x08,
            All = FPU | PrimaryMemory | SecureMode,
        }

        internal static void AddFPUTypeFlag(MCUFamily mcuObj, CortexCore core, FPUType fpu)
        {
            if (fpu == FPUType.None)
                return;

            string sp = (fpu == FPUType.SP) ? "-sp" : "";
            string flag;

            switch (core)
            {
                case CortexCore.R4:
                case CortexCore.R5:
                    flag = $"-mfpu=vfpv3{sp}-d16";
                    break;
                case CortexCore.M7:
                case CortexCore.M33:
                    flag = $"-mfpu=fpv5{sp}-d16";
                    break;
                default:
                    if (fpu != FPUType.SP)
                        throw new Exception("Unsupported configuration. Recheck the original flags.");

                    flag = $"-mfpu=fpv4{sp}-d16";
                    break;
            }

            if (mcuObj.CompilationFlags == null)
                mcuObj.CompilationFlags = new ToolFlags();
            if (string.IsNullOrEmpty(mcuObj.CompilationFlags.COMMONFLAGS))
                mcuObj.CompilationFlags.COMMONFLAGS = flag;
            else
                mcuObj.CompilationFlags.COMMONFLAGS += " " + flag;
        }

        internal static void AddCoreSpecificFlags(CoreSpecificFlags flagsToDefine, MCUFamily family, CortexCore core, FPUType? fpuType)
        {
            string coreName = null, freertosPort = null, threadxPort = null;
            switch (core)
            {
                case CortexCore.M0:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m0 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM0" };
                    freertosPort = "ARM_CM0";
                    coreName = "M0";
                    break;
                case CortexCore.M0Plus:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m0plus -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM0PLUS" };
                    freertosPort = "ARM_CM0";
                    coreName = "M0";
                    break;
                case CortexCore.M3:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m3 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM3" };
                    coreName = "M3";
                    freertosPort = "ARM_CM3";
                    break;
                case CortexCore.M33:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m33 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM33" };
                    coreName = "M33";
                    freertosPort = "ARM_CM33_NTZ/non_secure";
                    break;
                case CortexCore.M4:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m4 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM4" };
                    if (fpuType == FPUType.None)
                        freertosPort = "ARM_CM3";
                    else
                        freertosPort = "ARM_CM4F";
                    coreName = "M4";
                    break;
                case CortexCore.M7:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m7 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM7" };
                    coreName = "M7";
                    freertosPort = "ARM_CM7/r0p1";
                    break;
                case CortexCore.R4:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-r4 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CR4" };
                    break;
                case CortexCore.R5:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-r5 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CR5" };
                    break;
                case CortexCore.A7:
                    throw new Exception("Cortex-A7 core requires a Linux-based toolchain.");
                case CortexCore.NonARM:
                    break;
                default:
                    throw new Exception("Unsupported core type");
            }

            if (fpuType.HasValue)
                AddFPUTypeFlag(family, core, fpuType.Value);

            if ((flagsToDefine & CoreSpecificFlags.PrimaryMemory) == CoreSpecificFlags.PrimaryMemory)
            {
                if (core == CortexCore.M0)
                    family.AdditionalSystemVars = new SysVarEntry[] { new SysVarEntry { Key = PrimaryMemoryOptionName, Value = "flash" } };
                else
                {
                    var prop = new PropertyEntry.Enumerated
                    {
                        Name = "Execute from",
                        UniqueID = PrimaryMemoryOptionName,
                        SuggestionList = new PropertyEntry.Enumerated.Suggestion[]
                        {
                            new PropertyEntry.Enumerated.Suggestion{InternalValue = "flash", UserFriendlyName = "FLASH"},
                            new PropertyEntry.Enumerated.Suggestion{InternalValue = "sram", UserFriendlyName = "SRAM"},
                        }
                    };

                    ProvideDefaultPropertyGroup(family).Properties.Add(prop);
                }
            }

            if (core == CortexCore.M33 && (flagsToDefine & CoreSpecificFlags.SecureMode) == CoreSpecificFlags.SecureMode)
            {
                family.CompilationFlags.COMMONFLAGS += $" $${SecureModeOptionName}$$";

                var prop = new PropertyEntry.Boolean
                {
                    Name = "Enable Armv8-M Security Extensions",
                    UniqueID = SecureModeOptionName,
                    ValueForTrue = "-mcmse",
                };

                ProvideDefaultPropertyGroup(family).Properties.Add(prop);
            }

            if (fpuType != FPUType.None)
            {
                if ((flagsToDefine & CoreSpecificFlags.ConciseFPUMacro) == CoreSpecificFlags.ConciseFPUMacro)
                {
                    if (family.ConfigurableProperties == null)
                        family.ConfigurableProperties = new PropertyList { PropertyGroups = new List<PropertyGroup> { new PropertyGroup() } };
                    family.ConfigurableProperties.PropertyGroups[0].Properties.Add(
                        new PropertyEntry.Enumerated
                        {
                            Name = "Floating point support",
                            UniqueID = "com.sysprogs.bspoptions.arm.floatmode.short",
                            SuggestionList = new PropertyEntry.Enumerated.Suggestion[]
                                        {
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "soft", UserFriendlyName = "Software"},
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "hard", UserFriendlyName = "Hardware"},
                                        },
                            DefaultEntryIndex = 1,
                        });

                    family.CompilationFlags.COMMONFLAGS += " -mfloat-abi=$$com.sysprogs.bspoptions.arm.floatmode.short$$";
                }
                else if ((flagsToDefine & CoreSpecificFlags.FPU) == CoreSpecificFlags.FPU)
                {
                    if (family.ConfigurableProperties == null)
                        family.ConfigurableProperties = new PropertyList { PropertyGroups = new List<PropertyGroup> { new PropertyGroup() } };
                    family.ConfigurableProperties.PropertyGroups[0].Properties.Add(
                        new PropertyEntry.Enumerated
                        {
                            Name = "Floating point support",
                            UniqueID = "com.sysprogs.bspoptions.arm.floatmode",
                            SuggestionList = new PropertyEntry.Enumerated.Suggestion[]
                                        {
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "-mfloat-abi=soft", UserFriendlyName = "Software"},
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "-mfloat-abi=hard", UserFriendlyName = "Hardware"},
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "", UserFriendlyName = "Unspecified"},
                                        },
                            DefaultEntryIndex = 1,
                        });

                    family.CompilationFlags.COMMONFLAGS += " $$com.sysprogs.bspoptions.arm.floatmode$$";
                }
            }

            List<SysVarEntry> vars = new List<SysVarEntry>();

            if (coreName != null)
                vars.Add(new SysVarEntry { Key = "com.sysprogs.bspoptions.arm.core", Value = coreName });
            if (freertosPort != null)
                vars.Add(new SysVarEntry { Key = "com.sysprogs.freertos.default_port", Value = freertosPort });

            if (vars.Count > 0)
                family.AdditionalSystemVars = LoadedBSP.Combine(family.AdditionalSystemVars, vars.ToArray());
        }

        static PropertyGroup ProvideDefaultPropertyGroup(MCUFamily family)
        {
            if (family.ConfigurableProperties == null)
                family.ConfigurableProperties = new PropertyList();
            if (family.ConfigurableProperties.PropertyGroups == null)
                family.ConfigurableProperties.PropertyGroups = new List<PropertyGroup>();

            var group = family.ConfigurableProperties.PropertyGroups.FirstOrDefault(g => string.IsNullOrEmpty(g.UniqueID));
            if (group == null)
                family.ConfigurableProperties.PropertyGroups.Add(group = new PropertyGroup());
            return group;
        }

        public static bool IsHeaderFile(string fn)
        {
            string ext = Path.GetExtension(fn).ToLower();
            return ext == ".h" || ext == ".hpp";
        }

        public void CopyFamilyFiles(ref ToolFlags flags, List<string> projectFiles)
        {
            var unused = new List<ConfigurationFileTemplate>();
            if (Definition.CoreFramework != null)
                foreach (var job in Definition.CoreFramework.CopyJobs)
                {
                    flags = flags.Merge(job.CopyAndBuildFlags(BSP,
                        projectFiles,
                        Definition.FamilySubdirectory,
                        ref Definition.CoreFramework.ConfigurableProperties,
                        BSP.ReverseFileConditions.RootHandle,
                        unused,
                        BSP.CopiedFileMonitor));
                }
        }

        class MemoryComparer : IEqualityComparer<Memory>
        {
            public bool Equals(Memory x, Memory y)
            {
                if (x.Name != y.Name)
                    return false;
                if (x.Start != y.Start)
                    return false;
                if (x.Size != y.Size)
                    return false;
                if (x.Type != y.Type)
                    return false;
                if (x.Access != y.Access)
                    return false;
                return true;
            }

            public int GetHashCode(Memory obj)
            {
                return (int)(obj.Start * obj.Size);
            }
        }

        public class MemoryLayoutCollection : Dictionary<string, MemoryLayoutAndSubstitutionRules>
        {
        }

        public virtual MemoryLayoutCollection GenerateLinkerScripts(bool generalizeWherePossible)
        {
            string ldsDirectory = Path.Combine(BSP.BSPRoot, Definition.FamilySubdirectory ?? ".", "LinkerScripts");
            Directory.CreateDirectory(ldsDirectory);

            Dictionary<string, bool> generalizationResults = new Dictionary<string, bool>();
            var memoryLayouts = new MemoryLayoutCollection();

            foreach (var mcu in MCUs)
                memoryLayouts[mcu.Name] = BSP.GetMemoryLayout(mcu, this);

            var comparer = new MemoryComparer();

            foreach (var mcu in MCUs)
            {
                var layout = memoryLayouts[mcu.Name];
                string generalizedName = null;
                bool generalized = false;

                if (generalizeWherePossible)
                {
                    List<string> generalizations = new List<string>();
                    for (int i = 0; i <= mcu.Name.Length; i++)
                        generalizations.Add(mcu.Name.Substring(0, i) + new string('x', mcu.Name.Length - i));
                    for (int i = 0; i < mcu.Name.Length; i++)
                        generalizations.Add(mcu.Name.Substring(0, i) + 'x' + mcu.Name.Substring(i + 1));

                    foreach (var g in generalizations)
                    {
                        generalizedName = g;
                        Regex regex = new Regex(generalizedName.Replace('x', '.'));
                        if (!generalizationResults.TryGetValue(generalizedName, out generalized))
                        {
                            generalized = true;

                            foreach (var kv in memoryLayouts)
                                if (regex.IsMatch(kv.Key))
                                {
                                    if (!Enumerable.SequenceEqual(kv.Value.Layout.Memories, layout.Layout.Memories, comparer))
                                    {
                                        generalized = false;
                                        break;
                                    }
                                }

                            generalizationResults[generalizedName] = generalized;
                        }

                        if (generalized)
                            break;
                    }
                }

                if (!generalized)
                    generalizedName = mcu.Name;

                layout.Layout.DeviceName = generalizedName;

                BSP.GenerateLinkerScriptsAndUpdateMCU(ldsDirectory, FamilyFilePrefix, mcu, layout, generalizedName);
                mcu.AttachedMemoryLayout = layout.Layout;
            }

            return memoryLayouts;
        }


        public IEnumerable<EmbeddedFramework> GenerateFrameworkDefinitions(HashSet<string> excludedFrameworks = null)
        {
            if (Definition.AdditionalFrameworks != null)
            {
                IEnumerable<Framework> allFrameworks = Definition.AdditionalFrameworks;
                if (Definition.AdditionalFrameworkTemplates != null)
                    foreach (var t in Definition.AdditionalFrameworkTemplates)
                        allFrameworks = allFrameworks.Concat(t.Expand(BSP));

                foreach (var fw in allFrameworks)
                {
                    if (excludedFrameworks?.Contains(fw.ID) == true)
                        continue;

                    List<string> projectFiles = new List<string>();
                    var fwDef = new EmbeddedFramework
                    {
                        ID = fw.ID,
                        UserFriendlyName = fw.Name,
                        ProjectFolderName = fw.ProjectFolderName,
                        DefaultEnabled = fw.DefaultEnabled,
                        RequiredFrameworks = fw.RequiredFrameworks,
                        IncompatibleFrameworks = fw.IncompatibleFrameworks,
                        ClassID = fw.ClassID,
                        AdditionalForcedIncludes = fw.AdditionalForcedIncludes?.Split(';'),
                    };

                    var configTemplates = new List<ConfigurationFileTemplate>();
                    if (fw.ConfigurationFileTemplates != null)
                        configTemplates.AddRange(fw.ConfigurationFileTemplates);

                    if (fw.Filter != null)
                        fwDef.MCUFilterRegex = fw.Filter;
                    else if (Definition.DeviceRegex != null)
                        fwDef.MCUFilterRegex = Definition.DeviceRegex;

                    ToolFlags flags = new ToolFlags();
                    foreach (var job in fw.CopyJobs)
                    {
                        flags = flags.Merge(job.CopyAndBuildFlags(BSP,
                            projectFiles,
                            Definition.FamilySubdirectory,
                            ref fw.ConfigurableProperties,
                            BSP.ReverseFileConditions.GetHandleForFramework(fw),
                            configTemplates,
                            BSP.CopiedFileMonitor));
                    }

                    if (configTemplates.Count > 0)
                        fwDef.ConfigurationFileTemplates = configTemplates.ToArray();

                    fwDef.AdditionalSourceFiles = projectFiles.Where(f => !IsHeaderFile(f) && !f.EndsWith(".a", StringComparison.InvariantCultureIgnoreCase)).ToArray();
                    fwDef.AdditionalHeaderFiles = projectFiles.Where(f => IsHeaderFile(f)).ToArray();
                    fwDef.AdditionalLibraries = projectFiles.Where(f => f.EndsWith(".a", StringComparison.InvariantCultureIgnoreCase)).ToArray();

                    if (fw.LibraryOrder != null && fwDef.AdditionalLibraries.Length > 0)
                    {
                        Regex[] regexes = fw.LibraryOrder.Split(';').Select(s => new Regex(s, RegexOptions.IgnoreCase)).ToArray();
                        Dictionary<string, int> libRanks = new Dictionary<string, int>();
                        foreach (var lib in fwDef.AdditionalLibraries)
                        {
                            int match = -1;
                            for (int i = 0; i < regexes.Length; i++)
                                if (regexes[i].IsMatch(lib))
                                {
                                    if (match != -1)
                                        throw new Exception($"'{lib}' matches both '{regexes[match]}' and '{regexes[i]}' for {fw.ID}");
                                    match = i;
                                }

                            if (match < 0)
                                throw new Exception($"'{lib}' doesn't match any order regexes for {fw.ID}");

                            libRanks[lib] = match;
                        }

                        fwDef.AdditionalLibraries = fwDef.AdditionalLibraries.OrderBy(l => libRanks[l]).ToArray();
                    }

                    fwDef.AdditionalIncludeDirs = flags.IncludeDirectories;
                    fwDef.AdditionalPreprocessorMacros = flags.PreprocessorMacros;
                    fwDef.ConfigurableProperties = fw.ConfigurableProperties;
                    fwDef.AdditionalSystemVars = fw.AdditionalSystemVars;

                    if (fw.ConfigFiles != null)
                        fwDef.ConfigurationFileTemplates = fw.ConfigFiles.Select(cf => BSP.ParseConfigurationFile(cf, this, fwDef)).ToArray();

                    yield return fwDef;
                }
            }
        }

        public struct CopiedSample
        {
            public string RelativePath;
            public bool IsTestProjectSample;
        }

        protected class MissingSampleFileArgs
        {
            public string UnexpandedPath, ExpandedPath;

            public string SubstitutePath;   //Must work for ALL samples
        }

        int _SuppressMissingSampleErrorsUntilEndOfDebugSession;

        protected virtual void OnMissingSampleFile(MissingSampleFileArgs args)
        {
            if (_SuppressMissingSampleErrorsUntilEndOfDebugSession == 0)
            {
                //DO NOT REPLACE THIS WITH A WARNING! If needed, override this method in specific generators.
                throw new Exception($"Missing sample file: {args.ExpandedPath}. Please setup fallback lookup rules.");
            }
            else
                Console.WriteLine($"Missing sample file: {args.ExpandedPath}. Please setup fallback lookup rules.");
        }

        public IEnumerable<CopiedSample> CopySamples(IEnumerable<EmbeddedFramework> allFrameworks = null, IEnumerable<SysVarEntry> extraVariablesToValidateSamples = null)
        {
            if (Definition.SmartSamples != null)
            {
                foreach (var sample in Definition.SmartSamples)
                {
                    string destFolder = Path.Combine(BSP.BSPRoot, sample.DestinationFolder);
                    string sourceDir = sample.SourceFolder;
                    BSP.ExpandVariables(ref sourceDir);

                    if (sample.CopyFilters == null)
                        PathTools.CopyDirectoryRecursive(sourceDir, destFolder);
                    else
                    {
                        var filters = new CopyFilters(sample.CopyFilters);
                        foreach (var fn in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
                        {
                            string relPath = fn.Substring(sourceDir.Length).TrimStart('\\');
                            if (filters.IsMatch(relPath))
                            {
                                string targetPath = Path.Combine(destFolder, relPath);
                                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                                File.Copy(fn, targetPath);
                            }
                        }
                    }

                    if (sample.AdditionalBuildTimeSources != null)
                    {
                        foreach (var src in sample.AdditionalBuildTimeSources)
                        {
                            string source = BSP.ExpandVariables(src);
                            string targetPath = Path.Combine(destFolder, Path.GetFileName(source));
                            try
                            {
                                File.Copy(source, targetPath);
                            }
                            catch (Exception ex)
                            {
                                BSP.Report.ReportMergeableError("Failed to copy sample file", $"{source} => {targetPath}: {ex.Message}");
                            }
                        }
                    }

                    if (sample.Patches != null)
                        foreach (var p in sample.Patches)
                        {
                            foreach (var fn in p.FilePath.Split(';'))
                            {
                                string path = Path.Combine(destFolder, fn);
                                try
                                {
                                    List<string> allLines = File.ReadAllLines(path).ToList();
                                    p.Apply(allLines);
                                    File.WriteAllLines(Path.Combine(destFolder, fn), allLines);
                                }
                                catch (Exception ex)
                                {
                                    BSP.Report.ReportMergeableError("Failed to patch file", $"{path}: {ex.Message}");
                                }
                            }
                        }

                    var sampleObj = sample.EmbeddedSample ?? XmlTools.LoadObject<EmbeddedProjectSample>(Path.Combine(destFolder, "sample.xml"));
                    if (sampleObj.RequiredFrameworks == null && allFrameworks != null)
                        sampleObj.RequiredFrameworks = allFrameworks.Where(fw => fw.DefaultEnabled).Select(fw => fw.ClassID ?? fw.ID)?.ToArray();

                    if (sample.CommonConfiguration != null)
                    {
                        Dictionary<string, string> config = new Dictionary<string, string>();
                        HashSet<string> frameworks = new HashSet<string>();

                        CommonSampleConfiguration.Read(BSP.ExpandVariables(sample.CommonConfiguration), config, frameworks);

                        foreach (var fw in sampleObj.RequiredFrameworks ?? new string[0])
                            frameworks.Add(fw);

                        foreach (var kv in sampleObj.DefaultConfiguration?.Entries ?? new PropertyDictionary2.KeyValue[0])
                            config[kv.Key] = kv.Value;

                        sampleObj.DefaultConfiguration = new PropertyDictionary2(config);
                        sampleObj.RequiredFrameworks = frameworks.ToArray();
                    }

                    if (sample.AdditionalSources != null)
                    {
                        sampleObj.AdditionalSourcesToCopy = sample.AdditionalSources.Select(f =>
                            {
                                int idx = f.IndexOf("=>");
                                if (idx == -1)
                                    return new AdditionalSourceFile { SourcePath = f };
                                else
                                    return new AdditionalSourceFile { SourcePath = f.Substring(0, idx).Trim(), TargetFileName = f.Substring(idx + 2).Trim() };
                            }).ToArray();


                        for (int i = 0; i < sampleObj.AdditionalSourcesToCopy.Length; i++)
                        {
                            string path = sampleObj.AdditionalSourcesToCopy[i].SourcePath.Replace("$$SYS:BSP_ROOT$$", BSP.Directories.OutputDir);
                            if (!File.Exists(path))
                            {
                                if (extraVariablesToValidateSamples != null)
                                    foreach (var v in extraVariablesToValidateSamples)
                                        path = path.Replace("$$" + v.Key + "$$", v.Value);

                                if (!File.Exists(path))
                                {
                                    var args = new MissingSampleFileArgs { UnexpandedPath = sampleObj.AdditionalSourcesToCopy[i].SourcePath, ExpandedPath = path };
                                    OnMissingSampleFile(args);
                                    if (args.SubstitutePath != null)
                                    {
                                        if (args.SubstitutePath.StartsWith(BSP.BSPRoot))
                                            args.SubstitutePath = "$$SYS:BSP_ROOT$$" + args.SubstitutePath.Substring(BSP.BSPRoot.Length).Replace('\\', '/');
                                        else if (args.SubstitutePath.StartsWith("$$"))
                                        {
                                            //Already encoded
                                        }
                                        else
                                            throw new Exception("Invalid substitute path: " + args.SubstitutePath);

                                        sampleObj.AdditionalSourcesToCopy[i].SourcePath = args.SubstitutePath;
                                    }
                                }
                            }
                        }
                    }

                    if (sampleObj.MCUFilterRegex == null & allFrameworks != null && sampleObj.RequiredFrameworks != null)
                    {
                        string[] devices = null;

                        foreach (var fw in allFrameworks)
                        {
                            if (fw.MCUFilterRegex == null)
                                continue;

                            if (sampleObj.RequiredFrameworks.Contains(fw.ID) || sampleObj.RequiredFrameworks.Contains(fw.ClassID))
                            {
                                if (devices == null)
                                    devices = fw.MCUFilterRegex.Split('|');
                                else
                                    devices = devices.Intersect(fw.MCUFilterRegex.Split('|')).ToArray();
                            }
                        }

                        if (devices != null)
                            sampleObj.MCUFilterRegex = string.Join("|", devices);
                    }

                    if (sampleObj.MCUFilterRegex == null && sample.MCUFilterRegex != null)
                        sampleObj.MCUFilterRegex = sample.MCUFilterRegex;

                    XmlTools.SaveObject(sampleObj, Path.Combine(destFolder, "sample.xml"));
                    yield return new CopiedSample { RelativePath = sample.DestinationFolder, IsTestProjectSample = sample.IsTestProjectSample };
                }
            }
        }

        public static bool MaskToBitRange(ulong mask, out int firstBit, out int bitCount)
        {
            const int maxBits = 64;
            for (firstBit = 0; firstBit < maxBits; firstBit++)
                if ((mask & (1UL << firstBit)) != 0)
                    break;

            for (bitCount = 0; bitCount < (maxBits - firstBit); bitCount++)
                if ((mask & (1UL << (firstBit + bitCount))) == 0)
                    break;

            return firstBit < maxBits;
        }

        public void AttachPeripheralRegisters(IEnumerable<MCUDefinitionWithPredicate> registers, string deviceDefinitionFolder = "DeviceDefinitions", bool throwIfNotFound = true)
        {
            var allFiles = registers.ToArray();
            foreach (var mcu in MCUs)
            {
                bool matched = false;
                foreach (var f in allFiles)
                {
                    if (f.MatchPredicate == null || f.MatchPredicate(mcu) || allFiles.Length == 1)
                    {
                        mcu.MCUDefinitionFile = FamilyFilePrefix + deviceDefinitionFolder + "/" + f.MCUName + ".xml";
                        string outputFile = Path.Combine(BSP.BSPRoot, Definition.FamilySubdirectory ?? ".", deviceDefinitionFolder, f.MCUName + ".xml.gz");
                        Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
                        {
                            XmlSerializer ser = new XmlSerializer(typeof(MCUDefinition));
                            using (var fs = File.Create(outputFile))
                            using (var gs = new GZipStream(fs, CompressionMode.Compress, true))
                                ser.Serialize(gs, new MCUDefinition(f));
                        }

                        matched = true;
                        break;
                    }
                }

                if (!matched && throwIfNotFound)
                    throw new Exception("Cannot find a peripheral register set for " + mcu.Name);
            }

        }

        public virtual void AttachStartupFiles(IEnumerable<StartupFileGenerator.InterruptVectorTable> files, string startupFileFolder = "StartupFiles", string pFileNameTemplate = "StartupFileTemplate.c")
        {
            var allFiles = files.ToArray();
            foreach (var mcu in MCUs)
            {
                bool matched = false;
                foreach (var f in allFiles)
                {
                    if (f.MatchPredicate == null || f.MatchPredicate(mcu))
                    {
                        mcu.StartupFile = "$$SYS:BSP_ROOT$$/" + FamilyFilePrefix + startupFileFolder + "/" + f.FileName;
                        f.Save(Path.Combine(BSP.BSPRoot, Definition.FamilySubdirectory ?? ".", startupFileFolder, f.FileName), pFileNameTemplate);
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                    throw new Exception("Cannot find a startup file for " + mcu.Name);
            }
        }

        public MCUBuilder[] RemoveUnsupportedMCUs()
        {
            List<MCUBuilder> removedMCUs = new List<MCUBuilder>();
            foreach (var classifier in Definition.Subfamilies ?? new MCUClassifier[0])
            {
                if (!classifier.Required)
                    continue;

                var removed = MCUs.Where(m => classifier.TryMatchMCUName(m.Name) == null).ToArray();
                MCUs = MCUs.Where(m => classifier.TryMatchMCUName(m.Name) != null).ToList();
                var rgUnsupported = string.IsNullOrEmpty(classifier.UnsupportedMCUs) ? null : new Regex(classifier.UnsupportedMCUs);
                foreach (var mcu in removed)
                {
                    if (rgUnsupported?.IsMatch(mcu.Name) == true)
                        BSP.Report.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, $"Ignored unsupported MCU(s)", mcu.Name, true);
                    else
                        BSP.Report.ReportMergeableError("Unsupported MCU(s) found. Please update the MCUClassifier tags.", mcu.Name, true);
                }

                removedMCUs.AddRange(removed);
            }

            return removedMCUs.ToArray();
        }
    }

    public class MCUDefinitionWithPredicate : MCUDefinition
    {
        public Predicate<MCUBuilder> MatchPredicate;
    }

    public static class BSPGeneratorTools
    {
        public static CortexCore ParseCoreName(string core, out FPUType fpu)
        {
            fpu = FPUType.None;
            switch (core.Replace(" ", ""))
            {
                case "ARMCortex-M0":
                    return CortexCore.M0;
                case "ARMCortex-M0+":
                    return CortexCore.M0Plus;
                case "ARMCortex-M3":
                    return CortexCore.M3;
                case "ARMCortex-M4":
                    return CortexCore.M4;
                case "ARMCortex-M7":
                    fpu = FPUType.DP;
                    return CortexCore.M7;
                case "Cortex-M0":
                    return CortexCore.M0;
                case "Cortex-M0+":
                    return CortexCore.M0Plus;
                case "Cortex-M3":
                    return CortexCore.M3;
                case "Cortex-M3 ":
                    return CortexCore.M3;
                case "Cortex-M4":
                    return CortexCore.M4;
                case "Cortex-M4F": //FPU
                    fpu = FPUType.SP;
                    return CortexCore.M4;
                case "Cortex-M4F;M0"://MultiCore
                    fpu = FPUType.SP;
                    return CortexCore.M4;
                case "Cortex-M4F; Cortex-M0+"://MultiCore
                    fpu = FPUType.SP;
                    return CortexCore.M4;
                case "Cortex-M7":
                    fpu = FPUType.DP;
                    return CortexCore.M7;
                default:
                    return CortexCore.Invalid;
            }
        }

        public static List<MCUBuilder> ReadMCUDevicesFromCommaDelimitedCSVFile(string filePath, string nameColumn, string flashSizeColumn, string ramSizeColumn, string coreColumn, bool sizesAreInKilobytes)
        {
            List<MCUBuilder> rawmcu_list = new List<MCUBuilder>();

            bool header_row = true;
            Dictionary<string, int> headers = new Dictionary<string, int>();
            string[] strFileMCU = File.ReadAllLines(filePath);

            for (int il = 0; il < strFileMCU.Length; il++)
            {
                string line = strFileMCU[il];
                string[] items = line.Split(',');

                if (header_row)
                {
                    for (int i = 0; i < items.Length; i++)
                        headers[items[i]] = i;

                    header_row = false;
                    continue;
                }

                var mcu = new MCUBuilder
                {
                    Name = items[headers[nameColumn]],
                    FlashSize = Int32.Parse(items[headers[flashSizeColumn]]),
                    RAMSize = Int32.Parse(items[headers[ramSizeColumn]]),
                };

                if (coreColumn != null)
                    mcu.Core = ParseCoreName(items[headers[coreColumn]], out mcu.FPU);

                if (sizesAreInKilobytes)
                {
                    mcu.FlashSize *= 1024;
                    mcu.RAMSize *= 1024;
                }

                if (rawmcu_list.IndexOf(mcu) < 0)
                    rawmcu_list.Add(mcu);

            }
            rawmcu_list.Sort((a, b) => a.Name.CompareTo(b.Name));
            return rawmcu_list;
        }

        public static List<MCUBuilder> AssignMCUsToFamilies(IEnumerable<MCUBuilder> devices, List<MCUFamilyBuilder> allFamilies)
        {
            List<MCUBuilder> orphanedDevices = new List<MCUBuilder>();
            var families = (from f in allFamilies select new KeyValuePair<Regex, MCUFamilyBuilder>(new Regex(f.Definition.DeviceRegex), f)).ToArray();
            foreach (var mcu in devices)
            {
                bool found = false;
                foreach (var fam in families)
                    if (fam.Key.IsMatch(mcu.Name))
                    {
                        fam.Value.MCUs.Add(mcu);
                        found = true;
                        break;
                    }
                if (!found)
                    orphanedDevices.Add(mcu);
            }
            return orphanedDevices;
        }

        public static void MergeFamilies(MCUFamily updatedFamily, MCUFamily familyToCopy)
        {
            updatedFamily.GDBStartupCommands = LoadedBSP.Combine(familyToCopy.GDBStartupCommands, updatedFamily.GDBStartupCommands);
            updatedFamily.AdditionalSystemVars = LoadedBSP.Combine(familyToCopy.AdditionalSystemVars, updatedFamily.AdditionalSystemVars);

            if (updatedFamily.ConfigurableProperties == null)
                updatedFamily.ConfigurableProperties = familyToCopy.ConfigurableProperties;
            else if (familyToCopy.ConfigurableProperties != null)
            {
                var lst = new List<PropertyGroup>();
                lst.AddRange(familyToCopy.ConfigurableProperties.PropertyGroups);
                lst.AddRange(updatedFamily.ConfigurableProperties.PropertyGroups);
                updatedFamily.ConfigurableProperties.PropertyGroups = lst;
            }

            updatedFamily.CompilationFlags = familyToCopy.CompilationFlags.Merge(updatedFamily.CompilationFlags);
            updatedFamily.AdditionalSourceFiles = LoadedBSP.Combine(familyToCopy.AdditionalSourceFiles, updatedFamily.AdditionalSourceFiles);
            updatedFamily.AdditionalHeaderFiles = LoadedBSP.Combine(familyToCopy.AdditionalHeaderFiles, updatedFamily.AdditionalHeaderFiles);
            updatedFamily.AdditionalMakefileLines = LoadedBSP.Combine(familyToCopy.AdditionalMakefileLines, updatedFamily.AdditionalMakefileLines);
        }

        public static FPUType GetDefaultFPU(CortexCore core)
        {
            switch (core)
            {
                case CortexCore.M4:
                    return FPUType.SP;
                default:
                    return FPUType.None;
            }
        }
    }

    public static class Extensions
    {
        static MCUMemory MakeMCUMemory(Memory arg)
        {
            var mem = new MCUMemory
            {
                Address = arg.Start,
                Size = arg.Size,
                Name = arg.Name,
            };

            if (arg.Name == "FLASH" || arg.Name == "FLASH1")
                mem.Flags |= MCUMemoryFlags.IsDefaultFLASH;

            return mem;
        }

        public static AdvancedMemoryMap ToMemoryMap(this MemoryLayout layout)
        {
            var result = layout?.Memories?.Select(MakeMCUMemory)?.ToArray();
            if (result != null)
                return new AdvancedMemoryMap { Memories = result };
            else
                return null;
        }
    }

    public struct MemoryLayoutAndSubstitutionRules
    {
        public MemoryLayout Layout;
        public Dictionary<string, string> MemorySubstitutionsForRAMScript;

        public MemoryLayoutAndSubstitutionRules(MemoryLayout layout, Dictionary<string, string> memorySubstitutionRulesForRAMMode = null)
        {
            Layout = layout;
            MemorySubstitutionsForRAMScript = memorySubstitutionRulesForRAMMode;
        }
    }
}
