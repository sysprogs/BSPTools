/* Copyright (c) 2016 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/
using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CC3200_bsp_generator
{
    static class PeripheralRegisterGenerator
    {
        public class CBaseAdress
        {
            public string Name;
            public ulong adress;
            public bool used;
        }

        private static List<CBaseAdress> lstBaseAdress = null; //list base adress
        private static Dictionary<string, ulong> adicAdrrOffsetDriv;
        private static SortedList<string, string> adicBitsOffsetDriv;
        private static string mNameCurrentAdr;
       //---------------------------------
       private static bool LoadBaseAdress(string pFileMap)
        {
            if (!File.Exists(pFileMap)) return false;
            //--------Load Adress Base ------------------
            lstBaseAdress = new List<CBaseAdress>();
            foreach (var ln in File.ReadAllLines(pFileMap))
            {
                Match m = Regex.Match(ln, @"#define[ \t]+([\w]+)_BASE[ \t]+0x([0-9A-F]+)?.*");
                if (m.Success)
                    lstBaseAdress.Add(new CBaseAdress { Name = m.Groups[1].Value, adress = ParseHex(m.Groups[2].Value) });
            }
            return true;
        }
        //---------------------------------
        private static void CheckAllBaseAdress()
        {
            //-----Check base adress
            foreach (var ba in lstBaseAdress)
                if (ba.used == false)
                    Console.WriteLine("not used base adress " + ba.Name);
        }
        //---------------------------------
        private static List<HardwareRegisterSet> ProcessParseSetsReg(string aDir)
        {
            if (!LoadBaseAdress(Path.Combine(aDir, "hw_memmap.h")))
                throw new Exception("no map memory file");
            //-------Load Registry-------------------------
            List<HardwareRegisterSet> setOut = new List<HardwareRegisterSet>();
            foreach (var fn in Directory.GetFiles(aDir, "hw_*.h"))
            {
                var aHR = ProcessRegisterSetNamesList(fn);
                if (aHR != null)
                    setOut.AddRange(aHR);
            }
            //----------------------
            CheckAllBaseAdress();

            return setOut;
        }
        //---------------------------------
        public static HardwareRegisterSet[] GeneratePeripheralRegisters(string familyDirectory)
        {
            // Create the hardware register sets 
            Console.WriteLine("Process Peripheral Registers");
            return (ProcessParseSetsReg(familyDirectory).ToArray());
        }
        //---------------------------------
        private static string ChangeNamePrefixAdress(string pNamePrefix)
        {
            switch (pNamePrefix ?? "")
            {
                case "MCSPI":
                    return "SPI";
                case "APPS_RCM":
                    return "ARCM";
                case "FLASH_CTRL":
                    return "FLASH_CONTROL";
                case "MCASP":
                    return "I2S";
                case "MMCHS":
                    return "SDHOST";
                case "STACK_DIE_CTRL":
                    return "STACKDIE_CTRL";
            }
            return pNamePrefix;
        }
        //---------------------------------
        public static List<CBaseAdress> GetBaseAdress(string NameReg)
        {
            var astrShortName = ChangeNamePrefixAdress(NameReg);
            return lstBaseAdress.FindAll(p => (astrShortName != "" && p.Name.Contains(astrShortName)));
        }
        //---------------------------------
        private static bool IsExcepMacrosRegisters(string pMacros)
        {
            string[] astrMacExc = new[] { "#define SHAMD5_HASH512_ODIGEST_O_DATA_M ", "#define SHAMD5_HASH512_IDIGEST_O_DATA_M " };

            foreach (var st in astrMacExc)
                if (pMacros.StartsWith(st))
                    return true;

            return false;
        }
        //---------------------------------
        private static bool IsParametrMacros(string pMacros, SortedList<string, string> alstOffset)
        {
            string astrNameSubReg = pMacros;
            int idxRegisters = astrNameSubReg.LastIndexOf('_');
            while (idxRegisters > 0)
            {
                astrNameSubReg = astrNameSubReg.Remove(idxRegisters);
                string shotNameReg = astrNameSubReg + "_M";
                if (alstOffset.ContainsKey(shotNameReg))
                {
                    return true;
                }
                idxRegisters = astrNameSubReg.LastIndexOf('_');
            }
            return false;
        }
        //---------------------------------
        private static  bool LoadDictonaryRegisters(string pFileName)
        {
            adicAdrrOffsetDriv = new Dictionary<string, ulong>();
            adicBitsOffsetDriv = new SortedList<string, string>();
            string strLndef = "";
            string astrUserFrendlyName = "";

            if (!File.Exists(pFileName))
                return false;
            foreach (var ln in File.ReadAllLines(pFileName))
            {
                if (ln.EndsWith(@"\")) //  check macros to next string
                {
                    strLndef = ln.Replace(@"\", "");
                    continue;
                }
                strLndef = strLndef + ln;
                //// The following are defines for the register offsets.
                Match m1 = Regex.Match(strLndef, @"#define[ \t]+([\w\d]+)_O_([\w\d]+)[ \t]+0x([0-9A-F]+)");

                bool ablExcept = IsExcepMacrosRegisters(strLndef);

                if (m1.Success && !ablExcept)
                {
                    adicAdrrOffsetDriv[m1.Groups[1].Value + "_" + m1.Groups[2].Value] = ParseHex(m1.Groups[3].Value);
                    if (astrUserFrendlyName != m1.Groups[1].Value && astrUserFrendlyName != "")
                        throw new Exception("different name prefix of registers");

                    astrUserFrendlyName = m1.Groups[1].Value;
                }
                else
                {
                    //     The following are defines for the bit fields in the register.
                    m1 = Regex.Match(strLndef, @"#define[ \t]+([\w\d]+)[ \t]+0x([0-9A-F]+)");
                    if (m1.Success)
                        if (!IsParametrMacros(m1.Groups[1].Value, adicBitsOffsetDriv) || ablExcept)
                            adicBitsOffsetDriv[m1.Groups[1].Value] = m1.Groups[2].Value;
                }
                strLndef = "";
            }

            mNameCurrentAdr = astrUserFrendlyName;
            return true;
        }
        //---------------------------------
        private static List<HardwareRegisterSet> ProcessLoadHardwareRegisterSet()
        {
            List<HardwareRegisterSet> oReg = new List<HardwareRegisterSet>();
            List<HardwareSubRegister> alstSubReg = new List<HardwareSubRegister>();
            var aLstPerep = GetBaseAdress(mNameCurrentAdr);
            foreach (var perep in aLstPerep)
            {
                List<HardwareRegister> alstReg = new List<HardwareRegister>();
                ulong aBaseAdress = perep.adress;
                perep.used = true;
                foreach (var reg in adicAdrrOffsetDriv)
                {

                    HardwareRegister Reg = new HardwareRegister();
                    alstSubReg.Clear();
                    Reg.Name = reg.Key;
                    Reg.Address = FormatToHex(reg.Value + aBaseAdress);
                    Reg.SizeInBits = 32;
                    foreach (var subReg in adicBitsOffsetDriv)
                    {
                        if (subReg.Key.StartsWith(Reg.Name))
                        {
                            HardwareSubRegister hsr = new HardwareSubRegister
                            {
                                Name = subReg.Key.Remove(0, Reg.Name.Length + 1),
                                ParentRegister = Reg,
                                OriginalMacroBase = Reg.Name,
                                SizeInBits = GetSizeBit(subReg.Value),
                                FirstBit = GetFirstBit(subReg.Value),
                            };
                            if (hsr.SizeInBits == 0)
                                Console.WriteLine("size subreg 0 " + hsr.Name);
                            alstSubReg.Add(hsr);

                        }
                    }
                    Reg.SubRegisters = alstSubReg.ToArray();
                    alstReg.Add(Reg);
                }

                if (alstReg.Count > 0)
                    oReg.Add(new HardwareRegisterSet
                    {
                        ExpressionPrefix = mNameCurrentAdr,
                        UserFriendlyName = perep.Name,// astrUserFrendlyName,
                        Registers = alstReg.ToArray()
                    });
            }
            return oReg;
        }
        //---------------------------------
        public static List<HardwareRegisterSet> ProcessRegisterSetNamesList(string pFileName)
        {
            if(LoadDictonaryRegisters(pFileName))
                return (ProcessLoadHardwareRegisterSet());

            return null;
        }
        //---------------------------------
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
        //---------------------------------
        private static int GetFirstBit(string pHex)
        {
            int aOut = 0;
            ulong intHex = ParseHex(pHex);
            if (intHex == 0)
                Console.WriteLine(" new Exception(no set bit");
            else
                while ((intHex & 0x01) != 1)
                {
                    aOut++;
                    intHex = intHex >> 1;
                }
            return aOut;
        }
        //------------------------------
        private static ulong ParseHex(string hex)
        {
            if (hex.StartsWith("0x"))
                hex = hex.Substring(2);
            return ulong.Parse(hex, System.Globalization.NumberStyles.HexNumber);
        }
        //---------------------------------
        private static string FormatToHex(ulong addr, int length = 32)
        {
            string format = "0x{0:x" + length / 4 + "}";
            return string.Format(format, (uint)addr);
        }
        //---------------------------------
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
        //---------------------------------
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
        //---------------------------------
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
