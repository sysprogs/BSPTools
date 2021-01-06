using BSPEngine;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace AtmelStartSDKImporter
{
    public class AtmelStartPackageParser : ISDKImporter
    {
        public string Name => "Atmel START Project";

        public const string ID = "com.sysprogs.sdkimporters.atmel.start";

        public string UniqueID => ID;

        public string CommandName => "Import an Atmel START Project";

        public string OpenFileFilter => "Atmel START archives|*.atzip";

        public string Target => "arm-eabi";

        public bool IsCompatibleWithToolchain(LoadedToolchain toolchain)
        {
            var id = toolchain?.Toolchain?.GNUTargetID?.ToLower();
            return id?.Contains("arm") ?? true;
        }

        public ImportedExternalSDK GenerateBSPForSDK(ImportedSDKLocation location, ISDKImportHost host)
        {
            string temporaryDir = Path.Combine(host.GetDefaultDirectoryForImportedSDKs("arm-eabi"), Guid.NewGuid().ToString());
            Directory.CreateDirectory(temporaryDir);

            try
            {
                host.ExtractZIPFile(location.OriginallySelectedFile, temporaryDir);

                var bsp = GenerateBSPForSTARTProject(temporaryDir, host.WarningSink);

                string newDir = Path.Combine(Path.GetDirectoryName(temporaryDir), bsp.PackageID);
                if (Directory.Exists(newDir))
                {
                    if (!host.AskWarn($"{newDir} already exists. Overwrite?"))
                        throw new OperationCanceledException();

                    host.DeleteDirectoryRecursively(newDir);
                }

                Directory.Move(temporaryDir, newDir);
                return new ImportedExternalSDK { BSPID = bsp.PackageID, Directory = newDir };
            }
            catch
            {
                try
                {
                    host.DeleteDirectoryRecursively(temporaryDir);
                }
                catch { }
                throw;
            }
        }

        struct DetectedLinkerScripts
        {
            const string PrimaryMemoryVariableName = "com.sysprogs.bspoptions.primary_memory";

            public string RelativePathFormat;   //With '*' corresponding to the memory ID
            public string[] Variants;
            public int FLASHLinkerScriptIndex;

            public string LinkerScriptFormat
            {
                get
                {
                    string relativePath;

                    if (Variants?.Length == 1)
                        relativePath = RelativePathFormat.Replace("*", Variants[0]);
                    else
                        relativePath = RelativePathFormat.Replace("*", "$$" + PrimaryMemoryVariableName + "$$");

                    return "$$SYS:BSP_ROOT$$/" + relativePath;
                }
            }

            public string RelativeFLASHScript
            {
                get
                {
                    if ((Variants?.Length ?? 0) < 1)
                        return RelativePathFormat;
                    else
                        return RelativePathFormat.Replace("*", Variants[FLASHLinkerScriptIndex]);
                }
            }

            public PropertyGroup ToPropertyGroup()
            {
                if ((Variants?.Length ?? 0) < 2)
                    return null;

                return new PropertyGroup
                {
                    Properties = new List<PropertyEntry>
                    {
                        new PropertyEntry.Enumerated
                        {
                            Name = "Execute from",
                            UniqueID = PrimaryMemoryVariableName,
                            SuggestionList = Variants.Select(v=> new PropertyEntry.Enumerated.Suggestion{ InternalValue = v, UserFriendlyName = v.ToUpper()}).ToArray(),
                            DefaultEntryIndex = FLASHLinkerScriptIndex,
                        }
                    }
                };
            }
        }

        const string GCCExportHint = "Please ensure you check 'Makefile (standalone)' when exporting the project from Atmel START.";

        static DetectedLinkerScripts DetectLinkerScripts(string directory)
        {
            directory = Path.GetFullPath(directory);
            string[] allLinkerScripts = Directory.GetFiles(directory, "*.ld", SearchOption.AllDirectories).Select(f => f.Substring(directory.Length).TrimStart('\\').Replace('\\', '/')).ToArray();

            if (allLinkerScripts.Length == 0)
                throw new Exception($"The Atmel START project does not contain any linker scripts. {GCCExportHint}");

            Regex rgMemory = new Regex("^(.*_)(flash|sram)(.*)$", RegexOptions.IgnoreCase);
            //Group linker scripts (xxx_flash and xxx_sram) together as xxx_* and return the available variants (flash/ram).

            var g = allLinkerScripts.Select(s => rgMemory.Match(s)).Where(m => m.Success).GroupBy(m => m.Groups[1].Value).FirstOrDefault();
            if (g == null)
                return new DetectedLinkerScripts { RelativePathFormat = allLinkerScripts[0], Variants = new string[0] };

            var arr = g.ToArray();

            return new DetectedLinkerScripts
            {
                RelativePathFormat = g.First().Groups[1].Value + "*" + g.First().Groups[3].Value,
                Variants = g.Select(m => m.Groups[2].Value).ToArray(),
                FLASHLinkerScriptIndex = Enumerable.Range(0, arr.Length).FirstOrDefault(i => arr[i].Groups[2].Value.StartsWith("flash", StringComparison.InvariantCultureIgnoreCase))
            };
        }

        struct FlagsFromMakefile
        {
            public string[] CommonFlags;
            public string[] RelativeIncludeDirs;
        }

        static FlagsFromMakefile ScanMakefileForCommonFlags(string makefile)
        {
            //Do a very basic scan of the Makefile for anything that looks like -mcpu=xxx or -mfpu=xxx, resolving contradicting entries.
            //This won't work if the Makefile starts specifying those options conditionally, but should cover most of the regular cases.

            Dictionary<string, string> foundFlags = new Dictionary<string, string>();
            string[] lines = File.ReadAllLines(makefile);

            foreach (var rawLine in lines)
            {
                string line = rawLine;
                int comment = line.IndexOf('#');
                if (comment != -1)
                    line = line.Substring(0, comment);

                foreach (var token in line.Split(' '))
                {
                    int idx = token.IndexOf('=');
                    string key, value;
                    if (idx == -1)
                    {
                        key = token;
                        value = "";
                    }
                    else
                    {
                        key = token.Substring(0, idx);
                        value = token.Substring(idx + 1);
                    }

                    switch (key)
                    {
                        case "-mcpu":
                        case "-mfpu":
                        case "-marm":
                        case "-mthumb":
                            foundFlags[key] = value;
                            break;
                    }
                }
            }

            HashSet<string> includeDirs = new HashSet<string>();
            bool insideDirIncludes = false;
            Regex rgInclude = new Regex("-I\"?../([^\"]*)\"?[ \t]*\\\\?$");
            foreach (var line in lines)
            {
                if (line.Trim().StartsWith("DIR_INCLUDES"))
                    insideDirIncludes = true;
                else if (insideDirIncludes)
                {
                    var m = rgInclude.Match(line);
                    if (m.Success)
                        includeDirs.Add(m.Groups[1].Value);
                }

                if (!line.Trim().EndsWith("\\"))
                    insideDirIncludes = false;
            }

            return new FlagsFromMakefile
            {
                CommonFlags = foundFlags.Select(kv => kv.Value == "" ? kv.Key : $"{kv.Key}={kv.Value}").ToArray(),
                RelativeIncludeDirs = includeDirs.ToArray(),
            };
        }

        static MCUMemory[] ScanLinkerScriptForMemories(string linkerScript)
        {
            if (!File.Exists(linkerScript))
                throw new Exception($"Missing {linkerScript}");

            bool insideMemoryBlock = false;

            Regex rgMemory = new Regex("([a-zA-Z0-9]+)[ \t]+\\([^()]+\\)[ \t:]+ORIGIN[ \t]*=[ \t]*0x([0-9A-Fa-f]+),[ \t]*LENGTH[ \t]*=[ \t]*0x([0-9A-Fa-f]+)");
            List<MCUMemory> memories = new List<MCUMemory>();

            foreach (var line in File.ReadAllLines(linkerScript))
            {
                if (line.Trim() == "MEMORY")
                    insideMemoryBlock = true;
                else if (line.Trim() == "}")
                    insideMemoryBlock = false;
                else if (insideMemoryBlock)
                {
                    var m = rgMemory.Match(line);
                    if (m.Success)
                    {
                        memories.Add(new MCUMemory { Name = m.Groups[1].Value, Address = ulong.Parse(m.Groups[2].Value, NumberStyles.HexNumber), Size = ulong.Parse(m.Groups[3].Value, NumberStyles.HexNumber) });
                    }
                }
            }

            return memories.ToArray();
        }

        public static BoardSupportPackage GenerateBSPForSTARTProject(string extractedProjectDirectory, IWarningSink sink)
        {
            var gpdscFile = Path.Combine(extractedProjectDirectory, "AtmelStart.gpdsc");
            if (!File.Exists(gpdscFile))
                throw new Exception($"{gpdscFile} does not exist!");

            var gccMakefile = Path.Combine(extractedProjectDirectory, "gcc\\Makefile");
            if (!File.Exists(gccMakefile))
                throw new Exception($"{gccMakefile} does not exist. {GCCExportHint}");

            var xml = new XmlDocument();
            xml.Load(gpdscFile);
            string device = null;
            foreach (var node in xml.SelectNodes("package/generators/generator/select").OfType<XmlElement>())
            {
                var name = node.GetAttribute("Dname");
                var vendor = node.GetAttribute("Dvendor");
                if (!string.IsNullOrEmpty(vendor) && !string.IsNullOrEmpty(name))
                    device = name;
            }

            if (device == null)
                throw new Exception($"Could not find the device ID in {gpdscFile}");

            var linkerScripts = DetectLinkerScripts(extractedProjectDirectory);
            var memories = ScanLinkerScriptForMemories(Path.Combine(extractedProjectDirectory, linkerScripts.RelativeFLASHScript));

            var flagsFromMakefile = ScanMakefileForCommonFlags(gccMakefile);

            var mcu = new MCU
            {
                ID = device,
                FamilyID = "ATSTART",

                FLASHBase = (uint)(memories.FirstOrDefault(m => m.Name == "rom")?.Address ?? uint.MaxValue),
                FLASHSize = (int)(memories.FirstOrDefault(m => m.Name == "rom")?.Size ?? uint.MaxValue),

                RAMBase = (uint)(memories.FirstOrDefault(m => m.Name == "ram")?.Address ?? uint.MaxValue),
                RAMSize = (int)(memories.FirstOrDefault(m => m.Name == "ram")?.Size ?? uint.MaxValue),

                CompilationFlags = new ToolFlags
                {
                    PreprocessorMacros = new[] { $"__{device}__" },
                    COMMONFLAGS = string.Join(" ", flagsFromMakefile.CommonFlags),
                    LinkerScript = linkerScripts.LinkerScriptFormat,
                    IncludeDirectories = flagsFromMakefile.RelativeIncludeDirs?.Select(d => "$$SYS:BSP_ROOT$$/" + d).ToArray(),
                    LDFLAGS = "-Wl,--entry=Reset_Handler", //Unless this is specified explicitly, the gdb's "load" command won't set $pc to the entry point, requiring an explicit device reset.
                },

                ConfigurableProperties = new PropertyList
                {
                    PropertyGroups = new[] { linkerScripts.ToPropertyGroup() }.Where(g => g != null).ToList()
                },

                MemoryMap = new AdvancedMemoryMap
                {
                    Memories = memories.ToArray()
                }
            };

            string mainFileName = "main.c";
            if (!File.Exists(Path.Combine(extractedProjectDirectory, mainFileName)))
            {
                var candidates = Directory.GetFiles(extractedProjectDirectory, "*_main.c");
                if (candidates.Length == 1)
                    mainFileName = Path.GetFileName(candidates[0]);
            }

            var bsp = new BoardSupportPackage
            {
                PackageID = "com.sysprogs.atstart." + device,
                GNUTargetID = "arm-eabi",
                PackageDescription = $"{device} Support",
                BSPImporterID = ID,
                MCUFamilies = new[] { new MCUFamily { ID = "ATSTART" } },
                SupportedMCUs = new[] { mcu },
                Frameworks = xml.SelectNodes("package/components/component").OfType<XmlElement>().Select(f => GenerateFrameworkForComponent(f, mainFileName)).Where(f => f != null).ToArray(),

                EmbeddedSamples = new[]
                {
                    new EmbeddedProjectSample
                    {
                        Name = "Default Project",
                        Description = "A basic project generated by Atmel START",
                        AdditionalSourcesToCopy = new[]
                        {
                            new AdditionalSourceFile
                            {
                                SourcePath = "$$SYS:BSP_ROOT$$/" + mainFileName,
                                TargetFileName = "$$PROJECTNAME$$.c",
                            }
                        }
                    }
                }
            };

            FixGPDSCErrors(bsp, mcu, extractedProjectDirectory, flagsFromMakefile, linkerScripts.RelativeFLASHScript);

            XmlTools.SaveObject(bsp, Path.Combine(extractedProjectDirectory, LoadedBSP.PackageFileName));
            return bsp;
        }

        private static void FixGPDSCErrors(BoardSupportPackage bsp, MCU mcu, string extractedProjectDirectory, FlagsFromMakefile flagsFromMakefile, string relativeFLASHScript)
        {
            //1. Startup files may not be referenced in the GPDSC file
            string[] startupFiles = Directory.GetFiles(Path.Combine(extractedProjectDirectory, Path.GetDirectoryName(relativeFLASHScript)), "*.c")
                .Select(f => "$$SYS:BSP_ROOT$$/" + f.Substring(extractedProjectDirectory.Length).TrimStart('\\').Replace('\\', '/'))
                .Except(bsp.Frameworks.SelectMany(fw => fw.AdditionalSourceFiles))
                .ToArray();

            //2. Some include directories are not referenced in the GPDSC file
            string[] extraIncludeDirs = flagsFromMakefile.RelativeIncludeDirs
                .Select(d => "$$SYS:BSP_ROOT$$/" + d)
                .Except(bsp.Frameworks.SelectMany(fw => fw.AdditionalIncludeDirs))
                .ToArray();

            mcu.AdditionalSourceFiles = startupFiles;
            mcu.CompilationFlags = mcu.CompilationFlags.Merge(new ToolFlags { IncludeDirectories = extraIncludeDirs });
        }

        private static EmbeddedFramework GenerateFrameworkForComponent(XmlElement el, string mainFileName)
        {
            string name = el.GetAttribute("Cclass");
            if (string.IsNullOrEmpty(name))
                return null;

            var files = el.SelectNodes("files/file")
                .OfType<XmlElement>()
                .Select(e => new { OriginalPath = e.GetAttribute("name"), Path = "$$SYS:BSP_ROOT$$/" + e.GetAttribute("name"), Category = e.GetAttribute("category"), Condition = e.GetAttribute("condition") })
                .Where(f => string.IsNullOrEmpty(f.Condition) || f.Condition.IndexOf("GCC", StringComparison.InvariantCultureIgnoreCase) != -1)
                .ToArray();

            return new EmbeddedFramework
            {
                ID = $"com.sysprogs.atstart.{name}",
                UserFriendlyName = name,
                ProjectFolderName = name,
                DefaultEnabled = true,

                AdditionalSourceFiles = files.Where(f => f.Category == "source" && StringComparer.InvariantCultureIgnoreCase.Compare(f.OriginalPath.ToLower(), mainFileName) != 0).Select(f => f.Path).ToArray(),
                AdditionalHeaderFiles = files.Where(f => f.Category == "header").Select(f => f.Path).ToArray(),
                AdditionalIncludeDirs = files.Where(f => f.Category == "include").Select(f => f.Path).Distinct().ToArray(),
            };
        }
    }
}
