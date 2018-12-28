/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BSPEngine;
using System.IO;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace stm32_bsp_generator
{
    public struct RegisterID
    {
        public readonly string SetName;
        public readonly string RegName;
        public string SubregisterPrefix
        {
            get
            {
                return SetName + "_" + RegName + "_";
            }
        }

        public RegisterID(string set, string reg)
        {
            SetName = set;
            RegName = reg.TrimEnd(')');
        }

        public override string ToString()
        {
            return SetName + "_" + RegName;
        }
    }

    public class RegisterParserConfiguration
    {
        Dictionary<string, bool> _IgnoredSubregisters = new Dictionary<string, bool>();
        Dictionary<string, bool> _PotentiallyNotSequentialRegisters = new Dictionary<string, bool>();
        string[] _IgnoredSubregisterPrefixes;
        string[] _IgnoredSubregisterSuffixes;
        Regex[] _IgnoredSubregisterRegexes;
        Regex[] _IgnoredMismatchingSubregisters;
        KnownRegisterWithoutSubregisters[] _KnownRegistersWithoutSubregisters;

        public class KnownRegisterWithoutSubregisters
        {
            public Regex SetName;
            public Regex SetType;
            public Regex RegName;

            public bool IsMatch(string setName, string setType, string regName)
            {
                if (SetName != null && !SetName.IsMatch(setName))
                    return false;
                if (SetType != null && !SetType.IsMatch(setType))
                    return false;
                return RegName.IsMatch(regName);
            }
        }

        public string KnownRegistersWithoutSubregisters
        {
            get
            { throw new NotImplementedException(); }
            set
            {
                List<KnownRegisterWithoutSubregisters> rules = new List<KnownRegisterWithoutSubregisters>();
                foreach (var spec in value.Split(';'))
                {
                    int idx = spec.IndexOf('/');
                    bool type = false;
                    if (idx == -1)
                    {
                        idx = spec.IndexOf(':');
                        type = true;
                    }

                    var r = new KnownRegisterWithoutSubregisters
                    {
                        RegName = new Regex("^" + spec.Substring(idx + 1) + "$", RegexOptions.IgnoreCase)
                    };

                    var rg2 = new Regex("^" + spec.Substring(0, idx) + "$", RegexOptions.IgnoreCase);
                    if (type)
                        r.SetType = rg2;
                    else
                        r.SetName = rg2;

                    rules.Add(r);
                }

                _KnownRegistersWithoutSubregisters = rules.ToArray();
            }
        }

        public bool IsKnownRegisterWithoutSubregisters(string setName, string setType, string regName)
        {
            foreach (var r in _KnownRegistersWithoutSubregisters)
                if (r.IsMatch(setName, setType, regName))
                    return true;
            return false;
        }

        public class RegisterSetRenameRule
        {
            public string OriginalName;
            string[] _RegisterNames;
            public string RegisterNames
            {
                get
                {
                    throw new NotImplementedException();
                }
                set
                {
                    _RegisterNames = value.Split(';');
                }
            }

            public string NewName;
            public string NewRegisterName;

            public bool Apply(ref RegisterID register)
            {
                if (register.SetName != OriginalName)
                    return false;
                if (!_RegisterNames.Contains(register.RegName))
                    return false;

                register = new RegisterID(NewName == null ? register.SetName : NewName, NewRegisterName == null ? register.RegName : NewRegisterName);
                return true;
            }
        }

        public class SubregisterRenameRule
        {
            Regex _SetRegex, _RegisterRegex, _SubregisterRegex;

            public string OldSet { get { throw new NotImplementedException(); } set { _SetRegex = new Regex("^" + value + "$"); } }
            public string OldRegister { get { throw new NotImplementedException(); } set { _RegisterRegex = new Regex("^" + value + "$"); } }
            public string OldSubregister { get { throw new NotImplementedException(); } set { _SubregisterRegex = new Regex("^" + value + "$"); } }

            public string NewSubregister;
            public string NewSet;
            public string NewRegister;
            public bool StripRegisterNameFromSubregister;
            public bool Breakpoint;

            public bool Apply(ref RegisterID regId, string fullName, out string shortName)
            {
                if (Breakpoint)
                    Debugger.Break();

                shortName = null;
                Match mSet = null, mReg = null, mSubreg = null;
                if (_SetRegex != null)
                {
                    mSet = _SetRegex.Match(regId.SetName);
                    if (!mSet.Success)
                        return false;
                }
                if (_RegisterRegex != null)
                {
                    mReg = _RegisterRegex.Match(regId.RegName);
                    if (!mReg.Success)
                        return false;
                }

                mSubreg = _SubregisterRegex.Match(fullName);
                if (!mSubreg.Success)
                    return false;

                shortName = ExpandString(NewSubregister, mSet, mReg, mSubreg);
                if (NewSet != null || NewRegister != null)
                {
                    string setName = NewSet == null ? regId.SetName : ExpandString(NewSet, mSet, mReg, mSubreg);
                    string regName = NewRegister == null ? regId.RegName : ExpandString(NewRegister, mSet, mReg, mSubreg);
                    regId = new RegisterID(setName, regName);
                }

                if (StripRegisterNameFromSubregister)
                {
                    if (!shortName.StartsWith(regId.RegName + "_", StringComparison.InvariantCultureIgnoreCase))
                        throw new Exception("Unexpected subregister name after transformation");
                    shortName = shortName.Substring(regId.RegName.Length + 1);
                }
                return true;
            }

            static string ExpandString(string shortName, Match mSet, Match mReg, Match mSubreg)
            {
                if (!shortName.Contains('\\'))
                    return shortName;

                StringBuilder result = new StringBuilder();
                for (int i = 0; i < shortName.Length; i++)
                {
                    if (shortName[i] == '\\')
                    {
                        int pos = (int)(shortName[i + 2] - '0');

                        switch (shortName[i + 1])
                        {
                            case 's':
                                result.Append(mSet.Groups[pos].Value);
                                break;
                            case 'r':
                                result.Append(mReg.Groups[pos].Value);
                                break;
                            case 'u':
                                result.Append(mSubreg.Groups[pos].Value);
                                break;
                            default:
                                throw new Exception("Invalid name override format string");
                        }

                        i += 2;
                    }
                    else
                        result.Append(shortName[i]);

                }

                return result.ToString();
            }
        }

        public SubregisterRenameRule[] SubregisterRenameRules;

        public class LinePatch
        {
            public string Prefix;
            public string SearchedText;
            public string ReplacementText;
            public string RegexFile;
            public int AnchorDistance;
            public string AnchorLine;

            public void ApplyAllFile(ref string[] lines, string pfilename)
            {
                
                for(int  i = 0; i< lines.Length;i++)
                {
                    if (i + AnchorDistance >= lines.Length)
                        continue;

                    if (AnchorLine == null)
                    {
                        Apply(ref lines[i], pfilename);
                        continue;
                    }

                    if (lines[i + AnchorDistance].Trim('\r') == AnchorLine)
                        Apply(ref lines[i], pfilename);

                }
            }

            public bool Apply(ref string line, string pfilename)
            {
                if (RegexFile != null)
                    if (!Regex.IsMatch(pfilename, $"^{RegexFile}$"))
                        return false;
                if (!line.StartsWith(Prefix))
                    return false;
                if (!line.Contains(SearchedText))
                    return false;
                line = line.Replace(SearchedText, ReplacementText);
                return true;
            }
        }

        public RegisterSetRenameRule[] RegisterSetRenameRules;
        public LinePatch[] LinePatches;
        public LinePatch[] SubregisterLinePatches;
        public string[] IgnoredBitDefinitionBlocks;

        public string IgnoredSubregisters
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                foreach (var r in value.Split(';'))
                    _IgnoredSubregisters[r] = true;
            }
        }

        //Those registers might be non-sequential for some devices, but not all. We will only ignore the actual non-sequential instances of them.
        public bool IsKnownNonSequentialRegister(string register) => _PotentiallyNotSequentialRegisters.ContainsKey(register);


        public string PotentiallyNotSequentialRegisters
        {
            get => throw new NotImplementedException();
            set
            {
                foreach (var r in value.Split(';'))
                    _PotentiallyNotSequentialRegisters[r] = true;
            }
        }

        public string IgnoredSubregisterPrefixes
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                _IgnoredSubregisterPrefixes = value.Split(';');
            }
        }

        public string IgnoredSubregisterSuffixes
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                _IgnoredSubregisterSuffixes = value.Split(';');
            }
        }

        public string IgnoredSubregisterRegexes
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                _IgnoredSubregisterRegexes = value.Split(';').Select(s => new Regex("^" + s + "$")).ToArray();
            }
        }

        public string IgnoredMismatchingSubregisters
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                _IgnoredMismatchingSubregisters = value.Split(';').Select(s => new Regex("^" + s + "$")).ToArray();
            }
        }

        string[] _IgnoredDefinitionsInBaseAddressArea;
        public string IgnoredDefinitionsInBaseAddressArea
        {
            get
            {

                throw new NotImplementedException();
            }
            set
            {
                _IgnoredDefinitionsInBaseAddressArea = value.Split(';');
            }
        }

        public bool IsBaseAddrDefinitionIgnored(string name)
        {
            return _IgnoredDefinitionsInBaseAddressArea.Contains(name);
        }


        public bool IsSubregisterIgnored(string subregisterName)
        {
            if (_IgnoredSubregisters.ContainsKey(subregisterName))
                return true;
            foreach (var prefix in _IgnoredSubregisterPrefixes)
                if (subregisterName.StartsWith(prefix))
                    return true;
            foreach (var suffix in _IgnoredSubregisterSuffixes)
                if (subregisterName.EndsWith(suffix))
                    return true;
            foreach (var rg in _IgnoredSubregisterRegexes)
                if (rg.IsMatch(subregisterName))
                    return true;
            return false;
        }

        public bool IsMismatchingSubregisterIgnored(string subregisterName)
        {
            foreach (var rg in _IgnoredMismatchingSubregisters)
                if (rg.IsMatch(subregisterName))
                    return true;
            return false;
        }

        public bool IsBlockDefinitionIgnored(string line)
        {
            foreach (var str in IgnoredBitDefinitionBlocks)
                if (line.Contains(str))
                    return true;
            return false;
        }

        public bool ApplySubregisterRenameRules(ref RegisterID thisReg, string subreg_name, out string shortSubregName)
        {
            shortSubregName = null;
            foreach (var rule in SubregisterRenameRules)
                if (rule.Apply(ref thisReg, subreg_name, out shortSubregName))
                    return true;
            return false;
        }

    }

    public class RegisterParserErrors
    {
        public class ErrorBase
        {
            protected ErrorBase()
            {

            }

            public int LineNumber;
            public string FileName;
            public string LineContents;
        }

        public class MissingSubregisterDefinitions : ErrorBase
        {
            public string RegisterName;
            public string SetName;

            public override string ToString()
            {
                return string.Format("Missing subregister definitions for {0}_{1}", SetName, RegisterName);
            }
        }

        public class BadBitDefinition : ErrorBase
        {
            public override string ToString()
            {
                return "Bad bit definition line: " + LineContents;
            }
        }

        public class BadSubregisterDefinition : ErrorBase
        {
            public BadSubregisterDefinition()
            {

            }
            public override string ToString()
            {
                return "Failed to recognize the bit definition line: " + LineContents;
            }
        }


        public class UnexpectedSubregisterName : ErrorBase
        {
            public string SubregName;
            public override string ToString()
            {
                return "Unexpected subregister name: " + SubregName;
            }
        }


        public class BitmaskNotSequential : ErrorBase
        {
            public override string ToString()
            {
                return "Bitmask not sequential: " + LineContents;
            }
        }

        public class UnexpectedBaseAddressDefinition : ErrorBase
        {
            public override string ToString()
            {
                return "Unexpected base addr definition: " + LineContents;
            }
        }

        List<ErrorBase> _Errors = new List<ErrorBase>();

        public string DetalErrors(int num)
        {
            return (_Errors[num].FileName + ":" + _Errors[num].LineNumber + " - " + _Errors[num].ToString() + " : " + _Errors[num].GetType());
        }
        public void AddError(ErrorBase error)
        {
            lock (_Errors)
                _Errors.Add(error);
        }

        public override string ToString()
        {
            return _Errors.Count + " errors";
        }

        public int ErrorCount
        {
            get
            {
                lock (_Errors)
                    return _Errors.Count;
            }
        }
    }

    static class PeripheralRegisterGenerator
    {
        public static string LocateFamilyPeripheralHeaderFile(string dir, string family)
        {
            string file_to_search = family.Substring(0, family.Length - 2).ToLower() + ".h";
            if (file_to_search == "stm32f1xx.h")
                file_to_search = "stm32f10x.h";
            else if (file_to_search == "stm32w1xx.h")
                file_to_search = "stm32w108xx.h";
            foreach (var file in Directory.EnumerateFiles(dir, file_to_search, SearchOption.AllDirectories))
            {
                return file;
            }

            throw new Exception("Family peripheral header file not found!");
        }
        const string DirCoreReg = @"../../../CoreReg/OutCorexx";
        public static HardwareRegisterSet[] GenerateFamilyPeripheralRegisters(string PeripheralHeaderFile, RegisterParserConfiguration cfg, RegisterParserErrors errors, BSPGenerationTools.CortexCore atCore)
        {
            string file = File.ReadAllText(PeripheralHeaderFile);

            Dictionary<string, ulong> registerset_addresses = ProcessRegisterSetAddresses(PeripheralHeaderFile, file, cfg, errors);
            Dictionary<string, KeyValuePair<string, string>> registerset_names = ProcessRegisterSetNames(file);// (name, (type, addr))
            Dictionary<string, string> nested_types;
            Dictionary<string, HardwareRegisterSet> registerset_types = ProcessRegisterSetTypes(file, out nested_types);
            Dictionary<string, List<HardwareSubRegister>> subregisters = ProcessSubregisters(file, PeripheralHeaderFile, cfg, errors);

            // Process registers and register sets

            List<HardwareRegisterSet> sets = new List<HardwareRegisterSet>();
            KnownValueDatabase knonwValues = new KnownValueDatabase();

            Dictionary<string, string> dict_repeat_reg_addr = new Dictionary<string, string>();

            HardwareRegisterSet regCore = null;
            string aFileCore = "";

            switch (atCore)
            {
                case BSPGenerationTools.CortexCore.M0:
                    aFileCore = Path.Combine(DirCoreReg, "core_M0.xml");
                    break;
                case BSPGenerationTools.CortexCore.M0Plus:
                    aFileCore = Path.Combine(DirCoreReg, "core_M0Plus.xml");
                    break;
                case BSPGenerationTools.CortexCore.M3:
                    aFileCore = Path.Combine(DirCoreReg, "core_M3.xml");
                    break;
                case BSPGenerationTools.CortexCore.M4:
                    aFileCore = Path.Combine(DirCoreReg, "core_M4.xml");
                    break;
                case BSPGenerationTools.CortexCore.M7:
                    aFileCore = Path.Combine(DirCoreReg, "core_M7.xml");
                    break;
                default:
                    throw new Exception("Unsupported core type");

            }
            regCore = XmlTools.LoadObject<HardwareRegisterSet>(aFileCore);
            sets.Add(regCore);

            foreach (var set in registerset_names)
            {
                string set_name = set.Key;
                string set_type = set.Value.Key;
                ulong set_addr;
                if (!registerset_addresses.TryGetValue(set.Value.Value, out set_addr))
                {
                    if (set.Value.Value == "USB")
                        continue;
                    if (set.Value.Value == "SDMMC")
                        continue;
                    throw new Exception("Cannot find base address for " + set.Value.Value);
                }
                if (registerset_addresses.ContainsKey(set_name + "1") && (set_addr == registerset_addresses[set_name + "1"]))
                    continue;// Ignore generic aliases

                if (!registerset_types.ContainsKey(set.Value.Key))
                {
                    if (set.Value.Key == "HASH_DIGEST" || set.Value.Key == "HRTIM" || set.Value.Key == "HRTIM_TIM")
                        continue;
                    if (set.Value.Key == "DMA_request")
                        continue;
                    if (set.Value.Key == "RCC_Core")
                        continue;
                    if (set.Value.Key == "DMAMUX_IdRegisters")
                        continue; 
                    throw new Exception("Unknown set type: " + set.Value.Key);

                }

                List<HardwareRegister> registers = new List<HardwareRegister>(DeepCopy(registerset_types[set.Value.Key]).Registers);

                for (int i = 0; i < registers.Count; i++)
                {
                    var register = registers[i];

                    string hex_offset = register.Address;
                    if (!string.IsNullOrEmpty(hex_offset))
                    {
                        ulong offset = ParseHex(hex_offset);
                        hex_offset = register.Address = FormatToHex((set_addr + offset));
                    }
                    else
                        throw new Exception("Register address not specified!");

                    if (!nested_types.ContainsKey(set_type + "_" + register.Name))
                        if (!dict_repeat_reg_addr.ContainsKey(register.Address))
                            dict_repeat_reg_addr[register.Address] = set_name + "_" + register.Name;
                        else if (!(set_name.StartsWith("DMAMUX1_Channel0") && dict_repeat_reg_addr[register.Address].StartsWith("DMAMUX1")) &&
                                !(set_name.StartsWith("FMC_") && dict_repeat_reg_addr[register.Address].StartsWith("FSMC_")) &&
                                !(set_name.StartsWith("ADC") && dict_repeat_reg_addr[register.Address].StartsWith("ADC1")) &&
                                !(set_name.StartsWith("AES") && dict_repeat_reg_addr[register.Address].StartsWith("AES")) &&
                                 !(set_name.StartsWith("DAC") && dict_repeat_reg_addr[register.Address].StartsWith("DAC")) &&
                                 !(set_name.StartsWith("COMP") && dict_repeat_reg_addr[register.Address].StartsWith("COMP")) &&
                                (set_type != "SC_UART") && (set_type != "SC_SPI") && (set_type != "SC_I2C") && (set_type != "COMP") && (set_type != "OPAMP") && (set_type != "OPAMP_Common"))// This register is removed later on anyway as it is an either/or thing
                                                                                                                                                                                             //        throw new Exception("Register address for " + set_name + "_" + register.Name + " is already used by " + dict_repeat_reg_addr[register.Address] + "!");
                            Console.WriteLine("560 PrepReg throw new Exception(Register address for " + set_name + "_" + register.Name + " is already used by " + dict_repeat_reg_addr[register.Address]);

                    if (subregisters.ContainsKey(set_type + "_" + register.Name))
                    {
                        // Do some cleanup on the subregisters - remove all subregisters with the same size as the register itself and the same name as the register
                        List<HardwareSubRegister> subreg_to_clean = subregisters[set_type + "_" + register.Name];
                        for (int j = 0; j < subreg_to_clean.Count; j++)
                        {
                            HardwareSubRegister subreg_clean = subreg_to_clean[j];
                            if ((subreg_clean.Name == register.Name) && (subreg_clean.SizeInBits == register.SizeInBits))
                            {
                                subreg_to_clean.RemoveAt(j);
                                j--;
                                Debug.WriteLine("Removed unnecessary subregister at " + register.Name);
                            }
                        }

                        if (subreg_to_clean.Count > 0)
                            register.SubRegisters = subreg_to_clean.ToArray();
                    }
                    else if (nested_types.ContainsKey(set_type + "_" + register.Name))
                    {
                        string reg_name = register.Name;
                        HardwareRegister[] registers2 = registerset_types[nested_types[set_type + "_" + reg_name]].Registers;
                        registers.Remove(register);
                        i--;

                        foreach (var register2 in registers2)
                        {
                            HardwareRegister register2_cpy = DeepCopy(register2);

                            string hex_offset2 = register2_cpy.Address;
                            if (!string.IsNullOrEmpty(register.Address) && !string.IsNullOrEmpty(hex_offset2))
                            {
                                ulong offset = ParseHex(register.Address);
                                ulong offset2 = ParseHex(hex_offset2);
                                register2_cpy.Address = FormatToHex((offset + offset2));
                            }

                            if (subregisters.ContainsKey(nested_types[set_type + "_" + reg_name] + "_" + register2_cpy.Name))
                                register2_cpy.SubRegisters = subregisters[nested_types[set_type + "_" + reg_name] + "_" + register2_cpy.Name].ToArray();
                            else if (((nested_types[set_type + "_" + reg_name] != "CAN_TxMailBox")) && // Does not have any subregisters for any of its registers
                                ((nested_types[set_type + "_" + reg_name] != "CAN_FilterRegister")) && // Does not have any subregisters for any of its registers
                                ((nested_types[set_type + "_" + reg_name] != "CAN_FIFOMailBox"))) // Does not have any subregisters for any of its registers
                                throw new Exception("No subregisters found for register " + register2_cpy.Name + "!");

                            register2_cpy.Name = reg_name + "_" + register2_cpy.Name; // Make nested name to collapse the hierarchy

                            registers.Insert(i, register2_cpy);
                            if (!dict_repeat_reg_addr.ContainsKey(register2_cpy.Address))
                                dict_repeat_reg_addr[register2_cpy.Address] = set_name + "_" + register2_cpy.Name;
                            else
                                throw new Exception("Register address for" + set_name + "_" + register2_cpy.Name + " is already used by " + dict_repeat_reg_addr[register2_cpy.Address] + "!");
                            i++;
                        }
                    }
                    else if (set_type.StartsWith("HRTIM"))
                    {
                        List<HardwareSubRegister> subregs;
                        if (subregisters.TryGetValue("HRTIM_" + register.Name, out subregs))
                            register.SubRegisters = subregs.ToArray();
                        else if (register.Name.EndsWith("UPR") || register.Name.EndsWith("DLLCR"))
                            continue;
                        else
                            errors.AddError(new RegisterParserErrors.MissingSubregisterDefinitions { FileName = PeripheralHeaderFile, SetName = set_name, RegisterName = register.Name });
                    }
                    else if ((set_name == "DAC") && (register.Name == "DHR12R2"))//Header BUG: subregister definition missing
                    {
                        register.SubRegisters = new HardwareSubRegister[] {
                            new HardwareSubRegister { Name = "DACC2DHR", FirstBit = 0, SizeInBits = 12 }
                        };
                    }
                    else if ((set_name == "DAC") && (register.Name == "DHR12L2"))//Header BUG: subregister definition missing
                    {
                        register.SubRegisters = new HardwareSubRegister[] {
                            new HardwareSubRegister { Name = "DACC2DHR", FirstBit = 4, SizeInBits = 12 }
                        };
                    }
                    else if ((set_name == "DAC") && (register.Name == "DHR8R2"))//Header BUG: subregister definition missing
                    {
                        register.SubRegisters = new HardwareSubRegister[] {
                            new HardwareSubRegister { Name = "DACC2DHR", FirstBit = 0, SizeInBits = 8 }
                        };
                    }
                    else if ((set_name == "DAC") && (register.Name == "DHR12RD"))//Header BUG: subregister definition missing
                    {
                        register.SubRegisters = new HardwareSubRegister[] {
                            new HardwareSubRegister { Name = "DACC1DHR", FirstBit = 0, SizeInBits = 12 },
                            new HardwareSubRegister { Name = "DACC2DHR", FirstBit = 16, SizeInBits = 12 }
                        };
                    }
                    else if ((set_name == "DAC") && (register.Name == "DHR12LD"))//Header BUG: subregister definition missing
                    {
                        register.SubRegisters = new HardwareSubRegister[] {
                            new HardwareSubRegister { Name = "DACC1DHR", FirstBit = 4, SizeInBits = 12 },
                            new HardwareSubRegister { Name = "DACC2DHR", FirstBit = 20, SizeInBits = 12 }
                        };
                    }
                    else if ((set_name == "DAC") && (register.Name == "DHR8RD"))//Header BUG: subregister definition missing
                    {
                        register.SubRegisters = new HardwareSubRegister[] {
                            new HardwareSubRegister { Name = "DACC1DHR", FirstBit = 0, SizeInBits = 8 },
                            new HardwareSubRegister { Name = "DACC2DHR", FirstBit = 8, SizeInBits = 8 }
                        };
                    }
                    else if ((set_name == "DAC") && (register.Name == "DOR2"))//Header BUG: subregister definition missing
                    {
                        register.SubRegisters = new HardwareSubRegister[] {
                            new HardwareSubRegister { Name = "DACC2DOR", FirstBit = 0, SizeInBits = 12 }
                        };
                    }
                    else if ((set_type == "USB_OTG") && (register.Name == "HNPTXSTS"))//Header BUG: subregister definition missing
                    {
                        register.SubRegisters = new HardwareSubRegister[] {
                            new HardwareSubRegister { Name = "NPTXQTOP", FirstBit = 24, SizeInBits = 7 },
                            new HardwareSubRegister { Name = "NPTQXSAV", FirstBit = 16, SizeInBits = 8 },
                            new HardwareSubRegister { Name = "NPTXFSAV", FirstBit = 0, SizeInBits = 16 }
                        };
                    }
                    else if (((set_name == "FLASH") && ((register.Name == "KEYR2") || (register.Name == "SR2") || (register.Name == "CR2") ||
                        (register.Name == "CCR1") || (register.Name == "CCR2") || (register.Name == "CRCCR1") || (register.Name == "CRCCR2") ||
                        (register.Name.StartsWith("CRC")) || (register.Name == "ECC_FA1") || (register.Name == "ECC_FA2") ||
                        (register.Name == "SR1") || (register.Name == "CR1") || (register.Name == "AR2"))))// Reuse subregisters from non-2 FLASH registers
                        if (subregisters.ContainsKey(set_type + "_" + register.Name.Substring(0, register.Name.Length - 1)))
                            register.SubRegisters = subregisters[set_type + "_" + register.Name.Substring(0, register.Name.Length - 1)].ToArray();
                        else
                            continue;
                    else if ((set_name.StartsWith("GPIO") && ((register.Name == "BSRRL") || (register.Name == "BSRRH"))))// Reuse subregisters from BSRR defs
                        register.SubRegisters = subregisters[set_type + "_" + register.Name.Substring(0, register.Name.Length - 1)].ToArray();
                    else if (((set_name.StartsWith("DMA1_Stream") || (set_name.StartsWith("DMA2_Stream"))) && (register.Name == "CR")))// Reuse subregisters from DMA_SxCR defs
                        register.SubRegisters = subregisters["DMA_SxCR"].ToArray();
                    else if (((set_name.StartsWith("DMA1_Stream") || (set_name.StartsWith("DMA2_Stream"))) && (register.Name == "NDTR")))// Reuse subregisters from DMA_SxCNDTR defs
                        register.SubRegisters = subregisters["DMA_SxCNDTR"].ToArray();
                    else if (((set_name.StartsWith("DMA1_Stream") || (set_name.StartsWith("DMA2_Stream"))) && (register.Name == "FCR")))// Reuse subregisters from DMA_SxFCR defs
                        register.SubRegisters = subregisters["DMA_SxFCR"].ToArray();
                    else if ((set_name == "OPAMP") && (register.Name == "CSR"))// Reuse subregisters from OPAMPx_CSR defs
                        register.SubRegisters = subregisters["OPAMPx_CSR"].ToArray();
                    else if ((set_name.StartsWith("OPAMP")) && (register.Name == "CSR"))// Reuse subregisters from OPAMPx_CSR defs
                        register.SubRegisters = subregisters[set_name + "_" + register.Name].ToArray();
                    else if ((set_name.StartsWith("EXTI")) && (register.Name == "IMR2") && !(set_name.StartsWith("EXTI_D")))// Reuse subregisters
                        register.SubRegisters = subregisters["EXTI_IMR"].ToArray();
                    else if ((set_name.StartsWith("EXTI")) && (register.Name == "EMR2") && !(set_name.StartsWith("EXTI_D")))// Reuse subregisters
                        register.SubRegisters = subregisters["EXTI_EMR"].ToArray();
                    else if ((set_name.StartsWith("EXTI")) && (register.Name == "RTSR2") && !(set_name.StartsWith("EXTI_D")))// Reuse subregisters
                        register.SubRegisters = subregisters["EXTI_RTSR"].ToArray();
                    else if ((set_name.StartsWith("EXTI")) && (register.Name == "FTSR2") && !(set_name.StartsWith("EXTI_D")))// Reuse subregisters
                        register.SubRegisters = subregisters["EXTI_FTSR"].ToArray();
                    else if ((set_name.StartsWith("EXTI")) && (register.Name == "SWIER2") && !(set_name.StartsWith("EXTI_D")))// Reuse subregisters
                        register.SubRegisters = subregisters["EXTI_SWIER"].ToArray();
                    else if ((set_name.StartsWith("EXTI")) && (register.Name == "PR2") && !(set_name.StartsWith("EXTI_D")))// Reuse subregisters
                        register.SubRegisters = subregisters["EXTI_PR"].ToArray();
                    else if ((set_name.StartsWith("EXTI")) && (register.Name.StartsWith("TSR")) && !(set_name.StartsWith("EXTI_D")))// Reuse subregisters
                        register.SubRegisters = subregisters["EXTI_TSR"].ToArray();
                    else if ((set_name.StartsWith("EXTI")) && (register.Name.StartsWith("CR")) && !(set_name.StartsWith("EXTI_D")))// Reuse subregisters
                        register.SubRegisters = subregisters["EXTI_CR"].ToArray();
                    else if ((set_name.StartsWith("ADC1_2")))// Reuse subregisters
                        register.SubRegisters = subregisters["ADC12_" + register.Name].ToArray();
                    else if ((set_name.StartsWith("ADC1_")))// Reuse subregisters
                        register.SubRegisters = subregisters["ADC_" + register.Name].ToArray();
                    else if ((set_type == "ADC_Common" && register.Name == "CDR2"))// Reuse subregisters
                        register.SubRegisters = subregisters["ADC123_" + register.Name].ToArray();
                    else if ((set_name.StartsWith("ADC12_")))// Reuse subregisters
                        register.SubRegisters = subregisters["ADC_" + register.Name].ToArray();
                    else if ((set_name.StartsWith("ADC3_4")))// Reuse subregisters
                        register.SubRegisters = subregisters["ADC34_" + register.Name].ToArray();
                    else if (((set_type == "SAI_Block")))// Reuse subregisters
                        register.SubRegisters = subregisters["SAI_x" + register.Name].ToArray();
                    else if (((set_type == "LTDC_Layer")))// Reuse subregisters
                        register.SubRegisters = subregisters["LTDC_Lx" + register.Name].ToArray();
                    else if (((set_type == "LCD") && (register.Name.StartsWith("RAM"))))// Reuse subregisters
                        register.SubRegisters = subregisters["LCD_RAM"].ToArray();
                    else if (((set_type == "PWR") && ((register.Name == "WAKEPAR") || (register.Name == "WAKEPBR") || (register.Name == "WAKEPCR"))))// Reuse subregisters
                        register.SubRegisters = subregisters["PWR_WAKEPxR"].ToArray();
                    else if ((((set_type == "SC_UART") || (set_type == "SC_SPI") || (set_type == "SC_I2C")) && (register.Name == "DR")))// Reuse subregisters
                        register.SubRegisters = subregisters["SC_DR"].ToArray();
                    else if ((((set_type == "SC_UART") || (set_type == "SC_SPI") || (set_type == "SC_I2C")) && (register.Name == "CR")))// Reuse subregisters
                        register.SubRegisters = subregisters["SC_CR"].ToArray();
                    else if ((((set_type == "SC_SPI") || (set_type == "SC_I2C")) && (register.Name == "CRR1")))// Reuse subregisters
                        register.SubRegisters = subregisters["SC_CRR1"].ToArray();
                    else if ((((set_type == "SC_SPI") || (set_type == "SC_I2C")) && (register.Name == "CRR2")))// Reuse subregisters
                        register.SubRegisters = subregisters["SC_CRR2"].ToArray();
                    else if (((set_type == "USB_OTG") && (register.Name == "GRXSTSR")))// Reuse subregisters
                        register.SubRegisters = subregisters["USB_OTG_GRXSTSP"].ToArray();
                    else if (((set_type == "USB_OTG") && (register.Name == "DIEPTXF0_HNPTXFSIZ")))// Reroute subregisters
                        register.SubRegisters = subregisters["USB_OTG_DIEPTXF"].ToArray();
                    else if (((set_type == "USB_OTG") && (register.Name.StartsWith("DIEPTXF"))))// Reuse subregisters
                        register.SubRegisters = subregisters["USB_OTG_DIEPTXF"].ToArray();
                    else if (set_type == "FDCAN_ClockCalibrationUnit")// Reuse subregisters
                        register.SubRegisters = subregisters["FDCANCCU_" + register.Name].ToArray();
                    else if (set_type == "COMPOPT")// Reuse subregisters
                        register.SubRegisters = subregisters[$"COMP_{ register.Name}"].ToArray();
                    else if ((set_type == "COMP" || set_type == "COMP_Common") && register.Name == "CFGR")// Reuse subregisters
                        register.SubRegisters = subregisters["COMP_CFGRx"].ToArray();
                    else if (set_type == "EXTI_Core")// Reuse subregisters
                        register.SubRegisters = subregisters["EXTI_" + register.Name].ToArray();
                    else if (set_type == "BDMA_Channel")// Reuse subregisters
                        register.SubRegisters = subregisters["BDMA_" + register.Name].ToArray();
                    else if (set_type.StartsWith("DMAMUX"))// Reuse subregisters
                        continue;
                    else if (set_type == "MDMA_Channel")// Reuse subregisters
                        register.SubRegisters = subregisters["MDMA_" + register.Name].ToArray();
                    else if (set_type == "DFSDM_Channel" || set_type == "DFSDM_Filter")
                    {
                        List<HardwareSubRegister> subregs;
                        if (subregisters.TryGetValue("DFSDM_" + register.Name, out subregs))
                            register.SubRegisters = subregs.ToArray();
                        else if (register.Name == "CHWDATAR")
                            continue;
                        else
                        {
                            errors.AddError(new RegisterParserErrors.MissingSubregisterDefinitions { FileName = PeripheralHeaderFile, SetName = set_name, RegisterName = register.Name });
                            continue;
                        }
                    }
                    else if (set_type.StartsWith("FSMC_Bank") || set_type.StartsWith("FMC_Bank"))
                    {
                        List<HardwareSubRegister> subregs;
                        if (subregisters.TryGetValue("FMC_" + register.Name, out subregs))
                            register.SubRegisters = subregs.ToArray();
                        else if (subregisters.TryGetValue("FSMC_" + register.Name, out subregs))
                            register.SubRegisters = subregs.ToArray();
                        else if (subregisters.TryGetValue("FMC_" + register.Name.TrimEnd('1', '2', '3', '4', '5', '6') + "x", out subregs))
                            register.SubRegisters = subregs.ToArray();
                        else if (subregisters.TryGetValue("FSMC_" + register.Name.TrimEnd('1', '2', '3', '4', '5', '6') + "x", out subregs))
                            register.SubRegisters = subregs.ToArray();
                        else
                            continue;
                    }
                    else if (cfg.IsKnownRegisterWithoutSubregisters(set_name, set_type, register.Name))
                        continue;
                    else if (set_type == "SPI" && register.Name == "I2SPR")
                        continue;   //Bug: one header is missing the definition
                    else if (set_type == "RCC" && register.Name == "CRRCR")
                        continue;   //Bug: one header is missing the definition stm32l041xx.h
                    else if (set_type == "DCMI" && (register.Name == "RISR" || register.Name == "MISR"))
                        continue;
                    else if (set_type == "TAMP" && register.Name == "MISR")
                        continue;
                    else if (subregisters.ContainsKey(set_name + "_" + register.Name))
                    {
                        register.SubRegisters = subregisters[set_name + "_" + register.Name].ToArray();
                    }
                    else
                    {
                        errors.AddError(new RegisterParserErrors.MissingSubregisterDefinitions { FileName = PeripheralHeaderFile, SetName = set_name, RegisterName = register.Name });
                        continue;
                    }
                }

                knonwValues.AttachKnownValues(set_name, registers);

                // Check subregister first bits, they should be <= register size
                foreach (var s in sets)
                {
                    foreach (var r in s.Registers)
                    {
                        if (r.SubRegisters != null)
                            foreach (var sr in r.SubRegisters)
                            {
                                if (sr.FirstBit >= r.SizeInBits)
                                    throw new Exception("Subregister " + sr.Name + " first bit is out of range!");
                            }
                    }
                }

                sets.Add(new HardwareRegisterSet
                {
                    UserFriendlyName = set_name,
                    ExpressionPrefix = set_name + "->",
                    Registers = registers.ToArray()
                }
                );
            }

            return sets.ToArray();
        }

        class KnownValueDatabase
        {
            class RegisterMatch
            {
                public Regex rgName;
                public string PrefixToRemove;

                public int ExpectedNextValue;
                public List<KnownSubRegisterValue> KnownValues = new List<KnownSubRegisterValue>();
            }

            List<RegisterMatch> Matches = new List<RegisterMatch>();

            public KnownValueDatabase()
            {
                RegisterMatch match = null;
                Regex rgKV = new Regex("([^ \t]+)[ \t=]+0x([0-9a-fA-F]+)(,|$| |\t)");

                foreach (var line in File.ReadAllLines(@"..\..\enums.txt"))
                {
                    if (line.StartsWith("#"))
                        continue;

                    if (match == null)
                        match = new RegisterMatch { rgName = new Regex(line) };
                    else
                    {
                        if (line.Trim() == "")
                        {
                            if (match != null)
                            {
                                Matches.Add(match);
                                match = null;
                            }
                        }
                        else if (match != null)
                        {
                            if (line.StartsWith("Prefix:"))
                                match.PrefixToRemove = line.Substring(7).Trim();
                            else if (line.StartsWith(" ") || line.StartsWith("\t"))
                            {
                                var m = rgKV.Match(line.Trim());
                                if (!m.Success)
                                    throw new Exception("Unexpected definition: " + line);

                                string name = m.Groups[1].ToString();
                                int value = int.Parse(m.Groups[2].ToString(), System.Globalization.NumberStyles.HexNumber);

                                if (value != match.ExpectedNextValue)
                                {
                                    //TODO: detect alignment
                                }

                                if (match.PrefixToRemove != null && name.StartsWith(match.PrefixToRemove))
                                    name = name.Substring(match.PrefixToRemove.Length);
                                match.KnownValues.Add(new KnownSubRegisterValue { Name = name });
                                match.ExpectedNextValue++;
                            }
                            else
                                throw new Exception("Unexpected line: " + line);
                        }
                    }
                }

                if (match != null)
                {
                    Matches.Add(match);
                }
            }

            internal void AttachKnownValues(string set_name, List<HardwareRegister> registers)
            {
                foreach (var reg in registers)
                    if (reg.SubRegisters != null)
                        foreach (var subreg in reg.SubRegisters)
                        {
                            string fullName = string.Format("{0}->{1}->{2}", set_name, reg.Name, subreg.Name);
                            foreach (var m in Matches)
                            {
                                if (m.rgName.IsMatch(fullName))
                                {
                                    if (m.KnownValues.Count != (1 << subreg.SizeInBits))
                                        throw new Exception("Incomplete predefined value list");
                                    subreg.KnownValues = m.KnownValues.ToArray();
                                }
                            }
                        }
            }
        }

        private static Dictionary<string, HardwareRegisterSet> ProcessRegisterSetTypes(string file, out Dictionary<string, string> nested_types)
        {
            Dictionary<string, HardwareRegisterSet> types = new Dictionary<string, HardwareRegisterSet>();
            nested_types = new Dictionary<string, string>();

            Dictionary<string, int> dict_type_sizes = new Dictionary<string, int>();
            dict_type_sizes["uint32_t"] = 32;
            dict_type_sizes["int32_t"] = 32;
            dict_type_sizes["uint16_t"] = 16;
            dict_type_sizes["uint8_t"] = 8;

            Regex struct_regex = new Regex(@"typedef struct[ \t]*\r?\n\{[ \t]*\r?\n([^}]*)\r?\n\}[ \t\r?\n]*([A-Za-z0-9_]*)_(Global)?TypeDef;");

            var structs = struct_regex.Matches(file);
            foreach (Match strct in structs)
            {
                HardwareRegisterSet set = new HardwareRegisterSet() { UserFriendlyName = strct.Groups[2].Value, ExpressionPrefix = strct.Groups[2].Value + "->" };
                int set_size = 0;

                RegexOptions option = RegexOptions.IgnoreCase;
                Regex register_regex = new Regex(@"[ \t]*(__IO|__I)*[ ]*(?:const )*[ ]*([^ #\r?\n]*)[ ]*(?:const )*([^\[;#\r?\n]*)[\[]?([0-9xXa-fA-F]+)*[\]]?;[ ]*(/\*)*(!<)*[ ]?([^,*\r?\n]*)[,]?[ ]*(Ad[d]?ress)*( offset:)*[ ]*([0-9xXa-fA-F]*)[ ]?[-]?[ ]?([^ *\r?\n]*)[ ]*(\*/)*[ ]*(\r?\n)*", option);

                var regs = register_regex.Matches(strct.Groups[1].Value);

                List<HardwareRegister> hw_regs = new List<HardwareRegister>();
                if (regs.Count == 0)
                    throw new Exception("Register row parsing failed!");

                foreach (Match m in regs)
                {
                    string type = m.Groups[2].Value;
                    if (!dict_type_sizes.ContainsKey(type))
                        throw new Exception("Unknown register type: " + type);

                    int size = dict_type_sizes[type];
                    int array_size = 1;
                    try
                    {
                        array_size = string.IsNullOrEmpty(m.Groups[4].Value) ? 1 : Int32.Parse(m.Groups[4].Value);
                    }
                    catch (FormatException ex)
                    {
                        string hex = m.Groups[4].Value;
                        if (hex.StartsWith("0x"))
                            hex = hex.Substring("0x".Length);
                        array_size = Int32.Parse(hex, System.Globalization.NumberStyles.HexNumber);
                    }
                    string hex_offset = FormatToHex((ulong)(set_size / 8));// Do not use address from header file, sometimes they have errors //m.Groups[10].Value;
                    if (size == 32)// all 32 bit addresses should start from an address divisible by 4
                    {
                        hex_offset = FormatToHex((((ulong)(set_size / 8) + 3) / 4) * 4);
                        set_size = Math.Max(set_size, (int)(ParseHex(hex_offset) * 8)) + array_size * size;
                    }
                    else
                        set_size += array_size * size;

                    string name = m.Groups[3].Value;

                    if (name.StartsWith("RESERVED", StringComparison.InvariantCultureIgnoreCase))
                        continue;

                    string readonly_type = m.Groups[1].Value;
                    string desc = m.Groups[7].Value.TrimEnd();
                    bool flNameArrayFromOne = true;

                    if (desc.StartsWith("DSI "))
                        flNameArrayFromOne = false;


                    for (int i = 1; i <= array_size; i++)
                    {
                        if (array_size != 1)
                            if ((set.UserFriendlyName == "GPIO") && (m.Groups[3].Value == "AFR"))
                            {
                                if (i == 1)
                                    name = m.Groups[3].Value + "L";
                                else if (i == 2)
                                    name = m.Groups[3].Value + "H";
                                else
                                    throw new Exception("Cannot use low-high naming with array sizes greater than 2!");
                            }
                            else
                                if (flNameArrayFromOne)
                                name = m.Groups[3].Value + i.ToString();
                            else
                                name = m.Groups[3].Value + (i - 1).ToString();

                        if ((type != "uint32_t") && (type != "uint16_t") && (type != "uint8_t"))
                        {
                            int index = type.LastIndexOf("_TypeDef");
                            nested_types[set.UserFriendlyName + "_" + name] = type.Substring(0, index);// Chop the TypeDef off the type as it is easier to process later on
                        }

                        HardwareRegister hw_reg = new HardwareRegister
                        {
                            Name = name,
                            SizeInBits = size,
                            ReadOnly = (readonly_type == "__I") ? true : false,
                            Address = hex_offset
                        };

                        if (hw_regs.Find(x => ((x.Name == hw_reg.Name) && (x.Address == hw_reg.Address))) != null)
                            throw new Exception("Register with the same name and address already exists in the set!");
                        hw_regs.Add(hw_reg);
                        hex_offset = FormatToHex(ParseHex(hex_offset) + Math.Max((ulong)(size / 8.0), (ulong)4));
                    }
                }

                dict_type_sizes[set.UserFriendlyName + "_TypeDef"] = set_size;

                set.Registers = hw_regs.ToArray();

                if (types.ContainsKey(set.UserFriendlyName))
                    throw new Exception("Two registerset definitions with the same of " + set.UserFriendlyName + " found!");
                types[set.UserFriendlyName] = set;
            }

            return types;
        }

        private static Dictionary<string, KeyValuePair<string, string>> ProcessRegisterSetNames(string file)
        {
            Dictionary<string, KeyValuePair<string, string>> names = new Dictionary<string, KeyValuePair<string, string>>();

            Regex periph_decl_begin_regex = new Regex(@"/\*\* \@addtogroup Peripheral_declaration\r?\n(.*)\r?\n(.*)");
            var m_begin = periph_decl_begin_regex.Match(file);
            if (!m_begin.Success)
                throw new Exception("Failed to locate peripheral declaration group");

            string[] lines = file.Substring(m_begin.Index + m_begin.Groups[0].Length).Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Regex periph_def_regex = new Regex(@"#define[ ]+([a-zA-Z0-9_]*)[ ]+\(\(([a-zA-Z0-9_]*)_(|Global)TypeDef[ ]+\*\)[ ]*(.+)\)");
            Regex rgDirectValue = new Regex("^([a-zA-Z0-9_]*)_BASE$");

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if ((line == "\r") ||
                    (line == " \r") ||
                    line.StartsWith("#if defined (STM32F40_41xxx)") ||
                    line.StartsWith("#endif /* STM32F40_41xxx */") ||
                    line.StartsWith("#if defined (STM32F427_437xx) || defined (STM32F429_439xx)") ||
                    line.StartsWith("#endif /* STM32F427_437xx ||  STM32F429_439xx */")
                    || line.Contains("COMP_BASE + 0x0") //currently not supported
                    || line.Contains("OPAMP_BASE + 0x0") //currently not supported
                    || line.Contains("/* USB device FS") //currently not supported
                    || line.Contains("/* Legacy define") //currently not supported
                    || line.Contains("#define ADC1_2_COMMON       ADC12_COMMON") //currently not supported
                    || (line.Contains("#define ADC") && line.Contains("ADC123_COMMON")) //currently not supported
                    || (line.Contains("#define ADC3_4_COMMON") && line.Contains("ADC34_COMMON")) //currently not supported
                    || (line.Contains("#define ADC") && line.Contains("ADC1_COMMON")) //currently not supported
                    || line.StartsWith("#define COMP12_COMMON       ((COMP_Common_TypeDef *) COMP_BASE) ")
                    || line.StartsWith("#define ADC123_COMMON       ((ADC_Common_TypeDef *) ADC_BASE)") //2017
                    || (line.StartsWith("#define OPAMP") && line.Contains("((OPAMP_Common_TypeDef *) OPAMP"))
                    || line.StartsWith("/* Aliases to keep compatibility after ")
                    || (line.StartsWith("#define DFSDM_Channel") && line.Contains("DFSDM1_Channel"))
                    || (line.StartsWith("#define DFSDM_Filter") && line.Contains(" DFSDM1_Filter"))
                    || (line.StartsWith("#define DAC ") && line.Contains(" DAC1"))
                    || (line.Contains("#define USB_OTG") && (line.Contains("USB1_OTG") || line.Contains("USB2_OTG")))//stm32h7
                    || (line.Contains("#define AES1_") && line.Contains("AES_"))
                    
                        )
                    continue;

                if (line.StartsWith("/**") || line.StartsWith(" /**"))
                    break;

                var m = periph_def_regex.Match(line);
                if (m.Success)
                {
                    var value = m.Groups[4].Value;
                    if (names.ContainsKey(m.Groups[1].Value))
                        throw new Exception("Repeating register set name in peripheral declaration!");

                    var m2 = rgDirectValue.Match(value);
                    if (m2.Success)
                        names[m.Groups[1].Value] = new KeyValuePair<string, string>(m.Groups[2].Value, m2.Groups[1].Value);
                    else
                    {
                        throw new Exception("Unrecognized peripheral declaration line!");

                    }
                    continue;
                }

                Console.WriteLine(" throw new Exception(Unrecognized peripheral declaration line! >:" + line);

            }

            return names;
        }

        private static Dictionary<string, ulong> ProcessRegisterSetAddresses(string fn, string file, RegisterParserConfiguration cfg, RegisterParserErrors err)
        {
            Dictionary<string, ulong> addresses = new Dictionary<string, ulong>();

            Regex memory_map_begin_regex = new Regex(@"/\*\* \@addtogroup Peripheral_memory_map[\r]?\n(.*)[\r]?\n(.*)");
            Regex memory_map_begin_regex2 = new Regex(@"/\*\* \r?\n  \* \@brief Peripheral_memory_map");
            Regex rgComment = new Regex(@"^[ \t]*/\*[^/]+\*/[ \t]*$");
            var m_begin = memory_map_begin_regex.Match(file);
            if (!m_begin.Success)
                m_begin = memory_map_begin_regex2.Match(file);
            if (!m_begin.Success)
                throw new Exception("Cannot find peripheral memory map");

            string[] lines = file.Substring(m_begin.Index + m_begin.Groups[0].Length).Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');

                if (line == "\r" ||
                    line.StartsWith("#if defined (STM32F40_41xxx)") ||
                    line.StartsWith("#endif /* STM32F40_41xxx */") ||
                    line.StartsWith("#if defined (STM32F427_437xx) || defined (STM32F429_439xx)") ||
                    line.StartsWith("#endif /* STM32F427_437xx ||  STM32F429_439xx */") ||
                    line.StartsWith("  */") ||
                    line.StartsWith("#define USB_OTG_EP_REG_SIZE") ||
                    line.StartsWith("#define USB_OTG_HOST_CHANNEL_SIZE") ||
                    line.StartsWith("#define USB_OTG_FIFO_SIZE") ||
                    line.StartsWith("#define FLASH_END") ||
                    line.StartsWith("#define FLASH_OTP_END") ||
                    line.StartsWith("#define CCMDATARAM_END ") ||
                    line.StartsWith("#define DATA_EEPROM")
                    || line.StartsWith("#define FLASH_EEPROM_END")
                    || line.StartsWith("#define FLASH_BANK1_END")
                    || line.StartsWith("#define FLASH_BANK2_END")
                    || line.StartsWith("#define USB_PMAADDR")
                    || line.StartsWith("#define SRAM_BASE")
                   || line.StartsWith("#define SRAM_BB_BASE")
                    || line.StartsWith("#define SRAM_SIZE_MAX")
                    )
                    continue;

                if (line.StartsWith("/**"))
                    break;

                if (rgComment.IsMatch(line) || string.IsNullOrWhiteSpace(line))
                    continue;

                Regex defineRegex = new Regex(@"#define[ ]+([^ ]*)[ ]+([^ \t].*)");
                var m = defineRegex.Match(line);
                if (m.Success)
                {
                    var macroName = m.Groups[1].Value;
                    var value = m.Groups[2].Value;
                    int idx = value.IndexOf("/*");
                    if (idx != -1)
                        value = value.Substring(0, idx);
                    value = value.Trim();

                    if (macroName.EndsWith("_BASE"))
                    {
                        string regset_name = macroName.Substring(0, macroName.Length - "_BASE".Length);
                        Regex base_addr_regex = new Regex(@"\(\(([^\(\)]*)\)([^\(\)]*)\)");//#define PERIPH_BASE           ((uint32_t)0x40000000U)  
                        m = base_addr_regex.Match(value);
                        if (!m.Success && !value.Contains("+"))
                            m = Regex.Match(value, @"()(0x[0-9A-FU]+)");//#define PERIPH_BASE           0x40000000U 
                        if (m.Success)
                        {
                            string addr = m.Groups[2].Value;
                            addresses[regset_name] = ParseHex(addr);
                            continue;
                        }

                        Regex base_addr_equals_regex = new Regex(@"[\(]?([^ ]*)_BASE[\)]?(\r|$)");
                        m = base_addr_equals_regex.Match(value);
                        if (m.Success)
                        {
                            string regset2_name = m.Groups[1].Value;
                            if (addresses.ContainsKey(regset2_name))// && addresses.ContainsKey(regset_name))
                                addresses[regset_name] = addresses[regset2_name];
                            else
                            {
                                if (!addresses.ContainsKey(regset_name))
                                    Console.WriteLine("ERR1153 !addresses.ContainsKey(regset2_name) " + regset_name);
                                if (!addresses.ContainsKey(regset2_name))
                                    Console.WriteLine("ERR1154 !addresses.ContainsKey(regset2_name) " + regset2_name);
                            }
                            continue;
                        }

                        Regex typed_base_addr_equals_plus_regex = new Regex(@"\({2}[^()]+\)\(([^ ]*)_BASE[ ]+\+[ ]+(.*)\){2}");
                        m = typed_base_addr_equals_plus_regex.Match(value);
                        if (m.Success)
                        {
                            string regset2_name = m.Groups[1].Value;
                            string addr = m.Groups[2].Value;
                            addresses[regset_name] = addresses[regset2_name] + ParseHex(addr);

                            continue;
                        }
                        int s = 0;
                        if (line.Contains("#define APBPERIPH_BASE"))
                            s++;
                        Regex base_addr_equals_plus_regex = new Regex(@"\(([^ ]*)_BASE[ ]+\+[ ]+(.*)\)");
                        m = base_addr_equals_plus_regex.Match(value);
                        if (m.Success)
                        {
                            string regset2_name = m.Groups[1].Value;
                            string addr = m.Groups[2].Value;
                            if (addresses.ContainsKey(regset2_name))//&& addresses.ContainsKey(regset_name))
                                addresses[regset_name] = addresses[regset2_name] + ParseHex(addr);
                            else
                            {
                                if (!addresses.ContainsKey(regset2_name))
                                    Console.WriteLine("1180 Per Reg!addresses.ContainsKey(regset2_name" + regset2_name + s);

                            }
                            continue;
                        }


                        Regex base_addr_base_regex = new Regex(@"([^ ]+)_BASE");
                        m = base_addr_base_regex.Match(value);
                        if (m.Success)
                        {
                            string prevRegset = m.Groups[1].Value;
                            addresses[regset_name] = addresses[prevRegset.TrimStart('(')];

                            continue;
                        }
                    }
                    else if (cfg.IsBaseAddrDefinitionIgnored(macroName))
                        continue;
                }

                err.AddError(new RegisterParserErrors.UnexpectedBaseAddressDefinition { FileName = fn, LineContents = line });
            }

            return addresses;
        }



        private static Dictionary<string, List<HardwareSubRegister>> ProcessSubregisters(string fileContents, string fileName, RegisterParserConfiguration cfg, RegisterParserErrors errors)
        {
            Dictionary<string, List<HardwareSubRegister>> result = new Dictionary<string, List<HardwareSubRegister>>();
            Dictionary<string, uint> aDefPosDict = new Dictionary<string, uint>();

            // Process subregisters
            Regex rgSubregisterList = new Regex(@"/\*[!]?[<]?[\*]+[ ]+Bit[s]? definition [genric ]*for ([^_ ]*)_([^ ]*)[ ]+register[ ]+[*]+/[ ]*");
            Regex rgSubregisterListWildcard = new Regex(@"/\*+ +Bit definition for ([A-Z0-9a-z]+)_([A-Z0-9a-z]+) \(x *=[^()]+\) +register +\*+/");
            Regex rgSubregisterListWildcard2 = new Regex(@"/\*+ +Bit definition for ([A-Z0-9a-z]+)_([A-Z0-9a-z]+) registers? +\(x *=[^()]+\) +\*+/");
            Regex rbSubregisterListEth = new Regex(@"/\*+[ ]+Bit definition for (.*) [Rr]egister[ ]+[1-9]*[ ]*\*+/");
            Regex rbSubregisterListUsb = new Regex(@"/\*+ +([^ ]+ .*) register bits? definitions +\*+/", RegexOptions.IgnoreCase);
            Regex bit_def_regex = new Regex(@"#define[ ]+([^ \(\)]*)[ \t]+[,]?[ ]*\(\(([^\(\)]*)\)([0-9A-Fa-fx]*)[U]?\)[ ]*(/\*)?(!<)?[ ]*([^\*/]*)(\*/)?");


            string[] lines = fileContents.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int nextLine = 0;
            bool insideIgnoredBlock = false;

            foreach (var patch in cfg.LinePatches)
                patch.ApplyAllFile(ref lines,fileName);
            for (; ; )
            {
                if (nextLine >= lines.Length)
                    break;
                string line = lines[nextLine++];

                if (line.Contains(" Instances ***"))
                    break;

                //foreach (var patch in cfg.LinePatches)
                  //  if (patch.Apply(ref line, Path.GetFileName(fileName)))
                    //    break;

                Match m;
                RegisterID thisReg;
                if ((m = rgSubregisterList.Match(line)).Success)
                    thisReg = new RegisterID(m.Groups[1].Value, m.Groups[2].Value);
                else if (((m = rgSubregisterListWildcard.Match(line)).Success) || ((m = rgSubregisterListWildcard2.Match(line)).Success))
                    thisReg = new RegisterID(m.Groups[1].Value, m.Groups[2].Value);
                else if (((m = rbSubregisterListEth.Match(line)).Success) || (m = rbSubregisterListUsb.Match(line)).Success)
                {
                    var m4 = bit_def_regex.Match(lines[nextLine]);
                    if (!m4.Success)
                        m4 = Regex.Match(lines[nextLine], @"#define[ \t]+([\w\d]+)[ \t]+([0-9A-Fa-fx]*)[U]?");
                    if (!m4.Success)
                        errors.AddError(new RegisterParserErrors.BadBitDefinition { LineContents = line, LineNumber = nextLine - 1, FileName = fileName });

                    string subreg_def = m4.Groups[1].Value;
                    int index = subreg_def.IndexOf("_");
                    if (index <= 0)
                    {
                        errors.AddError(new RegisterParserErrors.BadBitDefinition { LineContents = line, LineNumber = nextLine - 1, FileName = fileName });
                        continue;
                    }
                    string regSet = subreg_def.Substring(0, index);
                    subreg_def = subreg_def.Substring(index + 1);
                    index = subreg_def.IndexOf("_");
                    if (index <= 0)
                    {
                        errors.AddError(new RegisterParserErrors.BadBitDefinition { LineContents = line, LineNumber = nextLine - 1, FileName = fileName });
                        continue;
                    }

                    thisReg = new RegisterID(regSet, subreg_def.Substring(0, index));
                }
                else
                {
                    if (line.StartsWith("/**") && (line.Contains("Bit definition") || line.Contains("Bits definition")))
                    {
                        if (cfg.IsBlockDefinitionIgnored(line))
                            insideIgnoredBlock = true;
                        else
                            errors.AddError(new RegisterParserErrors.BadBitDefinition { LineContents = line, LineNumber = nextLine - 1, FileName = fileName });
                    }

                    if (line.StartsWith("#define")
                        && line.Contains("0x")
                        && !line.Contains("CLEAR_REG")
                        && !line.Contains("USB_EP")
                        && !line.Contains("FLASH_FKEY")
                        && !line.Contains("FLASH_KEY")
                        && !line.Contains("EXTI_EMR")
                        && !line.Contains("FLASH_OPTKEY")
                        && !line.Contains("(USB_BASE +")
                        && !line.Contains("RTC_BKP_NUMBER")
                        && !line.Contains("USB_ISTR")
                        && !line.Contains("USB_LPMCSR")
                        && !line.Contains("USB_DADDR")
                        && !line.Contains("USB_BASE")
                        && !line.Contains("USB_CNTR")
                        && !line.Contains("USB_BCDR")
                        && !line.Contains("USB_FNR")
                        && !line.Contains("USB_PMAADDR")
                        && !line.Contains("define HRTIM_")  //Not much formal system in comments to parse. Currently ignoring.
                        && !line.Contains("_RST_VALUE")
                        && !line.Contains("TAMP_MISR_")
                      )
                    {
                        if (result.Count > 0 && !insideIgnoredBlock)
                            errors.AddError(new RegisterParserErrors.BadSubregisterDefinition { FileName = fileName, LineContents = line, LineNumber = nextLine - 1 });
                    }

                    continue;
                }

                if (thisReg.RegName.Contains("/"))
                {
                    int idx = thisReg.RegName.IndexOf('/');
                    thisReg = new RegisterID(thisReg.SetName, thisReg.RegName.Substring(0, idx).TrimEnd('1', '2', '3', '4'));
                }

                insideIgnoredBlock = false;

                //Apply name adjustment rules
                if ((thisReg.SetName == "DMA") && ((thisReg.RegName.Substring(0, thisReg.RegName.Length - 1) == "CCR") || (thisReg.RegName.Substring(0, thisReg.RegName.Length - 1) == "CNDTR") || (thisReg.RegName.Substring(0, thisReg.RegName.Length - 1) == "CPAR") || (thisReg.RegName.Substring(0, thisReg.RegName.Length - 1) == "CMAR")))// Header BUG: DMA_Channel defs not DMA defs
                    thisReg = new RegisterID("DMA_Channel", thisReg.RegName.Substring(0, thisReg.RegName.Length - 1));
                else
                {
                    foreach (var rule in cfg.RegisterSetRenameRules)
                    {
                        if (rule.Apply(ref thisReg))
                            break;
                    }
                }

                var subregisters = new List<HardwareSubRegister>();

                for (; ; )
                {
                    if (nextLine >= lines.Length)
                        break;
                    line = lines[nextLine++];

                    foreach (var patch in cfg.SubregisterLinePatches)
                        if (patch.Apply(ref line, fileName))
                        {
                            //  Console.WriteLine("\r\n patch SubregisterLinePatches " + line);
                            break;
                        }
                    string subreg_name = "";
                    string reg_type = "";
                    string address_offset = "";
                    bool aParseOk = false;
                    m = bit_def_regex.Match(line);
                    if (!m.Success)
                    {
                        m = Regex.Match(line, @"#define[ \t]+([\w]+)[ \t]+[\(]?([\w]+)[\)]?");
                        if (m.Success && !m.Groups[1].Value.EndsWith("_Pos") && !m.Groups[1].Value.EndsWith("_Msk") && !line.Contains("<<"))
                        {
                            subreg_name = m.Groups[1].Value;

                            reg_type = "uint32_t";
                            if (m.Groups[2].Value.StartsWith("0x"))
                            {
                                address_offset = m.Groups[2].Value.Replace("U", "");//#define GPIO_OTYPER_OT_0                (0x00000001U)     
                                aDefPosDict[m.Groups[1].Value] = (uint)ParseHex(address_offset);
                            }
                            else
                            {
                                if (aDefPosDict.ContainsKey(m.Groups[2].Value))
                                {
                                    address_offset = FormatToHex(aDefPosDict[m.Groups[2].Value]);

                                    // for Legacy defines */
                                    aDefPosDict[m.Groups[1].Value] = aDefPosDict[m.Groups[2].Value];
                                }
                                else
                                    if (Regex.IsMatch(m.Groups[2].Value, "(0x[0-9A-FU]+)"))
                                    aDefPosDict.Add(m.Groups[1].Value, (uint)ParseHex(m.Groups[2].Value));
                                else
                                {
                                    //    Console.WriteLine("No Hex value:{0}, : {1}", m.Groups[1].Value, m.Groups[2].Value);
                                    continue;
                                }
                            }
                            aParseOk = true;
                        }
                    }
                    else
                    {
                        subreg_name = m.Groups[1].Value;
                        reg_type = m.Groups[2].Value;
                        address_offset = m.Groups[3].Value;
                        if (!aDefPosDict.ContainsKey(m.Groups[1].Value))
                            aDefPosDict.Add(m.Groups[1].Value, (uint)ParseHex(m.Groups[3].Value));
                        aParseOk = true;
                    }

                    if (aParseOk)
                    {
                        string shortSubregName;
                        if (cfg.IsSubregisterIgnored(subreg_name))
                            continue;

                        //Checks to see if any known values were missed from the above filters
                        if (subreg_name.EndsWith("_RST") ||
                            subreg_name.EndsWith("HSI") ||
                            subreg_name.EndsWith("HSE") ||
                            subreg_name.EndsWith("LSI") ||
                            subreg_name.EndsWith("MSI") ||
                            subreg_name.EndsWith("PLL") ||
                            subreg_name.EndsWith("_NOCLOCK") ||
                            subreg_name.EndsWith("_SYSCLK") ||
                            subreg_name.EndsWith("KEY")
                            )
                        {
                            if (subreg_name != "MACTMR_CR_EN" &&
                                subreg_name != "MACTMR_CR_RST" &&
                                subreg_name != "ADC_DMACR_RST" &&
                                subreg_name != "RTC_WPR_KEY" &&
                                subreg_name != "IWDG_KR_KEY" &&
                                subreg_name != "SCB_AIRCR_VECTKEY" &&
                                subreg_name != "HASH_CR_LKEY" &&
                                subreg_name != "WDG_KR_KEY" &&
                                !subreg_name.StartsWith("RCC_CFGR_") &&
                                subreg_name != "FLASH_OPTR_SRAM2_RST" &&
                                !subreg_name.StartsWith("HSEM_") &&
                                subreg_name != "FDCAN_HPMS_MSI" &&
                                subreg_name != "RCC_PLLCKSELR_PLLSRC_HSE")

                            {
                                throw new Exception("Potential missed known subregister value!");
                            }
                        }


                        int offset, size;
                        try
                        {
                            ExtractFirstBitAndSize(ParseHex(address_offset), out size, out offset);
                        }
                        catch
                        {
                            if (cfg.IsKnownNonSequentialRegister(subreg_name))
                            {
                                //Nothing to do - ignore the error.
                            }
                            else
                                errors.AddError(new RegisterParserErrors.BitmaskNotSequential { FileName = fileName, LineContents = line, LineNumber = nextLine - 1 });
                            continue;
                        }

                        string desc = m.Groups[6].Value.TrimEnd();

                        if (subreg_name.StartsWith(thisReg.SubregisterPrefix))
                            shortSubregName = subreg_name.Substring(thisReg.SubregisterPrefix.Length);
                        else
                        {
                            if (cfg.IsMismatchingSubregisterIgnored(subreg_name))
                                continue;

                            if (!cfg.ApplySubregisterRenameRules(ref thisReg, subreg_name, out shortSubregName))
                            {
                                errors.AddError(new RegisterParserErrors.UnexpectedSubregisterName { FileName = fileName, LineContents = line, LineNumber = nextLine - 1, SubregName = subreg_name });
                                continue;
                            }
                        }

                        if (subregisters.Find(x => ((x.Name == shortSubregName) && (x.FirstBit == offset) && (x.SizeInBits == size))) != null)
                        {
                            Debug.WriteLine("Duplicate subregister not added: " + thisReg.SubregisterPrefix + shortSubregName);
                        }
                        else
                        {
                            subregisters.Add(new HardwareSubRegister
                            {
                                Name = shortSubregName,
                                SizeInBits = size,
                                FirstBit = offset,
                            });
                        }

                    }
                    else
                    {
                        //                        if (line.StartsWith("#define") && line.Contains("0x") && !line.Contains("CLEAR_REG"))
                        if (line.StartsWith("#define") && line.Contains("0x") && !line.Contains("CLEAR_REG") && !line.Contains("_Pos"))
                            errors.AddError(new RegisterParserErrors.BadSubregisterDefinition { FileName = fileName, LineContents = line, LineNumber = nextLine - 1 });
                        else if (line.Contains("/*                         USB Device General registers                       */"))
                            break;
                        else if (line.StartsWith("/*******") || line.Contains("Bit definition") || line.StartsWith("/*!<Common registers */"))
                            break;
                        else
                        {
                            var m1 = Regex.Match(line, @"#define[ \t]+([\w]+_Pos)[ \t]+\(([0-9A-Fa-f]+)U\)");
                            if (m1.Success)
                                aDefPosDict[m1.Groups[1].Value] = UInt32.Parse(m1.Groups[2].Value);
                            else
                            {
                                //#define SDMMC_STA_DPSMACT_Msk           (0x1U << SDMMC_STA_CPSMACT_Pos)        /*!< 0x00001000 */
                                //#define SDMMC_STA_CPSMACT_Pos  
                                m1 = Regex.Match(line, @"#define[ \t]+([\w]+)[ \t]+\(0x([0-9A-Fa-fx]+)U << ([\w]+)\)");
                                if (m1.Success)
                                {
                                    if (!aDefPosDict.ContainsKey(m1.Groups[3].Value))
                                    {
                                        for (int nextline2 = nextLine; nextline2 < lines.Length; nextline2++)
                                        {
                                            string line2 = lines[nextline2];
                                            if (line2.Contains(m1.Groups[3].Value))
                                            {
                                                var m2 = Regex.Match(line2, @"#define[ \t]+([\w]+_Pos)[ \t]+\(([0-9A-Fa-f]+)U\)");
                                                if (m2.Success)
                                                {
                                                    aDefPosDict[m2.Groups[1].Value] = UInt32.Parse(m2.Groups[2].Value);
                                                    break;
                                                }
                                            }
                                        }
                                    }

                                    try
                                    {
                                        UInt32 aValueMask = UInt32.Parse(m1.Groups[2].Value, System.Globalization.NumberStyles.AllowHexSpecifier);
                                        aValueMask = aValueMask << (int)aDefPosDict[m1.Groups[3].Value];
                                        aDefPosDict[m1.Groups[1].Value] = aValueMask;
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine("Exc 1589 Mes" + ex.Message + " Value" + m1.Groups[3].Value);
                                        continue;
                                    }
                                }
                            }
                        }
                    }
                }

                result[thisReg.ToString()] = subregisters;
                subregisters = new List<HardwareSubRegister>();

                nextLine--;
            }


            //Remove useless subregisters that just define a numbered bit of another subregister value
            Regex numbered_name_regex = new Regex(@"(.*)_[0-9]+");
            foreach (var list in result.Values)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var element = list[i];
                    var m = numbered_name_regex.Match(element.Name);
                    if (m.Success && (list.Find(x => (x.Name == m.Groups[1].Value)) != null) && (element.SizeInBits == 1))
                    {
                        //Debug.WriteLine("Removed bit definition " + element.Name);
                        list.Remove(element);
                        i--;
                    }
                }
            }

            return result;
        }

        public static ulong ParseHex(string hex)
        {
            if (hex.StartsWith("0x"))
                hex = hex.Substring(2);
            hex = hex.TrimEnd('U', 'L');
            return ulong.Parse(hex, System.Globalization.NumberStyles.HexNumber);
        }

        public static string FormatToHex(ulong addr, int length = 32)
        {
            string format = "0x{0:x" + length / 4 + "}";
            return string.Format(format, (uint)addr);
        }

        public class BitmaskNotSequentialException : Exception
        {
            public BitmaskNotSequentialException()
                : base()
            {
            }
        }

        private static void ExtractFirstBitAndSize(ulong val, out int size, out int firstBit)
        {
            size = 0;
            firstBit = -1;
            int state = 0;
            for (int i = 0; i < 64; i++)
            {
                if ((val & ((ulong)1 << i)) == ((ulong)1 << i))
                {
                    if (state == 0)
                        state = 1;
                    else if (state == 2)
                        throw new BitmaskNotSequentialException();

                    size++;
                    if (firstBit < 0)
                        firstBit = i;
                }
                else if (state == 1)
                    state = 2;
            }

            if (size == 0 || firstBit == -1)
            {
                size = 1;
                firstBit = 0;
                throw new Exception("Extracting first bit or size for subregister failed!");
            }
        }

        public static HardwareRegisterSet DeepCopy(HardwareRegisterSet set)
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
    }
}
