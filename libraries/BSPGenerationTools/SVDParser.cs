/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace BSPGenerationTools
{
    public class SVDParser
    {
        static void ProcessRegister(XmlElement reg, List<HardwareRegister> registers, string prefix, uint baseAddr, uint? defaultRegisterSize)
        {
            string regName = reg.SelectSingleNode("name").InnerText;
            string access = reg.SelectSingleNode("access")?.InnerText;
            uint addrOff = ParseScaledNonNegativeInteger(reg.SelectSingleNode("addressOffset").InnerText);
            var regSizeProp = reg.SelectSingleNode("size");
            uint regSize = regSizeProp != null ? ParseScaledNonNegativeInteger(regSizeProp.InnerText) : defaultRegisterSize.Value;

            int count = 1, step = 0;
            string dim = reg.SelectSingleNode("dim")?.InnerText;
            if (dim != null)
            {
                count = (int)ParseScaledNonNegativeInteger(dim);
                step = (int)ParseScaledNonNegativeInteger(reg.SelectSingleNode("dimIncrement").InnerText);
                if (defaultRegisterSize != null && step * 8 != defaultRegisterSize)
                    throw new Exception("Mismatching array step for " + regName);
            }

            for (int i = 0; i < count; i++)
            {
                List<HardwareSubRegister> subregs = new List<HardwareSubRegister>();
                foreach (XmlElement fld in reg.SelectNodes("fields/field"))
                {
                    var subreg = new HardwareSubRegister
                    {
                        Name = fld.SelectSingleNode("name").InnerText
                    };

                    var lsbProp = fld.SelectSingleNode("lsb");
                    subreg.FirstBit = lsbProp != null ? (int)ParseScaledNonNegativeInteger(lsbProp.InnerText) :
                        (int)ParseScaledNonNegativeInteger(fld.SelectSingleNode("bitOffset").InnerText);

                    subreg.SizeInBits = lsbProp != null ?
                        (int)ParseScaledNonNegativeInteger(fld.SelectSingleNode("msb").InnerText) - subreg.FirstBit + 1 :
                        (int)ParseScaledNonNegativeInteger(fld.SelectSingleNode("bitWidth").InnerText);
                    XmlElement vals = (XmlElement)fld.SelectSingleNode("enumeratedValues");
                    var numOfAddedKnownValues = 0;
                    if (vals != null && subreg.SizeInBits > 1 && subreg.SizeInBits != 32)
                    {
                        if (subreg.SizeInBits > 8)
                            Console.WriteLine(string.Format("Warning: suspiciously large register with known values: {0} ({1} bits)", subreg.Name, subreg.SizeInBits));
                        else
                        {
                            KnownSubRegisterValue[] values = new KnownSubRegisterValue[1 << subreg.SizeInBits];
                            foreach (XmlElement ev in vals)
                            {
                                var knownValueProp = ev.SelectSingleNode("value");
                                if (DoNotCareBits(knownValueProp.InnerText))
                                {
                                    continue;
                                }
                                var knownValueIndex = (int)ParseScaledNonNegativeInteger(knownValueProp.InnerText);
                                var knowValueName = ev.SelectSingleNode("name").InnerText;
                                if (IsUserFriendlyName(knowValueName))
                                {
                                    values[knownValueIndex] = new KnownSubRegisterValue { Name = knowValueName };
                                    ++numOfAddedKnownValues;
                                }
                            }

                            if (numOfAddedKnownValues > 0)
                            {
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
                        }
                    }
                    subregs.Add(subreg);
                }


                registers.Add(new HardwareRegister
                {
                    Address = string.Format("0x{0:x8}", baseAddr + addrOff + i * step),
                    Name = prefix + regName.Replace("%s", i.ToString()),
                    ReadOnly = access == "read-only",
                    SubRegisters = subregs.Count == 0 ? null : subregs.ToArray(),
                    SizeInBits = (int)regSize
                });
            }
        }


        public static MCUDefinitionWithPredicate ParseSVDFile(string file, string deviceName)
        {
            var doc = new XmlDocument();
            doc.Load(file);

            List<HardwareRegisterSet> sets = new List<HardwareRegisterSet>();
            Dictionary<string, XmlElement> periphNodes = new Dictionary<string, XmlElement>();

            foreach (XmlElement periph in doc.DocumentElement.SelectNodes("peripherals/peripheral"))
            {
                string name = periph.SelectSingleNode("name").InnerText;
                uint baseAddr = ParseScaledNonNegativeInteger(periph.SelectSingleNode("baseAddress").InnerText);
                uint? defaultRegisterSize = null;
                var defaultRegisterSizeProp = periph.SelectSingleNode("size");
                if (defaultRegisterSizeProp != null)
                {
                    defaultRegisterSize = ParseScaledNonNegativeInteger(defaultRegisterSizeProp.InnerText);
                }

                periphNodes[name] = periph;
                List<HardwareRegister> registers = new List<HardwareRegister>();
                var basePeriph = periph.GetAttribute("derivedFrom");
                List<XmlNode> regNodes = periph.SelectNodes("registers/*").Cast<XmlNode>().ToList();

                if (!string.IsNullOrEmpty(basePeriph))
                {
                    regNodes.InsertRange(0, periphNodes[basePeriph].SelectNodes("registers/*").Cast<XmlNode>());
                }

                foreach (XmlElement reg in regNodes)
                {
                    if (reg.Name == "register")
                        ProcessRegister(reg, registers, null, baseAddr, defaultRegisterSize);
                    else if (reg.Name == "cluster")
                        ProcessCluster(reg, registers, null, baseAddr, defaultRegisterSize);
                }

                HardwareRegisterSet set = new HardwareRegisterSet { UserFriendlyName = name, Registers = registers.ToArray() };
                sets.Add(set);
            }


            return new MCUDefinitionWithPredicate { MCUName = deviceName, RegisterSets = sets.ToArray() };
        }

        private static void ProcessCluster(XmlElement cluster, List<HardwareRegister> registers, string prefix, uint baseAddr, uint? defaultRegisterSize)
        {
            int count = 1, step = 0;
            string dim = cluster.SelectSingleNode("dim")?.InnerText;
            uint addrOff = ParseScaledNonNegativeInteger(cluster.SelectSingleNode("addressOffset").InnerText);

            if (dim != null)
            {
                count = int.Parse(dim);
                step = (int)ParseScaledNonNegativeInteger(cluster.SelectSingleNode("dimIncrement").InnerText);
            }

            for (int i = 0; i < count; i++)
            {
                string name = cluster.SelectSingleNode("name").InnerText.Replace("%s", i.ToString());
                foreach (XmlElement reg in cluster.SelectNodes("register"))
                    ProcessRegister(reg, registers, name + "_", (uint)(baseAddr + addrOff + i * step), defaultRegisterSize);
                if (cluster.SelectSingleNode("cluster") != null)
                    throw new Exception("Unexpected nested cluster");
            }

        }

        private static bool IsUserFriendlyName(string text) {
            return !Regex.Match(text, "0[xX][0-9a-fA-F]+|#*[0-9xX]+").Success;
        }

        private static readonly char[] DoNotCareBitsChars = new[] { 'x', 'X' };

        private static bool DoNotCareBits(string text) {
            return text.StartsWith("#") && text.IndexOfAny(DoNotCareBitsChars) > 0;
        }

        private static uint ParseScaledNonNegativeInteger(string text) {

            ulong scale = ComputeScale(text);
            if (scale > 1)
            {
                text = text.Substring(0, text.Length - 1);
            }

            int radix = 10;
            if (text.StartsWith("0x", StringComparison.InvariantCultureIgnoreCase))
            {
                radix = 16;
                text = text.Substring(2);
            } else if (text.StartsWith("#"))
            {
                radix = 2;
                text = text.Substring(1);
            }
            else if (text.StartsWith("0b"))
            {
                radix = 2;
                text = text.Substring(2);
            }
            else if (text.StartsWith("0"))
            {
                radix = 8;
            }

            return checked((uint)(Convert.ToUInt64(text, radix) * scale));
        }

        private static ulong ComputeScale(string text) {

            ulong scale = 1;
            var lastChar = text.Substring(text.Length - 1);
            if (Regex.Match(lastChar, "[kmgtKMGT]").Success)
            {
                lastChar = lastChar.ToLower();
                if (lastChar == "k")
                {
                    scale = 1024;
                } else if (lastChar == "m")
                {
                    scale = 1024 * 1024;
                } else if (lastChar == "g")
                {
                    scale = 1024 * 1024 * 1024;
                } else if (lastChar == "t")
                {
                    scale = 1024UL * 1024 * 1024 * 1024;
                }
            }
            return scale;
        }
    }
}
