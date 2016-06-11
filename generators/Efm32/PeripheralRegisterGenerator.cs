using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SLab_bsp_generator
{
    static class PeripheralRegisterGenerator
    {
        private static Dictionary<string, int> STANDARD_TYPE_SIZES = new Dictionary<string, int>()
            { { "uint32_t", 32 }, { "uint16_t", 16 }, { "uint8_t", 8 } };

        public static Dictionary<string, HardwareRegisterSet[]> GenerateFamilyPeripheralRegistersEFM32(string familyDirectory, string fam)
        {

            // Create the hardware register sets for each subfamily
            Dictionary<string, HardwareRegisterSet[]> peripherals = new Dictionary<string, HardwareRegisterSet[]>();
            // Create a hardware register set for each base address with the correctly calculated addresses
            List<HardwareRegisterSet> sets = new List<HardwareRegisterSet>();
            HardwareRegisterSet set = new HardwareRegisterSet();
            List<HardwareRegister> lstRegCustomType = new List<HardwareRegister>();
            List<HardwareRegisterSet> setsCustom = new List<HardwareRegisterSet>();

            Dictionary<string, int> aDicSizeTypeDefStuct = new Dictionary<string, int>();
            int aSizeStruct = 0;

            foreach (var fn in Directory.GetFiles(familyDirectory, fam + "_*.h"))
            {
                var aHRegs = ProcessRegisterSetNamesList(fn, ref lstRegCustomType);

                if (aHRegs != null)
                    sets.AddRange(aHRegs);
            }


            foreach (var fn in Directory.GetFiles(familyDirectory, fam + "*.h"))
            {
                string sr = "^" + fam + "[0-9]+.*";
                if (!Regex.IsMatch(Path.GetFileName(fn), sr, RegexOptions.IgnoreCase))
                    continue;

                setsCustom = ProcessRegisterSetNamesList(fn, ref lstRegCustomType);
                List<HardwareRegisterSet> setsCustomMcu = new List<HardwareRegisterSet>();

                foreach (var aHRegMcu in sets)
                {
                    bool aflRegRedefined = false;
                    foreach (var aHrCustom in setsCustom)
                    {
                        if (aHrCustom.UserFriendlyName == aHRegMcu.UserFriendlyName)
                        {
                            aflRegRedefined = true;
                            setsCustomMcu.Add(aHrCustom);
                            break;
                        }
                    }
                    if (!aflRegRedefined)
                        setsCustomMcu.Add(aHRegMcu);
                }

                //Calculate size  Struct
                foreach (var PerStruct in setsCustomMcu)
                {
                    aSizeStruct = 0;
                    foreach (var reg in PerStruct.Registers)
                    {
                        if (reg.SizeInBits == 0)
                        {
                            aSizeStruct = 0;
                            break;
                        }
                        else
                            aSizeStruct += reg.SizeInBits;
                    }
                    aDicSizeTypeDefStuct[PerStruct.UserFriendlyName] = aSizeStruct;
                }

                //Calculate size array custom type 
                foreach (var CustTypReg in lstRegCustomType)
                {
                    int strIdx1 = CustTypReg.Name.IndexOf("-");
                    int strIdx2 = CustTypReg.Name.IndexOf(":");
                    if (strIdx1 < 0 || strIdx2 < 0)
                        continue;
                    string str1 = CustTypReg.Name.Substring(strIdx1 + 1, CustTypReg.Name.Length - strIdx1 - 1);
                    string asArray = CustTypReg.Name.Substring(strIdx2 + 1, strIdx1 - strIdx2 - 1);

                    int asize = aDicSizeTypeDefStuct[str1] * Convert.ToInt32(asArray);
                    CustTypReg.SizeInBits = asize;
                    if(asize <= 32)
                        CustTypReg.Name = CustTypReg.Name.Substring(0, strIdx2);                  
                }
                //Rename big strucures
             foreach(var v in setsCustomMcu )
                {
                List<HardwareRegister> lr = new List<HardwareRegister>();
                 foreach (var r in   v.Registers)
                    {
                        int strIdx1 = r.Name.IndexOf("-");
                        int strIdx2 = r.Name.IndexOf(":");
                        if (r.SizeInBits <= 32 || r.Name.Contains("RESERVED"))
                        {
                            lr.Add(DeepCopy(r));
                            continue;
                        }                     
                        string typ = r.Name.Substring(strIdx1+1, r.Name.Length - strIdx1 - 1);
                        foreach (var st in setsCustomMcu)
                        {
                            if (st.UserFriendlyName != typ)
                                continue;
                            foreach(var stsub in st.Registers)
                             {
                                HardwareRegister hr = DeepCopy(r);
                                hr.Name = hr.Name.Substring(0,strIdx2) +"."+ stsub.Name;
                                hr.SizeInBits = stsub.SizeInBits;
                                lr.Add(hr);
                            }
                        }                      
                    }
                    v.Registers = lr.ToArray();
                }
                ///Set Base adress
                var aHrdRegFile = ProcessRegisterSetBaseAdress(fn, setsCustomMcu);
                peripherals.Add(Path.GetFileNameWithoutExtension(fn).ToUpper(), aHrdRegFile.ToArray());
            }
            return peripherals;
        }

        public class AdrTypReg
        {
            public string mStrTypAdr;
            public string mStrNameBaseAdr;
        }

        public static List<HardwareRegisterSet> ProcessRegisterSetBaseAdress(string pFileName, List<HardwareRegisterSet> pLstHardReg)
        {
            Dictionary<string, Int32> aDicBaseAdr = new Dictionary<string, Int32>();
            List<AdrTypReg> aLstTypBase = new List<AdrTypReg>();
            List<HardwareRegisterSet> oPerReg = new List<HardwareRegisterSet>();
            foreach (var ln in File.ReadAllLines(pFileName))
            {
                Match m = Regex.Match(ln, @"#define[ \t]+([\w]+)_BASE[ \t]+[\(]?([\w]+)[\)]?.*");
                if (m.Success)
                {
                    var str = m.Groups[2].Value.Replace("UL", "");
                    var aAdrBase = Convert.ToInt32(str, 16);
                    aDicBaseAdr.Add(m.Groups[1].Value + "_BASE", aAdrBase);
                }
                m = Regex.Match(ln, @"#define[ \t]+([\w]+)[ \t]+[\(]*([\w]+)_TypeDef[ \t\*\)]+([\w]+).*");
                if (m.Success)
                    aLstTypBase.Add(new AdrTypReg() { mStrTypAdr = m.Groups[2].Value, mStrNameBaseAdr = m.Groups[3].Value });
            }

            foreach (var aPointBase in aLstTypBase)
            {
                foreach (var aPerepReg in pLstHardReg)
                {
                    if (aPointBase.mStrTypAdr == aPerepReg.UserFriendlyName)
                    {
                        HardwareRegisterSet aPerepMcuReg = DeepCopy(aPerepReg);
                        var aBaseAdrUnit = aDicBaseAdr[aPointBase.mStrNameBaseAdr];
                        int aCntAdr = 0;
                        foreach (var aHardReg in aPerepMcuReg.Registers)
                        {
                            aBaseAdrUnit += aCntAdr;
                            aHardReg.Address = FormatToHex((ulong)aBaseAdrUnit);
                            aCntAdr = aHardReg.SizeInBits / 8;
                        }
                        aPerepMcuReg.UserFriendlyName = aPointBase.mStrNameBaseAdr.Replace("_BASE", "");
                        oPerReg.Add(aPerepMcuReg);
                        break;
                    }
                }
            }
            return oPerReg;
        }

        public static int GetSizeInBitsPoor(string pTyp, int pSizeArray = 1)
        {
            return (STANDARD_TYPE_SIZES[pTyp] * pSizeArray);
        }

        class RegexCollection
        {
            public Regex argSearchReg = new Regex(@"[ \t]*(__IO|__I|__O)[ \t]+(uint32_t|uint16_t|uint8_t)[ \t]+([A-Z0-9]+)[[]*([0-9]+)*[]]*[;]?.*", RegexOptions.Compiled);
            public Regex argSearchRegReserv = new Regex(@"[ \t]*(uint32_t|uint16_t|uint8_t)[ \t]+([A-Z0-9]+)[[]*([0-9]+)*[]]*[;]?.*", RegexOptions.Compiled);
            public Regex argSearchRegTypeDef = new Regex(@"[ \t]*([A-Z0-9_]+)_TypeDef[ \t]+([A-Z0-9]+)[[]*([0-9]+)*[]]*[;]?.*", RegexOptions.Compiled);
            public Regex argSearchFrendName = new Regex(@"^[ \t}]*([A-Z_0-9]+)(_TypeDef;).*", RegexOptions.Compiled);

            public Regex argSearchRegShift = new Regex(@"(#define)[ \t_]*([A-Z_]+)(_SHIFT)[ \t]+([0-9]+).*", RegexOptions.Compiled);
            public Regex argSearchRegMask = new Regex(@"(#define)[ \t_]*([A-Z_]+)(_MASK)[ \t]+([0-9xA-F]*[U]?[L]?).*", RegexOptions.Compiled);
        }

        static RegexCollection Regexes = new RegexCollection();

        public static List<HardwareRegisterSet> ProcessRegisterSetNamesList(string pFileName, ref List<HardwareRegister> lstRegCustomType)
        {
            List<HardwareRegisterSet> oReg = new List<HardwareRegisterSet>();
            bool aStartCheckReg = false;
            List<HardwareRegister> lstReg = new List<HardwareRegister>();
            int sizeArray;
            foreach (var ln in File.ReadAllLines(pFileName))
            {
                if (ln == "typedef struct")
                {
                    lstReg = new List<HardwareRegister>();
                    aStartCheckReg = true;
                }

                if (!aStartCheckReg)
                    continue;

                Match m = Regexes.argSearchReg.Match(ln);
                if (m.Success)
                {
                    sizeArray = m.Groups[4].Value == "" ? 1 : Convert.ToInt16(m.Groups[4].Value);
                    for (int a_cnt = 0; a_cnt < sizeArray; a_cnt++)
                    {
                        lstReg.Add(new HardwareRegister()
                        {
                            Name = (sizeArray > 1) ? m.Groups[3].Value + "[" + a_cnt + "]" : m.Groups[3].Value,
                            ReadOnly = (m.Groups[1].Value == "__I") ? true : false,
                            SizeInBits = GetSizeInBitsPoor(m.Groups[2].Value)
                        });
                    }
                }
                else
                {
                    m = Regexes.argSearchRegReserv.Match(ln); //reserverd array
                    if (m.Success)
                    {
                        sizeArray = m.Groups[3].Value == "" ? 1 : Convert.ToInt32(m.Groups[3].Value);
                        lstReg.Add(new HardwareRegister()
                        {
                            Name = m.Groups[2].Value,
                            SizeInBits = GetSizeInBitsPoor(m.Groups[1].Value, Convert.ToInt32(sizeArray)),
                        });
                    }
                    // Typedef Array 
                    m = Regexes.argSearchRegTypeDef.Match(ln);
                    if (m.Success)
                    {
                        sizeArray = m.Groups[3].Value == "" ? 1 : Convert.ToInt16(m.Groups[3].Value);
                        for (int a_cnt = 0; a_cnt < sizeArray; a_cnt++)
                        {
                            HardwareRegister setReg = new HardwareRegister();
                            if (sizeArray > 1)
                                setReg.Name = m.Groups[2].Value + "[" + a_cnt + "]:1-" + m.Groups[1].Value;//name register - custom type
                            else
                                setReg.Name = m.Groups[2].Value + ":" + sizeArray + "-" + m.Groups[1].Value;//name register - custom type

                            setReg.SizeInBits = 0;
                            lstReg.Add(setReg);
                            lstRegCustomType.Add(setReg);
                        }
                    }
                    //end
                    m = Regexes.argSearchFrendName.Match(ln);
                    if (m.Success)
                    {
                        HardwareRegisterSet setReg = new HardwareRegisterSet();
                        setReg.UserFriendlyName = m.Groups[1].Value;
                        aStartCheckReg = false;
                        oReg.Add(setReg);
                        var originalSubRegs = ProcessRegisterSetSubRegisters(pFileName);

                        foreach (var HardReg in lstReg)
                        {
                            var lstSubRegs = originalSubRegs.Select(s => CloneSubregister(s)).ToList();
                            List<HardwareSubRegister> lstSubRegToHard = new List<HardwareSubRegister>();
                            string aPrefNameSubReg = setReg.UserFriendlyName + "_" + HardReg.Name + "_";
                            foreach (var SubReg in lstSubRegs)
                            {
                                if (SubReg.Name.StartsWith(aPrefNameSubReg))
                                {
                                    SubReg.Name = SubReg.Name.Remove(0, aPrefNameSubReg.Length);
                                    SubReg.ParentRegister = HardReg;
                                    lstSubRegToHard.Add(SubReg);
                                }
                            }
                            HardReg.SubRegisters = lstSubRegToHard.ToArray();
                        }
                        setReg.Registers = lstReg.ToArray();
                    }
                }
            }
            return oReg;
        }

        private static HardwareSubRegister CloneSubregister(HardwareSubRegister s)
        {
            return new HardwareSubRegister
            {
                FirstBit = s.FirstBit,
                KnownValues = (KnownSubRegisterValue[])s.KnownValues?.Clone(),
                Name = s.Name,
                ParentRegister = s.ParentRegister,
                SizeInBits = s.SizeInBits
            };
        }

        private static int GetSizeBit(string pHex)
        {
            int aOut = 0;
            pHex = pHex.Remove(pHex.IndexOf("UL"));
            ulong intHex = ParseHex(pHex);
            while (intHex != 0)
            {
                if ((intHex & 0x01) == 1)
                    aOut++;
                intHex = intHex >> 1;
            }
            return aOut;
        }
        public static List<HardwareSubRegister> ProcessRegisterSetSubRegisters(string pFileName)
        {
            List<HardwareSubRegister> aSubRegs = new List<HardwareSubRegister>();
            HardwareSubRegister aSubReg = null;
            foreach (var ln in File.ReadAllLines(pFileName))
            {
                var m = Regexes.argSearchRegShift.Match(ln);
                if (m.Success)
                {
                    aSubReg = new HardwareSubRegister();
                    aSubReg.Name = m.Groups[2].Value;
                    aSubReg.FirstBit = Convert.ToInt32(m.Groups[4].Value);
                    continue;
                }
                m = Regexes.argSearchRegMask.Match(ln);
                if (m.Success)
                {
                    if (aSubReg == null)
                        continue;
                    aSubReg.Name = m.Groups[2].Value;
                    aSubReg.SizeInBits = GetSizeBit(m.Groups[4].Value);
                    if (aSubRegs.IndexOf(aSubReg) >= 0)
                        throw new Exception("Dubl Sub Registr");
                    aSubRegs.Add(aSubReg);
                    aSubReg = null;
                }
            }
            return aSubRegs;
        }
        //------------------------------

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
    }
}
