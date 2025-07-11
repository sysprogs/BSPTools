using BSPEngine;
using BSPGenerationTools;
using LinkerScriptGenerator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace mspm0_bsp_generator
{
    internal class Program
    {
        public readonly struct QualifiedStartupFile
        {
            public readonly string Prefix;
            public readonly string FileName;
            public QualifiedStartupFile(string prefix, string fileName)
            {
                Prefix = prefix;
                FileName = fileName;
            }

            public override string ToString() => $"{Prefix} => {FileName}";
        }

        class StartupFileCollection
        {
            public readonly List<QualifiedStartupFile> Files = new List<QualifiedStartupFile>();
            public readonly string StartupFilesDir;

            public StartupFileCollection(string baseDir, string relDir)
            {
                StartupFilesDir = relDir.Replace('\\', '/');

                if (!Directory.Exists(Path.Combine(baseDir, relDir)))
                    throw new Exception($"Startup files directory not found: {relDir}");

                var cFiles = Directory.GetFiles(Path.Combine(baseDir, relDir), "*.c");
                foreach (var file in cFiles)
                {
                    var fileName = Path.GetFileName(file);

                    if (!fileName.EndsWith("_gcc.c"))
                        throw new Exception($"Startup file '{fileName}' does not end with '_gcc.c'");

                    if (fileName.StartsWith("startup_mspm0"))
                    {
                        var remainder = fileName.Substring("startup_mspm0".Length,
                            fileName.Length - "startup_mspm0".Length - "_gcc.c".Length);

                        if (string.IsNullOrWhiteSpace(remainder))
                            throw new Exception($"Startup file '{fileName}' has unexpected format after trimming");

                        var parts = remainder.Split('_');
                        foreach (var part in parts)
                            Files.Add(new QualifiedStartupFile("mspm0" + part.TrimEnd('x'), fileName));
                    }
                    else if (fileName.StartsWith("startup_msps0"))
                    {
                        var remainder = fileName.Substring("startup_msps0".Length,
                            fileName.Length - "startup_msps0".Length - "_gcc.c".Length);

                        if (string.IsNullOrWhiteSpace(remainder))
                            throw new Exception($"Startup file '{fileName}' has unexpected format after trimming");

                        Files.Add(new QualifiedStartupFile("msps0" + remainder.TrimEnd('x'), fileName));
                    }
                    else
                    {
                        throw new Exception($"Startup file '{fileName}' does not start with 'startup_mspm0' or 'startup_msps0'");
                    }
                }

                Files.Sort((x, y) => -x.Prefix.Length.CompareTo(y.Prefix.Length));
            }

            public QualifiedStartupFile? FindBestMatch(string mcuName)
            {
                foreach (var file in Files)
                {
                    if (mcuName.StartsWith(file.Prefix, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
                return null;
            }
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: mspm0_bsp_generator.exe <MSPM0 SW package directory>");

            bool noPeripheralRegisters = args.Contains("/noperiph");
            var familyDefinitions = new List<MCUFamily>();
            var mcuDefinitions = new List<MCU>();
            var frameworks = new List<EmbeddedFramework>();
            var exampleDirs = new List<string>();

            using (var bspBuilder = new MspM0Builder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules", @"..\..\log")))
            {
                var devices = LoadDevicesFromLinkerScripts(
                    bspBuilder.Directories.InputDir,
                    "source/ti/devices/msp/m0p/linker_files/gcc"
                );

                var startupDir = @"source\ti\devices\msp\m0p\startup_system_files\gcc";
                var startupFiles = new StartupFileCollection(bspBuilder.Directories.InputDir, startupDir);

                var commonFamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\Family.xml"));
                var flags = new ToolFlags();
                var projectFiles = new List<string>();
                commonFamily.CopyFamilyFiles(ref flags, projectFiles);

                foreach (var mcu in devices)
                {
                    var startupFile = startupFiles.FindBestMatch(mcu.Name) ?? throw new Exception("Missing startup file");
                    mcu.StartupFile = $"$$SYS:BSP_ROOT$$/{startupFiles.StartupFilesDir}/{startupFile.FileName}";


                    mcuDefinitions.Add(mcu.GenerateDefinition(commonFamily, bspBuilder, !noPeripheralRegisters));
                    commonFamily.MCUs.Add(mcu);
                }

                familyDefinitions.Add(commonFamily.GenerateFamilyObject(true));


                foreach (var fw in commonFamily.GenerateFrameworkDefinitions())
                {
                    frameworks.Add(fw);
                }

                foreach (var sample in commonFamily.CopySamples())
                {
                    exampleDirs.Add(sample.RelativePath);
                }

                BoardSupportPackage bsp = new BoardSupportPackage
                {
                    PackageID = "com.sysprogs.arm.ti.mspm0",
                    PackageDescription = "TI MSPM0 Devices",
                    GNUTargetID = "arm-eabi",
                    GeneratedMakFileName = "mspm0.mak",
                    MCUFamilies = familyDefinitions.ToArray(),
                    SupportedMCUs = mcuDefinitions.ToArray(),
                    Frameworks = frameworks.ToArray(),
                    Examples = exampleDirs.ToArray(),
                    PackageVersion = "2.05.01"
                };

                bspBuilder.Save(bsp, true);
            }
        }

        static List<MCUBuilder> LoadDevicesFromLinkerScripts(string baseDir, string linkerScriptDir)
        {
            var devices = new List<MCUBuilder>();
            var fullDir = Path.Combine(baseDir, linkerScriptDir.Replace('/', Path.DirectorySeparatorChar));
            foreach (var file in Directory.GetFiles(fullDir, "msp*.lds"))
            {
                var memories = LinkerScriptTools.ScanLinkerScriptForMemories(file);
                var flash = memories.FirstOrDefault(m => m.Name.ToLower() == "flash");
                var ram = memories.FirstOrDefault(m => m.Name.ToLower().StartsWith("sram"));
                var fileName = Path.GetFileName(file);
                var device = new MCUBuilder
                {
                    Name = Path.GetFileNameWithoutExtension(file).ToUpper(),
                    FlashSize = (int)flash.Size,
                    RAMSize = (int)ram.Size,
                    Core = CortexCore.M0Plus,
                    LinkerScriptPath = "$$SYS:BSP_ROOT$$/" + linkerScriptDir + "/" + fileName,
                    AttachedMemoryLayout = new MemoryLayout
                    {
                        Memories = memories.Select(m => new Memory
                        {
                            Name = m.Name,
                            Type = m.Name.ToLower() == "flash" ? MemoryType.FLASH : 
                                     m.Name.ToLower().StartsWith("sram") ? MemoryType.RAM : MemoryType.Unknown,
                            Start = (uint)m.Address,
                            Size = (uint)m.Size
                        }).ToList()
                    }
                };

                devices.Add(device);
            }
            return devices;
        }

        class MspM0Builder : BSPBuilder
        {
            public MspM0Builder(BSPDirectories dirs)
                : base(dirs, linkerScriptLevel: 5)
            {
                ShortName = "MSPM0";
            }

            public override string GetMCUTypeMacro(MCUBuilder mcu)
            {
                return $"__{mcu.Name.ToUpper()}__";
            }

            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                throw new NotSupportedException();
            }

            public override MemoryLayoutAndSubstitutionRules GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                throw new NotSupportedException();
            }
        }
    }
}
