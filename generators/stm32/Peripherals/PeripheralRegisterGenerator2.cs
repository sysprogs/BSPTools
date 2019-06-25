using BSPEngine;
using BSPGenerationTools;
using BSPGenerationTools.Parsing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace stm32_bsp_generator
{
    static class PeripheralRegisterGenerator2
    {
        public struct DiscoveredPeripheral
        {
            public class Register
            {
                public string Name;
                public uint Offset;
                public int SizeInBytes;
                public bool IsReadOnly;

                public ParsedStructure.Entry OriginalField;
                public int ZeroBasedIndex;

                public override string ToString()
                {
                    return Name;
                }

                public HardwareRegister ToHardwareRegister(DiscoveredPeripheral peripheral)
                {
                    return new HardwareRegister
                    {
                        Address = $"0x{peripheral.ResolvedBaseAddress + Offset:x8}",
                        Name = Name,
                        ReadOnly = IsReadOnly,
                        SizeInBits = SizeInBytes * 8,
                        SubRegisters = BuildSubregisterList(peripheral)
                    };
                }

                private HardwareSubRegister[] BuildSubregisterList(DiscoveredPeripheral peripheral)
                {
                    if (OriginalField.Subregisters == null)
                        return null;

                    var qualifyingSubregisters = OriginalField.Subregisters.Where(sr => SubregisterMatchesContext(sr, peripheral)).OrderBy(r => r.Subregister.Offset).ToArray();

                    ulong coveredMask = 0;
                    List<HardwareSubRegister> result = new List<HardwareSubRegister>();
                    foreach (var r in qualifyingSubregisters)
                    {
                        if ((coveredMask & r.Subregister.Mask) != 0)
                            continue;   //Overlap with previous values

                        coveredMask |= r.Subregister.Mask;

                        result.Add(new HardwareSubRegister
                        {
                            FirstBit = r.Subregister.Offset,
                            SizeInBits = r.Subregister.BitCount,
                            Name = r.SubregisterName
                        });
                    }

                    if (result.Count == 0)
                        return null;

                    return result.ToArray();
                }

                private bool SubregisterMatchesContext(SubregisterWithConstraints sr, DiscoveredPeripheral peripheral)
                {
                    if (sr.PeripheralInstanceFilter != null && sr.PeripheralInstanceFilter != peripheral.Name)
                        return false;

                    if (sr.RegisterInstanceFilter != null && sr.RegisterInstanceFilter != ZeroBasedIndex)
                        return false;

                    return true;
                }
            }

            public string Name;
            public ParsedStructure Structure;
            public ulong ResolvedBaseAddress;
            public Register[] Registers;

            public override string ToString()
            {
                return Name;
            }
        }

        public static HardwareRegisterSet[] GeneratePeripheralRegisterDefinitionsFromHeaderFile(string peripheralHeaderFile, CortexCore core, ParseReportWriter reportWriter)
        {
            using (var handle = reportWriter.BeginParsingFile(peripheralHeaderFile))
            {
                var parser = new HeaderFileParser(peripheralHeaderFile, handle);
                var parsedFile = parser.ParseHeaderFile();

                var peripherals = LocateStructsReferencedInBaseExpressions(parsedFile);
                var subregisterParser = new PeripheralSubregisterParser(handle);

                subregisterParser.AttachSubregisterDefinitions(parsedFile, peripherals);

                List<HardwareRegisterSet> sets = new List<HardwareRegisterSet>();
                string coreFile = $@"../../../CoreReg/OutCorexx/{core}.xml";
                if (core != CortexCore.Invalid)
                {
                    if (!File.Exists(coreFile))
                        throw new Exception("Unknown ARM core: " + core);

                    sets.Add(XmlTools.LoadObject<HardwareRegisterSet>(coreFile));
                }

                foreach (var peripheral in peripherals)
                {
                    sets.Add(new HardwareRegisterSet
                    {
                        Registers = peripheral.Registers.Select(r => r.ToHardwareRegister(peripheral)).ToArray(),
                        UserFriendlyName = peripheral.Name,
                        ExpressionPrefix = peripheral.Name + "->",
                    });
                }

                return sets.ToArray();
            }
        }

        private static DiscoveredPeripheral[] LocateStructsReferencedInBaseExpressions(ParsedHeaderFile parsedFile)
        {
            List<DiscoveredPeripheral> structs = new List<DiscoveredPeripheral>();
            /*  
                We are looking for preprocessor macros like the one below:

                    #define TIM3                ((TIM_TypeDef *) TIM3_BASE)

                This is done by looping over ALL preprocessor macros defined in the source file and pick the ones that refer to typedef-ed structs.
                Then we recursively resolve the macro definition, getting something like ((TIM_TypeDef*)(((uint32_t)0x40000000U)+0x00000400)).
                
                Finally, we use a very simple parser to compute that address defined in this macro and to verify that it's being cast to
                the correct type.

                Once we have computed the address and confirmed its type, we can reliably conclude that a peripheral defined by that struct is
                present at the specified address.
            */

            var resolver = new BasicExpressionResolver(true);

            foreach (var macro in parsedFile.PreprocessorMacros.Values)
            {
                foreach (var token in macro.Value)
                {
                    if (token.Type == CppTokenizer.TokenType.Identifier && parsedFile.Structures.TryGetValue(token.Value, out var obj))
                    {
                        var addressExpression = parsedFile.ResolveMacrosRecursively(macro.Value);

                        if (addressExpression.Count(t => t.Value == "*") == 0)
                            continue;   //Not an address

                        BasicExpressionResolver.TypedInteger addr;

                        try
                        {
                            addr = resolver.ResolveAddressExpression(addressExpression);
                        }
                        catch (BasicExpressionResolver.UnexpectedNonNumberException ex)
                        {
                            if (ex.Token.Value == "SDMMC_BASE")
                                continue;   //Known bug in STM32H7. The value is used, but not defined anywhere
                            throw;
                        }

                        if (addr.Type?.First().Value == obj.Name)
                            structs.Add(new DiscoveredPeripheral
                            {
                                ResolvedBaseAddress = addr.Value,
                                Structure = obj,
                                Name = macro.Name,
                                Registers = TranslateStructureFieldsToRegisters(obj, parsedFile)
                            });
                        else
                        {
                            //We have found a preprocessor macro referencing one of the typedefs, but not resolving to its type. This needs investigation.
                            Debugger.Break();
                        }

                        break;
                    }
                }
            }

            return structs.ToArray();
        }

        class ConstructedRegisterList
        {
            List<DiscoveredPeripheral.Register> _Registers = new List<DiscoveredPeripheral.Register>();

            public DiscoveredPeripheral.Register[] Complete() => _Registers.ToArray();

            public void TranslateStructureFieldsToRegistersRecursively(ParsedStructure obj, ParsedHeaderFile parsedFile, ref RegisterTranslationContext ctx, string prefix)
            {
                foreach (var field in obj.Entries)
                {
                    var type = field.Type
                                .Where(t => t.Type == CppTokenizer.TokenType.Identifier)
                                .Select(t => t.Value)
                                .Where(t => t != "__IO" && t != "__I" && t != "__O" && t != "const")
                                .ToArray();

                    bool isReadOnly = field.Type.Count(t => t.Value == "__I" || t.Value == "const") > 0;

                    if (type.Length > 1)
                        throw new Exception("Could not reduce register type to a single token: " + string.Join("", type));

                    int size;

                    switch (type[0])
                    {
                        case "int32_t":
                        case "uint32_t":
                            size = 4;
                            break;
                        case "int16_t":
                        case "uint16_t":
                            size = 2;
                            break;
                        case "int8_t":
                        case "uint8_t":
                            size = 1;
                            break;
                        default:
                            for (int i = 0; i < field.ArraySize; i++)
                            {
                                string extraPrefix;
                                if (field.ArraySize == 1)
                                    extraPrefix = $"{field.Name}.";
                                else
                                    extraPrefix = $"{field.Name}[{i}].";

                                TranslateStructureFieldsToRegistersRecursively(parsedFile.Structures[type[0]], parsedFile, ref ctx, prefix + extraPrefix);
                            }
                            continue;
                    }

                    if ((ctx.CurrentOffset % size) != 0)
                    {
                        ctx.CurrentOffset += (size - (ctx.CurrentOffset % size));
                    }

                    for (int i = 0; i < field.ArraySize; i++)
                    {
                        string nameSuffix = "";
                        if (field.ArraySize > 1)
                            nameSuffix = $"[{i}]";

                        if (!field.Name.StartsWith("RESERVED", StringComparison.InvariantCultureIgnoreCase))
                            _Registers.Add(new DiscoveredPeripheral.Register
                            {
                                Offset = (uint)ctx.CurrentOffset,
                                Name = field.Name + nameSuffix,
                                SizeInBytes = size,
                                IsReadOnly = isReadOnly,
                                OriginalField = field,
                                ZeroBasedIndex = i,
                            });

                        ctx.CurrentOffset += size;
                    }
                }
            }

        }

        struct RegisterTranslationContext
        {
            public int CurrentOffset;
        }

        private static DiscoveredPeripheral.Register[] TranslateStructureFieldsToRegisters(ParsedStructure obj, ParsedHeaderFile parsedFile)
        {
            ConstructedRegisterList lst = new ConstructedRegisterList();
            RegisterTranslationContext ctx = new RegisterTranslationContext();
            lst.TranslateStructureFieldsToRegistersRecursively(obj, parsedFile, ref ctx, "");
            return lst.Complete();
        }
    }
}
