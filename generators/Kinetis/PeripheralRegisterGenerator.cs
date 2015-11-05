/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using System;
using System.Collections.Generic;
using BSPEngine;
using System.IO;
using System.Text.RegularExpressions;

namespace kinetis_bsp_generator {

    static class PeripheralRegisterGenerator {

        private static readonly List<string> REGISTERSETS_WITHOUT_SUBREGISTERS = new List<string>() { "CoreDebug", "DWT", "ETB", "ETF", "ETM", "FPB", "ITM", "TPIU", "BP" };
        private static readonly List<string> REGISTERS_WITHOUT_SUBREGISTERS = new List<string>() { "MCG_C9", "MCG_C10", "SIM_CLKDIV2", "AIPS_MPRA",
                "CAU_DIRECT", "CAU_LDR_CAA", "CAU_LDR_CA", "CAU_STR_CAA", "CAU_STR_CA", "CAU_ADR_CAA", "CAU_ADR_CA", "CAU_RADR_CAA", "CAU_RADR_CA", "CAU_XOR_CAA", "CAU_XOR_CA", "CAU_ROTL_CAA", "CAU_ROTL_CA", "CAU_AESC_CAA", "CAU_AESC_CA", "CAU_AESIC_CAA", "CAU_AESIC_CA",
                "USBHS_CONFIGFLAG",
                "ENET_RMON_T_CRC_ALIGN",
                "ENET_RMON_T_DROP",
                "ENET_RMON_T_PACKETS",
                "ENET_RMON_T_BC_PKT",
                "ENET_RMON_T_MC_PKT",
                "ENET_RMON_T_CRC_ALIGN",
                "ENET_RMON_T_UNDERSIZE",
                "ENET_RMON_T_OVERSIZE",
                "ENET_RMON_T_FRAG",
                "ENET_RMON_T_JAB",
                "ENET_RMON_T_COL",
                "ENET_RMON_T_P64",
                "ENET_RMON_T_P65TO127",
                "ENET_RMON_T_P128TO255",
                "ENET_RMON_T_P256TO511",
                "ENET_RMON_T_P512TO1023",
                "ENET_RMON_T_P1024TO2047",
                "ENET_RMON_T_P_GTE2048",
                "ENET_RMON_T_OCTETS",
                "ENET_IEEE_T_DROP",
                "ENET_IEEE_T_FRAME_OK",
                "ENET_IEEE_T_1COL",
                "ENET_IEEE_T_MCOL",
                "ENET_IEEE_T_DEF",
                "ENET_IEEE_T_LCOL",
                "ENET_IEEE_T_EXCOL",
                "ENET_IEEE_T_MACERR",
                "ENET_IEEE_T_CSERR",
                "ENET_IEEE_T_SQE",
                "ENET_IEEE_T_FDXFC",
                "ENET_IEEE_T_OCTETS_OK",
                "ENET_RMON_R_PACKETS",
                "ENET_RMON_R_BC_PKT",
                "ENET_RMON_R_MC_PKT",
                "ENET_RMON_R_CRC_ALIGN",
                "ENET_RMON_R_UNDERSIZE",
                "ENET_RMON_R_OVERSIZE",
                "ENET_RMON_R_FRAG",
                "ENET_RMON_R_JAB",
                "ENET_RMON_R_RESVD_0",
                "ENET_RMON_R_P64",
                "ENET_RMON_R_P65TO127",
                "ENET_RMON_R_P128TO255",
                "ENET_RMON_R_P256TO511",
                "ENET_RMON_R_P512TO1023",
                "ENET_RMON_R_P1024TO2047",
                "ENET_RMON_R_P_GTE2048",
                "ENET_RMON_R_OCTETS",
                "ENET_RMON_R_DROP",
                "ENET_RMON_R_FRAME_OK",
                "ENET_IEEE_R_CRC",
                "ENET_IEEE_R_ALIGN",
                "ENET_IEEE_R_MACERR",
                "ENET_IEEE_R_FDXFC",
                "ENET_IEEE_R_OCTETS_OK",
                "DDR_CR24",
                "MCG_C5",
                "SCB_ACTLR",
                "DMA_DSR",
                "SIM_SOPT1CFG",
                "MCG_C7",
                "FMC",
                "FMC_PFB1CR",
                "USBPHY_DEBUGr",
                "XCVR_SOFT_RESET"
            };

        private static readonly List<string> REGISTERS_ENDING_UNDERSCORE_NUMBER = new List<string> { "RMON_R_RESVD_0" };

        public static HardwareRegisterSet[] GenerateFamilyPeripheralRegisters(string PeripheralHeaderFile) {
            string file = File.ReadAllText(PeripheralHeaderFile);

            Dictionary<string, HardwareRegisterSet> types = new Dictionary<string, HardwareRegisterSet>();
            Dictionary<string, List<HardwareSubRegister>> subregisters = new Dictionary<string, List<HardwareSubRegister>>();
            Dictionary<string, KeyValuePair<string, ulong>> addresses = new Dictionary<string, KeyValuePair<string, ulong>>();
            
            var struct_regex = new Regex(@"typedef\s+struct\s*{(.+?)}\s*([^ ]*)_Type", RegexOptions.Singleline);            

            // 1. Process all structures in the file one by one
            int file_index = 0;
            Match struct_m;
            while ((struct_m = struct_regex.Match(file, file_index)).Success) {
                // 2. Create the register set based on the structure
                string struct_name = struct_m.Groups[2].ToString();
                string struct_regs = struct_m.Groups[1].ToString();
                ulong struct_size;
                HardwareRegisterSet set = new HardwareRegisterSet() {
                    UserFriendlyName = struct_name,
                    // 3. Add the registers to the register set
                    Registers = ProcessStructContents(struct_regs, false, out struct_size)
                };

                types.Add(set.UserFriendlyName, set);

                file_index = struct_m.Index + struct_m.Length;

                // 3. Find the subregisters of the register set
                Regex register_masks_regex = new Regex(struct_name + @"_Register_Masks " + struct_name + @" Register Masks(.+?)@}", RegexOptions.Singleline);
                Match register_masks_m = register_masks_regex.Match(file, file_index);
                if (!register_masks_m.Success)
                    throw new Exception("Failed to find register masks!");
                string register_masks_content = register_masks_m.Groups[0].ToString();

                Regex subregister_regex = new Regex(@"#define\s([^()\s]+)_MASK[\s]*([0-9A-Fa-fx]+)u\s*#define\s([^()\s]+)_SHIFT\s*([0-9]+)\s*", RegexOptions.Singleline);
                var subregisters_m = subregister_regex.Matches(register_masks_content);
                if ((subregisters_m.Count == 0) && !REGISTERSETS_WITHOUT_SUBREGISTERS.Contains(struct_name))
                    throw new Exception("No subregisters found from register masks!");

                foreach (Match subregister_m in subregisters_m) {
                    var subregister_mask_name = subregister_m.Groups[1].ToString();

                    if (!subregister_mask_name.StartsWith(set.UserFriendlyName)) {
                        throw new Exception(string.Format("Wrong subregister mask name {0} for register set {1}", subregister_mask_name, set.UserFriendlyName));
                    }

                    var subregister_mask_set_name = set.UserFriendlyName;
                    var temp_subregister_mask_reg_subreg_name = subregister_mask_name.Substring(subregister_mask_set_name.Length + 1);
                    string subregister_mask_reg_subreg_name = null;

                    foreach (var register in set.Registers) {
                        var cleaned_reg_name = CleanRegisterName(register.Name);
                        if (cleaned_reg_name.EndsWith("r")) {
                            cleaned_reg_name = cleaned_reg_name.Substring(0, cleaned_reg_name.Length - 1);
                        }
                        if (subregister_mask_name.Contains(cleaned_reg_name)) {
                            subregister_mask_reg_subreg_name = temp_subregister_mask_reg_subreg_name;
                            break;
                        }
                    }

                    if (subregister_mask_reg_subreg_name == null) {
                        throw new Exception(string.Format("Wrong combined register and subregister mask name {0} for register set {1}", temp_subregister_mask_reg_subreg_name, set.UserFriendlyName));
                    }
                                        
                    string subregister_mask = subregister_m.Groups[2].ToString();

                    var subregister_shift_name = subregister_m.Groups[3].ToString();
                    
                    if (!subregister_shift_name.StartsWith(set.UserFriendlyName)) {
                        throw new Exception(string.Format("Wrong subregister shift name {0} for register set {1}", subregister_mask_name, set.UserFriendlyName));
                    }

                    string subregister_shift_set_name = set.UserFriendlyName;
                    string subregister_shift_reg_subreg_name = subregister_shift_name.Substring(subregister_shift_set_name.Length + 1);
                    int subregister_first_bit = Int32.Parse(subregister_m.Groups[4].Value);

                    if (subregister_mask_set_name != subregister_shift_set_name)
                        throw new Exception("The subregister mask and shift have non-matching register set names!");

                    if (subregister_mask_reg_subreg_name != subregister_shift_reg_subreg_name)
                        throw new Exception("The subregister mask and shift have non-matching register_subregister names!");

                    string subregister_register_name = null;
                    string subregister_name = null;
                    var foundRegisterLength = int.MinValue;

                    foreach (var register in set.Registers) {
                        string cleaned_reg_name = CleanRegisterName(register.Name);
                        var testRegName = cleaned_reg_name;
                        if (testRegName.EndsWith("r")) {
                            testRegName = testRegName.Substring(0, testRegName.Length - 1);
                        }
                        if (subregister_mask_reg_subreg_name.StartsWith(testRegName + "_") && 
                            ((subregister_register_name == null) || 
                            (subregister_register_name.Length < cleaned_reg_name.Length)) &&
                            testRegName.Length > foundRegisterLength) {
                            foundRegisterLength = cleaned_reg_name.Length;
                            subregister_register_name = cleaned_reg_name;
                            subregister_name = subregister_mask_reg_subreg_name.Substring((testRegName + "_").Length);
                        }
                    }

                    string key = subregister_mask_set_name + "_" + subregister_register_name;
                    if (!subregisters.ContainsKey(key))
                        subregisters[key] = new List<HardwareSubRegister>();

                    subregisters[key].Add(new HardwareSubRegister() {
                        Name = subregister_name,
                        FirstBit = subregister_first_bit,
                        SizeInBits = BitMaskToBitLength(ParseHex(subregister_mask))
                    });
                }

                file_index = register_masks_m.Index + register_masks_m.Length;

                // 4. Find the instances of the register sets
                Regex instance_addresses_regex = new Regex(struct_name + @" - Peripheral instance base addresses(.+?)/\* --", RegexOptions.Singleline);
                Match instance_addresses_m = instance_addresses_regex.Match(file, file_index);
                if (!instance_addresses_m.Success)
                    throw new Exception("No register set base addresses found!");
                string instance_addresses_content = instance_addresses_m.Groups[1].ToString();

                Regex base_addresses_regex = new Regex(@"#define[ \t]+(.+)_BASE[_PTR]*[ \t]+\((.+)u\)");
                var base_addresses_m = base_addresses_regex.Matches(instance_addresses_content);                
                if (base_addresses_m.Count == 0) {
                    throw new Exception("Failed to parse base addresses!");
                }

                foreach (Match base_address_m in base_addresses_m) {
                    string base_address_name = base_address_m.Groups[1].ToString();
                    string base_address_value = base_address_m.Groups[2].ToString();

                    addresses[base_address_name] = new KeyValuePair<string, ulong>(struct_name, ParseHex(base_address_value));
                }

                file_index = instance_addresses_m.Index + instance_addresses_m.Length;
            }

            // Process registers and register sets

            List<HardwareRegisterSet> sets = new List<HardwareRegisterSet>();

            foreach (var set_name in addresses.Keys) {
                HardwareRegisterSet set = DeepCopy(types[addresses[set_name].Key]);
                set.UserFriendlyName = set_name;

                foreach (var register in set.Registers) {
                    // Fix the address
                    register.Address = FormatToHex(ParseHex(register.Address) + addresses[set_name].Value);

                    // Add the subregisters
                    string cleaned_reg_name = register.Name;
                    Regex reg_name_regex1 = new Regex(@"(.*)_[0-9]+_[0-9]+$");
                    Regex reg_name_regex2 = new Regex(@"(.*)_[0-9]+$");

                    Match reg_name1_m = reg_name_regex1.Match(register.Name);
                    Match reg_name2_m = reg_name_regex2.Match(register.Name);
                    if (reg_name1_m.Success)
                        cleaned_reg_name = reg_name1_m.Groups[1].ToString();
                    else if (reg_name2_m.Success && !REGISTERS_ENDING_UNDERSCORE_NUMBER.Contains(register.Name))
                        cleaned_reg_name = reg_name2_m.Groups[1].ToString();

                    string key = addresses[set_name].Key + "_" + cleaned_reg_name;
                    if (subregisters.ContainsKey(key)) {
                        List<HardwareSubRegister> subs = new List<HardwareSubRegister>();
                        foreach (var sub in subregisters[key]) {
                            subs.Add(DeepCopy(sub));
                        }
                        register.SubRegisters = subs.ToArray();
                    } else if (!REGISTERSETS_WITHOUT_SUBREGISTERS.Contains(set_name) && !REGISTERS_WITHOUT_SUBREGISTERS.Contains(key)) {
                        throw new Exception("No subregisters found!");
                    }
                }

                sets.Add(set);
            }

            return sets.ToArray();
        }

        private static HardwareRegister[] ProcessStructContents(string structContents, bool insideUnion, out ulong structSize) {
            List<HardwareRegister> regs = new List<HardwareRegister>();

            ulong hex_offset = 0;
            structSize = 0;

            Dictionary<string, int> dict_type_sizes = new Dictionary<string, int>();
            dict_type_sizes["uint32_t"] = 32;
            dict_type_sizes["uint16_t"] = 16;
            dict_type_sizes["uint8_t"] = 8;

            Regex reg_regex = new Regex(@"([ \t_IO]*)([^ }]+) ([^ \[\]]*)[\[]?([0-9]*)[\]]?[\[]?([0-9]*)[\]]?;", RegexOptions.Multiline);
            Regex union_regex = new Regex(@"union {(.+?)};", RegexOptions.Singleline);
            Regex inner_struct_regex = new Regex(@"struct {(.+?)} ([^ \[\]]+)[\[]?([0-9]*)[\]]?[\[]?([0-9]*)[\]]?", RegexOptions.Singleline);

            Match union_m = null, inner_struct_m = null, reg_m = null;
            int struct_regs_index = 0;
            while (true) {
                union_m = union_regex.Match(structContents, struct_regs_index);
                inner_struct_m = inner_struct_regex.Match(structContents, struct_regs_index);
                reg_m = reg_regex.Match(structContents, struct_regs_index);

                if (!(union_m.Success || inner_struct_m.Success || reg_m.Success))
                    break;

                if (reg_m.Success && !(union_m.Success && (union_m.Index < reg_m.Index)) && !(inner_struct_m.Success && (inner_struct_m.Index < reg_m.Index)))// next match is a register
                {
                    string reg_type = reg_m.Groups[2].ToString();
                    string reg_name = reg_m.Groups[3].ToString();
                    int reg_array_size = string.IsNullOrEmpty(reg_m.Groups[4].Value) ? 1 : Int32.Parse(reg_m.Groups[4].Value);
                    int reg_array_size2 = string.IsNullOrEmpty(reg_m.Groups[5].Value) ? 1 : Int32.Parse(reg_m.Groups[5].Value);
                    struct_regs_index = reg_m.Index + reg_m.Length;

                    if (reg_name.StartsWith("RESERVED")) {
                        hex_offset += (ulong)reg_array_size * (ulong)reg_array_size2 * (ulong)(dict_type_sizes[reg_type] / 8.0);
                        if (insideUnion)
                            throw new Exception("There should be no RESERVED registers inside unions!");
                        continue; // RESERVED registers are not true registers, do not save them
                    }

                    for (int i = 1; i <= reg_array_size; i++) {
                        for (int j = 1; j <= reg_array_size2; j++) {
                            string name = reg_name;
                            if (reg_array_size != 1)
                                name += "_" + (i - 1).ToString();
                            if (reg_array_size2 != 1)
                                name += "_" + (j - 1).ToString();

                            regs.Add(new HardwareRegister() {
                                Name = name,
                                SizeInBits = dict_type_sizes[reg_type],
                                Address = FormatToHex(hex_offset),
                            });
                            hex_offset += (ulong)(dict_type_sizes[reg_type] / 8.0);
                            if (insideUnion && (structSize == 0))
                                structSize = (ulong)reg_array_size * (ulong)reg_array_size2 * (ulong)(dict_type_sizes[reg_type] / 8.0);
                        }
                    }
                    if (insideUnion)
                        hex_offset = 0;
                } else if (union_m.Success && !(reg_m.Success && (reg_m.Index < union_m.Index)) && !(inner_struct_m.Success && (inner_struct_m.Index < union_m.Index)))// next match is an union
                  {
                    string union_contents = union_m.Groups[1].ToString();
                    ulong size;
                    HardwareRegister[] union_regs = ProcessStructContents(union_contents, true, out size);

                    //Filter out useless registers of unions
                    HardwareRegister main_reg = null;
                    foreach (var reg in union_regs) {
                        if ((main_reg == null) || (main_reg.Name.Length > reg.Name.Length))
                            main_reg = reg;
                        reg.Address = FormatToHex(ParseHex(reg.Address) + hex_offset);
                    }

                    bool split_reg = true;
                    foreach (var reg in union_regs) {
                        if (!((reg.Name == main_reg.Name) ||
                            (reg.Name == main_reg.Name + "L") ||
                            (reg.Name == main_reg.Name + "H") ||
                            (reg.Name == main_reg.Name + "LL") ||
                            (reg.Name == main_reg.Name + "LU") ||
                            (reg.Name == main_reg.Name + "HL") ||
                            (reg.Name == main_reg.Name + "HU"))) {
                            split_reg = false;
                            break;
                        }
                    }

                    if (split_reg)
                        regs.Add(main_reg);
                    else
                        regs.AddRange(union_regs);
                    hex_offset += size;
                    struct_regs_index = union_m.Index + union_m.Length;
                } else if (inner_struct_m.Success && !(union_m.Success && (union_m.Index < inner_struct_m.Index)) && !(reg_m.Success && (reg_m.Index < inner_struct_m.Index)))// next match is a struct
                  {
                    string inner_struct_name = inner_struct_m.Groups[2].ToString();
                    string inner_struct_contents = inner_struct_m.Groups[1].ToString();
                    int inner_struct_array_size = string.IsNullOrEmpty(inner_struct_m.Groups[3].Value) ? 1 : Int32.Parse(inner_struct_m.Groups[3].Value);
                    int inner_struct_array_size2 = string.IsNullOrEmpty(inner_struct_m.Groups[4].Value) ? 1 : Int32.Parse(inner_struct_m.Groups[4].Value);

                    ulong inner_struct_size;
                    HardwareRegister[] struct_regs = ProcessStructContents(inner_struct_contents, false, out inner_struct_size);

                    for (int i = 1; i <= inner_struct_array_size; i++) {
                        for (int j = 1; j <= inner_struct_array_size2; j++) {
                            string suffix = "";
                            if (inner_struct_array_size != 1)
                                suffix += "_" + i.ToString();
                            if (inner_struct_array_size2 != 1)
                                suffix += "_" + j.ToString();

                            foreach (var struct_reg in struct_regs) {
                                HardwareRegister cpy = DeepCopy(struct_reg);
                                cpy.Name += suffix;
                                cpy.Address = FormatToHex(ParseHex(cpy.Address) + hex_offset);
                                regs.Add(cpy);
                            }
                            if (!insideUnion)
                                hex_offset += inner_struct_size;
                            else {
                                structSize = inner_struct_size;
                                if ((inner_struct_array_size != 1) || (inner_struct_array_size2 != 1))
                                    throw new Exception("Structures inside unions are not expected to be arrays!");
                            }
                        }
                    }

                    struct_regs_index = inner_struct_m.Index + inner_struct_m.Length;
                } else {
                    throw new Exception("Cannot parse struct contents!");
                }
            }

            if (!insideUnion)
                structSize = hex_offset;
            return regs.ToArray();
        }

        private static string CleanRegisterName(string registerName) {
            string cleaned_reg_name = registerName;
            Regex reg_name_regex1 = new Regex(@"(.*)_[0-9]+_[0-9]+$");
            Regex reg_name_regex2 = new Regex(@"(.*)_[0-9]+$");

            Match reg_name1_m = reg_name_regex1.Match(registerName);
            Match reg_name2_m = reg_name_regex2.Match(registerName);
            if (reg_name1_m.Success)
                cleaned_reg_name = reg_name1_m.Groups[1].ToString();
            else if (reg_name2_m.Success && !REGISTERS_ENDING_UNDERSCORE_NUMBER.Contains(registerName))
                cleaned_reg_name = reg_name2_m.Groups[1].ToString();
            return cleaned_reg_name;
        }

        private static int BitMaskToBitLength(ulong mask) {
            int length = 0;

            for (int i = 0; i <= 31; i++) {
                if (((int)mask & (1 << i)) != 0)
                    length++;
            }

            return length;
        }

        private static ulong ParseHex(string hex) {
            if (hex.StartsWith("0x"))
                hex = hex.Substring(2);
            return ulong.Parse(hex, System.Globalization.NumberStyles.HexNumber);
        }

        private static string FormatToHex(ulong addr, int length = 32) {
            string format = "0x{0:x" + length / 4 + "}";
            return string.Format(format, (uint)addr);
        }

        private static HardwareRegisterSet DeepCopy(HardwareRegisterSet set) {
            HardwareRegisterSet set_new = new HardwareRegisterSet {
                UserFriendlyName = set.UserFriendlyName,
                ExpressionPrefix = set.ExpressionPrefix,
            };

            if (set.Registers != null) {
                set_new.Registers = new HardwareRegister[set.Registers.Length];
                for (int i = 0; i < set.Registers.Length; i++) {
                    set_new.Registers[i] = DeepCopy(set.Registers[i]);
                }
            }

            return set_new;
        }

        private static HardwareRegister DeepCopy(HardwareRegister reg) {
            HardwareRegister reg_new = new HardwareRegister {
                Name = reg.Name,
                Address = reg.Address,
                GDBExpression = reg.GDBExpression,
                ReadOnly = reg.ReadOnly,
                SizeInBits = reg.SizeInBits
            };

            if (reg.SubRegisters != null) {
                reg_new.SubRegisters = new HardwareSubRegister[reg.SubRegisters.Length];
                for (int i = 0; i < reg.SubRegisters.Length; i++) {
                    reg_new.SubRegisters[i] = DeepCopy(reg.SubRegisters[i]);
                }
            }

            return reg_new;
        }

        private static HardwareSubRegister DeepCopy(HardwareSubRegister subreg) {
            HardwareSubRegister subreg_new = new HardwareSubRegister {
                Name = subreg.Name,
                FirstBit = subreg.FirstBit,
                SizeInBits = subreg.SizeInBits,
                KnownValues = (subreg.KnownValues != null) ? (KnownSubRegisterValue[])subreg.KnownValues.Clone() : null
            };

            return subreg_new;
        }
    }
}
