using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        int _SubregistersRemoved, _SubregistersAdded, _SubregistersTotal;

        internal void CompareRegisterSets(PeripheralRegisterGenerator2.DiscoveredPeripheral[] peripherals, HardwareRegisterSet[] existingSets)
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

                        _SubregistersTotal += Math.Max(oldReg.SubRegisters?.Length ?? 0, reg.Subregisters?.Length ?? 0);

                        Dictionary<int, HardwareSubRegister> subregs = new Dictionary<int, HardwareSubRegister>();
                        foreach (var sr in oldReg.SubRegisters ?? new HardwareSubRegister[0])
                            subregs[sr.FirstBit] = sr;

                        foreach(var sr in reg.Subregisters ?? new HardwareSubRegister[0])
                        {
                            if (subregs.TryGetValue(sr.FirstBit, out var val))
                                subregs.Remove(sr.FirstBit);
                            else
                                _SubregistersAdded++;
                        }

                        _SubregistersRemoved += subregs.Count;
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
            Debug.WriteLine($"Registers added: {_RegistersAdded}/{_RegistersTotal}");
            Debug.WriteLine($"Registers removed: {_RegistersRemoved}/{_RegistersTotal}");
            Debug.WriteLine($"Registers mismatching: {_MismatchingRegisters}/{_RegistersTotal}");

            Debug.WriteLine($"Subregisters added: {_SubregistersAdded}/{_SubregistersTotal}");
            Debug.WriteLine($"Subregisters removed: {_SubregistersRemoved}/{_SubregistersTotal}");

        }
    }
}
