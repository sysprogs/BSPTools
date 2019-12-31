using BSPEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace msp430
{
    class Program
    {
        /*
         * Instructions for releasing a new msp430 toolchain:
         * 
         * 1. Download the toolchain and support files from TI.
         * 2. Copy support files into the 'include' subdirectory.
         * 3. Copy Toolchain.xml, IntelliSense.props and Toolchain.props files from an earlier release.
         * 4. Copy msp430-bsp subfolder from the older release.
         * 5. Copy make.exe, mspdebug.exe, msp430-gdbproxy.exe and *.dll from the older release.
         * 6. Download the latest msp430.dll and copy it to the bin directory.
         * 7. Run this tool to update the BSP based on the devices supported by the new toolchain.
         * 
         */
        static void Main(string[] args)
        {
            if (args.Length < 2)
                throw new Exception("Usage: msp430.exe <toolchain directory> <CCS directory>");

            GenerateBSP(args[0], args[1]);
        }

        static void GenerateBSP(string toolchainDir, string ccsDir)
        {
            string[] keys = null;

            string bspDir = toolchainDir + @"\msp430-bsp";

            Dictionary<string, Dictionary<string, string>> tiMCUs = new Dictionary<string, Dictionary<string, string>>(StringComparer.CurrentCultureIgnoreCase);

            foreach (var line in File.ReadAllLines(@"..\..\msp430.csv"))
            {
                string[] cells = line.Split(';');
                if (keys == null)
                {
                    keys = cells;
                    continue;
                }

                Dictionary<string, string> entry = new Dictionary<string, string>();

                for (int i = 0; i < cells.Length; i++)
                    entry[keys[i]] = cells[i];

                tiMCUs[cells[0]] = entry;
                int idx = cells[0].IndexOf('-');
                if (idx != -1)
                    tiMCUs[cells[0].Substring(0, idx)] = entry;
            }

            Regex rgLen = new Regex(".*LENGTH = 0x([0-9a-fA-F]{4}).*");
            Regex rgOrigin = new Regex(".*ORIGIN = 0x([0-9a-fA-F]{4}).*");
            Regex rgPeriph = new Regex("__([^ ]+) = (0x[a-fA-F0-9]+);");

            List<string> families = new List<string>();
            List<MCU> MCUs = new List<MCU>();
            List<MCUFamily> famList = new List<MCUFamily>();

            Directory.CreateDirectory(bspDir);
            Directory.CreateDirectory(bspDir + "\\devices");

            XmlSerializer regSer = new XmlSerializer(typeof(MCUDefinition));

            string[] files = Directory.GetFiles(Path.Combine(toolchainDir, "include"), "*.h");

            for (int i = 0; i <files.Length; i++)
            {
                string file = files[i];

                var proc = new Process();
                string mcuName = Path.GetFileNameWithoutExtension(file).ToLower();

                proc.StartInfo.FileName = toolchainDir + @"\bin\msp430-elf-gcc.exe";
                proc.StartInfo.Arguments = $"-I. -E {mcuName}.h -o - -mmcu={mcuName}";
                proc.StartInfo.WorkingDirectory = Path.Combine(toolchainDir, "include");
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardOutput = true;

                proc.Start();
                List<string> lines = new List<string>();
                for (; ; )
                {
                    var line = proc.StandardOutput.ReadLine();
                    if (line == null)
                        break;
                    lines.Add(line);
                }

                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    continue;

                List<HardwareRegister> regs = new List<HardwareRegister>();

                MCU mcu = new MCU();
                mcu.ID = mcuName;
                mcu.CompilationFlags.COMMONFLAGS = "-mmcu=" + mcuName;

                string ld = Path.ChangeExtension(file, ".ld");
                if (!File.Exists(ld))
                    continue;
                foreach (var line in File.ReadAllLines(ld))
                {
                    if (line.StartsWith("  RAM"))
                    {
                        var m = rgLen.Match(line);
                        mcu.RAMSize = int.Parse(m.Groups[1].ToString(), System.Globalization.NumberStyles.HexNumber);
                        m = rgOrigin.Match(line);
                        mcu.RAMBase = uint.Parse(m.Groups[1].ToString(), System.Globalization.NumberStyles.HexNumber);
                    }
                    if (line.StartsWith("  ROM"))
                    {
                        var m = rgLen.Match(line);
                        mcu.FLASHSize = int.Parse(m.Groups[1].ToString(), System.Globalization.NumberStyles.HexNumber);
                        m = rgOrigin.Match(line);
                        mcu.FLASHBase = uint.Parse(m.Groups[1].ToString(), System.Globalization.NumberStyles.HexNumber);
                    }
                }

                if (mcu.RAMSize == 0)
                    throw new Exception("RAM size cannot be 0");


                foreach (var line in lines)
                {
                    Regex rgRegister = new Regex("extern volatile (.*) ([^ ]+) __asm__\\(\"([^\"]+)\"\\)");

                    var m = rgRegister.Match(line);
                    if (!m.Success)
                    {
                        if (line.Contains("extern") && line.Contains("__asm__"))
                            throw new Exception("Suspicious line");
                        continue;
                    }

                    string type = m.Groups[1].Value;
                    string name = m.Groups[2].Value;
                    if (name.EndsWith("_H") || name.EndsWith("_L"))
                        continue;
                    if (!m.Groups[3].Value.StartsWith("0x"))
                        throw new Exception("Invalid addr for " + name);
                    ulong addr = ulong.Parse(m.Groups[3].Value.Substring(2), System.Globalization.NumberStyles.HexNumber);

                    HardwareRegister reg = new HardwareRegister();
                    // TODO: the registers are not all 8 bits
                    // According to some datasheets (not all were checked):
                    // 01FFh to 0100h -> 16 bits
                    // 0FFh to 010h -> 8bits
                    // 0Fh to 00h -> 8-bit SFR (special function register)
                    if (type == "unsigned char")
                        reg.SizeInBits = 8;
                    else if (type == "unsigned int")
                        reg.SizeInBits = 16;
                    else if (type == "unsigned long int")
                        reg.SizeInBits = 32;
                    else
                        throw new Exception("Unknown type");

                    reg.Name = name;
                    reg.Address = m.Groups[3].Value;
                    regs.Add(reg);
                }

                string family = "Other";

                Dictionary<string, string> info;
                if (tiMCUs.TryGetValue(mcu.ID, out info))
                    family = info["Description"];

                int idx = families.IndexOf(family);
                if (idx == -1)
                {
                    idx = families.Count;
                    families.Add(family);
                    famList.Add(new MCUFamily { ID = "fam_" + idx, UserFriendlyName = family, CompilationFlags = null });
                }

                mcu.FamilyID = "fam_" + idx.ToString();
                mcu.MCUDefinitionFile = "devices\\" + mcu.ID + ".xml";
                mcu.HierarchicalPath = family;
                MCUs.Add(mcu);


                MCUDefinition desc = new MCUDefinition { MCUName = mcu.ID, RegisterSets = new HardwareRegisterSet[] { new HardwareRegisterSet { Registers = regs.ToArray() } } };    //, Specs = specs };
                AdjustHardwareRegisters(ccsDir, mcu.ID, ref desc.RegisterSets);

                using (var fs = File.Create(bspDir + "\\" + mcu.MCUDefinitionFile + ".gz"))
                using (var gs = new GZipStream(fs, CompressionMode.Compress))
                    regSer.Serialize(gs, desc);


                Console.WriteLine($"Processed {mcuName} ({i}/{files.Length}) [{i * 100 / files.Length}%]");
            }


            //Build the XML file
            BoardSupportPackage bsp = new BoardSupportPackage { GNUTargetID = "msp430", PackageID = "com.sysprogs.msp430.core", PackageDescription = "MSP430 MCUs" };
            bsp.SupportedMCUs = MCUs.ToArray();
            bsp.MCUFamilies = famList.ToArray();
            bsp.DebugMethodPackages = new string[] { "debuggers\\core", "debuggers\\mspdebug" };


            bsp.Examples = new string[] { "Samples\\LEDBlink" };

#if BSP_ADDITIONAL_GCC_FLAGS
            bsp.AdditionalGCCFlags = new PropertyList
            {
                PropertyGroups = new PropertyGroup[] { new PropertyGroup{ 
                    Name = "MSP430 Options",
                    Properties = new PropertyEntry[]{
                        new PropertyEntry.Boolean{
                            Name = "Disable watchdog on startup",
                            Description = "Link the crt0 modules that disable the watchdog on startup",
                            UniqueID = "com.sysprogs.msp430.mdisable-watchdog",
                            ValueForTrue = "-mdisable-watchdog",
                            ValueForFalse = "",
                        },
                        new PropertyEntry.Boolean{
                            Name = "Enable libcalls for shifts",
                            Description = "Use library routines for non-constant shifts",
                            UniqueID = "com.sysprogs.msp430.menable-libcall-shift",
                            ValueForTrue = "-menable-libcall-shift",
                            ValueForFalse = "",
                        },
                        new PropertyEntry.Boolean{
                            Name = "Inline hardware multiplication",
                            Description = "Issue inline multiplication code for 32-bit integers",
                            UniqueID = "com.sysprogs.msp430.minline-hwmul",
                            ValueForTrue = "-minline-hwmul",
                            ValueForFalse = "",
                        },
                        new PropertyEntry.Enumerated{
                            Name = "Interrupt vector count",
                            Description = "Specify number of interrupt vectors on chip:",
                            UniqueID = "com.sysprogs.msp430.mivcnt",
                            GNUPrefix = "-mivcnt=",
                            SuggestionList = new PropertyEntry.Enumerated.Suggestion[]
                            {
                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "", UserFriendlyName = "(default)"},
                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "16"},
                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "32"},
                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "64"},
                            },
                            AllowFreeEntry = true,
                        },
                        new PropertyEntry.Enumerated{
                            Name = "Hardware multiplier",
                            Description = "Define available hardware multiplier",
                            UniqueID = "com.sysprogs.msp430.mmpy",
                            GNUPrefix = "-mmpy=",
                            SuggestionList = new PropertyEntry.Enumerated.Suggestion[]
                            {
                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "", UserFriendlyName = "(default)"},
                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "16"},
                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "16se"},
                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "32"},
                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "32dw"},
                            }
                        },
                        new PropertyEntry.Boolean{
                            Name = "No hardware multiplication in ISRs",
                            Description = "Assume interrupt routine does not do hardware multiplication",
                            UniqueID = "com.sysprogs.msp430.noint-hwmul",
                            ValueForTrue = "-noint-hwmul",
                            ValueForFalse = "",
                        },
                        new PropertyEntry.Boolean{
                            Name = "Prologue space optimization",
                            Description = "Use subroutine call for function prologue/epilogue when possible",
                            UniqueID = "com.sysprogs.msp430.msave-prologue",
                            ValueForTrue = "-msave-prologue",
                            ValueForFalse = "",
                        },
                    }
                }}
            };
#endif

            XmlSerializer ser = new XmlSerializer(typeof(BoardSupportPackage), PropertyEntry.EntryTypes);
            using (var fs = File.Create(bspDir + "\\BSP.xml"))
                ser.Serialize(fs, bsp);

            //mcuSelector1.Reset();
            var lBsp = LoadedBSP.Load(new BSPSummary(bspDir), null);
            //mcuSelector1.AddBSP(lBsp);
            //embeddedDebugSettingsControl1.Reset();
            //embeddedDebugSettingsControl1.AddDebugMethods(lBsp.KnownDebugMethods);
        }

        static void AdjustHardwareRegisters(string ccsDir, string mcuName, ref HardwareRegisterSet[] hardwareRegisterSets)
        {
            string dir = ccsDir + @"\ccs_base\common\targetdb\devices";
            if (!Directory.Exists(dir))
                throw new Exception("Missing " + dir);

            string tiDefinitionFile = dir + "\\" + mcuName + ".xml";
            if (!File.Exists(tiDefinitionFile))
                return;

            XmlDocument doc = new XmlDocument();
            doc.Load(tiDefinitionFile);

            Dictionary<string, ulong> oldRegisters = new Dictionary<string, ulong>();
            foreach (var set in hardwareRegisterSets)
                foreach (var reg in set.Registers)
                {
                    string name = reg.Name;
                    if (name.EndsWith("_H"))
                        continue;
                    else if (name.EndsWith("_L"))
                        name = name.Substring(0, name.Length - 2);
                    oldRegisters[name] = ParseAddr(reg.Address);
                }

            List<HardwareRegisterSet> newRegisterSets = new List<HardwareRegisterSet>();

            foreach (XmlNode node in doc.SelectNodes("device/cpu/instance"))
            {
                XmlElement el = node as XmlElement;
                if (el == null)
                    continue;

                string peripheralDefinition = Path.Combine(Path.GetDirectoryName(tiDefinitionFile), el.Attributes["href"].Value);
                if (!File.Exists(peripheralDefinition))
                    throw new NotSupportedException();

                string addr = el.Attributes["baseaddr"].Value;
                if (addr != "0x0000")
                    throw new NotSupportedException();


                if (peripheralDefinition.EndsWith("_NotVisible.xml"))
                    continue;

                XmlDocument xmlModule = new XmlDocument();
                xmlModule.Load(peripheralDefinition);

                var newSet = new HardwareRegisterSet { UserFriendlyName = xmlModule.SelectSingleNode("module").Attributes["description"].Value };
                newRegisterSets.Add(newSet);

                List<HardwareRegister> newRegs = new List<HardwareRegister>();

                foreach (XmlElement reg in xmlModule.SelectNodes("module/register"))
                {
                    string registerID = reg.Attributes["id"].Value;
                    ulong lAddr = ParseAddr(reg.Attributes["offset"].Value.Trim());
                    ulong oldAddr;
                    int sizeInBits = int.Parse(reg.Attributes["width"].Value);
                    if (oldRegisters.TryGetValue(registerID, out oldAddr))
                    {
                        if (oldAddr != lAddr)
                            Debugger.Log(0, "Warning", "Address mismatch for " + registerID + "\n");
                        oldRegisters[registerID] = ulong.MaxValue;
                    }

                    HardwareRegister newReg = new HardwareRegister { Name = registerID, Address = string.Format("0x{0:x}", lAddr), SizeInBits = sizeInBits };

                    List<HardwareSubRegister> subRegs = new List<HardwareSubRegister>();
                    foreach (XmlElement field in reg.SelectNodes("bitfield"))
                    {
                        string fieldID = field.Attributes["id"].Value;
                        int start = int.Parse(field.Attributes["begin"].Value);
                        int end = int.Parse(field.Attributes["end"].Value);
                        int width = int.Parse(field.Attributes["width"].Value);
                        if (start == (end + width - 1))
                        {
                            int tmp = start;
                            start = end;
                            end = tmp;
                        }

                        if (end != (start + width - 1))
                            throw new NotSupportedException();
                        var subReg = new HardwareSubRegister { Name = fieldID, FirstBit = start, SizeInBits = width };

                        KnownSubRegisterValue[] subValues = null;
                        bool bad = false;
                        int bitenumValuesChecked = 0;

                        foreach (XmlElement val in field.SelectNodes("bitenum"))
                        {
                            if (subValues == null)
                            {
                                subValues = new KnownSubRegisterValue[1 << width];
                            }

                            string valName = val.Attributes["id"].Value;
                            int value = int.Parse(val.Attributes["value"].Value);
                            if (value >= subValues.Length)
                                bad = true;
                            else
                                subValues[value] = new KnownSubRegisterValue { Name = valName };
                            bitenumValuesChecked++;
                        }

                        if (bad)
                        {
                            subValues = null;

                            if (bitenumValuesChecked == (1 << width))
                            {
                                //There's a typo in the XML files. Sometimes the 'value' for a divider is taken from the divider value and not the bitfield value. Let's try fixing it!
                                subValues = new KnownSubRegisterValue[1 << width];
                                int idx = 0;
                                int lastDividerValue = 0;
                                foreach (XmlElement val in field.SelectNodes("bitenum"))
                                {
                                    string valName = val.Attributes["id"].Value;

                                    int tmp = valName.LastIndexOf('_');
                                    int dividerValue = int.Parse(valName.Substring(tmp + 1));
                                    if (dividerValue < lastDividerValue)
                                        throw new NotSupportedException();  //If the values are listed in the ascending order, we can assume that they are listed in the bitfield value order.

                                    lastDividerValue = dividerValue;

                                    subValues[idx++] = new KnownSubRegisterValue { Name = valName };
                                }
                            }
                        }

                        if (subValues != null)
                        {
                            foreach (var v in subValues)
                                if (v == null)
                                {
                                    subValues = null;
                                    break;
                                }
                        }
                        subReg.KnownValues = subValues;

                        subRegs.Add(subReg);
                    }


                    if (subRegs.Count > 0)
                        newReg.SubRegisters = subRegs.ToArray();

                    newRegs.Add(newReg);
                }

                newSet.Registers = newRegs.ToArray();
            }

            foreach (var kv in oldRegisters)
                if (kv.Value != ulong.MaxValue)
                    Debugger.Log(0, "", "TI XML does not list " + kv.Key + "\n");

            hardwareRegisterSets = newRegisterSets.ToArray();
        }


        public static UInt64 ParseAddr(string str)
        {
            if (str == null)
            {
                throw new ArgumentException();
            }
            int idx = str.IndexOf(' ');
            if (idx != -1)
                str = str.Substring(0, idx);

            if (str.StartsWith("0x"))
                return UInt64.Parse(str.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier, null);
            else
                return UInt64.Parse(str);
        }
    }
}
