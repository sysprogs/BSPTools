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

        static public void SaveReg(HardwareRegisterSet registers, string pOutFolder, CortexCore core)
        {
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
            SaveReg(setsCortexM0, aDirCoreReg, CortexCore.M0);
            if (setsCortexM0 == null)
                throw new Exception("No ppb reg M0");

            // Cortex M4---
            var setsCortexM4 = SVDParser.ParseSVDFileToHardSet(Path.Combine(bspDir.RulesDir, @"XMC4100.svd"), "Cortex-M4 Private Peripheral Block");
            if (setsCortexM4 == null)
                throw new Exception("No ppb reg M4");

            SaveReg(setsCortexM4, aDirCoreReg, CortexCore.M4);

            // Cortex M3---
            registers = new List<HardwareRegister>(setsCortexM4.Registers);

            //FPU Remove
            registers.Remove(registers.FirstOrDefault(f => "CPACR" == f.Name));
            registers.Remove(registers.FirstOrDefault(f => "FPCCR" == f.Name));
            registers.Remove(registers.FirstOrDefault(f => "FPCAR" == f.Name));
            registers.Remove(registers.FirstOrDefault(f => "FPSCR" == f.Name));
            registers.Remove(registers.FirstOrDefault(f => "FPDSCR" == f.Name));
            setsCortexM4.Registers = registers.ToArray();

            SaveReg(setsCortexM4, aDirCoreReg, CortexCore.M3);

            // Cortex M0Plus---
            registers.Clear();
            registers.AddRange(setsCortexM0.Registers);
            registers.AddRange(LoadAddReg(Path.Combine(bspDir.RulesDir, "core_m0mpu.txt")));
            setsCortexM4.Registers = registers.ToArray();

            SaveReg(setsCortexM4, aDirCoreReg, CortexCore.M0Plus);

            // Cortex M7---
            setsCortexM4 = SVDParser.ParseSVDFileToHardSet(Path.Combine(bspDir.InputDir, @"CMSIS\Infineon\SVD\XMC4100.svd"), "Cortex-M4 Private Peripheral Block");
            registers.Clear();
            registers.AddRange(setsCortexM4.Registers);
            registers.AddRange(LoadAddReg(Path.Combine(bspDir.RulesDir, "core_m7ppb.txt")));
            setsCortexM4.Registers = registers.ToArray();

            SaveReg(setsCortexM4, aDirCoreReg, CortexCore.M7);
        }

    }
}
