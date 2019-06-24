using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BSPEngine;
using BSPGenerationTools.Parsing;

namespace stm32_bsp_generator
{
    class PeripheralRegisterComparer
    {
        int _RegistersRemoved, _RegistersAdded, _RegistersTotal;
        private int _MismatchingRegisters;

        int _SubregistersRemoved, _SubregistersAdded, _TotalOldSubregisters, _TotalNewSubregisters;

        int _OldRegistersWithSubregisters, _OldRegistersWithSubregistersGone, _NewRegistersWithSubregisters;

        HashSet<string> _UniqueRegisters = new HashSet<string>();
        StreamWriter _Log = File.CreateText(@"e:\temp\registers.txt");

        internal void CompareRegisterSets(PeripheralRegisterGenerator2.DiscoveredPeripheral[] peripherals, HardwareRegisterSet[] existingSets, string mcu)
        {
            Dictionary<ulong, HardwareRegister> oldRegisters = new Dictionary<ulong, HardwareRegister>();
            foreach (var set in existingSets)
            {
                if (set.UserFriendlyName.StartsWith("ARM "))
                    continue;

                foreach (var reg in set.Registers)
                    oldRegisters[HeaderFileParser.ParseMaybeHex(reg.Address)] = reg;
            }

            Dictionary<ulong, HardwareRegister> remainingOldRegisters = new Dictionary<ulong, HardwareRegister>(oldRegisters);
            _Log.WriteLine($"--- {mcu} ---");

            foreach (var set in peripherals)
            {
                foreach(var reg in set.Registers)
                {
                    _RegistersTotal++;

                    var addr = set.ResolvedBaseAddress + reg.Offset;
                    if (oldRegisters.TryGetValue(addr, out var oldReg))
                    {
                        remainingOldRegisters.Remove(addr);

                        if (oldReg.SizeInBits != reg.SizeInBytes * 8 || oldReg.ReadOnly != reg.IsReadOnly)
                        {
                            _MismatchingRegisters++;
                        }

                        var newSubregisters = reg.OriginalField?.Subregisters;

                        _TotalOldSubregisters += oldReg.SubRegisters?.Length ?? 0;
                        _TotalNewSubregisters += newSubregisters.Count;

                        Dictionary<int, HardwareSubRegister> oldSubregistersByOffset = new Dictionary<int, HardwareSubRegister>();
                        foreach (var sr in oldReg.SubRegisters ?? new HardwareSubRegister[0])
                            oldSubregistersByOffset[sr.FirstBit] = sr;

                        int oldSubregCount = oldSubregistersByOffset.Count;

                        foreach (var sr in newSubregisters)
                        {
                            int firstBit = sr.Subregister.Offset;

                            if (oldSubregistersByOffset.TryGetValue(firstBit, out var val))
                                oldSubregistersByOffset.Remove(firstBit);
                            else
                                _SubregistersAdded++;
                        }

                        _SubregistersRemoved += oldSubregistersByOffset.Count;

                        if (oldSubregCount > 0)
                        {
                            _OldRegistersWithSubregisters++;
                            if (newSubregisters.Count == 0)
                            {
                                _OldRegistersWithSubregistersGone++;
                                _Log.WriteLine($"{set.Name}->{reg.Name}");
                                _UniqueRegisters.Add($"{set.Name}->{reg.Name}");
                                //Debug.WriteLine($"{set.Name}->{reg.Name}");
                            }
                        }

                        if (newSubregisters.Count > 0)
                        {
                            _NewRegistersWithSubregisters++;
                        }

                    }
                    else
                    {
                        _RegistersAdded++;
                    }
                }
            }

            _RegistersRemoved += remainingOldRegisters.Count;
        }

        internal void ShowStatistics()
        {
            _Log.Dispose();
            File.WriteAllLines(@"e:\temp\registers.unique", _UniqueRegisters);
            Debug.WriteLine($"Registers added: {_RegistersAdded}/{_RegistersTotal}");
            Debug.WriteLine($"Registers removed: {_RegistersRemoved}/{_RegistersTotal}");
            Debug.WriteLine($"Registers mismatching: {_MismatchingRegisters}/{_RegistersTotal}");

            //Debug.WriteLine($"Subregisters added: {_SubregistersAdded}/{_TotalOldSubregisters}/{_TotalNewSubregisters}");
            //Debug.WriteLine($"Subregisters removed: {_SubregistersRemoved}/{_TotalOldSubregisters}/{_TotalNewSubregisters}");

            Debug.WriteLine($"Subregisters lost: {_OldRegistersWithSubregistersGone}/{_OldRegistersWithSubregisters}");

        }
    }
}
