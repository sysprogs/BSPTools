using BSPEngine;
using BSPGenerationTools;
using BSPGenerationTools.Parsing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace stm8_bsp_generator
{
    class STM8BSPBuilder : BSPBuilder
    {
        public const uint FLASHBase = 0x8000, RAMBase = 0;

        public STM8BSPBuilder(BSPDirectories dirs)
            : base(dirs, null, -1)
        {
        }

        public override void GetMemoryBases(out uint flashBase, out uint ramBase)
        {
            flashBase = FLASHBase;
            ramBase = RAMBase;
        }

        public override MemoryLayoutAndSubstitutionRules GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
        {
            throw new NotImplementedException();
        }

        public void PatchDriverFiles()
        {
            foreach (var dir in Directory.GetDirectories(Directories.OutputDir, "*_StdPeriph_Driver"))
            {
                foreach (var src in Directory.GetFiles(dir + "\\src", "*.c"))
                {
                    var name = Path.GetFileNameWithoutExtension(src);
                    int idx = name.LastIndexOf('_');
                    if (idx == -1)
                        throw new Exception("Invalid driver name");

                    var instName = name.Substring(idx + 1).ToUpper();

                    var lines = File.ReadAllLines(src).ToList();
                    if (string.Join("\r\n", lines).Contains(instName + "->"))
                    {
                        bool patched = false;
                        for (int i = 0; i < lines.Count; i++)
                        {
                            if (lines[i].StartsWith("#include \"stm8"))
                            {
                                lines.Insert(i + 1, $"#ifdef {instName}");
                                lines.Add($"#endif //{instName}");
                                patched = true;
                                break;
                            }
                        }

                        if (!patched)
                            throw new Exception("Could not patch " + src);

                        File.WriteAllLines(src, lines);
                    }

                }
            }
        }
    }

    class STM8MCUBuilder : MCUBuilder
    {
        static Regex rgSegmentArg = new Regex("-(b|m)[ \t]+(0x[0-9a-fA-F]+|0-9+)");

        static int GetSegmentSize(string[] lines, string segName, out int baseAddr)
        {
            string prefix = "+seg " + segName;

            foreach (var line in lines)
            {
                if (line.StartsWith(prefix))
                {
                    int size = -1;
                    baseAddr = -1;

                    foreach (Match m in rgSegmentArg.Matches(line.Substring(prefix.Length)))
                    {
                        switch (m.Groups[1].Value)
                        {
                            case "b":
                                baseAddr = (int)HeaderFileParser.ParseMaybeHex(m.Groups[2].Value);
                                break;
                            case "m":
                                size = (int)HeaderFileParser.ParseMaybeHex(m.Groups[2].Value);
                                break;
                        }
                    }

                    if (size == -1)
                        throw new Exception("Unknown size for " + segName);

                    return size;
                }
            }

            throw new Exception("Could not locate address for " + segName);
        }

        public CXSTM8MCU Definition;

        public STM8MCUBuilder(CXSTM8MCU dev)
        {
            Definition = dev;
            Name = dev.MCUName;
            FlashSize = GetSegmentSize(dev.LinkerScript, ".vector", out int flashBase);
            RAMSize = GetSegmentSize(dev.LinkerScript, ".data", out _);
            Core = CortexCore.NonARM;

            if (flashBase != STM8BSPBuilder.FLASHBase)
                throw new Exception("Unexpected FLASH base address");
        }

        public string MakeRelativePath(string ext) => $"{Definition.Family}/{Definition.MCUName}{ext}";


        public override MCU GenerateDefinition(MCUFamilyBuilder fam, BSPBuilder bspBuilder, bool requirePeripheralRegisters, bool allowIncompleteDefinition = false, MCUFamilyBuilder.CoreSpecificFlags flagsToAdd = MCUFamilyBuilder.CoreSpecificFlags.All)
        {
            var result = base.GenerateDefinition(fam, bspBuilder, requirePeripheralRegisters, allowIncompleteDefinition, flagsToAdd);

            result.CompilationFlags.COMMONFLAGS = string.Join(" ", Definition.Options);
            result.CompilationFlags.PreprocessorMacros = result.CompilationFlags.PreprocessorMacros.Concat(new[] { "$$com.sysprogs.stm8.stp_crts$$" }).ToArray();
            result.AdditionalSystemVars = (result.AdditionalSystemVars ?? new SysVarEntry[0]).Concat(new[] 
            { 
                new SysVarEntry { Key = "com.sysprogs.stm8.devpath", Value = MakeRelativePath("") },
            }).ToArray();

            if (result.ConfigurableProperties != null)
                throw new Exception("Support merging of properties");

            result.ConfigurableProperties = new PropertyList
            {
                PropertyGroups = new List<PropertyGroup>
                {
                    new PropertyGroup
                    {
                        Properties = new List<PropertyEntry>
                        {
                            new PropertyEntry.Boolean
                            {
                                Name = "Include CRT startup files",
                                UniqueID= "com.sysprogs.stm8.stp_crts",
                                ValueForTrue = "__STP_CRTS__",
                                ValueForFalse = "",
                                DefaultValue = true
                            }
                        }
                    }
                }
            };

            result.CompilationFlags.LinkerScript = "$$SYS:BSP_ROOT$$/Devices/" + MakeRelativePath(".lkf");
            return result;
        }
    }

    class STM8FamilyBuilder : MCUFamilyBuilder
    {
        public STM8FamilyBuilder(BSPBuilder bspBuilder, FamilyDefinition definition)
            : base(bspBuilder, definition)
        {
        }

        public string LocateDefaultConfigFile()
        {
            var driverDir = BSP.ExpandVariables(Definition.AdditionalFrameworks[0].CopyJobs[0].SourceFolder);

            var dir = Path.Combine(driverDir, @"..\..\Project");
            if (!Directory.Exists(dir))
                dir = Path.Combine(driverDir, @"..\..\Projects");
            dir = Directory.GetDirectories(dir, "*_Examples")[0];
            dir = Path.GetFullPath(Path.Combine(dir, "GPIO"));

            var dirs = Directory.GetDirectories(dir);
            if (dirs.Length == 1)
                dir = dirs[0];
            else
                dir = dirs.Where(f => Path.GetFileName(f).Contains("Polling")).First();

            return Directory.GetFiles(dir, "*_conf.h")[0];
        }

        public override MCUFamily GenerateFamilyObject(CoreSpecificFlags flagsToGenerate, bool allowExcludingStartupFiles = false)
        {
            var result = base.GenerateFamilyObject(flagsToGenerate, allowExcludingStartupFiles);
            var cfgFile = LocateDefaultConfigFile();
            var configDir = Path.Combine(BSP.Directories.OutputDir, "Devices");
            Directory.CreateDirectory(configDir);
            var targetCfgFile = Path.Combine(configDir, Path.GetFileName(cfgFile));
            File.Copy(cfgFile, targetCfgFile);
            File.SetAttributes(targetCfgFile, File.GetAttributes(targetCfgFile) & ~FileAttributes.ReadOnly);

            var sdkName = Path.GetFileNameWithoutExtension(cfgFile);
            if (!sdkName.EndsWith("_conf"))
                throw new Exception("Unexpected SDK conf file: " + cfgFile);
            sdkName = sdkName.Substring(0, sdkName.Length - 5);

            result.AdditionalSystemVars = (result.AdditionalSystemVars ?? new  SysVarEntry[0]).Concat(new[] { new SysVarEntry { Key = "com.sysprogs.stm8.sdk_name", Value = sdkName } }).ToArray();

            return result;
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: stm8_bsp_generator.exe <STM8 SDK directory>");

            const string CXSTM8Dir = @"C:\Program Files (x86)\COSMIC\FSE_Compilers\CXSTM8";

            using (var bspBuilder = new STM8BSPBuilder(BSPDirectories.MakeDefault(args)))
            {
                bool generateLinkerScripts = args.Contains("/test");

                var devices = CXSTM8DefinitionParser.ParseMCUs(CXSTM8Dir).Select(dev => new STM8MCUBuilder(dev)).ToArray();

                List<MCUFamilyBuilder> allFamilies = new List<MCUFamilyBuilder>();

                foreach (var fn in Directory.GetFiles(bspBuilder.Directories.RulesDir, "*.xml"))
                {
                    MCUFamilyBuilder famBuilder = new STM8FamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(fn));
                    allFamilies.Add(famBuilder);
                }

                List<MCUFamily> familyDefinitions = new List<MCUFamily>();
                List<MCU> mcuDefinitions = new List<MCU>();
                List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();

                var rejects = BSPGeneratorTools.AssignMCUsToFamilies(devices, allFamilies);

                foreach (var fam in allFamilies)
                {
                    familyDefinitions.Add(fam.GenerateFamilyObject(MCUFamilyBuilder.CoreSpecificFlags.None, false));

                    foreach (var mcu in fam.MCUs)
                        mcuDefinitions.Add(mcu.GenerateDefinition(fam, bspBuilder, false, true));

                    foreach (var fw in fam.GenerateFrameworkDefinitions())
                        frameworks.Add(fw);
                }

                if (generateLinkerScripts)
                {
                    foreach (var dev in devices)
                        dev.Definition.GenerateScriptsAndVectors(Path.Combine(bspBuilder.Directories.OutputDir, "Devices"));
                }

                bspBuilder.PatchDriverFiles();

                var sampleDir = Path.Combine(bspBuilder.Directories.OutputDir, "Samples");
                PathTools.CopyDirectoryRecursive(Path.Combine(bspBuilder.Directories.RulesDir, "Samples"), sampleDir);

                BoardSupportPackage bsp = new BoardSupportPackage
                {
                    PackageID = "com.sysprogs.stm8",
                    PackageDescription = "STM8 Devices",
                    GNUTargetID = "stm8",
                    GeneratedMakFileName = "stm8.mak",
                    MCUFamilies = familyDefinitions.ToArray(),
                    SupportedMCUs = mcuDefinitions.ToArray(),
                    Frameworks = frameworks.ToArray(),
                    Examples = Directory.GetDirectories(sampleDir).Select(s => @"Samples\" + s).ToArray(),
                    FileConditions = bspBuilder.MatchedFileConditions.Values.ToArray(),
                    PackageVersion = "2022.01",
                };

                bspBuilder.ValidateBSP(bsp);
                bspBuilder.Save(bsp, !generateLinkerScripts, false);
            }
        }
    }
}
