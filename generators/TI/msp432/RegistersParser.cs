/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace msp432_bsp_generator
{
    internal class RegistersParser
    {
        private static string[] REGISTERS_WITH_WRONG_BITS_SUM = {
            "DMA->STAT",
            "DMA->CFG",
            "DMA->CTLBASE",
            "DMA->ERRCLR",
            "EUSCI_A0->IRCTL",
            "EUSCI_A1->IRCTL",
            "EUSCI_A2->IRCTL",
            "EUSCI_A3->IRCTL"};

        private static readonly Regex REGISTER_SET_BEGIN = new Regex(@"[ \t]*typedef\s+struct\s*{", RegexOptions.Singleline);
        private static readonly Regex REGISTER_BEGIN = new Regex(@"[ \t]*union\s*{\s*\/\*\s*([\w]+\s+Register)\s*\*\/", RegexOptions.Singleline);
        private static readonly Regex REGISTER_DEFINITION = new Regex(@"[ \t]*(__I|__O|__IO)+\s+uint(8|16|32)_t\s+r\s*;", RegexOptions.Singleline);
        private static readonly Regex REGISTER_WITHOUT_SUBREGISTERS = new Regex(@"[ \t]*(__I|__O|__IO)+\s+uint(8|16|32)_t\s+r([\w]+)\s*;\s*(.*)?", RegexOptions.Singleline);
        private static readonly Regex SUBREGISTERS_BEGIN = new Regex(@"[ \t]*struct\s*{\s*\/\*\s*([\w]+\s+Bits)\s*\*\/", RegexOptions.Singleline);
        private static readonly Regex SUBREGISTER_DEFINITION = new Regex(@"[ \t]*(__I|__O|__IO)\s+uint(8|16|32)_t\s+b([\w]*)\s*:\s*([\d]+)\s*;\s*\/\*\s*(.*)\*\/", RegexOptions.Singleline);
        private static readonly Regex SUBREGISTERS_END = new Regex(@"[ \t]*}\s*(b|a)\s*;", RegexOptions.Singleline);
        private static readonly Regex REGISTER_END = new Regex(@"[ \t]*}\s*r([\w]+)\s*;", RegexOptions.Singleline);
        private static readonly Regex REGISTER_SET_END = new Regex(@"[ \t]*} ([\w]+)_Type\s*;", RegexOptions.Singleline);
        private static readonly Regex RESERVED_BITS = new Regex(@"[ \t]*(__I|__O|__IO)*\s*uint(8|16|32)_t\s+(RESERVED[\d]*)\s*\[([\d]+)\]\s*;", RegexOptions.Singleline);
        private static readonly Regex REGISTER_ADDRESS = new Regex(@"[ \t]*#define[ \t]+([\w]+)[ \t]+\(HWREG[\d]+\((0x[0-9A-F]+)\)\)", RegexOptions.Singleline);
        private static readonly Regex IGNORE_LINE = new Regex(@"(\/\/)|(\/\*)|([ \t]*)|(\n\r)", RegexOptions.Singleline);
        private static readonly Regex REGISTER_SET_ADDRESS = new Regex(@"[ \t]*#define[ \t]+([\w]+)_BASE[ \t]+\((0x[0-9A-F]+)\)", RegexOptions.Singleline);
        private static readonly Regex KNOWN_VALUE = new Regex(@"[ \t]*#define[ \t]+([\w]+)__([\w]+)[ \t]*\((0x[0-9A-F]+)\)", RegexOptions.Singleline);

        private readonly string _file;
        private int _processedBits;
        bool _duplicateRegisterDefinition;
        Match _lastMatch;
        Regex _matchedRegex;
        private IDictionary<string, string> _registerNameToAddress;
        private IDictionary<string, string> _registerSetToAddress;
        private IDictionary<string, IList<KeyValuePair<string, ulong>>> _subRegistersKnownValues;
        private List<HardwareRegister> _registers;
        private List<HardwareRegisterSet> _registerSets;
        private List<HardwareSubRegister> _subRegisters;
        private List<string> _registersWithWrongBitsSum;
        private HardwareRegisterSet _registerSet;
        private HardwareRegister _register;
        private HardwareSubRegister _subRegister;
        private IDictionary<string, HardwareSubRegister> _registerBits;
        private List<Regex> _expectedMatches;

        public RegistersParser(string file)
        {
            _file = file;
        }

        public HardwareRegisterSet[] Parse()
        {
            OnStart();

            foreach (var line in File.ReadLines(_file))
            {
                _matchedRegex = null;
                _lastMatch = Match.Empty;

                CheckForMatches(line);

                if (MissmatchHandled(line))
                {
                    continue;
                }

                if (_matchedRegex == REGISTER_SET_ADDRESS)
                {
                    OnRegisterSetAddress();
                }
                else if (_matchedRegex == REGISTER_ADDRESS)
                {
                    OnRegisterAddress();
                }
                else if (_matchedRegex == REGISTER_SET_BEGIN)
                {
                    OnRegisterSetBegin();
                }
                else if (_matchedRegex == REGISTER_BEGIN)
                {
                    OnRegisterBegin();
                }
                else if (_matchedRegex == REGISTER_DEFINITION)
                {
                    OnRegisterDefinition();
                }
                else if (_matchedRegex == SUBREGISTERS_BEGIN)
                {
                    OnSubRegistersBegin();
                }
                else if (_matchedRegex == SUBREGISTER_DEFINITION)
                {
                    SubRegisterDefinition();
                }
                else if (_matchedRegex == SUBREGISTERS_END)
                {
                    OnSubRegistersEnd();
                }
                else if (_matchedRegex == REGISTER_END)
                {
                    OnRegisterEnd();
                }
                else if (_matchedRegex == RESERVED_BITS)
                {
                    OnReservedBits();
                }
                else if (_matchedRegex == REGISTER_WITHOUT_SUBREGISTERS)
                {
                    OnRegisterWithoutSubregisters();
                }
                else if (_matchedRegex == REGISTER_SET_END)
                {
                    OnRegisterSetEnd();
                }
                else if (_matchedRegex == KNOWN_VALUE)
                {
                    OnKnownValue();
                }
            }

            ProcessKnownValues();

            _registerSets.Sort((x, y) => { return x.UserFriendlyName.CompareTo(y.UserFriendlyName); });

            return _registerSets.ToArray();
        }

        private void ProcessKnownValues()
        {
            foreach (var knownValues in _subRegistersKnownValues)
            {
                if (!_registerBits.ContainsKey(knownValues.Key))
                {
                    continue;
                }

                var knownSubRegValues = new List<KnownSubRegisterValue>();
                var subReg = _registerBits[knownValues.Key];
                ulong numOfValues = (ulong)1 << subReg.SizeInBits;
                ulong valueMask = (numOfValues - 1) << subReg.FirstBit;

                for (ulong i = 0; i < numOfValues; ++i)
                {
                    knownSubRegValues.Add(new KnownSubRegisterValue { Name = "Unknown (" + FormatToHex(i, subReg.SizeInBits) + ")" });
                }

                foreach (var knownValue in knownValues.Value)
                {
                    if ((knownValue.Value & ~valueMask) != 0)
                    {
                        throw new Exception("Know value outside of subregister's values range");
                    }

                    knownSubRegValues[(int)(knownValue.Value >> subReg.FirstBit)] = new KnownSubRegisterValue { Name = knownValue.Key };
                }

                subReg.KnownValues = knownSubRegValues.ToArray();
            }
        }

        private void OnKnownValue()
        {
            var knownValueKey = _lastMatch.Groups[1].Value;
            var knownValueName = _lastMatch.Groups[2].Value;
            var knownValueValue = _lastMatch.Groups[3].Value;

            if (!_subRegistersKnownValues.ContainsKey(knownValueKey))
            {
                _subRegistersKnownValues.Add(knownValueKey, new List<KeyValuePair<string, UInt64>>());
            }

            _subRegistersKnownValues[knownValueKey].Add(new KeyValuePair<string, UInt64>(knownValueName, Convert.ToUInt64(knownValueValue, 16)));
            SetExpectedMatches(KNOWN_VALUE);
        }

        private void OnRegisterSetEnd()
        {
            _registerSet.UserFriendlyName = _lastMatch.Groups[1].Value;
            _registerSet.ExpressionPrefix = _registerSet.UserFriendlyName + "->";

            if (_registersWithWrongBitsSum.Count > 0)
            {
                foreach (var registerName in _registersWithWrongBitsSum)
                {
                    if (!REGISTERS_WITH_WRONG_BITS_SUM.Contains(_registerSet.ExpressionPrefix + registerName))
                    {
                        throw new Exception("Sum of bits less than size of the register " + _register.Name);
                    }
                }
            }

            _registersWithWrongBitsSum.Clear();
            _registerSet.Registers = new HardwareRegister[_registers.Count];

            for (int i = 0; i < _registers.Count; ++i)
            {
                var currentRegister = _registers[i];

                if (currentRegister.SubRegisters != null)
                {
                    foreach (var subReg in currentRegister.SubRegisters)
                    {
                        var subRegKey = _registerSet.UserFriendlyName + subReg.Name;
                        if (!_registerBits.ContainsKey(subRegKey))
                        {
                            _registerBits.Add(subRegKey, subReg);
                        }
                    }
                }

                var regAddressKey = _registerSet.UserFriendlyName + currentRegister.Name;
                string registerAddress;
                if (_registerNameToAddress.TryGetValue(regAddressKey, out registerAddress))
                {
                    currentRegister.Address = registerAddress;
                }
                else
                {
                    if (i == 0)
                    {
                        currentRegister.Address = _registerSetToAddress[_registerSet.UserFriendlyName];
                    }
                    else
                    {
                        var previousRegister = _registers[i - 1];
                        var calculatedAddress = Convert.ToUInt64(previousRegister.Address, 16) + (UInt32)(previousRegister.SizeInBits / 8);
                        currentRegister.Address = string.Format("0x{0:x}", calculatedAddress);
                    }
                }

                if (i > 0)
                {
                    var previousRegister = _registers[i - 1];
                    var calculatedAddress = Convert.ToUInt64(previousRegister.Address, 16) + (UInt32)(previousRegister.SizeInBits / 8);
                    if (Convert.ToUInt64(currentRegister.Address, 16) != calculatedAddress)
                    {
                        throw new Exception("Wrong register address");
                    }
                }

                _registerNameToAddress.Remove(regAddressKey);
                _registerSet.Registers[i] = _registers[i];
            }

            _registerSets.Add(_registerSet);
            SetExpectedMatches(REGISTER_SET_BEGIN, KNOWN_VALUE);
        }

        private void OnRegisterWithoutSubregisters()
        {
            _register = new HardwareRegister();
            _register.Name = _lastMatch.Groups[3].Value;
            _register.ReadOnly = IsReadOnly(_lastMatch.Groups[1].Value);
            _register.SizeInBits = int.Parse(_lastMatch.Groups[2].Value);
            _registers.Add(_register);
            SetExpectedMatches(REGISTER_WITHOUT_SUBREGISTERS, REGISTER_BEGIN, REGISTER_SET_END, RESERVED_BITS);
        }

        private void OnReservedBits()
        {
            var arraySize = int.Parse(_lastMatch.Groups[4].Value);
            var arrayName = _lastMatch.Groups[3].Value;
            var isReadOnly = IsReadOnly(_lastMatch.Groups[1].Value);

            for (int i = 0; i < arraySize; i++)
            {
                var register = new HardwareRegister
                {
                    Name = arrayName + "_" + i.ToString(),
                    ReadOnly = isReadOnly,
                    SizeInBits = int.Parse(_lastMatch.Groups[2].Value)
                };

                _registers.Add(register);
            }

            SetExpectedMatches(REGISTER_WITHOUT_SUBREGISTERS, REGISTER_BEGIN, REGISTER_SET_END, RESERVED_BITS);
        }

        private void OnRegisterEnd()
        {
            _register.Name = _lastMatch.Groups[1].Value;

            if (_processedBits != _register.SizeInBits)
            {
                if (_processedBits > _register.SizeInBits)
                {
                    throw new Exception("Sum of bits more than size of the register " + _register.Name);
                }

                _registersWithWrongBitsSum.Add(_register.Name);
            }

            _register.SubRegisters = _subRegisters.ToArray();
            _registers.Add(_register);
            SetExpectedMatches(REGISTER_WITHOUT_SUBREGISTERS, REGISTER_BEGIN, REGISTER_SET_END, RESERVED_BITS);
        }

        private void OnSubRegistersEnd()
        {
            if (_lastMatch.Groups[1].Value != "b")
            {
                throw new Exception("Possibly a wrong or duplicate list of subregisters");
            }

            SetExpectedMatches(REGISTER_END);
        }

        private void SubRegisterDefinition()
        {
            _subRegister = new HardwareSubRegister();
            _subRegister.Name = _lastMatch.Groups[3].Value;
            _subRegister.FirstBit = _processedBits;
            _subRegister.SizeInBits = int.Parse(_lastMatch.Groups[4].Value);

            if (_subRegister.Name == string.Empty)
            {
                _subRegister.Name = string.Format("Unknown (0x{0:x})", _subRegister.FirstBit);
            }

            _subRegisters.Add(_subRegister);
            _processedBits += _subRegister.SizeInBits;
            SetExpectedMatches(SUBREGISTER_DEFINITION, SUBREGISTERS_END);
        }

        private void OnSubRegistersBegin()
        {
            _subRegisters = new List<HardwareSubRegister>();
            SetExpectedMatches(SUBREGISTER_DEFINITION);
        }

        private void OnRegisterDefinition()
        {
            _register.ReadOnly = IsReadOnly(_lastMatch.Groups[1].Value);
            _register.SizeInBits = int.Parse(_lastMatch.Groups[2].Value);
            _processedBits = 0;
            SetExpectedMatches(SUBREGISTERS_BEGIN);
        }

        private void OnRegisterBegin()
        {
            _duplicateRegisterDefinition = false;
            _register = new HardwareRegister();
            SetExpectedMatches(REGISTER_DEFINITION);
        }

        private void OnRegisterSetBegin()
        {
            _registers = new List<HardwareRegister>();
            _registerSet = new HardwareRegisterSet();
            SetExpectedMatches(REGISTER_BEGIN, RESERVED_BITS, REGISTER_WITHOUT_SUBREGISTERS);
        }

        private void OnRegisterAddress()
        {
            _registerNameToAddress.Add(_lastMatch.Groups[1].Value, _lastMatch.Groups[2].Value);
            SetExpectedMatches(REGISTER_ADDRESS, REGISTER_SET_BEGIN);
        }

        private void OnRegisterSetAddress()
        {
            _registerSetToAddress.Add(_lastMatch.Groups[1].Value, _lastMatch.Groups[2].Value);
            SetExpectedMatches(REGISTER_SET_ADDRESS, REGISTER_ADDRESS);
        }

        private void CheckForMatches(string line)
        {
            foreach (var expectedMatch in _expectedMatches)
            {
                _lastMatch = expectedMatch.Match(line);
                if (_lastMatch.Success)
                {
                    _matchedRegex = expectedMatch;
                    break;
                }
            }
        }

        private bool MissmatchHandled(string line)
        {
            if (!_lastMatch.Success)
            {
                if (_expectedMatches.Contains(REGISTER_END))
                {
                    if (_duplicateRegisterDefinition)
                    {
                        _lastMatch = SUBREGISTERS_END.Match(line);
                        if (_lastMatch.Success)
                        {
                            if (_lastMatch.Groups[1].Value != "a")
                            {
                                throw new Exception("Possibly not a duplicate list of subregisters");
                            }
                        }

                        return true;
                    }

                    _lastMatch = SUBREGISTERS_BEGIN.Match(line);
                    if (_lastMatch.Success)
                    {
                        _duplicateRegisterDefinition = true;
                        return true;
                    }

                }

                // outside of type definitions
                if (_expectedMatches.Contains(REGISTER_SET_ADDRESS) ||
                    _expectedMatches.Contains(REGISTER_ADDRESS) ||
                    _expectedMatches.Contains(REGISTER_SET_BEGIN) ||
                    _expectedMatches.Contains(KNOWN_VALUE))
                {
                    return true;
                }

                throw new Exception("Failed to parse the line");
            }

            // there was a match
            return false;
        }

        private void OnStart()
        {
            _processedBits = 0;
            _registerNameToAddress = new Dictionary<string, string>();
            _registerSetToAddress = new Dictionary<string, string>();
            _subRegistersKnownValues = new Dictionary<string, IList<KeyValuePair<string, ulong>>>();
            _registers = new List<HardwareRegister>();
            _registerSets = new List<HardwareRegisterSet>();
            _subRegisters = new List<HardwareSubRegister>();
            _registersWithWrongBitsSum = new List<string>();
            _duplicateRegisterDefinition = false;
            _lastMatch = Match.Empty;
            _matchedRegex = null;
            _registerSet = null;
            _register = null;
            _subRegister = null;
            _registerBits = new Dictionary<string, HardwareSubRegister>();
            _expectedMatches = new List<Regex> { REGISTER_SET_ADDRESS };
        }

        private static bool IsReadOnly(string definition)
        {
            return definition.Trim() == "__I";
        }

        private static string FormatToHex(ulong addr, int length = 32)
        {
            string format = "0x{0:x" + length / 4 + "}";
            return string.Format(format, (uint)addr);
        }

        private void SetExpectedMatches(params Regex[] expectedMatches)
        {
            _expectedMatches.Clear();
            _expectedMatches.AddRange(expectedMatches);
        }
    }
}
