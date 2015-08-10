/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;

namespace BSPGenerationTools
{
    public class SVDParser
    {
        static void ProcessRegister(XmlElement reg, List<HardwareRegister> registers, string prefix, uint baseAddr, int periphSize)
        {
            string regName = reg.SelectSingleNode("name").InnerText;
            string access = reg.SelectSingleNode("access")?.InnerText;
            uint addrOff = ParseAddr(reg.SelectSingleNode("addressOffset").InnerText);

            int count = 1, step = 0;
            string dim = reg.SelectSingleNode("dim")?.InnerText;
            if (dim != null)
            {
                count = int.Parse(dim);
                step = (int)ParseAddr(reg.SelectSingleNode("dimIncrement").InnerText);
                if (step * 8 != periphSize)
                    throw new Exception("Mismatching array step for " + regName);
            }

            for (int i = 0; i < count; i++)
            {
                List<HardwareSubRegister> subregs = new List<HardwareSubRegister>();
                foreach (XmlElement fld in reg.SelectNodes("fields/field"))
                {
                    var subreg = new HardwareSubRegister
                    {
                        Name = fld.SelectSingleNode("name").InnerText,
                        FirstBit = int.Parse(fld.SelectSingleNode("lsb").InnerText),
                    };

                    subreg.SizeInBits = int.Parse(fld.SelectSingleNode("msb").InnerText) - subreg.FirstBit + 1;
                    XmlElement vals = (XmlElement)fld.SelectSingleNode("enumeratedValues");
                    if (vals != null && subreg.SizeInBits > 1 && subreg.SizeInBits != 32)
                    {
                        KnownSubRegisterValue[] values = new KnownSubRegisterValue[1 << subreg.SizeInBits];
                        foreach (XmlElement ev in vals)
                            values[(int)ParseAddr(ev.SelectSingleNode("value").InnerText)] = new KnownSubRegisterValue { Name = ev.SelectSingleNode("name").InnerText };

                        int found = 0;
                        for (int j = 0; j < values.Length; j++)
                        {
                            if (values[j] == null)
                                values[j] = new KnownSubRegisterValue { Name = string.Format("Unknown (0x{0:x})", j) };
                            else
                                found++;
                        }

                        double utilization = (double)found / values.Length;
                        if (utilization > 0.5 || values.Length < 16)
                            subreg.KnownValues = values;
                    }
                    subregs.Add(subreg);
                }


                registers.Add(new HardwareRegister
                {
                    Address = string.Format("0x{0:x8}", baseAddr + addrOff + i * step),
                    Name = prefix + regName.Replace("%s", i.ToString()),
                    ReadOnly = access == "read-only",
                    SubRegisters = subregs.Count == 0 ? null : subregs.ToArray(),
                    SizeInBits = periphSize
                });
            }
        }


        public static MCUDefinitionWithPredicate ParseSVDFile(string file, string deviceName)
        {
            var doc = new XmlDocument();
            doc.Load(file);

            List<HardwareRegisterSet> sets = new List<HardwareRegisterSet>();

            foreach (XmlElement periph in doc.DocumentElement.SelectNodes("peripherals/peripheral"))
            {
                string name = periph.SelectSingleNode("name").InnerText;
                uint baseAddr = ParseAddr(periph.SelectSingleNode("baseAddress").InnerText);
                int periphSize = int.Parse(periph.SelectSingleNode("size").InnerText);
                List<HardwareRegister> registers = new List<HardwareRegister>();
                foreach (XmlElement reg in periph.SelectNodes("registers/*"))
                {
                    if (reg.Name == "register")
                        ProcessRegister(reg, registers, null, baseAddr, periphSize);
                    else if (reg.Name == "cluster")
                        ProcessCluster(reg, registers, null, baseAddr, periphSize);
                }

                HardwareRegisterSet set = new HardwareRegisterSet { UserFriendlyName = name, Registers = registers.ToArray() };
                sets.Add(set);
            }


            return new MCUDefinitionWithPredicate { MCUName = deviceName, RegisterSets = sets.ToArray() };
        }

        private static void ProcessCluster(XmlElement cluster, List<HardwareRegister> registers, string prefix, uint baseAddr, int periphSize)
        {
            int count = 1, step = 0;
            string dim = cluster.SelectSingleNode("dim")?.InnerText;
            uint addrOff = ParseAddr(cluster.SelectSingleNode("addressOffset").InnerText);

            if (dim != null)
            {
                count = int.Parse(dim);
                step = (int)ParseAddr(cluster.SelectSingleNode("dimIncrement").InnerText);
            }

            for (int i = 0; i < count; i++)
            {
                string name = cluster.SelectSingleNode("name").InnerText.Replace("%s", i.ToString());
                foreach (XmlElement reg in cluster.SelectNodes("register"))
                    ProcessRegister(reg, registers, name + "_", (uint)(baseAddr + addrOff + i * step), periphSize);
                if (cluster.SelectSingleNode("cluster") != null)
                    throw new Exception("Unexpected nested cluster");
            }

        }

        static uint ParseAddr(string text)
        {
            if (text.StartsWith("0x"))
                return uint.Parse(text.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier);
            else
                return uint.Parse(text);
        }
    }
}
