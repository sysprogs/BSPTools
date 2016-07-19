/* Copyright (c) 2016 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/
using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;


namespace Atmel_bsp_generator
{
    static class PeripheralRegisterGenerator
    {
        private static Dictionary<string, int> STANDARD_TYPE_SIZES = new Dictionary<string, int>()
            {       { "WoReg", 32 },//Write only
                    { "RwReg", 32 },//Read Write
                    { "RoReg", 32 },//Read only
                    { "uint32_t", 32 }, { "uint16_t", 16 }, { "uint8_t", 8 }
        };
        static int aCountProgress = 0;
        public static Dictionary<string, HardwareRegisterSet[]> GenerateFamilyPeripheralRegistersAtmel(string familyDirectory, string fam)
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
           
             Console.WriteLine("{0}Process PeripheralRegisters{1} ", ++aCountProgress, fam);

            foreach (var fn in Directory.GetFiles(familyDirectory + "\\component", "*.h"))
            {
                var aHRegs = ProcessRegisterSetNamesList(fn, ref lstRegCustomType);

                if (aHRegs != null)
                    sets.AddRange(aHRegs);
            }

            List<HardwareRegisterSet> setsCustomMcu = sets;
          
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
                if (asize <= 32)
                    CustTypReg.Name = CustTypReg.Name.Substring(0, strIdx2);
            }
            //Rename big strucures
            foreach (var v in setsCustomMcu)
            {
                List<HardwareRegister> lr = new List<HardwareRegister>();
                foreach (var r in v.Registers)
                {
                    int strIdx1 = r.Name.IndexOf("-");
                    int strIdx2 = r.Name.IndexOf(":");
                    if (r.SizeInBits <= 32 || r.Name.Contains("RESERVED"))
                    {
                        lr.Add(DeepCopy(r));
                        continue;
                    }
                    string typ = r.Name.Substring(strIdx1 + 1, r.Name.Length - strIdx1 - 1);
                    foreach (var st in setsCustomMcu)
                    {
                        if (st.UserFriendlyName != typ)
                            continue;
                        foreach (var stsub in st.Registers)
                        {
                            HardwareRegister hr = DeepCopy(r);
                            hr.Name = hr.Name.Substring(0, strIdx2) + "." + stsub.Name;
                            hr.SizeInBits = stsub.SizeInBits;
                            lr.Add(hr);
                        }
                    }
                }
                v.Registers = lr.ToArray();
            }




            foreach (var fn in Directory.GetFiles(familyDirectory, "*.h"))
            {
                string aFileNameout = fn;
                if (fn.EndsWith("_1.h"))
                    continue;
                if (fn.EndsWith("_0.h"))
                    aFileNameout = fn.Replace("_0.h", ".h");

                ///Set Base adress
                var aHrdRegFile = ProcessRegisterSetBaseAdress(fn, sets);//etsCustomMcu);
                if (aHrdRegFile.Count > 0)
                  peripherals.Add(Path.GetFileNameWithoutExtension(aFileNameout).ToUpper(), aHrdRegFile.ToArray());
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
                //#define SPI        ((Spi    *)0x40008000U) /**< \brief (SPI       ) Base Address */
                Match m = Regex.Match(ln, @"#define[ \t]+([\w]+)[ \t]+[\(]+([\w]+)[* \t\)]+0x([\w]+)[\)]?.*");
                if (m.Success)
                {
                    var str = m.Groups[3].Value.Replace("U", "");
                    var aAdrBase = Convert.ToInt32(str, 16);
                    aDicBaseAdr.Add(m.Groups[1].Value, aAdrBase);
                    aLstTypBase.Add(new AdrTypReg() { mStrTypAdr = m.Groups[2].Value, mStrNameBaseAdr = m.Groups[1].Value });
                }
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

        public static bool IsDigit(string pstr)
        {
            for (int i = 0; i < pstr.Length; i++)
                if (pstr[i] < 0x30 || pstr[i] > 0x39)
                    return false;

            return true;
        }
        public static List<HardwareRegisterSet> ProcessRegisterSetNamesList(string pFileName, ref List<HardwareRegister> lstRegCustomType)
        {
            List<HardwareRegisterSet> oReg = new List<HardwareRegisterSet>();
            bool aStartCheckReg = false;
            Regex argSearchReg = new Regex(@"[ \t]*([\w]+)[ \t]+([\w]+)[[]*([\w]+)*[]]*[;]?.*");
            Regex argSearchFrendName = new Regex(@"^[ \t]*}[ ]*([\w]*).*");
            List<HardwareRegister> lstReg = new List<HardwareRegister>();
            int sizeArray;
            foreach (var ln in File.ReadAllLines(pFileName))
            {
                if (ln.Contains("typedef struct"))
                {
                    lstReg = new List<HardwareRegister>();
                    aStartCheckReg = true;
                    continue;
                }

                if (!aStartCheckReg)
                    continue;

                Match m = argSearchReg.Match(ln);
                if (m.Success)
                {
                    sizeArray = 0;
                    if (m.Groups[1].Value == "__I" || m.Groups[1].Value == "__O" || m.Groups[1].Value == "__IO")
                    {//SAM4
                        m = Regex.Match(ln, @"[ \t]*[_IO \t]+(uint32_t|uint16_t|uint8_t)[ \t]+([a-zA-Z0-9_]+)[[]*([0-9]+)*[]]*[;]?.*");
                        if (!m.Success)
                            throw new Exception("unkonow format registr :" + ln);
                    }

                    if (IsDigit(m.Groups[3].Value))
                        sizeArray = m.Groups[3].Value == "" ? 1 : Convert.ToInt16(m.Groups[3].Value);
                    else
                    {
                        string astrDefArray = m.Groups[3].Value;
                        foreach (var lndef in File.ReadAllLines(pFileName))
                        {
                            var md = Regex.Match(lndef, @"[ \t]*#define[ \t]+" + astrDefArray + @"[ \t]+([0-9]+).*");
                            if (md.Success)
                                if (IsDigit(md.Groups[1].Value))
                                    sizeArray = Convert.ToInt16(md.Groups[1].Value);
                                else
                                    throw new Exception("No define  " + astrDefArray);
                        }                      
                    }
                    string aType = m.Groups[1].Value;
                    for (int a_cnt = 0; a_cnt < sizeArray; a_cnt++)
                    {
                       HardwareRegister setReg = new HardwareRegister
                        {
                            Name = (sizeArray > 1) ? m.Groups[2].Value + "[" + a_cnt + "]" : m.Groups[2].Value,
                        };
                        setReg.ReadOnly = (aType == "RoReg" || m.Groups[0].Value.Contains("__I ")) ? true : false;

                        if (!STANDARD_TYPE_SIZES.ContainsKey(aType))
                        {
                            setReg.SizeInBits = 0;
                            setReg.Name = setReg.Name + ":1-" + aType;//name register - custom type
                            lstRegCustomType.Add(setReg);
                        }
                        else
                            setReg.SizeInBits = GetSizeInBitsPoor(aType);
                        lstReg.Add(setReg);
                    }
                }
                else
                {
                //end
                    m = argSearchFrendName.Match(ln);
                    if (m.Success)
                    {
                        HardwareRegisterSet setReg = new HardwareRegisterSet();
                        setReg.UserFriendlyName = m.Groups[1].Value;
                        aStartCheckReg = false;
                        oReg.Add(setReg);
                        foreach (var HardReg in lstReg)
                        {
                            var lstSubRegs = ProcessRegisterSetSubRegisters(pFileName);
                            List<HardwareSubRegister> lstSubRegToHard = new List<HardwareSubRegister>();
                            string aPrefNameSubReg = (HardReg.Name + "_").ToUpper();
                            int idx = aPrefNameSubReg.LastIndexOf("[");
                            if (idx > 0)
                                aPrefNameSubReg = aPrefNameSubReg.Substring(0, idx) + "_";
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
        private static int GetSizeBit(string pHex)
        {
            int aOut = 0;
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
            Regex argSearchReg = new Regex(@"(#define) ([A-Z_0-9]+)[ \t]+\(0x([0-9a-f]+)[u< ]+([\w]+).*");
            Regex argSearchRegShift = new Regex(@"(#define)[ \t_]*([A-Z_0-9]+)(_Pos)[ \t]+([0-9]+).*");
            Regex argSearchRegMask = new Regex(@"(#define)[ \t_]*([A-Z_0-9]+)(_Msk)[ \t]+\(0x([0-9a-f]+)[u< ]+([\w]+).*");
            HardwareSubRegister aSubReg = null;
            Dictionary<string, int> aDicPos = new Dictionary<string, int>();

     //       Console.WriteLine("ProcessRegisterSetSubRegisters " + pFileName);

            foreach (var ln in File.ReadAllLines(pFileName))
            {
                var m = argSearchReg.Match(ln);
                if (m.Success)
                {
                    aSubReg = new HardwareSubRegister();
                    aSubReg.Name = m.Groups[2].Value;

                    if (IsDigit(m.Groups[4].Value))
                        aSubReg.FirstBit = Convert.ToInt32(m.Groups[4].Value);
                    else
                    {
                        string astrDefArray = m.Groups[4].Value;
                        aSubReg.FirstBit = aDicPos[astrDefArray];
                    }
                    aSubReg.SizeInBits = GetSizeBit(m.Groups[3].Value);
                    if (aSubRegs.IndexOf(aSubReg) >= 0)
                        throw new Exception("Dubl Sub Registr");
                    aSubRegs.Add(aSubReg);
                    aSubReg = null;
                    continue;
                }
                m = argSearchRegShift.Match(ln);
                if (m.Success)
                {
                    aDicPos[m.Groups[2].Value + "_Pos"]= Convert.ToInt32(m.Groups[4].Value);
                    continue;
                }
                m = argSearchRegMask.Match(ln);
                if (m.Success)
                {
                    aSubReg = new HardwareSubRegister();
                    aSubReg.Name = m.Groups[2].Value;
                    aSubReg.FirstBit = aDicPos[m.Groups[5].Value];
                    aSubReg.SizeInBits = GetSizeBit(m.Groups[4].Value);
                    if (aSubRegs.IndexOf(aSubReg) >= 0)
                        Console.WriteLine("Dubl Sub Registr "+ m.Groups[2].Value);
                    else
                        aSubRegs.Add(aSubReg);
                    aSubReg = null;
                    continue;
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
