using BSPEngine;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using static STM32FLASHPatcher.STM32DeviceDatabase;

namespace STM32FLASHPatcher
{
    public class SimpleFLASHPatcher : IFLASHPatcherWithConfiguration<STM32DeviceDatabase>
    {
        public string UniqueID => "com.sysprogs.flash.stm32";
        public string Name => "STM32 Internal FLASH";

        public const uint FLASHStart = 0x08000000;

        public STM32DeviceDatabase Configuration { private get; set; }

        class STM32InternalFLASH : IPatchableFLASHMemory
        {
            private DeviceDefinition _Definition;

            class Bank : IFLASHBank
            {
                private DeviceDefinition _Definition;

                public Bank(uint[] pageSizes, bool isDualBank)
                {
                    Pages = new FLASHPage[pageSizes.Length];
                    ulong addr = FLASHStart;
                    int pageID = 0;

                    for (int i = 0; i < pageSizes.Length; i++)
                    {
                        if (isDualBank && i == (pageSizes.Length / 2))
                            throw new NotImplementedException();

                        Pages[i] = new FLASHPage(pageID++, addr, pageSizes[i]);
                        addr += pageSizes[i];
                    }
                }

                public int ID => 0;

                public FLASHPage[] Pages { get; }
            }


            public STM32InternalFLASH(DeviceDefinition def, uint FLASHSize, uint sectorSize, bool isDualBank)
            {
                _Definition = def;
                var layout = def.SectorLayout ?? throw new Exception("Missing sector layout for " + def.Name);

                Banks = new[] { new Bank(layout.ComputePageSizes(FLASHSize, sectorSize, isDualBank), isDualBank) };
            }

            public PatcherModuleInfo PatcherModule => new PatcherModuleInfo
            {
                Path = _Definition.Patcher ?? throw new Exception($"Patcher for {_Definition.Name} is undefined"),
                RAMBase = 0x20000000,
                AsyncEntryPointSymbol = "FLASHPatcher_RunRequestLoop",
                SyncInitFunction = "FLASHPatcher_Init",
                SyncEraseFunction = "FLASHPatcher_EraseSectors",
                SyncProgramFunction = "FLASHPatcher_ProgramBuffer",
                SyncCompletionFunction = "FLASHPatcher_Complete",
                BufferPointerSymbol = "g_pBuffer",
                RegistersToPreserve = new[] { new PreservedRegister("primask", "1"), new PreservedRegister("faultmask", "1") },
                StackSize = 128,
            };

            public FLASHAlias[] Aliases { get; } = new[] { new FLASHAlias(0x0, FLASHStart, FLASHStart) };
            public FLASHAlias PrimaryRegion { get; } = new FLASHAlias(FLASHStart, FLASHStart, 0x01000000);
            public byte[] BreakpointInstruction { get; } = new byte[] { 0xFF, 0xBE };
            public IFLASHBank[] Banks { get; }

            public override string ToString() => _Definition.Name;
        }

        public ProbedSoftwareBreakpointTarget Probe(ILowLevelRegisterAccessor accessor)
        {
            var config = Configuration ?? throw new Exception("Missing configuration for the STM32 patcher");

            var bpcountReg = ParseUInt32(config.BreakpointCountRegister, "breakpoint count register");
            var bpcountMask = ParseUInt32(config.BreakpointCountMask, "breakpoint count mask");

            var id = accessor.ReadHardwareRegister(ParseUInt32(config.DeviceIDRegister, "device ID register"));
            id = ExtractMaskedValue(id, ParseUInt32(config.DeviceIDMask, "device ID mask"));

            foreach(var fam in config.Families ?? new DeviceFamily[0])
            {
                foreach(var dev in fam.Devices ?? new DeviceDefinition[0])
                {
                    if (dev.HardwareID != null && dev.HardwareID.Split('|').Select(s => ParseUInt32(s, "ID for " + dev.Name)).Any(i => i == id))
                    {
                        var effectiveDef = fam.BaseDefinition.OverrideWith(dev);

                        var sectorSize = ParseUInt32(effectiveDef.MaxSectorSize, $"sector size for " + effectiveDef.Name);
                        ushort rawSize = 0;

                        try
                        {
                            rawSize = (ushort)accessor.ReadHardwareRegister(ParseUInt32(effectiveDef.FLASHSizeRegister, $"FLASH size register " + effectiveDef.Name));
                        }
                        catch { }

                        uint FLASHSize;
                        if (rawSize == 0 || rawSize == ushort.MaxValue)
                            FLASHSize = ParseUInt32(effectiveDef.MaxFLASHSize, $"FLASH size for " + effectiveDef.Name);
                        else
                            FLASHSize = rawSize * 1024U;

                        bool isDualBank = effectiveDef?.DualBankCondition?.IsTrue(new ConditionMatchingContext(accessor, FLASHSize)) == true;

                        return new ProbedSoftwareBreakpointTarget(dev.Name ?? "STM32xxxx", new[] { new STM32InternalFLASH(effectiveDef, FLASHSize, sectorSize, isDualBank) },
                            (int)ExtractMaskedValue(accessor.ReadHardwareRegister(bpcountReg), bpcountMask));
                    }
                }
            }

            throw new Exception($"No STM32 device matches 0x{id:x3}. Please update device definitions.");
        }
    }

}
