using BSPEngine;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace STM32FLASHPatcher
{
    public class STM32DeviceDatabase
    {
        public const uint FLASHStart = 0x08000000;

        [XmlInclude(typeof(Or))]
        [XmlInclude(typeof(MinimumFLASHSize))]
        [XmlInclude(typeof(Masked))]
        public abstract class Condition
        {
            public abstract bool IsTrue(ConditionMatchingContext ctx);

            public class Or : Condition
            {
                public Condition[] Conditions;

                public override bool IsTrue(ConditionMatchingContext ctx) => Conditions?.Any(c => c.IsTrue(ctx)) == true;
            }

            public class MinimumFLASHSize : Condition
            {
                public string Size;

                public override bool IsTrue(ConditionMatchingContext ctx) => ctx.FLASHSize >= ParseUInt32(Size, "FLASH size threshold");
            }


            public class Masked : Condition
            {
                public enum MaskMode
                {
                    AnyBits,
                    AllBits,
                    NoBits
                }

                public string RegisterAddress;
                public int Bit;
                public MaskMode Mode = MaskMode.AnyBits;

                public override bool IsTrue(ConditionMatchingContext ctx)
                {
                    var addr = ParseUInt32(RegisterAddress, "dual-bank check address");
                    var value = ctx.Accessor.ReadHardwareRegister(addr);

                    if (Mode == MaskMode.NoBits)
                        return (value & (1U << Bit)) == 0;
                    else
                        return (value & (1U << Bit)) != 0;
                }
            }
        }

        public class ConditionMatchingContext
        {
            public readonly ILowLevelRegisterAccessor Accessor;
            public readonly uint FLASHSize;

            public ConditionMatchingContext(ILowLevelRegisterAccessor accessor, uint fLASHSize)
            {
                Accessor = accessor;
                FLASHSize = fLASHSize;
            }
        }

        public class DeviceDefinition
        {
            public string HardwareID;
            public string Name;
            public string Patcher;
            public SectorLayout SectorLayout;

            public string FLASHSizeRegister;
            public string MaxFLASHSize;
            public string BaseSectorSize;
            public bool IsDualBank;
            public DeviceOverrides[] Overrides;

            public DeviceDefinition OverrideWith(DeviceDefinition dev)
            {
                if (dev == null)
                    return this;

                return new DeviceDefinition
                {
                    HardwareID = dev.HardwareID ?? HardwareID,
                    Name = dev.Name ?? Name,
                    Patcher = dev.Patcher ?? Patcher,
                    SectorLayout = dev.SectorLayout ?? SectorLayout,
                    FLASHSizeRegister = dev.FLASHSizeRegister ?? FLASHSizeRegister,
                    MaxFLASHSize = dev.MaxFLASHSize ?? MaxFLASHSize,
                    BaseSectorSize = dev.BaseSectorSize ?? BaseSectorSize,
                    IsDualBank = dev.IsDualBank || IsDualBank,
                };
            }

            public bool MatchesID(uint id) => HardwareID != null && HardwareID.Split('|').Select(s => ParseUInt32(s, "ID for " + Name)).Any(i => i == id);
        }

        public class DeviceFamily : DeviceDefinition
        {
            public DeviceDefinition[] NestedDefinitions;
        }

        public class DeviceOverrides : DeviceDefinition
        {
            public Condition Condition;
        }

        static int GetSecondBankMask(int pageCount)
        {
            //Set page ID to 0b100..0, where the trailing zeroes are sufficient to fit the page number within the bank.
            for (int x = 31; x >= 0; x--)
                if ((pageCount & (1 << x)) != 0)
                {
                    return 1 << (x + 1);
                }

            return 0;
        }

        [XmlInclude(typeof(FirstSplitInto5))]
        [XmlInclude(typeof(Linear))]
        public abstract class SectorLayout
        {
            public abstract FLASHBankDefinition[] ComputeLayout(uint FLASHSize, uint sectorSize, bool isDualBank);

            static IEnumerable<uint> Repeat(uint value, uint count) => Enumerable.Range(0, (int)count).Select(x => value);

            public class FirstSplitInto5 : SectorLayout
            {
                static uint[] DoComputePageSizes(uint totalSize, uint normalSectorSize)
                {
                    var normalSectorCount = totalSize / normalSectorSize;
                    if (normalSectorCount < 1)
                        throw new Exception($"Invalid FLASH size ({totalSize}) for sector size of {normalSectorSize}");

                    return Repeat(normalSectorSize / 8, 4).Concat(new[] { normalSectorSize / 2 }).Concat(Repeat(normalSectorSize, normalSectorCount - 1)).ToArray();
                }

                public override FLASHBankDefinition[] ComputeLayout(uint FLASHSize, uint sectorSize, bool isDualBank)
                {
                    if (isDualBank)
                    {
                        var sizes = DoComputePageSizes(FLASHSize / 2, sectorSize);

                        return new[] {
                            new FLASHBankDefinition { FirstPageAddress = FLASHStart, PageSizes = sizes },
                            new FLASHBankDefinition { FirstPageAddress = FLASHStart + FLASHSize / 2, PageSizes = sizes, FirstPageID = GetSecondBankMask(sizes.Length) },
                        };
                    }
                    else
                        return new[] { new FLASHBankDefinition { FirstPageAddress = FLASHStart, PageSizes = DoComputePageSizes(FLASHSize, sectorSize) } };
                }
            }

            public class Linear : SectorLayout
            {
                public bool HasBankIDs;

                public override FLASHBankDefinition[] ComputeLayout(uint FLASHSize, uint sectorSize, bool isDualBank)
                {
                    uint pageCount = FLASHSize / sectorSize;

                    if (isDualBank)
                    {
                        var sizes = Enumerable.Range(0, (int)pageCount / 2).Select(x => sectorSize).ToArray();

                        return new[] {
                            new FLASHBankDefinition { FirstPageAddress = FLASHStart, PageSizes = sizes, BankID = 1, },
                            new FLASHBankDefinition {
                                FirstPageAddress = FLASHStart + FLASHSize / 2,
                                PageSizes = sizes,
                                FirstPageID = HasBankIDs ? 0 : GetSecondBankMask(sizes.Length),
                                BankID = 2,
                            }
                        };
                    }
                    else
                        return new[] { new FLASHBankDefinition { 
                            FirstPageAddress = FLASHStart,
                            PageSizes = Enumerable.Range(0, (int)pageCount).Select(x => sectorSize).ToArray(),
                            BankID = 1,
                        } };
                }
            }
        }

        public string DeviceIDRegisters;
        public string DeviceIDMask;
        public string BreakpointCountRegister;
        public string BreakpointCountMask;
        public DeviceFamily[] Families;


        public static uint ParseUInt32(string value, string name)
        {
            if (value == null)
                throw new Exception("Unspecified value for " + name);

            uint multiplier = 1;
            if (value.EndsWith("K", StringComparison.CurrentCultureIgnoreCase))
            {
                multiplier = 1024;
                value = value.Substring(0, value.Length - 1);
            }
            else if (value.EndsWith("M", StringComparison.CurrentCultureIgnoreCase))
            {
                multiplier = 1024 * 1024;
                value = value.Substring(0, value.Length - 1);
            }

            bool parsed;
            uint result;
            if (value.StartsWith("0x"))
                parsed = uint.TryParse(value.Substring(2), NumberStyles.AllowHexSpecifier, null, out result);
            else
                parsed = uint.TryParse(value, out result);

            if (!parsed)
                throw new Exception($"Invalid value for {name}: {value}");

            return result * multiplier;
        }

        public static uint ExtractMaskedValue(uint value, uint mask)
        {
            uint result = 0;
            for (int i = 31; i >= 0; i--)
                if ((mask & (1 << i)) != 0)
                    result = (result << 1) | ((value >> i) & 1);

            return result;
        }
    }

    public struct FLASHBankDefinition
    {
        public int BankID;
        public int FirstPageID;
        public uint FirstPageAddress;
        public uint[] PageSizes;
    }
}
