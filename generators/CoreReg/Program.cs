using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BSPGenerationTools;
using BSPEngine;
using System.IO;
using System.Xml.Serialization;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace CoreReg
{
    class Program
    {
        public static UInt64? TryParseMaybeHex(string str)
        {
            if (str == null)
                return null;

            int idx = str.IndexOf(' ');
            if (idx != -1)
                str = str.Substring(0, idx);

            ulong addr;

            if (str.StartsWith("0x"))
                return UInt64.TryParse(str.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out addr) ? new ulong?(addr) : null;
            else
                return UInt64.TryParse(str, out addr) ? new ulong?(addr) : null;
        }

        static public void SaveReg(HardwareRegisterSet registers, string pOutFolder, CortexCore core, bool extendNVICRegisters)
        {
            if (extendNVICRegisters)
            {
                var lst = registers.Registers.ToList();
                Regex rgName = new Regex("NVIC_(.*)3");
                for (int i = 0; i < lst.Count; i++)
                {
                    var m = rgName.Match(lst[i].Name);
                    if (m.Success)
                    {
                        if (lst[i - 1].Name != lst[i].Name.Replace("3", "2"))
                            continue;

                        if (lst[i + 1].Name == lst[i].Name.Replace("3", "4"))
                            continue;
                        else
                        {
                            var originalReg = lst[i];
                            ulong address = TryParseMaybeHex(originalReg.Address).Value;
                            ulong increment = TryParseMaybeHex(lst[i].Address).Value - TryParseMaybeHex(lst[i - 1].Address).Value;
                            for (int j = 4; j < 8; j++)
                            {
                                var reg = XmlTools.LoadObjectFromString<HardwareRegister>(XmlTools.SaveObjectToString(originalReg));
                                reg.Name = $"NVIC_{m.Groups[1].Value}{j}";
                                address += increment;
                                reg.Address = $"0x{address:x8}";
                                lst.Insert(++i, reg);
                            }
                        }
                    }
                }

                registers.Registers = lst.ToArray();
            }

            registers.UserFriendlyName = "ARM Cortex " + (core.ToString().Replace("Plus", "+"));
            string outputFile = $@"{pOutFolder}\core_{core}.xml";
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
            XmlTools.SaveObject(registers, outputFile);
        }

        static public List<HardwareRegister> LoadAddReg(string pNameF)
        {
            List<HardwareRegister> aRegisters = new List<HardwareRegister>();
            foreach (var ln in File.ReadAllLines(pNameF))
            {
                var m = Regex.Match(ln, @"(0x[A-F0-9]+)[ ]+([\w]+)[ ]+([\w]+)");
                if (m.Success)
                {
                    bool aflRO = m.Groups[3].Value == "RO" ? true : false;
                    aRegisters.Add(new HardwareRegister
                    { Name =  m.Groups[2].Value, Address = m.Groups[1].Value, SizeInBits = 32, ReadOnly = aflRO });
                }
            }
            return aRegisters;
        }
        //===========================================================
        static void Main(string[] args)
        {
            List<HardwareRegister> registers;

            if (args.Length < 1)
                throw new Exception("Usage: InfineonXMC.exe <InfineonXMC SW package directory>");

            var bspDir = new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules");
            string aDirCoreReg = @"..\..\OutCorexx"; //bspDir.OutputDir

            var setsCortexM0 = SVDParser.ParseSVDFileToHardSet(Path.Combine(bspDir.RulesDir, @"XMC1100.svd"), "Cortex-M0 Private Peripheral Block");
            SaveReg(setsCortexM0, aDirCoreReg, CortexCore.M0, false);
            if (setsCortexM0 == null)
                throw new Exception("No ppb reg M0");

            // Cortex M4---
            var setsCortexM4 = SVDParser.ParseSVDFileToHardSet(Path.Combine(bspDir.RulesDir, @"XMC4100.svd"), "Cortex-M4 Private Peripheral Block");
            if (setsCortexM4 == null)
                throw new Exception("No ppb reg M4");

            SaveReg(setsCortexM4, aDirCoreReg, CortexCore.M4, true);

            // Cortex M3---
            registers = new List<HardwareRegister>(setsCortexM4.Registers);

            //FPU Remove
            registers.Remove(registers.FirstOrDefault(f => "CPACR" == f.Name));
            registers.Remove(registers.FirstOrDefault(f => "FPCCR" == f.Name));
            registers.Remove(registers.FirstOrDefault(f => "FPCAR" == f.Name));
            registers.Remove(registers.FirstOrDefault(f => "FPSCR" == f.Name));
            registers.Remove(registers.FirstOrDefault(f => "FPDSCR" == f.Name));
            setsCortexM4.Registers = registers.ToArray();

            SaveReg(setsCortexM4, aDirCoreReg, CortexCore.M3, true);

            // Cortex M0Plus---
            registers.Clear();
            registers.AddRange(setsCortexM0.Registers);
            registers.AddRange(LoadAddReg(Path.Combine(bspDir.RulesDir, "core_m0mpu.txt")));
            setsCortexM4.Registers = registers.ToArray();

            SaveReg(setsCortexM4, aDirCoreReg, CortexCore.M0Plus, false);

            // Cortex M7---
            setsCortexM4 = SVDParser.ParseSVDFileToHardSet(Path.Combine(bspDir.InputDir, @"CMSIS\Infineon\SVD\XMC4100.svd"), "Cortex-M4 Private Peripheral Block");
            registers.Clear();
            registers.AddRange(setsCortexM4.Registers);
            registers.AddRange(LoadAddReg(Path.Combine(bspDir.RulesDir, "core_m7ppb.txt")));
            setsCortexM4.Registers = registers.ToArray();

            SaveReg(setsCortexM4, aDirCoreReg, CortexCore.M7, true);
        }

    }
}
