using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace esp32
{
    class PeripheralRegisterParser
    {
        class ConstructedField
        {
            public string Name;
            public uint? Offset;
            public uint? Mask;

            public bool MaskIsSequential
            {
                get
                {
                    if (!Mask.HasValue || Mask.Value == 0)
                        return false;
                    var mask = Mask.Value;
                    while (mask != 0)
                    {
                        if ((mask & 1) == 0)
                            return false;
                        mask >>= 1;
                    }
                    return true;
                }
            }

            static int MaskToBits(uint mask)
            {
                int bits = 0;
                while (mask != 0)
                {
                    if ((mask & 1) == 0)
                        throw new Exception("Bitmask not sequential");
                    bits++;
                    mask >>= 1;
                }
                return bits;
            }


            public HardwareSubRegister ToSubregister()
            {
                return new HardwareSubRegister
                {
                    Name = Name.TrimStart('_'),
                    FirstBit = (int)Offset.Value,
                    SizeInBits = MaskToBits(Mask.Value),
                };
            }
        }

        class ConstructedRegister
        {
            public string Name;
            public ulong Address;
            public Dictionary<string, ConstructedField> Fields = new Dictionary<string, ConstructedField>();

            public HardwareRegister ToRegister()
            {
                return new HardwareRegister
                {
                    Name = Name,
                    Address = $"0x{Address:x8}",
                    SizeInBits = 32,
                    SubRegisters = Fields.Values.Where(f => f.MaskIsSequential).OrderBy(f => f.Offset.Value).Select(f => f.ToSubregister()).ToArray()
                };
            }

            public ConstructedRegister CloneWithOffset(ulong offset)
            {
                return new ConstructedRegister { Name = Name, Address = Address + offset, Fields = Fields };
            }
        }

        class ConstructedRegisterSet
        {
            public string Name;
            public ulong Address;
            public List<ConstructedRegister> Registers = new List<ConstructedRegister>();

            public int MultipleInstanceMultiplier;

            public HardwareRegisterSet ToRegisterSet()
            {
                return new HardwareRegisterSet
                {
                    UserFriendlyName = Name,
                    Registers = Registers.OrderBy(r => r.Address).Select(r => r.ToRegister()).ToArray()
                };
            }

            public override string ToString()
            {
                return Name;
            }

            public bool IsPossibleInstanceOf(ConstructedRegisterSet set, int maxInstances)
            {
                if (Address < set.Address)
                    return false;

                ulong delta = Address - set.Address;
                if (delta % (uint)set.MultipleInstanceMultiplier != 0)
                    return false;

                int instance = (int)(delta / (uint)set.MultipleInstanceMultiplier);
                if (instance > maxInstances)
                    return false;

                return true;
            }
        }

        struct MultiInstanceRegister
        {
            public string BaseName;
            public int Multiplier;

            public override string ToString()
            {
                return $"{BaseName} + i * 0x{Multiplier:x}";
            }
        }

        public static HardwareRegisterSet[] ParsePeripheralRegisters(string sdkDir)
        {
            Dictionary<string, ConstructedRegisterSet> registerSets = new Dictionary<string, ConstructedRegisterSet>();
            Regex rgBaseDefinition = new Regex("#define[ \t]+DR_REG_(.*)_BASE[ \t]+0x([0-9a-fA-F]+)$");
            Regex rgRegisterDef = new Regex("#define[ \t]+([^ \t]+)[ \t]+\\(DR_REG_(.*)_BASE \\+ (.*)\\)");
            Regex rgRegisterDefWithIndex = new Regex(@"#define[ \t]+([^ \t]+)[ \t]+\(([0-9A-Za-z_]+_BASE)[ \t]*\(i\) \+ (.*)\)");
            Regex rgBitDef = new Regex("#define[ \t]+([^ \t]+)_(S|V)[ \t]+\\(?(0x[0-9a-fA-F]+|[0-9]+)\\)?");

            Regex rgiTimesNumber = new Regex(@"^[ (\t]*i[ \t)]*\*[ \t]*[ \t(]*0x([0-9a-fA-F]+)[ \t)]*$");

            foreach (var match in File.ReadAllLines(Path.Combine(sdkDir, @"components\soc\esp32\include\soc\soc.h")).Select(line => rgBaseDefinition.Match(line)).Where(m => m.Success))
                registerSets[match.Groups[1].Value] = new ConstructedRegisterSet
                {
                    Name = match.Groups[1].Value,
                    Address = ulong.Parse(match.Groups[2].Value, System.Globalization.NumberStyles.HexNumber)
                };

            foreach (var fn in Directory.GetFiles(Path.Combine(sdkDir, @"components\soc\esp32\include\soc"), "*_reg.h"))
            {
                Dictionary<string, MultiInstanceRegister> multiInstanceRegisterBases = new Dictionary<string, MultiInstanceRegister>();

                ConstructedRegister currentRegister = null;
                foreach (var line in File.ReadAllLines(fn))
                {
                    var m = rgRegisterDef.Match(line);
                    bool isIndexedRegister = false;
                    if (!m.Success)
                    {
                        m = rgRegisterDefWithIndex.Match(line);
                        if (m.Success)
                            isIndexedRegister = true;
                    }

                    if (m.Success)
                    {
                        if (!registerSets.TryGetValue(m.Groups[2].Value, out var set))
                        {
                            if (isIndexedRegister && multiInstanceRegisterBases.TryGetValue(m.Groups[2].Value, out var multiRegSet))
                            {
                                if (registerSets.TryGetValue(multiRegSet.BaseName, out set))
                                {
                                    set.MultipleInstanceMultiplier = multiRegSet.Multiplier;
                                }
                            }

                            if (set == null)
                                continue;
                        }

                        string name = m.Groups[1].Value;
                        if (name.Contains("("))
                        {
                            if (!name.EndsWith("(i)"))
                                continue;   //Unexpected syntax.

                            name = name.Substring(0, name.Length - 3).Trim();

                            if (name.EndsWith("_BASE"))
                            {
                                var m2 = rgiTimesNumber.Match(m.Groups[3].Value);
                                if (!m2.Success)
                                {
                                    Console.WriteLine("Unexpected base definition: " + line);
                                    continue;
                                }

                                multiInstanceRegisterBases[name] = new MultiInstanceRegister { BaseName = m.Groups[2].Value, Multiplier = int.Parse(m2.Groups[1].Value, System.Globalization.NumberStyles.HexNumber) };
                                continue;
                            }
                        }

                        currentRegister = new ConstructedRegister
                        {
                            Name = name,
                            Address = set.Address + ParseOffset(m.Groups[3].Value)
                        };

                        set.Registers.Add(currentRegister);
                        continue;
                    }

                    m = rgBitDef.Match(line);
                    if (m.Success && currentRegister != null)
                    {
                        string fieldName = m.Groups[1].Value;
                        int commonPrefixLength = 0;
                        for (int i = 0; i < currentRegister.Name.Length; i++)
                            if (i >= fieldName.Length || fieldName[i] != currentRegister.Name[i])
                            {
                                commonPrefixLength = i;
                                break;
                            }

                        if (commonPrefixLength == fieldName.Length)
                            fieldName = fieldName.Split('_').Last();
                        else if (commonPrefixLength > 0)
                            fieldName = fieldName.Substring(commonPrefixLength);
                        else
                        {
                            continue;
                            throw new Exception("Unexpected mismatching field name");
                        }

                        ConstructedField fld;
                        if (!currentRegister.Fields.TryGetValue(fieldName, out fld))
                            currentRegister.Fields[fieldName] = fld = new ConstructedField { Name = fieldName };

                        uint value;
                        if (m.Groups[3].Value.StartsWith("0x"))
                            value = uint.Parse(m.Groups[3].Value.Substring(2), System.Globalization.NumberStyles.HexNumber);
                        else
                            value = uint.Parse(m.Groups[3].Value);


                        switch (m.Groups[2].Value)
                        {
                            case "V":
                                fld.Mask = value;
                                break;
                            case "S":
                                fld.Offset = value;
                                break;
                            default:
                                throw new Exception("Unexpected definition");
                        }
                    }
                }
            }

            foreach (var set in registerSets.Values.Where(s => s.MultipleInstanceMultiplier != 0))
            {
                var possibleNextInstances = registerSets.Values.Where(s => s != set && s.Registers.Count == 0 && s.IsPossibleInstanceOf(set, 1)).ToArray();

                foreach(var set2 in possibleNextInstances)
                {
                    var offset = set2.Address - set.Address;
                    foreach(var reg in set.Registers)
                    {
                        set2.Registers.Add(reg.CloneWithOffset(offset));
                    }
                }
            }

            var emptyRegisterSets = registerSets.Values.Where(s => s.Registers.Count == 0).ToArray();

            return registerSets.Values.Where(s => s.Registers.Count != 0).OrderBy(v => v.Address).Select(s => s.ToRegisterSet()).ToArray();
        }

        static Regex rgHexNumber = new Regex("^0x[0-9a-fA-F]+$");
        static Regex rgTwoNumbers = new Regex("^0x([0-9a-fA-F]+) \\* ([0-9]+)$");

        private static ulong ParseOffset(string value)
        {
            if (rgHexNumber.IsMatch(value))
                return ulong.Parse(value.Substring(2), System.Globalization.NumberStyles.HexNumber);
            var m = rgTwoNumbers.Match(value);
            if (m.Success)
                return ulong.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber) * ulong.Parse(m.Groups[2].Value);
            return 0;
        }
    }
}
