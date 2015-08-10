using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Tiva_bsp_generator
{
    static class PeripheralRegisterGenerator
    {
        private static int REGISTERLENGTHINBITS = 32;

        private static string[] FILESWITHOUTREGISTERS = new string[] { "hw_fan.h", "hw_ints.h", "hw_types.h" };
        private static string[] REGISTERSWITHOUTSUBREGISTERS = new string[] { 
            "FLASH_FMPRE0", "FLASH_FMPRE1", "FLASH_FMPRE2", "FLASH_FMPRE3", "FLASH_FMPRE4", "FLASH_FMPRE5", "FLASH_FMPRE6", "FLASH_FMPRE7",
            "FLASH_FMPPE0", "FLASH_FMPPE1", "FLASH_FMPPE2", "FLASH_FMPPE3", "FLASH_FMPPE4", "FLASH_FMPPE5", "FLASH_FMPPE6", "FLASH_FMPPE7",
            "GPIO_DATA", "GPIO_DIR", "GPIO_IS", "GPIO_IBE", "GPIO_IEV", "GPIO_AFSEL", "GPIO_DR2R", "GPIO_DR4R", "GPIO_DR8R", "GPIO_ODR", "GPIO_PUR", "GPIO_PDR", "GPIO_SLR", "GPIO_DEN", "GPIO_CR", "GPIO_AMSEL", "GPIO_PCTL", "GPIO_ADCCTL", "GPIO_DMACTL"
        };
        private static string[] ADDRESSESWITHOUTTYPES = new string[] { "SRAM", "WATCHDOG", "GPIO_PORT_AHB", "ONEWIRE", "FLASH_CTRL", "ITM", "DWT", "FPB", "TPIU" };

        private static Dictionary<string, string> TYPENAMEMAP = new Dictionary<string, string> { { "GPIO_PORT", "GPIO" }, { "WTIMER", "WDT" } };

        public static string[] FindRelevantHeaderFiles(string headerDir)
        {
            return Directory.GetFiles(headerDir, "hw_*.h", SearchOption.AllDirectories).Where((fn) => (Path.GetFileName(fn) != "hw_memmap.h")).ToArray();
        }

        public static HardwareRegisterSet[] GenerateFamilyPeripheralRegisters(string addressesFile, string[] headerFiles)
        {
            Dictionary<string, KeyValuePair<string, ulong>> addresses = ProcessRegisterSetAddresses(addressesFile);//set name , (set type name, address)
            Dictionary<string, HardwareRegisterSet> types = new Dictionary<string, HardwareRegisterSet>();
            Dictionary<string, List<HardwareSubRegister>> subregs = new Dictionary<string, List<HardwareSubRegister>>();

            // The following are used just for testing that everything parsed is used
            List<string> used_types = new List<string>();
            List<string> used_subregs = new List<string>();

            foreach(var header in headerFiles)
            {
                ProcessRegisterSetTypes(header, ref types);
                ProcessSubregisters(header, ref subregs);
            }

            List<HardwareRegisterSet> sets = new List<HardwareRegisterSet>();
            foreach(var set_name in addresses.Keys)
            {
                string type = addresses[set_name].Key;
                if (TYPENAMEMAP.ContainsKey(type))
                    type = TYPENAMEMAP[type];
                else if (ADDRESSESWITHOUTTYPES.Contains(type))
                    continue;

                List<HardwareRegister> registers = new List<HardwareRegister>(DeepCopy(types[type]).Registers);
                used_types.Add(type);

                for (int i = 0; i < registers.Count; i++)
                {
                    var register = registers[i];
                    string hex_offset = register.Address;
                    if (!string.IsNullOrEmpty(hex_offset))
                    {
                        ulong offset = ParseHex(hex_offset);
                        hex_offset = register.Address = FormatToHex((addresses[set_name].Value + offset));
                    }
                    else
                        throw new Exception("Register address not specified!");

                    // Add the subregisters
                    string subreg_key = type + "_" + register.Name;
                    if (subregs.ContainsKey(subreg_key))
                    {
                        register.SubRegisters = subregs[subreg_key].ToArray();
                        used_subregs.Add(subreg_key);
                    }
                    else if(!REGISTERSWITHOUTSUBREGISTERS.Contains(subreg_key))
                        throw new Exception("Subregisters not found!");
                }

                sets.Add(new HardwareRegisterSet
                {
                    UserFriendlyName = set_name,
                    ExpressionPrefix = set_name + "->",
                    Registers = registers.ToArray()
                });
            }

            //Sort the hardware register sets alphabetically
            sets.Sort((x,y) => {return x.UserFriendlyName.CompareTo(y.UserFriendlyName);});

            // Check that everything parsed was used
            used_types = new List<string>(used_types.Distinct());
            used_subregs = new List<string>(used_subregs.Distinct());
            if(used_types.Count != types.Count)
            {
                List<string> unused_types = new List<string>(types.Keys.Except(used_types));
            }
            if(used_subregs.Count != subregs.Count)
            {
                List<string> unused_subregs = new List<string>(subregs.Keys.Except(used_subregs));
            }

            return sets.ToArray();
        }

        private static Dictionary<string, KeyValuePair<string, ulong>> ProcessRegisterSetAddresses(string file)
        {
            Dictionary<string, KeyValuePair<string, ulong>> addresses = new Dictionary<string, KeyValuePair<string, ulong>>();
            Regex basedef_regex = new Regex(@"^#define[ \t]+((GPIO_PORT)[A-Z]|(GPIO_PORT)[A-Z](_AHB)|SHAMD5|([A-Z0-9_]+?)[0-9]?)_BASE[ \t]+0x([0-9xa-fA-F]{8})[ \t]+\/\/ (.*?)[\r]?$", RegexOptions.Multiline);

            foreach(Match m in basedef_regex.Matches(File.ReadAllText(file)))
            {
                string name = m.Groups[1].ToString();
                string type = m.Groups[2].ToString();
                if (type == "")
                    type = m.Groups[3].ToString() + m.Groups[4].ToString();
                if (type == "")
                    type = m.Groups[5].ToString();
                if (type == "")
                    type = name;
                ulong address = ulong.Parse(m.Groups[6].ToString(), System.Globalization.NumberStyles.HexNumber);
                string comment = m.Groups[7].ToString();

                addresses.Add(name,new KeyValuePair<string, ulong>(type, address));
            }

            return addresses;
        }

        private static void ProcessRegisterSetTypes(string file, ref Dictionary<string, HardwareRegisterSet> types)
        {
            Regex type_regex = new Regex(@"\/\/[ \t]+The following are defines for the ([A-Za-z0-9_\/ ]+) register[ \n\r\/]+(offsets|addresses).[^#]+([^\*]+)\/\/\*", RegexOptions.Singleline);
            var type_m = type_regex.Matches(File.ReadAllText(file));

            foreach(Match m in type_m)
            {
                string type_name = m.Groups[1].ToString();
                string type_regs = m.Groups[3].ToString();

                Regex reg_regex = new Regex("#define (" + type_name + @"|[A-Z0-9_]+?)(_O)?_([A-Z0-9_]+)[ \t]+(0x[0-9A-Fa-f]{8})[ \t\/]*([^\n\r]*)([\r\n \t]*[\/\/ ]{0,3}[^\n\r#]*)+[\n\r]*", RegexOptions.Singleline);

                List<HardwareRegister> regs = new List<HardwareRegister>();
                int index = 0; // This is for checking only, ensuring that all the registers where processed
                foreach(Match m2 in reg_regex.Matches(type_regs))
                {
                    if (index != m2.Index)
                        throw new Exception("Potentially missed parsing a register!");
                    string type_name2 = m2.Groups[1].ToString();
                    if (type_name != type_name2)
                        type_name = type_name2;
                    string reg_name = m2.Groups[3].ToString();
                    string reg_addr = m2.Groups[4].ToString();
                    string comment = m2.Groups[5].ToString();
                    for (int i = 0; i < m2.Groups[6].Captures.Count; i++)
                    {
                        string cont_comment = m2.Groups[6].Captures[i].ToString();
                        if (cont_comment.Trim() != "")
                            comment += " " + cont_comment.Trim().Substring(2).Trim();
                    }

                    regs.Add(new HardwareRegister { Name = reg_name, Address = reg_addr, SizeInBits = REGISTERLENGTHINBITS });

                    index += m2.Length;
                }

                if (regs.Count == 0)
                {
                    if(!FILESWITHOUTREGISTERS.Contains(Path.GetFileName(file)))
                        throw new Exception("No registers found for set!");
                }
                else
                    types.Add(type_name, new HardwareRegisterSet { UserFriendlyName = type_name, Registers = regs.ToArray() });
            }
        }

        private static void ProcessSubregisters(string file, ref Dictionary<string, List<HardwareSubRegister>> subregs)
        {
            Regex reg_regex = new Regex(@"\/\/[ \t]+The following are defines for the bit fields in the ([A-Z0-9_]+)[ \n\r\/]+register.[ \n\r\/]+\/\/[\*]+\r\n(.+?)(\/\/\*|#endif)", RegexOptions.Singleline);
            var reg_m = reg_regex.Matches(File.ReadAllText(file));

            foreach (Match m in reg_m)
            {
                string reg_name = m.Groups[1].ToString().Replace("_O_","_");
                string reg_subregs = m.Groups[2].ToString().Trim();

                Regex subreg_regex = new Regex("#define " + reg_name + @"_([A-Z0-9_]+)[ \t\n\r\\]+(0x)?([0-9A-Fa-f]{8}|[0-9]+)[ \t\/]*([^\n\r]*)([\r\n \t]*[\/\/ ]{0,3}[^\n\r#]*)+[\n\r]*", RegexOptions.Singleline);

                List<HardwareSubRegister> subs = new List<HardwareSubRegister>();
                List<KnownSubRegisterValue> known_values = null;
                ulong last_subreg_mask = 0;
                int index = 0; // This is for checking only, ensuring that all the registers where processed
                foreach (Match m2 in subreg_regex.Matches(reg_subregs))
                {
                    if (index != m2.Index)
                        throw new Exception("Potentially missed parsing a register!");
                    string subreg_name = m2.Groups[1].ToString();
                    if (subreg_name.EndsWith("_M"))
                        subreg_name = subreg_name.Substring(0, subreg_name.Length - 2);
                    else if (subreg_name == "M")
                        subreg_name = reg_name;
                    else if (subreg_name.EndsWith("_S") || (subreg_name == "S"))
                    {
                        index += m2.Length;
                        continue;
                    }
                    ulong subreg_mask = ulong.Parse(m2.Groups[3].ToString(),System.Globalization.NumberStyles.HexNumber);
                    string comment = m2.Groups[4].ToString();
                    for (int i = 0; i < m2.Groups[5].Captures.Count; i++)
                    {
                        string cont_comment = m2.Groups[5].Captures[i].ToString();
                        if (cont_comment.Trim() != "")
                            comment += " " + cont_comment.Trim().Substring(2).Trim();
                    }

                    if((known_values == null) || ((last_subreg_mask | subreg_mask) != last_subreg_mask))
                    {
                        // If there are previous known_values gathered, save it to the previous subregister
                        if ((known_values != null) && (known_values.Count > 0) && (subs.Last().SizeInBits <= 4))
                            subs[subs.Count - 1].KnownValues = known_values.ToArray();
                        known_values = new List<KnownSubRegisterValue>();

                        int size, first_bit;
                        ExtractFirstBitAndSize(subreg_mask, out size, out first_bit, false);
                        subs.Add(new HardwareSubRegister { Name = subreg_name, FirstBit = first_bit, SizeInBits = size });
                        last_subreg_mask = subreg_mask;
                    }
                    else if (subs.Last().SizeInBits <= 4)//Must be a known value instead of a new subregister
                    {
                        for (ulong i = (ulong)known_values.Count; i < (subreg_mask >> subs.Last().FirstBit); i++)
                        {
                            known_values.Add(new KnownSubRegisterValue { Name = "Unknown (" + FormatToHex(i,subs.Last().SizeInBits) + ")"});
                        }
                        known_values.Add(new KnownSubRegisterValue { Name = CleanKnownSubregisterValue(subreg_name, subs.Last().Name) });
                    }
                    else
                    {
                        Console.WriteLine("Skipped: " + m2.ToString());
                    }

                    index += m2.Length;
                }

                if(index != reg_subregs.Length)
                    throw new Exception("Potentially missed parsing a register!");

                if (subs.Count == 0)
                    throw new Exception("No subregisters found for register!");

                // Clean the subregisters: remove 32-bit subregisters, too sparse known values and repetitions
                CleanSubregisters(ref subs);
                if((subs != null) && (subs.Count > 0))
                    subregs.Add(reg_name, subs);
            }

            if ((reg_m.Count == 0) && !FILESWITHOUTREGISTERS.Contains(Path.GetFileName(file)))
                throw new Exception("No registers found!");
        }

        class HardwareSubRegisterComparer : IEqualityComparer<HardwareSubRegister>
        {
            public bool Equals(HardwareSubRegister x, HardwareSubRegister y)
            {
                return x.Name.Equals(y.Name, StringComparison.InvariantCultureIgnoreCase) && (x.FirstBit == y.FirstBit) && (x.SizeInBits == y.SizeInBits);
            }

            public int GetHashCode(HardwareSubRegister obj)
            {
                return obj.Name.ToUpperInvariant().GetHashCode() ^ obj.FirstBit.GetHashCode() ^ obj.SizeInBits.GetHashCode();
            }
        }

        private static void CleanSubregisters(ref List<HardwareSubRegister> subs)
        {
            if ((subs != null) && (subs.Count > 1))
            {
                // Sort the subregisters based on first bit
                subs.Sort((x, y) => { return (x.FirstBit - y.FirstBit); });

                // Remove repetitions
                var tmp = subs.Distinct(new HardwareSubRegisterComparer()).ToList();
                if (subs.Count > tmp.Count)
                    Console.WriteLine("Removed subregister repetitions!");
                subs.Clear();
                subs.AddRange(tmp);

                // Check the subregisters for any overlap in ranges
                int index = -1;
                foreach (var subreg in subs)
                {
                    if (subreg.FirstBit < index)
                        Console.WriteLine("Overlap in subregister ranges for subregister " + subreg.Name);
                    index = subreg.FirstBit + subreg.SizeInBits;
                }
            }
        }

        private static string CleanKnownSubregisterValue(string raw_known_value, string subreg_name)
        {
            if (raw_known_value.StartsWith(subreg_name + "_"))
                return raw_known_value.Substring(subreg_name.Length + 1);
            return raw_known_value;
        }

        private static HardwareRegisterSet DeepCopy(HardwareRegisterSet set)
        {
            HardwareRegisterSet set_new = new HardwareRegisterSet
            {
                UserFriendlyName = set.UserFriendlyName,
                ExpressionPrefix = set.ExpressionPrefix,
            };

            if (set.Registers != null)
            {
                set_new.Registers = new HardwareRegister[set.Registers.Length];
                for (int i = 0; i < set.Registers.Length; i++)
                {
                    set_new.Registers[i] = DeepCopy(set.Registers[i]);
                }
            }

            return set_new;
        }

        private static HardwareRegister DeepCopy(HardwareRegister reg)
        {
            HardwareRegister reg_new = new HardwareRegister
            {
                Name = reg.Name,
                Address = reg.Address,
                GDBExpression = reg.GDBExpression,
                ReadOnly = reg.ReadOnly,
                SizeInBits = reg.SizeInBits
            };

            if (reg.SubRegisters != null)
            {
                reg_new.SubRegisters = new HardwareSubRegister[reg.SubRegisters.Length];
                for (int i = 0; i < reg.SubRegisters.Length; i++)
                {
                    reg_new.SubRegisters[i] = DeepCopy(reg.SubRegisters[i]);
                }
            }

            return reg_new;
        }

        private static HardwareSubRegister DeepCopy(HardwareSubRegister subreg)
        {
            HardwareSubRegister subreg_new = new HardwareSubRegister
            {
                Name = subreg.Name,
                FirstBit = subreg.FirstBit,
                SizeInBits = subreg.SizeInBits,
                KnownValues = (subreg.KnownValues != null) ? (KnownSubRegisterValue[])subreg.KnownValues.Clone() : null
            };

            return subreg_new;
        }

        private static ulong ParseHex(string hex)
        {
            if (hex.StartsWith("0x"))
                hex = hex.Substring(2);
            return ulong.Parse(hex, System.Globalization.NumberStyles.HexNumber);
        }

        private static string FormatToHex(ulong addr, int length = 32)
        {
            string format = "0x{0:x" + length / 4 + "}";
            return string.Format(format, (uint)addr);
        }

        private static bool ExtractFirstBitAndSize(ulong val, out int size, out int firstBit, bool throwWhenSecondOneRegionExists = true)
        {
            bool second_one_region = false;
            size = 0;
            firstBit = -1;
            int lastBit = -1;
            int state = 0;
            for (int i = 0; i < 64; i++)
            {
                if ((val & ((ulong)1 << i)) == ((ulong)1 << i))
                {
                    if (state == 0)
                        state = 1;
                    else if (state == 2)
                    {
                        second_one_region = true;
                        if(throwWhenSecondOneRegionExists)
                            throw new Exception("Hit a second 1 region inside subregister bit mask!");
                    }

                    lastBit = i;
                    if (firstBit < 0)
                        firstBit = i;
                }
                else if (state == 1)
                    state = 2;
            }

            if(lastBit >= 0)
                size = lastBit - firstBit + 1;

            if (size == 0 || firstBit == -1)
            {
                size = 1;
                firstBit = 0;
                throw new Exception("Extracting first bit or size for subregister failed!");
            }

            return second_one_region;
        }
    }
}
