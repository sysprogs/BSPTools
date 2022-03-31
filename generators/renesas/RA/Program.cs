using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace renesas_ra_bsp_generator
{
    class Program
    {
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
                public string FrameworkID => $"com.renesas.arm.{Vendor}.{Class}.{Subgroup}";

                public readonly string Vendor, Class, Group, Subgroup;

                public Component(PackDescriptionReader packDescriptionReader, XmlElement c)
                {
                    _Reader = packDescriptionReader;
                    _Element = c;

                    Vendor = _Element.GetAttribute("Cvendor");
                    Class = _Element.GetAttribute("Cclass");
                    Group = _Element.GetAttribute("Cgroup");
                    Subgroup = _Element.GetAttribute("Csub");
                }

                public string Description => _Element.SelectSingleNode("description")?.InnerXml;

                public struct PathAndType
                {
                    public string Path;
                    public string Type;

                    public override string ToString() => $"[{Type}] {Path}";
                }

                public List<PathAndType> TranslateAndCopy(string baseOutputDir, string subdir, BSPBuilder bspGen)
                {
                    var outputDir = Path.Combine(baseOutputDir, subdir);
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

                        string mappedPath = $"$$SYS:BSP_ROOT$$/{subdir}/{name}".TrimEnd('/');
                        if (ShouldRenameFile(name, out var newName))
                        {
                            int idx = mappedPath.LastIndexOf('/');
                            mappedPath = mappedPath.Substring(0, idx + 1) + newName;
                        }

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
                            using (var fs = File.Create(targetPath))
                                _Reader._ZipFile.ExtractEntry(e, fs);
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
                internal string LinkerScript;
            }

            public PackFileSummary TranslateComponents(string packFile, List<EmbeddedFramework> frameworkList, string subdir)
            {
                PackFileSummary summary = new PackFileSummary();
                Console.WriteLine($"Translating {packFile}...");

                using (var zf = ZipFile.Open(packFile))
                {
                    var rdr = new PackDescriptionReader(zf);

                    summary.Version = rdr.ReleaseVersion;

                    foreach (var comp in rdr.Components)
                    {
                        var translatedPaths = comp.TranslateAndCopy(Directories.OutputDir, subdir, this);
                        var description = comp.Description;

                        if (description == "Renesas Bluetooth Low Energy Library")
                        {
                            //As of v3.3.0, multiple BLE components have the same description, causing conflicts in folder names.
                            //We fix it manually by renaming them based in the subgroup ID.
                            if (!comp.Subgroup.StartsWith("r_ble_"))
                                throw new Exception("Unexpected subgroup for a BLE component");

                            description = "Renesas BLE - " + char.ToUpper(comp.Subgroup[6]) + comp.Subgroup.Substring(7);
                        }

                        EmbeddedFramework fw = new EmbeddedFramework
                        {
                            ID = comp.FrameworkID,
                            ProjectFolderName = description,
                            UserFriendlyName = description,
                            AdditionalSourceFiles = translatedPaths.Where(p => p.Type == "source").Select(p => p.Path).ToArray(),
                            AdditionalHeaderFiles = translatedPaths.Where(p => p.Type == "header").Select(p => p.Path).ToArray(),
                            AdditionalIncludeDirs = translatedPaths.Where(p => p.Type == "include").Select(p => p.Path).ToArray(),
                            AdditionalLibraries = translatedPaths.Where(p => p.Type == "library").Select(p => p.Path).ToArray(),
                        };

                        var linkerScript = translatedPaths.Where(p => p.Type == "linkerScript").SingleOrDefault(p => p.Type == "linkerScript").Path;
                        if (comp.Subgroup == "fsp_common")
                        {
                            summary.LinkerScript = linkerScript ?? throw new Exception("Missing linker script");
                            fw.DefaultEnabled = true;
                        }
                        else if (linkerScript != null)
                            throw new Exception("Linker script can only be defined by the core framework");

                        frameworkList.Add(fw);
                    }
                }

                return summary;
            }
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: renesas_ra_bsp_generator.exe <Renesas Packs directory>");

            using (var bspGen = new RenesasBSPBuilder(BSPDirectories.MakeDefault(args)))
            {
                var devs = RenesasDeviceDatabase.DiscoverDevices(bspGen.Directories.InputDir);

                List<MCUFamily> familyDefinitions = new List<MCUFamily>();
                List<MCU> mcuDefinitions = new List<MCU>();
                List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();

                foreach (var fam in devs.GroupBy(d => d.FamilyName))
                {
                    var famBuilder = new MCUFamilyBuilder(bspGen, new FamilyDefinition
                    {
                        Name = fam.Key,
                        CompilationFlags = new ToolFlags
                        {
                            PreprocessorMacros = new[] 
                            {
                                "_RENESAS_RA_",
                                "_RA_CORE=C$$com.sysprogs.bspoptions.arm.core$$",
                            }
                        }
                    });

                    foreach (var mcu in fam)
                    {
                        famBuilder.MCUs.Add(new MCUBuilder { Name = mcu.Name, Core = mcu.Core });
                        mcuDefinitions.Add(new MCU
                        {
                            ID = mcu.Name,
                            FamilyID = fam.Key,
                            HierarchicalPath = $@"Renesas\ARM\{fam.Key}",
                            RAMSize = (int)mcu.MemoryMap.First(m => m.Type == "InternalRam").Size,
                            RAMBase = (uint)mcu.MemoryMap.First(m => m.Type == "InternalRam").Start,
                            FLASHSize = (int)mcu.MemoryMap.First(m => m.Type == "InternalRom").Size,
                            FLASHBase = (uint)mcu.MemoryMap.First(m => m.Type == "InternalRom").Start,
                        });

                        if (mcu.MemoryMap.Length != 2)
                            throw new Exception("Unexpected number of memories. Generate a proper memory map!");
                    }

                    var famObj = famBuilder.GenerateFamilyObject(MCUFamilyBuilder.CoreSpecificFlags.All);

                    familyDefinitions.Add(famObj);
                }

                var mainPackFile = Directory.GetFiles(bspGen.Directories.InputDir, "Renesas.RA.*.pack").Single();
                var summary = bspGen.TranslateComponents(mainPackFile, frameworks, "core");

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
                    //Examples = exampleDirs.Where(s => !s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                    //TestExamples = exampleDirs.Where(s => s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                    PackageVersion = summary.Version,
                    FileConditions = bspGen.MatchedFileConditions.Values.ToArray(),
                    MinimumEngineVersion = "5.4",
                };

                bspGen.ValidateBSP(bsp);
                bspGen.Save(bsp, false, false);
            }


        }
    }
}
