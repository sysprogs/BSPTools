using BSPEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace avr
{
    class Program
    {
        class DeviceInfo
        {
            public string Name;
            public string MacroName;

            public int RAMSTART, RAMSIZE, FLASHSIZE;

            public class ParsedRegister
            {
                public int Size, Addr;
                internal string Name;
                public string[] PinNames;

                public HardwareRegister ToBSPRegister(int arch)
                {
                    return new HardwareRegister
                    {
                        Name = Name,
                        Address = string.Format("0x{0:x2}", ((arch >= 100) ? 0 : 0x20) + Addr),
                        SizeInBits = Size,
                        SubRegisters = (PinNames == null) ? null : Enumerable.Range(0, PinNames.Length).Where(i => PinNames[i] != null).Select(i => new HardwareSubRegister { FirstBit = i, SizeInBits = 1, Name = PinNames[i] }).ToArray()
                    };
                }
            }

            public List<ParsedRegister> Registers = new List<ParsedRegister>();
            internal int Arch;

            public override string ToString()
            {
                return Name;
            }

            IEnumerable<string> ExpandIncludes(string file, Dictionary<string, bool> includedFiles = null)
            {
                if (includedFiles == null)
                    includedFiles = new Dictionary<string, bool>(StringComparer.InvariantCultureIgnoreCase);

                if (includedFiles.ContainsKey(file) || Path.GetFileName(file).ToLower() == "io.h")
                    yield break;
                else
                    includedFiles[file] = true;

                foreach (var line in File.ReadAllLines(file))
                {
                    Regex rgInclude = new Regex("#[ \t]*include[ \t]*\"(.*\\.h)\"");
                    Regex rgInclude2 = new Regex(@"#[ \t]*include[ \t]*<avr/(.*\.h)>");
                    var m = rgInclude.Match(line);
                    if (m.Success)
                        foreach (var sl in ExpandIncludes(Path.Combine(Path.GetDirectoryName(file), m.Groups[1].Value), includedFiles))
                            yield return sl;
                    else
                    {
                        m = rgInclude2.Match(line);
                        if (m.Success)
                            foreach (var sl in ExpandIncludes(Path.Combine(Path.GetDirectoryName(file), m.Groups[1].Value), includedFiles))
                                yield return sl;
                        else
                            yield return line;
                    }
                }
            }

            public void ParseHeaderFile(string file)
            {
                Regex rgFLASHRAM = new Regex(@"#define[ \t]+(RAMSIZE|RAMSTART|RAMEND|FLASHEND|INTERNAL_SRAM_START|INTERNAL_SRAM_SIZE|PROGMEM_SIZE)[ \t]+\(?([0-9a-fA-Fx]+)\)?[ \t]*($|/)");
                Regex rgFLASHRAM2 = new Regex(@"#define[ \t]+(FLASHEND)[ \t]+\(?([0-9a-fA-Fx]+) - 1\)?[ \t]*($|/)");
                Regex rgIO = new Regex(@"#define[ \t]+([^ ]+)[ \t]+_SFR_IO([0-9]+)\(([0-9a-fA-Fx]+)\)");
                Regex rgPin = new Regex(@"#define[ \t]+([^ ]+)[ \t]+([0-9]+)");
                var lines = File.ReadAllLines(file);

                ParsedRegister lastReg = null;

                foreach (var line in ExpandIncludes(file))
                {
                    var iom = rgIO.Match(line);
                    if (iom.Success)
                        Registers.Add(lastReg = new ParsedRegister { Name = iom.Groups[1].Value, Addr = ParseInt(iom.Groups[3].Value), Size = int.Parse(iom.Groups[2].Value) });
                    else if (lastReg != null)
                    {
                        var pm = rgPin.Match(line);
                        if (pm.Success)
                        {
                            int pin = int.Parse(pm.Groups[2].Value);
                            if (pin >= 0 && pin < lastReg.Size)
                            {
                                if (lastReg.PinNames == null)
                                    lastReg.PinNames = new string[lastReg.Size];
                                lastReg.PinNames[pin] = pm.Groups[1].Value;
                            }
                            else
                                lastReg = null;
                        }
                        else
                            lastReg = null;
                    }


                    var m = rgFLASHRAM.Match(line);
                    if (m.Success)
                    {
                        switch(m.Groups[1].Value)
                        {
                            case "RAMSTART":
                            case "INTERNAL_SRAM_START":
                                RAMSTART = ParseInt(m.Groups[2].Value);
                                break;
                            case "RAMSIZE":
                            case "INTERNAL_SRAM_SIZE":
                                RAMSIZE = ParseInt(m.Groups[2].Value);
                                break;
                            case "RAMEND":
                                RAMSIZE = ParseInt(m.Groups[2].Value) + 1;
                                break;
                            case "FLASHEND":
                                FLASHSIZE = ParseInt(m.Groups[2].Value) + 1;
                                break;
                            case "PROGMEM_SIZE":
                                FLASHSIZE = ParseInt(m.Groups[2].Value);
                                break;
                        }
                    }
                    else if ((m = rgFLASHRAM2.Match(line)).Success)
                    {
                        switch (m.Groups[1].Value)
                        {
                            case "FLASHEND":
                                FLASHSIZE = ParseInt(m.Groups[2].Value);
                                break;
                        }
                    }
                    
                }

                if (RAMSTART == 0 || RAMSIZE < 4 || FLASHSIZE < 4)
                    throw new Exception("Failed to detect FLASH or RAM parameters for " + file);
            }

            private int ParseInt(string value)
            {
                if (value.StartsWith("0x"))
                    return int.Parse(value.Substring(2), NumberStyles.HexNumber);
                else
                    return int.Parse(value);
            }

            public MCU ToBSPMCU()
            {
                return new MCU
                {
                    ID = Name.ToUpper(),
                    FamilyID = "avr",
                    FLASHSize = FLASHSIZE,
                    FLASHBase = 0,
                    RAMSize = RAMSIZE,
                    RAMBase = (uint)RAMSTART,
                    CompilationFlags = new ToolFlags
                    {
                        COMMONFLAGS = "-mmcu=" + this.Name
                    },
                    MCUDefinitionFile = $"DeviceDefinitions/{Name}.xml"
                };
            }

            public MCUDefinition ToDeviceDefinition()
            {
                if (Registers.Count == 0)
                    return null;

                return new MCUDefinition
                {
                    MCUName = Name.ToUpper(),
                    RegisterSets = new HardwareRegisterSet[]
                    {
                        new HardwareRegisterSet
                        {
                            UserFriendlyName = "AVR",
                            Registers = Registers.Select(r => r.ToBSPRegister(Arch)).ToArray()
                        }
                    }
                };
            }
        }

        static int DetectArch(string device, string toolchain)
        {
            Process proc = new Process();
            proc.StartInfo.FileName = "cmd.exe";
            string fn = Path.GetTempFileName();
            proc.StartInfo.Arguments = $"/c echo | avr-gcc.exe -mmcu={device} -dM -E - > \"{fn}\"";
            proc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            proc.StartInfo.WorkingDirectory = toolchain + @"\bin";
            proc.Start();
            proc.WaitForExit();
            try
            {
                foreach (var line in File.ReadAllLines(fn))
                {
                    if (line.Contains("__AVR_ARCH__"))
                    {
                        int idx = line.LastIndexOf(' ');
                        return int.Parse(line.Substring(idx + 1));
                    }
                }
                return 0;
            }
            finally
            {
                File.Delete(fn);
            }
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: avr_bsp_generator <toolchain dir>");

            Console.WriteLine("Building device list...");

            Dictionary<string, DeviceInfo> devices = new Dictionary<string, DeviceInfo>();
            int done = 0;


            //Build device list
            foreach (var subdir in Directory.GetDirectories(Path.Combine(args[0], @"lib\gcc\avr")))
            {
                var files = Directory.GetFiles(Path.Combine(subdir, "device-specs"), "specs-*");
                foreach(var fn in files)
                {
                    string macro = ExtractMacroName(fn);
                    if (macro == null)
                        continue;
                    var name = Path.GetFileName(fn).Substring(6);
                    devices[macro] = new DeviceInfo { Name = name, MacroName = macro, Arch = DetectArch(name, args[0]) };
                    Console.Write("\r[{0:d2}%]", (++done * 100) / files.Length);
                }
                break;
            }

            Console.WriteLine();
            Console.WriteLine("Extracting device information...");

            //Find header files for all devices
            Regex rgDefined = new Regex(@"defined[ \t]+\(?__AVR_([^()]+)__\)?");
            Regex rgInclude = new Regex(@"#[ \t]*include[ \t]*<avr/(.*\.h)>");

            var lines = File.ReadAllLines(Path.Combine(args[0], @"avr\include\avr\io.h"));
            done = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                var m1 = rgDefined.Match(lines[i]);
                if (m1.Success)
                {
                    var m2 = rgInclude.Match(lines[i + 1]);
                    if (m2.Success)
                    {
                        if (!devices.ContainsKey(m1.Groups[1].Value))
                        {
                            Console.WriteLine("Skipping unknown device: " + m1.Groups[1].Value);
                            continue;
                        }
                        devices[m1.Groups[1].Value].ParseHeaderFile(Path.Combine(args[0], @"avr\include\avr", m2.Groups[1].Value));
                        Console.Write("\r[{0:d2}%]", (++done * 100) / devices.Count);
                    }
                }
            }

            string outputDir = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "..", "..", "Output");
            Directory.CreateDirectory(outputDir);
            List<DeviceInfo> devList = new List<DeviceInfo>(devices.Values);
            devList.Sort((a, b) => a.Name.CompareTo(b.Name));
            Console.WriteLine("\nGenerating BSP...");
            GenerateBSP(devList, outputDir);
        }


        private static void GenerateBSP(List<DeviceInfo> devList, string outputDir)
        {
            BoardSupportPackage bsp = new BoardSupportPackage
            {
                GNUTargetID = "avr",
                PackageID = "com.sysprogs.avr.core",
                PackageDescription = "AVR MCUs",
                GeneratedMakFileName = "avr.mak",
                Examples = new string[]
                {
                    "Samples/LEDBlink"
                }
            };

            bsp.SupportedMCUs = devList.Select(d => d.ToBSPMCU()).ToArray();
            bsp.MCUFamilies = new MCUFamily[] { new MCUFamily{ ID = "avr" } };
            bsp.DebugMethodPackages = new string[] { "debuggers\\core", "debuggers\\avarice" };

            string defDir = Path.Combine(outputDir, "DeviceDefinitions");
            Directory.CreateDirectory(defDir);
            foreach (var dev in devList)
            {
                MCUDefinition d = dev.ToDeviceDefinition();
                if (d == null)
                {
                    Console.WriteLine("Warning: no register information found for " + dev.Name);
                    continue;
                }
                using (var fs = File.Create(Path.Combine(defDir, dev.Name + ".xml.gz")))
                using (var gs = new GZipStream(fs, CompressionMode.Compress))
                    XmlTools.SaveObjectToStream(d, gs);
            }

            XmlTools.SaveObject(bsp, Path.Combine(outputDir, "bsp.xml"));
        }

        private static string ExtractMacroName(string fn)
        {
            Regex rgDef = new Regex("-D__AVR_([^ ]+)__[ \t]+-D__AVR_DEVICE_NAME__=");
            foreach(var line in File.ReadAllLines(fn))
            {
                var m = rgDef.Match(line);
                if (m.Success)
                    return m.Groups[1].Value;
            }
            return null;
        }
    }
}
