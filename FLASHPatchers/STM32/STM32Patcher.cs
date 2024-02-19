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
    public class STM32InternalFLASHPatcher : IFLASHPatcherWithConfiguration<STM32DeviceDatabase>
    {
        public string UniqueID => "com.sysprogs.flash.stm32";
        public string Name => UserFriendlyName;

        public const string UserFriendlyName = "STM32 Internal FLASH";

        public STM32DeviceDatabase Configuration { private get; set; }

        class STM32InternalFLASH : IPatchableFLASHMemory
        {
            private DeviceDefinition _Definition;

            class Bank : IFLASHBank
            {
                public Bank(FLASHBankDefinition definition, bool sectorIndexesAreAddresses)
                {
                    ID = definition.BankID;
                    Pages = new FLASHPage[definition.PageSizes.Length];
                    ulong addr = definition.FirstPageAddress;
                    int pageID = definition.FirstPageID;

                    for (int i = 0; i < definition.PageSizes.Length; i++)
                    {
                        Pages[i] = new FLASHPage(this, sectorIndexesAreAddresses ? (int)addr : pageID++, addr, definition.PageSizes[i]);
                        addr += definition.PageSizes[i];
                    }
                }

                public int ID { get; }

                public FLASHPage[] Pages { get; }
            }


            public STM32InternalFLASH(DeviceDefinition def, uint FLASHSize, uint sectorSize, bool isDualBank)
            {
                _Definition = def;
                var layout = def.SectorLayout ?? throw new Exception("Missing sector layout for " + def.Name);

                Banks = layout.ComputeLayout(FLASHSize, sectorSize, isDualBank).Select(l => new Bank(l, layout.SectorIndexesAreAddresses)).ToArray();
                ValueAfterErasing = ParseUInt32(def.ValueAfterErasing ?? "0xFFFFFFFF", "erased value");

                RegistersToPreserve = new[] { new PreservedRegister("primask", "1", true), new PreservedRegister("faultmask", "1", true) };
                if (def.MPUControlRegister != null)
                    RegistersToPreserve = RegistersToPreserve.Concat(new[] { new PreservedRegister("MPU_CTRL", ParseUInt32(def.MPUControlRegister, "MPU control register address"), "0") }).ToArray();
            }

            public PatcherModuleInfo PatcherModule => new PatcherModuleInfo
            {
                Path = _Definition.Patcher ?? throw new Exception($"Patcher for {_Definition.Name} is undefined"),
                AsyncEntryPointSymbol = "FLASHPatcher_RunRequestLoop",
                SyncInitFunction = "FLASHPatcher_Init",
                SyncEraseFunction = "FLASHPatcher_EraseSectors",
                SyncProgramFunction = "FLASHPatcher_ProgramWords",
                SyncFillFunction = "FLASHPatcher_ProgramRepeatedWords",
                SyncCompletionFunction = "FLASHPatcher_Complete",
                BufferPointerSymbol = "g_pBuffer",
                StackSize = 256,
            };

            public RAMLayout RAMLayout => new RAMLayout(0x20000000, 1024 * 1024, "_estack", "__StackTop");

            public FLASHAlias[] Aliases { get; } = new[] { new FLASHAlias(0x0, FLASHStart, FLASHStart) };
            public FLASHAlias PrimaryRegion { get; } = new FLASHAlias(FLASHStart, FLASHStart, 0x01000000);
            public byte[] BreakpointInstruction { get; } = new byte[] { 0xFF, 0xBE };
            public uint ValueAfterErasing { get; }
            public IFLASHBank[] Banks { get; }

            public PreservedRegister[] RegistersToPreserve { get; }
            public string UserFriendlyName => STM32InternalFLASHPatcher.UserFriendlyName;

            public override string ToString() => _Definition.Name;
        }

        public ProbedSoftwareBreakpointTarget Probe(ILowLevelRegisterAccessor accessor)
        {
            var config = Configuration ?? throw new Exception("Missing configuration for the STM32 patcher");

            var bpcountReg = ParseUInt32(config.BreakpointCountRegister, "breakpoint count register");
            var bpcountMask = ParseUInt32(config.BreakpointCountMask, "breakpoint count mask");

            var idRegs = config.DeviceIDRegisters.Split(';').Select(a => ParseUInt32(a, "device ID register")).ToArray();
            uint mask = ParseUInt32(config.DeviceIDMask, "device ID mask");
            uint id = 0;

            foreach (var idReg in idRegs)
            {
                try
                {
                    id = accessor.ReadHardwareRegister(idReg);
                    id = ExtractMaskedValue(id, mask);

                    if (id != 0 && id != mask)
                        break;
                }
                catch { }
            }

            foreach (var fam in config.Families ?? new DeviceFamily[0])
            {
                foreach (var dev in fam.NestedDefinitions ?? new DeviceDefinition[0])
                {
                    if (dev.MatchesID(id))
                    {
                        var effectiveDef = fam.OverrideWith(dev);
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

                        if (effectiveDef.PatchableFLASHAreaSize is string limit)
                            FLASHSize = Math.Min(FLASHSize, ParseUInt32(limit, "normal FLASH size limit"));

                        var cctx = new ConditionMatchingContext(accessor, FLASHSize);

                        foreach (var subDef in dev.Overrides ?? new DeviceOverrides[0])
                            if (subDef.Condition?.IsTrue(cctx) == true)
                                effectiveDef = effectiveDef.OverrideWith(subDef);

                        var sectorSize = ParseUInt32(effectiveDef.BaseSectorSize, $"sector size for " + effectiveDef.Name);

                        return new ProbedSoftwareBreakpointTarget(dev.Name ?? "STM32xxxx", new[] { new STM32InternalFLASH(effectiveDef, FLASHSize, sectorSize, effectiveDef.IsDualBank) },
                            (int)ExtractMaskedValue(accessor.ReadHardwareRegister(bpcountReg), bpcountMask));
                    }
                }
            }

            throw new Exception($"No STM32 device matches 0x{id:x3}. Please update device definitions.");
        }

        public void ValidateConfiguration(string baseDirectory)
        {
            var config = Configuration ?? throw new Exception("Missing configuration for the STM32 patcher");
            HashSet<uint> ids = new HashSet<uint>();
            foreach (var family in config.Families)
            {
                if (family.Overrides != null)
                    throw new Exception("MCU families should use " + nameof(family.NestedDefinitions));

                foreach (var dev in family.NestedDefinitions)
                {
                    var x = family.OverrideWith(dev);
                    foreach (var sub in dev.Overrides ?? new DeviceOverrides[0])
                        x = x.OverrideWith(sub);

                    if (x.Name == null || x.Patcher == null)
                        throw new Exception("Incomplete definition");

                    foreach (var id in x.HardwareID.Split('|'))
                    {
                        var parsedID = ParseUInt32(id, "hardware ID");
                        if (ids.Contains(parsedID))
                            throw new Exception("Duplicate hardware ID: " + parsedID);
                        ids.Add(parsedID);
                    }
                    ParseUInt32(x.FLASHSizeRegister, "FLASH size register");
                    ParseUInt32(x.MaxFLASHSize, "max FLASH size");
                    ParseUInt32(x.BaseSectorSize, "sector size");
                    if (!File.Exists(Path.Combine(baseDirectory, x.Patcher)))
                        throw new Exception("Missing patcher executable: " + x.Patcher);

                    if (x.MPUControlRegister != null)
                        ParseUInt32(x.MPUControlRegister, "MPU control register");
                }
            }
        }
    }

}
