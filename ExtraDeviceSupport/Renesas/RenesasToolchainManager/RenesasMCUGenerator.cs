using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace RenesasToolchainManager
{
    class RenesasMCUGenerator
    {
        struct RenamedFile
        {
            public string SourceFileFormat;
            public string TargetFileName;
            public bool MakeWeakFunctions;

            public RenamedFile(string sourceFileFormat, string targetFileName, bool makeWeakFunctions = false)
            {
                SourceFileFormat = sourceFileFormat;
                TargetFileName = targetFileName;
                MakeWeakFunctions = makeWeakFunctions;
            }
        }

        static RenamedFile[] RenamedFiles = new[]
        {
            new RenamedFile(@"IntPRG\{0}.c", "inthandler.c", true),
            new RenamedFile(@"iodefine\{0}.h", "iodefine.h"),
            new RenamedFile(@"iodefine_ext\{0}.h", "iodefine_ext.h"),
            new RenamedFile(@"vect\{0}.h", "interrupt_handlers.h"),
            new RenamedFile(@"vecttbl\{0}.c", "vects.c"),
        };

        public static MCU GenerateMCUDefinition(string bspDir, string linkerScript, string generatorResourceDir, string target, string debugComponentDir)
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

            List<string> headers = new List<string>();

            foreach (var rf in RenamedFiles)
            {
                string file = Path.Combine(generatorResourceDir, string.Format(rf.SourceFileFormat, mcuName));
                if (!File.Exists(file))
                {
                    Directory.Delete(copiedFilesDir, true);
                    return null;
                }

                var lines = File.ReadAllLines(file);
                if (rf.MakeWeakFunctions)
                {
                    Regex rgFunc = new Regex("void[ \t]+(INT_[a-zA-Z0-9_]+)[ \t]*\\(void\\)");
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var m = rgFunc.Match(lines[i]);
                        if (m.Success)
                        {
                            lines[i] = lines[i].Substring(0, m.Groups[1].Index) + "__attribute__((weak)) " + lines[i].Substring(m.Groups[1].Index);
                        }
                    }
                }

                File.WriteAllLines(Path.Combine(copiedFilesDir, rf.TargetFileName), lines);

                if (rf.TargetFileName.EndsWith(".h"))
                    headers.Add($"$$SYS:BSP_ROOT$$/DeviceFiles/{mcuName}/rf.TargetFileName");
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
                AdditionalSourcesRequiredForTesting = true,
                AdditionalHeaderFiles = headers.ToArray()
            };

            string peripheralFile = Path.Combine(debugComponentDir, "IoFiles", mcuName + ".sfrx");
            if (File.Exists(peripheralFile))
            {
                var doc = new XmlDocument();
                doc.Load(peripheralFile);

                MCUDefinition definition = new MCUDefinition
                {
                    MCUName = mcuName,
                    RegisterSets = doc.DocumentElement.SelectNodes("moduletable/module").OfType<XmlElement>().Select(TransformRegisterSet).Where(s => s != null).ToArray()
                };

                using (var fs = new FileStream(Path.Combine(bspDir, "DeviceDefinitions", mcuName + ".xml.gz"), FileMode.Create, FileAccess.Write))
                using (var gs = new GZipStream(fs, CompressionMode.Compress))
                    XmlTools.SaveObjectToStream(definition, gs);

                mcu.MCUDefinitionFile = $"DeviceDefinitions/{mcuName}.xml";
            }

            memorySizes.TryGetValue("ROM", out mcu.FLASHSize);
            memorySizes.TryGetValue("RAM", out mcu.RAMSize);
            return mcu;
        }

        static int TryParseAddress(string addrString)
        {
            if (addrString?.StartsWith("0x") != true || !int.TryParse(addrString.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var addr))
                return 0;
            return addr;
        }

        private static HardwareRegisterSet TransformRegisterSet(XmlElement el)
        {
            var name = el.GetAttribute("name");
            if (name == null)
                return null;

            return new HardwareRegisterSet
            {
                UserFriendlyName = name,
                Registers = el.SelectNodes("register").OfType<XmlElement>().SelectMany(r => new[] { r }.Concat(r.SelectNodes("register").OfType<XmlElement>())).Select(r =>
                 {
                     var regSize = r.GetAttribute("size");
                     var regAccess = r.GetAttribute("access");

                     var reg = new HardwareRegister { Name = r.GetAttribute("name"), Address = r.GetAttribute("address") };
                     if (reg.Name == null || reg.Address == null)
                         return null;

                     switch (regSize ?? "")
                     {
                         case "B":
                             reg.SizeInBits = 8;
                             break;
                         case "W":
                             reg.SizeInBits = 16;
                             break;
                         default:
                             return null;
                     }

                     switch (regAccess)
                     {
                         case "R":
                             reg.ReadOnly = true;
                             break;
                         case "RW":
                             reg.ReadOnly = false;
                             break;
                         default:
                             return null;
                     }

                     reg.SubRegisters = r.SelectNodes("bitfield").OfType<XmlElement>().Select(TransformSubregister).Where(sr => sr != null).ToArray();
                     return reg;
                 }).Where(r => r != null).ToArray()
            };
        }

        private static HardwareSubRegister TransformSubregister(XmlElement el)
        {
            string name = el.GetAttribute("name");
            if (!int.TryParse(el.GetAttribute("bit"), out int bit))
                return null;
            if (!int.TryParse(el.GetAttribute("bitlength"), out int bitlength))
                return null;

            return new HardwareSubRegister { FirstBit = bit, SizeInBits = bitlength, Name = name };
        }

        public static MCUFamily GenerateMCUFamilyDefinition(string target)
        {
            return new MCUFamily
            {
                ID = target,
                CompilationFlags = new ToolFlags
                {
                    PreprocessorMacros = new[] { "__GCC__", "$$com.sysprogs.renesas.cppapp$$" }
                },
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
                                },

                                new PropertyEntry.Boolean
                                {
                                    UniqueID = "com.sysprogs.renesas.cppapp",
                                    Name = "Project Contains C++ Sources",
                                    ValueForTrue = "CPPAPP"
                                }
                            }
                        }
                    }
                }
            };
        }

        public static EmbeddedFramework GenerateStartupFilesFramework(string target)
        {
            return new EmbeddedFramework
            {
                ID = "com.sysprogs.renesas.startupfiles",
                UserFriendlyName = "Default Startup/Interrupt Files",
                DefaultEnabled = true,
                AdditionalSourceFiles = RenamedFiles
                    .Where(rf => !rf.TargetFileName.EndsWith(".h"))
                    .Select(rf => $"$$SYS:BSP_ROOT$$/DeviceFiles/$$SYS:MCU_ID$$/{rf.TargetFileName}")
                    .Concat(new[]
                    {
                        "$$SYS:BSP_ROOT$$/start.S",
                        "$$SYS:BSP_ROOT$$/stubs.c",
                    })
                    .ToArray()
            };
        }
    }
}
