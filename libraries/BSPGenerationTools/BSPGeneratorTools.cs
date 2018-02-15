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

namespace BSPGenerationTools
{
    public enum CortexCore
    {
        Invalid,
        M0,
        M0Plus,
        M3,
        M4,
        M4_NOFPU,
        M7,
        R5F,
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

        public string LinkerScriptPath;
        public string StartupFile;
        public string MCUDefinitionFile;
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

        public MCU GenerateDefinition(MCUFamilyBuilder fam, BSPBuilder bspBuilder, bool requirePeripheralRegisters, bool allowIncompleteDefinition = false, MCUFamilyBuilder.CoreSpecificFlags flagsToAdd = MCUFamilyBuilder.CoreSpecificFlags.All)
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
                MCUDefinitionFile = MCUDefinitionFile
            };

            if (fam.Definition.HasMixedCores)
                MCUFamilyBuilder.AddCoreSpecificFlags(flagsToAdd, mcu, Core);

            List<SysVarEntry> sysVars = new List<SysVarEntry>();
            foreach (var classifier in fam.Definition.Subfamilies)
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

            bspBuilder.GetMemoryBases(out mcu.FLASHBase, out mcu.RAMBase);
            return mcu;
        }
    }

    public struct BSPDirectories
    {
        public readonly string InputDir, OutputDir, RulesDir;

        public BSPDirectories(string inputDir, string outputDir, string rulesDir)
        {
            InputDir = inputDir;
            OutputDir = outputDir;
            RulesDir = rulesDir;
        }
    }

    public abstract class BSPBuilder
    {
        public LinkerScriptTemplate LDSTemplate;
        public readonly string BSPRoot;
        public string ShortName;
        public readonly Dictionary<string, string> SystemVars = new Dictionary<string, string>();

        public List<FileCondition> MatchedFileConditions = new List<FileCondition>();
        public Dictionary<string, string> RenamedFileTable = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        public readonly BSPDirectories Directories;

        public BSPBuilder(BSPDirectories dirs, string linkerScriptTemplate = null, int linkerScriptLevel = 4)
        {
            if (linkerScriptTemplate == null)
            {
                for (int i = 0; i < linkerScriptLevel; i++)
                    linkerScriptTemplate += @"..\";
                linkerScriptTemplate += @"GenericARM.ldsx";
            }

            Directories = dirs;
            SystemVars["$$BSPGEN:INPUT_DIR$$"] = dirs.InputDir;
            SystemVars["$$BSPGEN:RULES_DIR$$"] = dirs.RulesDir;
            if (linkerScriptTemplate != null && linkerScriptLevel >= 0)
                LDSTemplate = XmlTools.LoadObject<LinkerScriptTemplate>(linkerScriptTemplate);
            BSPRoot = dirs.OutputDir;
            if (Directory.Exists(dirs.OutputDir))
            {
                Console.Write("Deleting {0}...", dirs.OutputDir);
                try
                {
                    Directory.Delete(dirs.OutputDir, true);
                }
                catch
                {
                    if (Directory.GetFiles(dirs.OutputDir, "*", SearchOption.AllDirectories).Length != 0)
                        throw;
                }
                Console.WriteLine(" done");
            }
            Directory.CreateDirectory(dirs.OutputDir);
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

            XmlTools.SaveObject(bsp, Path.Combine(BSPRoot, "BSP.XML"));

            string archiveName = string.Format("{0}-{1}.vgdbxbsp", bsp.PackageID.Split('.').Last(), bsp.PackageVersion);

            if (produceBSPArchive)
                TarPacker.PackDirectoryToTGZ(BSPRoot, Path.Combine(BSPRoot, archiveName), fn => Path.GetExtension(fn).ToLower() != ".vgdbxbsp");

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

            XmlTools.SaveObject(lst, Path.Combine(BSPRoot, Path.ChangeExtension(archiveName, ".xml")));
        }

        public abstract MemoryLayout GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family);

        public abstract void GetMemoryBases(out uint flashBase, out uint ramBase);

        protected virtual LinkerScriptTemplate GetTemplateForMCU(MCUBuilder mcu)
        {
            return LDSTemplate;
        }

        public virtual void GenerateLinkerScriptsAndUpdateMCU(string ldsDirectory, string familyFilePrefix, MCUBuilder mcu, MemoryLayout layout, string generalizedName)
        {
            using (var gen = new LdsFileGenerator(GetTemplateForMCU(mcu), layout))
            {
                using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_flash.lds")))
                    gen.GenerateLdsFile(sw);
                gen.RedirectMainFLASHToRAM = true;
                using (var sw = new StreamWriter(Path.Combine(ldsDirectory, generalizedName + "_sram.lds")))
                    gen.GenerateLdsFile(sw);

                mcu.LinkerScriptPath = string.Format("$$SYS:BSP_ROOT$$/{0}LinkerScripts/{1}_$${2}$$.lds", familyFilePrefix, generalizedName, MCUFamilyBuilder.PrimaryMemoryOptionName);
            }
        }

        public virtual bool OnFilePathTooLong(string pathInsidePackage)
        {
            throw new Exception("File path too long: " + pathInsidePackage);
        }

        internal void ExpandVariables(ref string primaryHeaderDir)
        {
            if (primaryHeaderDir != null && primaryHeaderDir.Contains("$$"))
                foreach (var entry in SystemVars)
                    primaryHeaderDir = primaryHeaderDir.Replace(entry.Key, entry.Value);
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
        public readonly FamilyDefinition Definition;

        public MCUFamily GenerateFamilyObject(bool defineConfigurationVariables) => GenerateFamilyObject(defineConfigurationVariables ? CoreSpecificFlags.All : CoreSpecificFlags.None);

        public MCUFamily GenerateFamilyObject(CoreSpecificFlags flagsToGenerate)
        {
            var family = new MCUFamily { ID = Definition.Name };

            if (!Definition.HasMixedCores)
            {
                if (MCUs.Count == 0)
                    throw new Exception("No MCUs found for " + Definition.Name);

                var core = MCUs[0].Core;

                foreach (var mcu in MCUs)
                    if (mcu.Core != core)
                        throw new Exception("Different MCUs within " + Definition.Name + " have different core types");

                AddCoreSpecificFlags(flagsToGenerate, family, core);
            }

            family.CompilationFlags = family.CompilationFlags.Merge(Definition.CompilationFlags);

            List<string> projectFiles = new List<string>();
            CopyFamilyFiles(ref family.CompilationFlags, projectFiles);

            family.AdditionalSourceFiles = projectFiles.Where(f => !IsHeaderFile(f)).ToArray();
            family.AdditionalHeaderFiles = projectFiles.Where(f => IsHeaderFile(f)).ToArray();

            family.AdditionalSystemVars = LoadedBSP.Combine(family.AdditionalSystemVars, Definition.AdditionalSystemVars);

            if (Definition.ConfigurableProperties != null)
            {
                if (family.ConfigurableProperties == null)
                    family.ConfigurableProperties = new PropertyList();

                family.ConfigurableProperties.Import(Definition.ConfigurableProperties);
            }

            return family;
        }

        [Flags]
        public enum CoreSpecificFlags
        {
            None = 0,
            FPU = 0x01,
            PrimaryMemory = 0x02,
            All = FPU | PrimaryMemory
        }

        internal static void AddCoreSpecificFlags(CoreSpecificFlags flagsToDefine, MCUFamily family, CortexCore core)
        {
            string coreName = null;
            switch (core)
            {
                case CortexCore.M0:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m0 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM0" };
                    coreName = "M0";
                    break;
                case CortexCore.M0Plus:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m0plus -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM0PLUS" };
                    coreName = "M0";
                    break;
                case CortexCore.M3:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m3 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM3" };
                    coreName = "M3";
                    break;
                case CortexCore.M4:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m4 -mthumb -mfpu=fpv4-sp-d16";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM4" };
                    coreName = "M4";
                    break;
                case CortexCore.M4_NOFPU:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m4 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM4" };
                    coreName = "M4";
                    break;
                case CortexCore.M7:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m7 -mthumb -mfpu=fpv4-sp-d16";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM7" };
                    coreName = "M7";
                    break;
                case CortexCore.R5F:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-r5 -mfpu=vfpv3-d16";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CR5" };
                    break;
                default:
                    throw new Exception("Unsupported core type");
            }


            if ((flagsToDefine & CoreSpecificFlags.PrimaryMemory) == CoreSpecificFlags.PrimaryMemory)
            {
                if (core == CortexCore.M0)
                    family.AdditionalSystemVars = new SysVarEntry[] { new SysVarEntry { Key = PrimaryMemoryOptionName, Value = "flash" } };
                else
                {
                    family.ConfigurableProperties = new PropertyList
                    {
                        PropertyGroups = new List<PropertyGroup>
                            {
                                new PropertyGroup
                                {
                                    Properties = new List<PropertyEntry>
                                    {
                                        new PropertyEntry.Enumerated
                                        {
                                            Name = "Execute from",
                                            UniqueID = PrimaryMemoryOptionName,
                                            SuggestionList = new PropertyEntry.Enumerated.Suggestion[]
                                            {
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "flash", UserFriendlyName = "FLASH"},
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "sram", UserFriendlyName = "SRAM"},
                                            }
                                        }
                                    }
                                }
                            }
                    };
                }
            }

            if ((flagsToDefine & CoreSpecificFlags.FPU) == CoreSpecificFlags.FPU)
            {
                if (core == CortexCore.M4 || core == CortexCore.M7 || core == CortexCore.R5F)
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
                                        }
                        });

                    family.CompilationFlags.COMMONFLAGS += " $$com.sysprogs.bspoptions.arm.floatmode$$";
                }
            }

            if (coreName != null)
                    family.AdditionalSystemVars = LoadedBSP.Combine(family.AdditionalSystemVars, new SysVarEntry[] { new SysVarEntry { Key = "com.sysprogs.bspoptions.arm.core", Value = coreName } });
        }

        public static bool IsHeaderFile(string fn)
        {
            string ext = Path.GetExtension(fn).ToLower();
            return ext == ".h" || ext == ".hpp";
        }

        public void CopyFamilyFiles(ref ToolFlags flags, List<string> projectFiles)
        {
            if (Definition.CoreFramework != null)
                foreach (var job in Definition.CoreFramework.CopyJobs)
                    flags = flags.Merge(job.CopyAndBuildFlags(BSP, projectFiles, Definition.FamilySubdirectory, ref Definition.CoreFramework.ConfigurableProperties));
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

        public void GenerateLinkerScripts(bool generalizeWherePossible)
        {
            string ldsDirectory = Path.Combine(BSP.BSPRoot, Definition.FamilySubdirectory, "LinkerScripts");
            Directory.CreateDirectory(ldsDirectory);

            Dictionary<string, bool> generalizationResults = new Dictionary<string, bool>();
            Dictionary<string, MemoryLayout> memoryLayouts = new Dictionary<string, MemoryLayout>();

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
                                    if (!Enumerable.SequenceEqual(kv.Value.Memories, layout.Memories, comparer))
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

                layout.DeviceName = generalizedName;

                BSP.GenerateLinkerScriptsAndUpdateMCU(ldsDirectory, FamilyFilePrefix, mcu, layout, generalizedName);
            }
        }


        public IEnumerable<EmbeddedFramework> GenerateFrameworkDefinitions()
        {
            if (Definition.AdditionalFrameworks != null)
            {
                IEnumerable<Framework> allFrameworks = Definition.AdditionalFrameworks;
                if (Definition.AdditionalFrameworkTemplates != null)
                    foreach (var t in Definition.AdditionalFrameworkTemplates)
                        allFrameworks = allFrameworks.Concat(t.Expand());

                foreach (var fw in allFrameworks)
                {
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
                        ConfigurationFileTemplates = fw.ConfigurationFileTemplates,
                        AdditionalForcedIncludes = fw.AdditionalForcedIncludes?.Split(';'),
                    };

                    if (fw.Filter != null)
                        fwDef.MCUFilterRegex = fw.Filter;
                    else if (Definition.DeviceRegex != null)
                        fwDef.MCUFilterRegex = Definition.DeviceRegex;

                    ToolFlags flags = new ToolFlags();
                    foreach (var job in fw.CopyJobs)
                        flags = flags.Merge(job.CopyAndBuildFlags(BSP, projectFiles, Definition.FamilySubdirectory, ref fw.ConfigurableProperties));

                    fwDef.AdditionalSourceFiles = projectFiles.Where(f => !IsHeaderFile(f)).ToArray();
                    fwDef.AdditionalHeaderFiles = projectFiles.Where(f => IsHeaderFile(f)).ToArray();

                    fwDef.AdditionalIncludeDirs = flags.IncludeDirectories;
                    fwDef.AdditionalPreprocessorMacros = flags.PreprocessorMacros;
                    fwDef.ConfigurableProperties = fw.ConfigurableProperties;
                    fwDef.AdditionalSystemVars = fw.AdditionalSystemVars;

                    yield return fwDef;
                }
            }
        }

        public struct CopiedSample
        {
            public string RelativePath;
            public bool IsTestProjectSample;
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
                        foreach(var fn in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
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

                    var sampleObj = sample.EmbeddedSample ?? XmlTools.LoadObject<EmbeddedProjectSample>(Path.Combine(destFolder, "sample.xml"));
                    if (sampleObj.RequiredFrameworks == null && allFrameworks != null)
                        sampleObj.RequiredFrameworks = allFrameworks.Where(fw => fw.DefaultEnabled).Select(fw => fw.ClassID ?? fw.ID)?.ToArray();

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

                        foreach (var f in sampleObj.AdditionalSourcesToCopy)
                        {
                            string path = f.SourcePath.Replace("$$SYS:BSP_ROOT$$", BSP.Directories.OutputDir);
                            if (!File.Exists(path))
                            {
                                if (extraVariablesToValidateSamples != null)
                                    foreach (var v in extraVariablesToValidateSamples)
                                        path = path.Replace("$$" + v.Key + "$$", v.Value);

                                if (!File.Exists(path))
                                    Console.WriteLine("Missing sample file: " + f.SourcePath);
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

        public void AttachPeripheralRegisters(IEnumerable<MCUDefinitionWithPredicate> registers, string deviceDefinitionFolder = "DeviceDefinitions")
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

                if (!matched)
                    throw new Exception("Cannot find a peripheral register set for " + mcu.Name);
            }

        }

        public void AttachStartupFiles(IEnumerable<StartupFileGenerator.InterruptVectorTable> files, string startupFileFolder = "StartupFiles", string pFileNameTemplate = "StartupFileTemplate.c")
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
                        f.Save(Path.Combine(BSP.BSPRoot, Definition.FamilySubdirectory, startupFileFolder, f.FileName), pFileNameTemplate);
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                    throw new Exception("Cannot find a startup file for " + mcu.Name);
            }
        }

        public MCUBuilder[] RemoveUnsupportedMCUs(bool throwIfUnexpected)
        {
            List<MCUBuilder> removedMCUs = new List<MCUBuilder>();
            foreach (var classifier in Definition.Subfamilies)
            {
                if (!classifier.Required)
                    continue;

                var removed = MCUs.Where(m => classifier.TryMatchMCUName(m.Name) == null).ToArray();
                MCUs = MCUs.Where(m => classifier.TryMatchMCUName(m.Name) != null).ToList();
                if (throwIfUnexpected)
                {
                    var rgUnsupported = string.IsNullOrEmpty(classifier.UnsupportedMCUs) ? null : new Regex(classifier.UnsupportedMCUs);
                    foreach (var mcu in removed)
                        if (rgUnsupported == null || !rgUnsupported.IsMatch(mcu.Name))
                            throw new Exception(mcu.Name + " is not marked as unsupported, but cannot be categorized " + mcu.Name);
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
        static CortexCore ParseCoreName(string core)
        {
            switch (core.Replace(" ",""))
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
                    return CortexCore.M4;
                case "Cortex-M4F;M0"://MultiCore
                    return CortexCore.M4;
                case "Cortex-M4F; Cortex-M0+"://MultiCore
                    return CortexCore.M4;
                case "Cortex-M7":
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
                    mcu.Core = ParseCoreName(items[headers[coreColumn]]);

                if (sizesAreInKilobytes)
                {
                    mcu.FlashSize *= 1024;
                    mcu.RAMSize *= 1024;
                }

                if (rawmcu_list.IndexOf(mcu) < 0)
                    rawmcu_list.Add(mcu);

            }
            rawmcu_list.Sort((a,b)=> a.Name.CompareTo(b.Name));
            return rawmcu_list;
        }

        public static List<MCUBuilder> AssignMCUsToFamilies(List<MCUBuilder> devices, List<MCUFamilyBuilder> allFamilies)
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
    }
}
