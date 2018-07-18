using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RenesasToolchainManager
{
    class RenesasMCUGenerator
    {
        struct RenamedFile
        {
            public string SourceFileFormat;
            public string TargetFileName;

            public RenamedFile(string sourceFileFormat, string targetFileName)
            {
                SourceFileFormat = sourceFileFormat;
                TargetFileName = targetFileName;
            }
        }

        static RenamedFile[] RenamedFiles = new[]
        {
            new RenamedFile(@"IntPRG\{0}.c", "inthandler.c"),
            new RenamedFile(@"iodefine\{0}.h", "iodefine.h"),
            new RenamedFile(@"iodefine_ext\{0}.h", "iodefine_ext.h"),
            new RenamedFile(@"vect\{0}.h", "interrupt_handlers.h"),
            new RenamedFile(@"vecttbl\{0}.c", "vects.c"),
        };

        public static MCU GenerateMCUDefinition(string bspDir, string linkerScript, string generatorResourceDir, string target)
        {
            string mcuName = Path.GetFileNameWithoutExtension(linkerScript).TrimStart('D');
            string copiedFilesDir = Path.Combine(bspDir, "DeviceFiles", mcuName);
            Directory.CreateDirectory(copiedFilesDir);
            File.Copy(linkerScript, Path.Combine(bspDir, "LinkerScripts", Path.GetFileName(linkerScript)), true);
            Dictionary<string, int> memorySizes = new Dictionary<string, int>();

            Regex rgMemory = new Regex("^[ \t]+([^ ]+)[ \t]+:[ \t]+ORIGIN[ \t]*=[ \t]*0x([0-9a-fA-F]+),[ \t]*LENGTH[ \t]*=[ \t]*([0-9]+)");
            foreach (var line in File.ReadAllLines(linkerScript))
            {
                var m = rgMemory.Match(line);
                if (m.Success)
                    memorySizes[m.Groups[1].Value] = int.Parse(m.Groups[3].Value);
            }

            List<string> sources = new List<string>(), headers = new List<string>();

            foreach (var rf in RenamedFiles)
            {
                string file = Path.Combine(generatorResourceDir, string.Format(rf.SourceFileFormat, mcuName));
                if (File.Exists(file))
                {
                    File.Copy(file, Path.Combine(copiedFilesDir, rf.TargetFileName));
                    if (file.EndsWith(".h", StringComparison.InvariantCultureIgnoreCase))
                        headers.Add($"$$SYS:BSP_ROOT$$/DeviceFiles/{mcuName}/{rf.TargetFileName}");
                    else
                        sources.Add($"$$SYS:BSP_ROOT$$/DeviceFiles/{mcuName}/{rf.TargetFileName}");
                }
            }

            var mcu = new MCU
            {
                ID = mcuName,
                FamilyID = target,
                CompilationFlags = new ToolFlags
                {
                    IncludeDirectories = new[] { $"$$SYS:BSP_ROOT$$/DeviceFiles/{mcuName}" },
                    LinkerScript = "$$SYS:BSP_ROOT$$/LinkerScripts/" + Path.GetFileName(linkerScript),
                    LDFLAGS = "-nostartfiles -Wl,-e_PowerON_Reset",
                    COMMONFLAGS = "$$com.sysprogs.renesas.doubles$$ $$com.sysprogs.renesas.core$$",
                },
                AdditionalSourceFiles = sources.ToArray(),
                AdditionalHeaderFiles = headers.ToArray(),
                AdditionalSourcesRequiredForTesting = true,
            };

            memorySizes.TryGetValue("ROM", out mcu.FLASHSize);
            memorySizes.TryGetValue("RAM", out mcu.RAMSize);
            return mcu;
        }

        public static MCUFamily GenerateMCUFamilyDefinition(string target)
        {
            return new MCUFamily
            {
                ID = target,
                AdditionalSourceFiles = new[] { "$$SYS:BSP_ROOT$$/start.S" },
                ConfigurableProperties = new PropertyList
                {
                    PropertyGroups = new List<PropertyGroup>
                    {
                        new PropertyGroup
                        {
                            Properties = new List<PropertyEntry>
                            {
                                new PropertyEntry.Enumerated
                                {
                                    UniqueID = "com.sysprogs.renesas.doubles",
                                    Name = "Size of 'double' type",
                                    SuggestionList = new[]
                                    {
                                        new PropertyEntry.Enumerated.Suggestion
                                        {
                                            InternalValue = "",
                                            UserFriendlyName = "Unspecified"
                                        },
                                        new PropertyEntry.Enumerated.Suggestion
                                        {
                                            InternalValue = "-m32bit-doubles",
                                            UserFriendlyName = "32 bits"
                                        },
                                        new PropertyEntry.Enumerated.Suggestion
                                        {
                                            InternalValue = "-m64bit-doubles",
                                            UserFriendlyName = "64 bits"
                                        }
                                    },
                                    DefaultEntryIndex = 2
                                },

                                new PropertyEntry.Enumerated
                                {
                                    UniqueID = "com.sysprogs.renesas.core",
                                    Name = "RL78 Core Type",
                                    SuggestionList = new[]
                                    {
                                        new PropertyEntry.Enumerated.Suggestion
                                        {
                                            InternalValue = "",
                                            UserFriendlyName = "Unspecified"
                                        },
                                        new PropertyEntry.Enumerated.Suggestion
                                        {
                                            InternalValue = "-mg10",
                                            UserFriendlyName = "G10"
                                        },
                                        new PropertyEntry.Enumerated.Suggestion
                                        {
                                            InternalValue = "-mg13",
                                            UserFriendlyName = "G13"
                                        },
                                        new PropertyEntry.Enumerated.Suggestion
                                        {
                                            InternalValue = "-mg14",
                                            UserFriendlyName = "G14"
                                        },
                                    },
                                    DefaultEntryIndex = 2
                                }
                            }
                        }
                    }
                }
            };
        }
    }
}
