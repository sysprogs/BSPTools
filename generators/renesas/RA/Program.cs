using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

            public string SimplifiedID => $"com.renesas.arm.{Vendor}.{Class}.{Subgroup}".Replace(' ', '_');
            public string FullID => $"com.renesas.arm.{Vendor}.{Class}.{Group}.{Subgroup}".Replace(' ', '_');
            public string FrameworkID => Group == "all" ? SimplifiedID : FullID;

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
                            {
                                var oldContents = File.ReadAllText(targetPath).Replace("\r", "");
                                var newContents = Encoding.UTF8.GetString(data).Replace("\r", "");
                                if (AreFilesIncompatible(oldContents, newContents))
                                    throw new Exception("Conflicting contents for " + targetPath);
                            }
                        }
                    }

                    return translatedPaths;
                }

                static bool AreFilesIncompatible(string oldContents, string newContents)
                {
                    if (oldContents == newContents)
                        return false;

                    string longVersion, shortVersion;
                    if (oldContents.Length > newContents.Length)
                    {
                        longVersion = oldContents;
                        shortVersion = newContents;
                    }
                    else
                    {
                        longVersion = newContents;
                        shortVersion = oldContents;
                    }

                    const string placeholder = "/* ${REA_DISCLAIMER_PLACEHOLDER} */\n";
                    if (shortVersion.StartsWith(placeholder))
                    {
                        shortVersion = shortVersion.Substring(placeholder.Length);
                        if (longVersion.EndsWith(shortVersion))
                        {
                            //One of the files has the disclaimer placeholder expanded
                            return false;
                        }

                    }

                    return true;
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
                Other,
            }

            HashSet<string> _BoardFrameworks = new HashSet<string>();
            HashSet<string> _FamilyPrefixesWithPrimaryBoards = new HashSet<string>();
            Dictionary<ComponentID, string> _TranslatedComponents = new Dictionary<ComponentID, string>();

            string _BoardPackageClassID, _FamilyPackageClassID, _MCUPackageClassID, _FSPClassID;
            ConfigurationFileTranslator _ConfigFileTranslator = new ConfigurationFileTranslator();

            public PackFileSummary TranslateComponents(string packFile, List<EmbeddedFramework> frameworkList, ComponentType type)
            {
                PackFileSummary summary = new PackFileSummary();
                Console.WriteLine($"Translating {packFile}...");

                HashSet<ComponentID> usedIDs = new HashSet<ComponentID>();

                using (var zf = ZipFile.Open(packFile))
                {
                    var rdr = new PackDescriptionReader(zf);
                    var filesByName = zf.Entries.ToDictionary(e => e.FileName, StringComparer.InvariantCultureIgnoreCase);

                    summary.Version = rdr.ReleaseVersion;

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


                        if (comp.ID.Variant == "wrapper" && comp.ID.FrameworkID == "com.renesas.arm.AWS.Abstractions.Platform.network_afr")
                            continue;

                        if (description.EndsWith(" (Deprecated)"))
                            continue;

                        if (!string.IsNullOrEmpty(comp.ID.Variant))
                        {
                            if (type != ComponentType.MCU)
                                throw new Exception("MCU-specific variants are only expected for MCU support packages");

                            fw.MCUFilterRegex = comp.ID.Variant;
                            fw.ClassID = comp.ID.SimplifiedID + ".mcu";
                            VerifyMatch(ref _MCUPackageClassID, fw.ClassID);
                            fw.ID += "." + comp.ID.Variant;
                        }
                        else if (FamilyPrefixes.TryGetValue(comp.ID.Group, out var prefix))
                        {
                            fw.MCUFilterRegex = prefix + ".*";
                            fw.ClassID = comp.ID.SimplifiedID + ".family";

                            if (comp.ID.Subgroup == "fsp")
                                VerifyMatch(ref _FSPClassID, fw.ClassID);
                            else
                                VerifyMatch(ref _FamilyPackageClassID, fw.ClassID);

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
                        else if (fw.ID == "com.renesas.arm.Arm.PSA.TF-M.Core")
                        {
                            //Special case: TFM linker script. Ignore it for now.
                        }
                        else if (linkerScript != null)
                            throw new Exception("Linker script can only be defined by the core framework");

                        _TranslatedComponents[comp.ID] = fw.IDForReferenceList;

                        if (filesByName.TryGetValue(comp.ExpectedModuleDescriptionFile, out var moduleDesc))
                            _ConfigFileTranslator.TranslateModuleDescriptionFiles(fw, zf.ExtractXMLFile(moduleDesc), Report);

                        if (filesByName.TryGetValue(comp.ExpectedModuleConfigurationFile, out var moduleConf))
                            _ConfigFileTranslator.ProcessModuleConfiguration(fw, zf.ExtractXMLFile(moduleConf), Report);

                        if (type == ComponentType.Common && fw.ID.EndsWith(".fsp_common"))
                            fw.GeneratedConfigurationFiles = fw.GeneratedConfigurationFiles.Concat(XmlTools.LoadObject<GeneratedConfigurationFile[]>(@"..\..\Rules\common_data.xml")).ToArray();

                        if (type == ComponentType.MCU && comp.ID.Variant != "")
                        {
                            //This is a device support component. Attach a pinout property group to it.
                            var (pg, cf) = PinConfigurationTranslator.BuildPinPropertyGroup(_AllPinouts[comp.ID.Variant]);
                            fw.ConfigurableProperties ??= new PropertyList { PropertyGroups = new List<PropertyGroup>() };
                            fw.ConfigurableProperties.PropertyGroups.Add(pg);
                            fw.GeneratedConfigurationFragments = (fw.GeneratedConfigurationFragments ?? new GeneratedConfigurationFile[0]).Append(cf).ToArray();
                        }

                        AddGeneratedHeadersToSearchPath(fw);
                        frameworkList.Add(fw);
                    }
                }

                return summary;
            }

            private void AddGeneratedHeadersToSearchPath(EmbeddedFramework fw)
            {
                if (fw.GeneratedConfigurationFiles == null)
                    return;

                var dirsRelativeToProject = fw.GeneratedConfigurationFiles
                    .Where(f => f.Name.EndsWith(".h", StringComparison.InvariantCultureIgnoreCase))
                    .Select(f => GetDirectoryName(f.Name))
                    .Distinct().ToArray();

                fw.AdditionalIncludeDirs = fw.AdditionalIncludeDirs.Concat(dirsRelativeToProject).Distinct().ToArray();
            }

            private void VerifyMatch(ref string storedID, string ID)
            {
                if (storedID == null)
                    storedID = ID;

                if (storedID != ID)
                    throw new Exception($"Mismatching ID: {ID}, expected {storedID}");
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
            Dictionary<string, PinConfigurationTranslator.DevicePinout> _AllPinouts = new Dictionary<string, PinConfigurationTranslator.DevicePinout>();

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
                    foreach (var v in dev.Variants)
                        _AllPinouts[v.Name] = v.Pinout;

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

            class ExclusivityClass
            {
                public HashSet<EmbeddedFramework> Frameworks = new HashSet<EmbeddedFramework>();
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

                var frameworkExclusivityClasses = new Dictionary<EmbeddedFramework, ExclusivityClass>();

                foreach (var kv in frameworksByFile)
                {
                    if (kv.Value.Count == 1)
                        continue;

                    var cls = new ExclusivityClass();

                    foreach (var fw in kv.Value)
                    {
                        if (fw.IncompatibleFrameworks != null)
                            throw new Exception("Incompatible frameworks already defined for " + fw.UserFriendlyName);

                        cls.Frameworks.Add(fw);
                        if (frameworkExclusivityClasses.TryGetValue(fw, out var oldClass) && oldClass != cls)
                        {
                            //Merge the exclusivity classes
                            foreach (var fw2 in oldClass.Frameworks)
                            {
                                frameworkExclusivityClasses[fw2] = cls;
                                cls.Frameworks.Add(fw2);
                            }
                        }

                        frameworkExclusivityClasses[fw] = cls;
                    }
                }

                foreach (var cls in frameworkExclusivityClasses.Values.Distinct())
                {
                    foreach (var fw in cls.Frameworks)
                    {
                        var incompatibleFrameworks = cls.Frameworks.Select(f2 => f2.ID).Except(new[] { fw.ID }).ToArray();

                        if (fw.IncompatibleFrameworks != null)
                            throw new Exception($"{fw.UserFriendlyName} already has an incompatible framework list");

                        fw.IncompatibleFrameworks = incompatibleFrameworks;
                    }
                }
            }

            public string[] TranslateExamples(string mainPackFile, IEnumerable<EmbeddedFramework> frameworks)
            {
                Console.WriteLine("Translating sample projects...");
                List<string> result = new List<string>();

                foreach (var dir in Directory.GetDirectories(Path.Combine(Directories.RulesDir, "Samples")))
                {
                    var sampleName = Path.GetFileName(dir);
                    var targetDir = Path.Combine(Directories.OutputDir, "samples", sampleName);

                    PathTools.CopyDirectoryRecursive(dir, targetDir);
                    PathTools.CopyDirectoryRecursive(Path.Combine(Directories.RulesDir, "FixedFiles", "baremetal"), targetDir);
                    PathTools.CopyDirectoryRecursive(Path.Combine(Directories.RulesDir, "FixedFiles", "all"), targetDir);
                    result.Add("samples/" + sampleName);
                }

                var frameworksByID = frameworks.ToLookup(fw => fw.IDForReferenceList);

                Regex rgBlinkySample = new Regex("(.*)_blinky");

                using (var zf = ZipFile.Open(mainPackFile))
                {
                    foreach (var e in zf.Entries)
                    {
                        if (e.FileName.StartsWith(".templates", StringComparison.CurrentCultureIgnoreCase) && e.FileName.EndsWith("/configuration.xml", StringComparison.InvariantCultureIgnoreCase))
                        {
                            var sampleName = Path.GetFileName(Path.GetDirectoryName(e.FileName));
                            var xml = new XmlDocument();
                            xml.LoadXml(Encoding.UTF8.GetString(zf.ExtractEntry(e)));

                            var m = rgBlinkySample.Match(sampleName);

                            if (!m.Success)
                                continue;

                            var refs = xml.DocumentElement.SelectElements("raComponentSelection/component")
                                .Select(c => new ComponentID(c, true))
                                .Where(c => c.Class != "Projects")
                                .Select(c => _TranslatedComponents[c])
                                .Concat(new[] {
                                    _BoardPackageClassID ?? throw new Exception("Could not locate the primary board package"),
                                    _MCUPackageClassID ?? throw new Exception("Could not locate the primary MCU package"),
                                    _FamilyPackageClassID ?? throw new Exception("Could not locate the primary MCU package"),
                                    _FSPClassID ?? throw new Exception("Could not locate the primary FSP package"),
                                })
                                .ToArray();

                            RemoveIncompatibleReferences(ref refs, frameworksByID);

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

                            PathTools.CopyDirectoryRecursive(Path.Combine(Directories.RulesDir, "FixedFiles", m.Groups[1].Value), targetDir);
                            PathTools.CopyDirectoryRecursive(Path.Combine(Directories.RulesDir, "FixedFiles", "all"), targetDir);
                            XmlTools.SaveObject(sample, Path.Combine(targetDir, "sample.xml"));
                        }
                    }
                }

                return result.ToArray();
            }

            private void RemoveIncompatibleReferences(ref string[] refs, ILookup<string, EmbeddedFramework> frameworksByID)
            {
                while (DoRemoveIncompatibleReferences(ref refs, frameworksByID))
                {
                }
            }

            private bool DoRemoveIncompatibleReferences(ref string[] refs, ILookup<string, EmbeddedFramework> frameworksByID)
            {
                for (int i = 0; i < refs.Length; i++)
                {
                    foreach (var fw1 in frameworksByID[refs[i]])
                    {
                        for (int j = i + 1; j < refs.Length; j++)
                        {
                            foreach (var fw2 in frameworksByID[refs[j]])
                            {
                                if (fw1.IncompatibleFrameworks?.Contains(fw2.ID) == true || fw1.IncompatibleFrameworks?.Contains(fw2.IDForReferenceList) == true ||
                                    fw2.IncompatibleFrameworks?.Contains(fw1.ID) == true || fw2.IncompatibleFrameworks?.Contains(fw1.IDForReferenceList) == true)
                                {
                                    //The sample references incompatible frameworks (e.g. FreeRTOS vs. FreeRTOS port).
                                    //Unreference the framework with the least source files.
                                    if (fw1.AdditionalSourceFiles.Length > fw2.AdditionalSourceFiles.Length)
                                        refs = refs.Except(new[] { refs[j] }).ToArray();
                                    else
                                        refs = refs.Except(new[] { refs[i] }).ToArray();

                                    return true;
                                }
                            }
                        }
                    }
                }

                return false;
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

            public void GenerateFrameworkDependentDefaultValues()
            {
                _ConfigFileTranslator.GenerateFrameworkDependentDefaultValues(Report, _AllPinouts);
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
                    Regex rgBlinky = new Regex("Renesas.RA_([^_]+)_blinky\\.", RegexOptions.IgnoreCase);
                    Match m;

                    var nameOnly = Path.GetFileName(fn);
                    if ((m = rgMCU.Match(nameOnly)).Success)
                        bspGen.TranslateComponents(fn, frameworks, RenesasBSPBuilder.ComponentType.MCU);
                    else if ((m = rgBoard.Match(nameOnly)).Success)
                        bspGen.TranslateComponents(fn, frameworks, RenesasBSPBuilder.ComponentType.Board);
                    else if (nameOnly.StartsWith("Arm.CMSIS", StringComparison.InvariantCultureIgnoreCase))
                        bspGen.TranslateComponents(fn, frameworks, RenesasBSPBuilder.ComponentType.CMSIS);
                    else if ((m = rgBlinky.Match(nameOnly)).Success)
                        continue;   //Sample projects are handled separately.
                    else if (fn == mainPackFile)
                        continue;   //Already handled
                    else
                        bspGen.TranslateComponents(fn, frameworks, RenesasBSPBuilder.ComponentType.Other);
                }

                bspGen.GenerateFrameworkDependentDefaultValues();
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

                    var hwregisterDir = Path.Combine(bspGen.Directories.OutputDir, "DeviceDefinitions");
                    Directory.CreateDirectory(hwregisterDir);

                    foreach (var mcu in fam)
                    {
                        famBuilder.MCUs.Add(new MCUBuilder { Name = mcu.Name, Core = mcu.Core });

                        using (var fs = File.Create(Path.Combine(hwregisterDir, mcu.HardwareRegisters.MCUName + ".xml.gz")))
                        using (var gs = new GZipStream(fs, CompressionMode.Compress))
                            XmlTools.SaveObjectToStream(mcu.HardwareRegisters, gs);

                        foreach (var v in mcu.Variants)
                        {
                            var linkerScript = bspGen.GenerateLinkerScript(mcu, summary.LinkerScript);

                            mcuDefinitions.Add(new MCU
                            {
                                ID = v.Name,
                                FamilyID = fam.Key,
                                HierarchicalPath = $@"Renesas\ARM\{fam.Key}",
                                RAMSize = (int)mcu.MemoryMap.First(m => m.Type == "InternalRam").Size,
                                RAMBase = (uint)mcu.MemoryMap.First(m => m.Type == "InternalRam").Start,
                                FLASHSize = (int)mcu.MemoryMap.First(m => m.Type == "InternalRom").Size,
                                FLASHBase = (uint)mcu.MemoryMap.First(m => m.Type == "InternalRom").Start,
                                MCUDefinitionFile = $"DeviceDefinitions/{mcu.HardwareRegisters.MCUName}.xml",
                                CompilationFlags = new ToolFlags
                                {
                                    LinkerScript = linkerScript,
                                }
                            });

                            if (mcu.MemoryMap.Length != 2)
                                throw new Exception("Unexpected number of memories. Generate a proper memory map!");
                        }
                    }

                    var famObj = famBuilder.GenerateFamilyObject(MCUFamilyBuilder.CoreSpecificFlags.All & ~MCUFamilyBuilder.CoreSpecificFlags.PrimaryMemory);
                    famObj.AdditionalTestProgramLines = new[] { "int __Vectors;" };

                    familyDefinitions.Add(famObj);
                }

                frameworks.Sort((a, b) => StringComparer.InvariantCultureIgnoreCase.Compare(a.UserFriendlyName, b.UserFriendlyName));

                foreach (var fw in frameworks)
                    fw.AdditionalSystemVars = fw.AdditionalSystemVars?.Distinct().ToArray();

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
                    Examples = bspGen.TranslateExamples(mainPackFile, frameworks),
                    PackageVersion = summary.Version,
                    FileConditions = bspGen.MatchedFileConditions.Values.ToArray(),
                    MinimumEngineVersion = "5.6.105",
                };

                bspGen.ValidateBSP(bsp, BSPBuilder.BSPValidationFlags.SuppressDebuggerStops);
                bspGen.Save(bsp, false, false);
            }


        }
    }
}
