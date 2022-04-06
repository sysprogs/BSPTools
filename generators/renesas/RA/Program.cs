using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace renesas_ra_bsp_generator
{
    class Program
    {
        public struct ComponentID
        {
            public readonly string Vendor, Class, Group, Subgroup;

            public readonly string Variant;

            public string FrameworkID => $"com.renesas.arm.{Vendor}.{Class}.{Subgroup}".Replace(' ', '_');

            public ComponentID(XmlElement el, bool isReference = false)
            {
                string prefix = isReference ? "" : "C";

                Vendor = el.GetAttribute(prefix + "vendor");
                Class = el.GetAttribute(prefix + "class");
                Group = el.GetAttribute(prefix + "group");
                Subgroup = el.GetAttribute(isReference ? "subgroup" : "Csub");
                Variant = el.GetAttribute(prefix + "variant");
            }

            public override string ToString() => $"{Vendor}.{Class}.{Group}.{Subgroup}";
        }

        static string GetDirectoryName(string path)
        {
            int idx = path.LastIndexOfAny(new[] { '/', '\\' });
            if (idx == -1)
                throw new Exception($"{path} does not contain a '/' or '\\'");

            return path.Substring(0, idx);
        }

        static string RenamePath(string path, string newName) => GetDirectoryName(path) + "/" + newName;

        class PackDescriptionReader
        {
            private ZipFile _ZipFile;
            private XmlDocument _Xml;

            public readonly string ReleaseVersion;

            public abstract class ParsedCondition
            {
                public class Resolved : ParsedCondition
                {
                    public bool IsTrue;
                }

                public class CoreDependent : ParsedCondition
                {
                    public string Core;
                }
            }

            public readonly Dictionary<string, ParsedCondition> Conditions = new Dictionary<string, ParsedCondition>();

            public PackDescriptionReader(ZipFile zf)
            {
                _ZipFile = zf;

                var pdscFile = zf.Entries.Where(e => e.FileName.EndsWith(".pdsc") && !e.FileName.Contains("/")).Single();

                _Xml = new XmlDocument();
                _Xml.LoadXml(Encoding.UTF8.GetString(zf.ExtractEntry(pdscFile)));

                ReleaseVersion = _Xml.DocumentElement.SelectSingleNode("releases/release")?.InnerText;

                foreach (var cond in _Xml.DocumentElement.SelectElements("conditions/condition"))
                {
                    var id = cond.GetAttribute("id");
                    ParsedCondition parsedCond = null;

                    foreach (var req in cond.SelectElements("require"))
                    {
                        var core = req.GetAttribute("Dcore");
                        var compiler = req.GetAttribute("Tcompiler");

                        if (!string.IsNullOrEmpty(core))
                        {
                            if (parsedCond is ParsedCondition.Resolved rc && !rc.IsTrue)
                                continue;   //Already excluded by toolchain
                            else if (parsedCond is ParsedCondition.CoreDependent)
                                throw new Exception("Multiple core conditions");

                            //Either no condition, or unconditionally enabled
                            parsedCond = new ParsedCondition.CoreDependent { Core = core };
                        }
                        if (!string.IsNullOrEmpty(compiler))
                        {
                            if (compiler != "GCC")
                                parsedCond = new ParsedCondition.Resolved { IsTrue = false };   //Unconditionally exclude
                            else if (parsedCond is ParsedCondition.CoreDependent)
                            {
                                //Core-dependent and GCC.
                            }
                            else if (parsedCond == null)
                                parsedCond = new ParsedCondition.Resolved { IsTrue = true };   //Unconditionally include
                            else
                                throw new Exception("Unsupported combination of conditions");
                        }
                    }

                    Conditions[id] = parsedCond ?? throw new Exception("Failed to parse condition");
                }
            }

            static bool ShouldRenameFile(string pathInArchive, out string newNameOnly)
            {
                if (pathInArchive.Contains("r_sce_protected/crypto_procedures_protected") && pathInArchive.EndsWith("s_flash2.c"))
                {
                    //This avoids colision with another s_flash2.c file in the r_sce subdirectory
                    newNameOnly = "s_flash2_p.c";
                    return true;
                }

                newNameOnly = null;
                return false;
            }

            public class Component
            {
                private PackDescriptionReader _Reader;
                private XmlElement _Element;

                public readonly ComponentID ID;
                public readonly string Version;
                public Component(PackDescriptionReader packDescriptionReader, XmlElement c)
                {
                    _Reader = packDescriptionReader;
                    _Element = c;

                    ID = new ComponentID(_Element);
                    Version = _Element.GetStringAttribute("Cversion");
                }

                public string Description => _Element.SelectSingleNode("description")?.InnerXml;
                public string MCUVariant
                {
                    get
                    {
                        var v = _Element.GetAttribute("Cvariant");
                        if (v == "")
                            v = null;

                        return v;
                    }
                }

                public string ExpectedModuleDescriptionFileBase => $".module_descriptions/{ID.Vendor}##{ID.Class}##{ID.Group}##{ID.Subgroup}##{ID.Variant}##{Version}";
                public string ExpectedModuleDescriptionFile => ExpectedModuleDescriptionFileBase + ".xml";
                public string ExpectedModuleConfigurationFile => ExpectedModuleDescriptionFileBase + "##configuration.xml";

                public struct PathAndType
                {
                    public string Path;
                    public string Type;

                    public override string ToString() => $"[{Type}] {Path}";
                }

                public List<PathAndType> TranslateAndCopy(string baseOutputDir, BSPBuilder bspGen)
                {
                    //var outputDir = Path.Combine(baseOutputDir, subdir);
                    var outputDir = baseOutputDir;
                    Directory.CreateDirectory(outputDir);

                    List<PathAndType> translatedPaths = new List<PathAndType>();

                    HashSet<string> filesToExtract = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

                    foreach (var file in _Element.SelectElements("files/file"))
                    {
                        var name = file.GetAttribute("name");
                        if (string.IsNullOrEmpty(name))
                            continue;

                        if (_Reader.Conditions.TryGetValue(file.GetAttribute("condition") ?? "", out var cond) && cond is ParsedCondition.Resolved rc)
                        {
                            if (rc.IsTrue)
                                cond = null;
                            else
                                continue;   //Unconditionally skip
                        }

                        string mappedPath = $"$$SYS:BSP_ROOT$$/{name}".TrimEnd('/');
                        if (ShouldRenameFile(name, out var newName))
                            mappedPath = RenamePath(mappedPath, newName);

                        translatedPaths.Add(new PathAndType { Path = mappedPath, Type = file.GetAttribute("category") });
                        filesToExtract.Add(name);

                        if (cond is ParsedCondition.CoreDependent cd)
                        {
                            bspGen.AddFileCondition(new FileCondition
                            {
                                FilePath = mappedPath,
                                ConditionToInclude = new Condition.Equals
                                {
                                    Expression = MCUFamilyBuilder.ARMCoreVariableName,
                                    ExpectedValue = RenesasDeviceDatabase.ParseCortexCore(cd.Core).ToString().ToUpper()
                                }
                            });
                        }
                    }

                    foreach (var e in _Reader._ZipFile.Entries)
                    {
                        if (filesToExtract.Contains(e.FileName))
                        {
                            if (e.FileName.EndsWith("/"))
                                continue;

                            var targetPath = Path.GetFullPath(Path.Combine(outputDir, e.FileName));
                            if (ShouldRenameFile(e.FileName, out var newName))
                            {
                                int idx = targetPath.LastIndexOf('\\');
                                targetPath = targetPath.Substring(0, idx + 1) + newName;
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                            var data = _Reader._ZipFile.ExtractEntry(e);
                            if (!File.Exists(targetPath))
                                File.WriteAllBytes(targetPath, data);
                            else if (!Enumerable.SequenceEqual(File.ReadAllBytes(targetPath), data))
                                throw new Exception("Conflicting contents for " + targetPath);
                        }
                    }

                    return translatedPaths;
                }
            }

            public IEnumerable<Component> Components => _Xml.DocumentElement.SelectElements("components/component").OfType<XmlElement>().Select(c => new Component(this, c));

        }

        class RenesasBSPBuilder : BSPBuilder
        {
            public RenesasBSPBuilder(BSPDirectories dirs)
                : base(dirs, null, 5)
            {
                ShortName = "Renesas";
            }

            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                throw new NotImplementedException();
            }

            public override MemoryLayoutAndSubstitutionRules GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                throw new NotImplementedException();
            }

            public struct PackFileSummary
            {
                public string Version;
                public string LinkerScript;
            }

            public enum ComponentType
            {
                Common,
                Board,
                MCU,
                CMSIS,
            }

            HashSet<string> _BoardFrameworks = new HashSet<string>();
            HashSet<string> _FamilyPrefixesWithPrimaryBoards = new HashSet<string>();
            Dictionary<ComponentID, string> _TranslatedComponents = new Dictionary<ComponentID, string>();

            string _BoardPackageClassID, _MCUPackageClassID;
            ConfigurationFileTranslator _ConfigFileTranslator = new ConfigurationFileTranslator();

            public PackFileSummary TranslateComponents(string packFile, List<EmbeddedFramework> frameworkList, ComponentType type)
            {
                PackFileSummary summary = new PackFileSummary();
                Console.WriteLine($"Translating {packFile}...");

                PackDescriptionReader.Component.PathAndType[] deviceSpecificFiles = null;
                HashSet<ComponentID> usedIDs = new HashSet<ComponentID>();

                const string BSPComponentPrefix = "Board support package for ";

                using (var zf = ZipFile.Open(packFile))
                {
                    var rdr = new PackDescriptionReader(zf);
                    var filesByName = zf.Entries.ToDictionary(e => e.FileName, StringComparer.InvariantCultureIgnoreCase);

                    summary.Version = rdr.ReleaseVersion;

                    var fspDataPaths = rdr.Components.FirstOrDefault(IsFSPDataComponent)?.TranslateAndCopy(Directories.OutputDir, this);

                    foreach (var comp in rdr.Components)
                    {
                        if (usedIDs.Contains(comp.ID))
                        {
                            Report.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, "Duplicate component in " + Path.GetFileName(packFile), comp.ID.ToString(), false);
                            continue;
                        }

                        usedIDs.Add(comp.ID);

                        var translatedPaths = comp.TranslateAndCopy(Directories.OutputDir, this);
                        var description = comp.Description;

                        if (comp.MCUVariant != null)
                        {
                            //As of 3.3.0, all MCU-specific components are always the same and can be folded into the family-specific component.
                            if (type != ComponentType.MCU || !description.StartsWith(BSPComponentPrefix))
                                throw new Exception("MCU-specific component folding only supported for BSP components");

                            if (deviceSpecificFiles == null)
                                deviceSpecificFiles = translatedPaths.ToArray();
                            else if (!Enumerable.SequenceEqual(deviceSpecificFiles, translatedPaths))
                                throw new Exception("BSP components for different devices have different file lists. They should be translated to conditions or variable references.");

                            continue;
                        }

                        if (comp.MCUVariant == null && description.StartsWith(BSPComponentPrefix))
                        {
                            if (deviceSpecificFiles == null)
                                throw new Exception("MCU-specific component was not parsed at the time of parsing the family-specific one");

                            if (fspDataPaths == null)
                                throw new Exception("FSP data component was not parsed at the time of parsing the family-specific one");

                            //Just merge the MCU-specific files (that should be the same) into the family-specific file list
                            translatedPaths.AddRange(deviceSpecificFiles);
                        }

                        if (IsFSPDataComponent(comp))
                            continue;

                        if (description == "Renesas Bluetooth Low Energy Library")
                        {
                            //As of v3.3.0, multiple BLE components have the same description, causing conflicts in folder names.
                            //We fix it manually by renaming them based in the subgroup ID.
                            if (!comp.ID.Subgroup.StartsWith("r_ble_"))
                                throw new Exception("Unexpected subgroup for a BLE component");

                            description = "Renesas BLE - " + char.ToUpper(comp.ID.Subgroup[6]) + comp.ID.Subgroup.Substring(7);
                        }

                        EmbeddedFramework fw = new EmbeddedFramework
                        {
                            ID = comp.ID.FrameworkID,
                            ProjectFolderName = description,
                            UserFriendlyName = description,
                            AdditionalSourceFiles = translatedPaths.Where(p => p.Type == "source").Select(p => p.Path).Distinct().ToArray(),
                            AdditionalHeaderFiles = translatedPaths.Where(p => p.Type == "header").Select(p => p.Path).Distinct().ToArray(),
                            AdditionalIncludeDirs = translatedPaths.Where(p => p.Type == "include").Select(p => p.Path).Distinct().ToArray(),
                            AdditionalLibraries = translatedPaths.Where(p => p.Type == "library").Select(p => p.Path).Distinct().ToArray(),
                        };

                        if (FamilyPrefixes.TryGetValue(comp.ID.Group, out var prefix))
                        {
                            fw.MCUFilterRegex = prefix + ".*";
                            fw.ClassID = fw.ID;
                            VerifyMatch(ref _MCUPackageClassID, fw.ClassID);

                            fw.ID += "." + comp.ID.Group;
                        }
                        else if (type == ComponentType.Board)
                        {
                            int idx = comp.ID.Subgroup.IndexOf('_');
                            string family;
                            if (comp.ID.Subgroup == "custom")
                                family = comp.ID.Subgroup;
                            else
                            {
                                family = comp.ID.Subgroup.Substring(0, idx);
                                if (family == "ra6m3g")
                                    prefix = FamilyPrefixes[family.TrimEnd('g')];
                                else
                                    prefix = FamilyPrefixes[family];

                                fw.MCUFilterRegex = prefix + ".*";

                                if (!_FamilyPrefixesWithPrimaryBoards.Contains(prefix))
                                {
                                    //Mark this as the primary board
                                    _FamilyPrefixesWithPrimaryBoards.Add(prefix);
                                    fw.ClassID = fw.ID.Substring(0, fw.ID.LastIndexOf('.')) + ".default_board";
                                    VerifyMatch(ref _BoardPackageClassID, fw.ClassID);
                                }
                            }
                        }

                        if (type == ComponentType.Board)
                            _BoardFrameworks.Add(fw.ID);

                        var linkerScript = translatedPaths.Where(p => p.Type == "linkerScript").SingleOrDefault(p => p.Type == "linkerScript").Path;
                        if (comp.ID.Subgroup == "fsp_common")
                        {
                            summary.LinkerScript = linkerScript ?? throw new Exception("Missing linker script");
                            fw.DefaultEnabled = true;
                        }
                        else if (linkerScript != null)
                            throw new Exception("Linker script can only be defined by the core framework");

                        _TranslatedComponents[comp.ID] = fw.IDForReferenceList;

                        if (filesByName.TryGetValue(comp.ExpectedModuleDescriptionFile, out var moduleDesc))
                            _ConfigFileTranslator.TranslateConfigurationFiles(fw, zf.ExtractXMLFile(moduleDesc), Report);

                        if (filesByName.TryGetValue(comp.ExpectedModuleConfigurationFile, out var moduleConf))
                            _ConfigFileTranslator.TranslateModuleConfiguration(fw, zf.ExtractXMLFile(moduleConf), Report);

                        frameworkList.Add(fw);
                    }
                }

                return summary;
            }

            private void VerifyMatch(ref string storedID, string ID)
            {
                if (storedID == null)
                    storedID = ID;

                if (storedID != ID)
                    throw new Exception($"Mismatching ID: {ID}, expected {storedID}");
            }

            static bool IsFSPDataComponent(PackDescriptionReader.Component component)
            {
                return component.Description.EndsWith("- FSP Data");
            }

            static int CountMatchingCharacters(string left, string right)
            {
                int i;
                for (i = 0; i < Math.Min(left.Length, right.Length); i++)
                    if (left[i] != right[i])
                        return i;
                return i;
            }

            Dictionary<string, string> FamilyPrefixes = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

            public void ComputeFamilyRegexes(RenesasDeviceDatabase.ParsedDevice[] devs)
            {
                foreach (var fam in devs.GroupBy(d => d.FamilyName))
                {
                    string prefix = null;
                    foreach (var dev in fam)
                    {
                        if (prefix == null)
                            prefix = dev.FinalMCUName;
                        else
                            prefix = prefix.Substring(0, CountMatchingCharacters(prefix, dev.FinalMCUName));
                    }

                    FamilyPrefixes[fam.Key] = prefix;
                }

                foreach (var dev in devs)
                {
                    foreach (var kv in FamilyPrefixes)
                    {
                        bool prefixMatch = dev.FinalMCUName.StartsWith(kv.Value);
                        bool isThisFamily = dev.FamilyName == kv.Key;
                        if (prefixMatch != isThisFamily)
                            throw new Exception("Computed family prefixes are either to narrow, or too broad");
                    }
                }
            }

            public void MakeBoardFrameworksMutuallyExclusive(List<EmbeddedFramework> frameworks)
            {
                foreach (var fw in frameworks)
                {
                    if (!_BoardFrameworks.Contains(fw.ID))
                        continue;

                    fw.IncompatibleFrameworks = _BoardFrameworks.Except(new[] { fw.ID }).ToArray();
                }
            }

            public void MakeOverlappingFrameworksMutuallyExclusive(List<EmbeddedFramework> frameworks)
            {
                Dictionary<string, List<EmbeddedFramework>> frameworksByFile = new Dictionary<string, List<EmbeddedFramework>>();
                foreach (var fw in frameworks)
                {
                    foreach (var src in fw.AdditionalSourceFiles)
                    {
                        if (!frameworksByFile.TryGetValue(src, out var lst))
                            frameworksByFile[src] = lst = new List<EmbeddedFramework>();

                        lst.Add(fw);
                    }
                }

                foreach (var kv in frameworksByFile)
                {
                    if (kv.Value.Count > 1)
                    {
                        foreach (var fw in kv.Value)
                        {
                            var incompatibleFrameworks = kv.Value.Select(f2 => f2.ID).Except(new[] { fw.ID }).ToArray();

                            if (fw.IncompatibleFrameworks != null && !Enumerable.SequenceEqual(incompatibleFrameworks, fw.IncompatibleFrameworks))
                                throw new Exception($"{fw.UserFriendlyName} already has an incompatible framework list");

                            fw.IncompatibleFrameworks = incompatibleFrameworks;
                        }
                    }
                }
            }

            public string[] TranslateExamples(string mainPackFile)
            {
                Console.WriteLine("Translating sample projects...");
                List<string> result = new List<string>();

                using (var zf = ZipFile.Open(mainPackFile))
                {
                    foreach (var e in zf.Entries)
                    {
                        if (e.FileName.StartsWith(".templates", StringComparison.CurrentCultureIgnoreCase) && e.FileName.EndsWith("/configuration.xml", StringComparison.InvariantCultureIgnoreCase))
                        {
                            var sampleName = Path.GetFileName(Path.GetDirectoryName(e.FileName));
                            var xml = new XmlDocument();
                            xml.LoadXml(Encoding.UTF8.GetString(zf.ExtractEntry(e)));

                            if (sampleName != "baremetal_blinky")
                                continue;

                            var refs = xml.DocumentElement.SelectElements("raComponentSelection/component")
                                .Select(c => new ComponentID(c, true))
                                .Where(c => c.Class != "Projects")
                                .Select(c => _TranslatedComponents[c])
                                .Concat(new[] {
                                    _BoardPackageClassID ?? throw new Exception("Could not locate the primary board package"),
                                    _MCUPackageClassID ?? throw new Exception("Could not locate the primary MCU package"),
                                })
                                .ToArray();

                            //Extract sample files, if any
                            var targetDir = Path.Combine(Directories.OutputDir, "samples", sampleName);
                            result.Add("samples/" + sampleName);
                            string sampleDir = GetDirectoryName(e.FileName);
                            Directory.CreateDirectory(targetDir);
                            foreach (var e2 in zf.Entries)
                            {
                                if (e2.FileName.StartsWith(sampleDir + "/src") && !e2.IsDirectory)
                                {
                                    var targetPath = Path.Combine(targetDir, e2.FileName.Substring(sampleDir.Length + 5));
                                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                                    File.WriteAllBytes(targetPath, zf.ExtractEntry(e2));
                                }
                            }

                            var sample = new EmbeddedProjectSample
                            {
                                Name = sampleName,
                                Description = xml.DocumentElement.SelectSingleNode("raComponentSelection/component[@class='Projects']/description")?.InnerText,
                                RequiredFrameworks = refs,
                            };

                            XmlTools.SaveObject(sample, Path.Combine(targetDir, "sample.xml"));
                        }
                    }
                }

                return result.ToArray();
            }

            bool _CommonLinkerScriptPatched;
            public string GenerateLinkerScript(RenesasDeviceDatabase.ParsedDevice mcu, string commonLinkerScript)
            {
                if (!_CommonLinkerScriptPatched)
                {
                    var expandedCommonLinkerScript = commonLinkerScript.Replace("$$SYS:BSP_ROOT$$", Directories.OutputDir);
                    var lines = File.ReadAllLines(expandedCommonLinkerScript);
                    bool found = false;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i] == "INCLUDE memory_regions.ld")
                        {
                            lines[i] = "/* " + lines[i] + " */";
                            found = true;
                        }
                    }

                    if (!found)
                        throw new Exception("Could not patch the common linker script");

                    File.WriteAllLines(expandedCommonLinkerScript, lines);
                    _CommonLinkerScriptPatched = true;
                }

                var deviceScriptPath = RenamePath(commonLinkerScript, mcu.Name + ".ld");
                var expandedDeviceScriptPath = deviceScriptPath.Replace("$$SYS:BSP_ROOT$$", Directories.OutputDir);

                File.WriteAllText(expandedDeviceScriptPath, mcu.UniqueMemoryRegionsDefinition + $"\r\nINCLUDE \"{Path.GetFileName(commonLinkerScript)}\"");

                return deviceScriptPath;
            }
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: renesas_ra_bsp_generator.exe <Renesas Packs directory>");

            using (var bspGen = new RenesasBSPBuilder(BSPDirectories.MakeDefault(args)))
            {
                var devs = RenesasDeviceDatabase.DiscoverDevices(bspGen.Directories.InputDir);

                bspGen.ComputeFamilyRegexes(devs);

                List<MCUFamily> familyDefinitions = new List<MCUFamily>();
                List<MCU> mcuDefinitions = new List<MCU>();
                List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();

                var mainPackFile = Directory.GetFiles(bspGen.Directories.InputDir, "Renesas.RA.*.pack").Single();
                var summary = bspGen.TranslateComponents(mainPackFile, frameworks, RenesasBSPBuilder.ComponentType.Common);

                foreach (var fn in Directory.GetFiles(bspGen.Directories.InputDir, "*.pack"))
                {
                    Regex rgMCU = new Regex("Renesas.RA_mcu_([^.]+)\\.", RegexOptions.IgnoreCase);
                    Regex rgBoard = new Regex("Renesas.RA_board_([^.]+)\\.", RegexOptions.IgnoreCase);
                    Match m;

                    var nameOnly = Path.GetFileName(fn);
                    if ((m = rgMCU.Match(nameOnly)).Success)
                        bspGen.TranslateComponents(fn, frameworks, RenesasBSPBuilder.ComponentType.MCU);
                    else if ((m = rgBoard.Match(nameOnly)).Success)
                        bspGen.TranslateComponents(fn, frameworks, RenesasBSPBuilder.ComponentType.Board);
                    else if (nameOnly.StartsWith("Arm.CMSIS", StringComparison.InvariantCultureIgnoreCase))
                        bspGen.TranslateComponents(fn, frameworks, RenesasBSPBuilder.ComponentType.CMSIS);
                }

                bspGen.MakeBoardFrameworksMutuallyExclusive(frameworks);
                bspGen.MakeOverlappingFrameworksMutuallyExclusive(frameworks);

                foreach (var fam in devs.GroupBy(d => d.FamilyName))
                {
                    var famBuilder = new MCUFamilyBuilder(bspGen, new FamilyDefinition
                    {
                        Name = fam.Key,
                        CompilationFlags = new ToolFlags
                        {
                            IncludeDirectories = new[] {
                                "$$SYS:BSP_ROOT$$", //Used in generated configuration files via '#include <ra/...>'
                            },
                            PreprocessorMacros = new[]
                            {
                                "_RENESAS_RA_",
                                "_RA_CORE=C$$com.sysprogs.bspoptions.arm.core$$",
                            },
                            AdditionalLibraryDirectories = new[]
                            {
                                GetDirectoryName(summary.LinkerScript), //So that the device-specific scripts can include the common script
                            }
                        }
                    });

                    foreach (var mcu in fam)
                    {
                        famBuilder.MCUs.Add(new MCUBuilder { Name = mcu.Name, Core = mcu.Core });
                        mcuDefinitions.Add(new MCU
                        {
                            ID = mcu.FinalMCUName,
                            FamilyID = fam.Key,
                            HierarchicalPath = $@"Renesas\ARM\{fam.Key}",
                            RAMSize = (int)mcu.MemoryMap.First(m => m.Type == "InternalRam").Size,
                            RAMBase = (uint)mcu.MemoryMap.First(m => m.Type == "InternalRam").Start,
                            FLASHSize = (int)mcu.MemoryMap.First(m => m.Type == "InternalRom").Size,
                            FLASHBase = (uint)mcu.MemoryMap.First(m => m.Type == "InternalRom").Start,
                            CompilationFlags = new ToolFlags
                            {
                                LinkerScript = bspGen.GenerateLinkerScript(mcu, summary.LinkerScript)
                            }
                        });

                        if (mcu.MemoryMap.Length != 2)
                            throw new Exception("Unexpected number of memories. Generate a proper memory map!");
                    }

                    var famObj = famBuilder.GenerateFamilyObject(MCUFamilyBuilder.CoreSpecificFlags.All);

                    familyDefinitions.Add(famObj);
                }

                frameworks.Sort((a, b) => StringComparer.InvariantCultureIgnoreCase.Compare(a.UserFriendlyName, b.UserFriendlyName));
                Console.WriteLine("Building BSP archive...");

                BoardSupportPackage bsp = new BoardSupportPackage
                {
                    PackageID = "com.sysprogs.arm.renesas",
                    PackageDescription = "Renesas ARM Devices",
                    GNUTargetID = "arm-eabi",
                    GeneratedMakFileName = "ra.mak",
                    MCUFamilies = familyDefinitions.ToArray(),
                    SupportedMCUs = mcuDefinitions.ToArray(),
                    Frameworks = frameworks.ToArray(),
                    Examples = bspGen.TranslateExamples(mainPackFile),
                    PackageVersion = summary.Version,
                    FileConditions = bspGen.MatchedFileConditions.Values.ToArray(),
                    MinimumEngineVersion = "5.6.105",
                };

                bspGen.ValidateBSP(bsp);
                bspGen.Save(bsp, false, false);
            }


        }
    }
}
