using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using BSPGenerationTools.Parsing;

namespace stm32_bsp_generator
{
    class PeripheralSubregisterParser
    {
        private ParseReportWriter.SingleDeviceFamilyHandle _ReportWriter;

        public PeripheralSubregisterParser(ParseReportWriter.SingleDeviceFamilyHandle reportWriter)
        {
            _ReportWriter = reportWriter;
        }

        private static bool ExtractFirstBitAndSize(ulong val, out int size, out int firstBit)
        {
            firstBit = 0;
            size = 0;
            //Find the first non-zero bit
            while (firstBit < 64 && (val & (1UL << firstBit)) == 0)
                firstBit++;

            firstBit = firstBit % 64;

            //Count the sequential non-zero bits
            while ((firstBit + size) < 64 && (val & (1UL << (firstBit + size))) != 0)
                size++;

            //Return false if there are other non-zero bit groups
            for (int end = firstBit + size; end < 64; end++)
                if ((val & (1UL << end)) != 0)
                    return false;

            return true;
        }

        static int CountMatchingItems(string[] left, string[] right)
        {
            int len = Math.Min(left.Length, right.Length);
            for (int i = 0; i < len; i++)
                if (left[i] != right[i])
                    return i;

            return len;
        }


        [Flags]
        enum SubregisterMatchingFlags
        {
            None = 0,
            StripIndicies = 1,
            ExpandIndicies = 2,
            ReplaceXes = 4,
            InvokeCustomRules = 8,
            MergeAdjacentNameComponents = 16,
        }

        bool RunSingleSubregisterLocatingPass(ParsedStructure str,
                                                     string[] nameComponents,
                                                     int skippedComponents,
                                                     SubregisterMatchingFlags flags,
                                                     out MatchedStructureField[] result)
        {
            for (int i = 1; i <= (nameComponents.Length - skippedComponents); i++)
            {
                string[] nameForMatching = nameComponents.Skip(skippedComponents).Take(i).ToArray();

                if (flags.HasFlag(SubregisterMatchingFlags.ReplaceXes))
                    nameForMatching = nameForMatching.Select(s => s.Replace("Sx", "").Replace("Lx", "").Replace("x", "")).ToArray();

                int? discardedIndex = null;
                if (flags.HasFlag(SubregisterMatchingFlags.StripIndicies))
                {
                    string lastComponent = nameForMatching.Last();
                    if (lastComponent.EndsWith("L") || lastComponent.EndsWith("H"))    //E.g. GPIO->AFRH/AFRL
                    {
                        discardedIndex = lastComponent.EndsWith("L") ? 0 : 1;
                        lastComponent = lastComponent.Substring(0, lastComponent.Length - 1);
                    }
                    else
                    {
                        int j = lastComponent.Length;
                        while (j > 0 && char.IsNumber(lastComponent[j - 1]))
                            j--;

                        if (j > 0 && j < lastComponent.Length)
                        {
                            discardedIndex = int.Parse(lastComponent.Substring(j)) - 1;
                            lastComponent = lastComponent.Substring(0, j);
                        }
                    }

                    nameForMatching[nameForMatching.Length - 1] = lastComponent;
                }

                if (flags.HasFlag(SubregisterMatchingFlags.InvokeCustomRules))
                    ManualPeripheralRegisterRules.ApplyKnownNameTransformations(ref nameForMatching);

                if (flags.HasFlag(SubregisterMatchingFlags.MergeAdjacentNameComponents))
                    nameForMatching = new[] { string.Join("", nameForMatching) };

                if (str.EntriesByName.TryGetValue(string.Join("_", nameForMatching), out var entry))
                {
                    result = new MatchedStructureField[]
                    {
                        new MatchedStructureField
                        {
                            SubregisterName = string.Join("_", nameComponents.Skip(skippedComponents + 1)),
                            Entry = entry,
                            StrippedIndex = discardedIndex,
                        }
                    };
                    return true;
                }

                if (flags.HasFlag(SubregisterMatchingFlags.ExpandIndicies) && str.EntriesByNameWithoutTrailingIndex.TryGetValue(string.Join("_", nameForMatching), out var entries))
                {
                    result = entries.Select(f =>
                        new MatchedStructureField
                        {
                            SubregisterName = string.Join("_", nameComponents.Skip(skippedComponents + 1)),
                            Entry = f,
                            StrippedIndex = discardedIndex,
                        }
                    ).ToArray();
                    return true;
                }
            }

            result = null;
            return false;
        }

        struct MatchedStructureField
        {
            public ParsedStructure.Entry Entry;
            public string SubregisterName;
            public int? StrippedIndex;
        }

        bool TryLocateFieldForSubregisterMacroName(ParsedStructure str, string[] nameComponents, int skippedComponents, out MatchedStructureField[] result)
        {
            //Pass 1. Try locating a structure field with the exact name of the macro (e.g. MODER for GPIO_MODER_xxx). 
            //We also iterate multi-component names for rare special cases (e.g. UCPD_TX_ORDSET_TXORDSET would split into UCPD->TX_ORDSET, not UCPD->TX)
            if (RunSingleSubregisterLocatingPass(str, nameComponents, skippedComponents, SubregisterMatchingFlags.None, out result))
                return true;

            //Pass 2. Try discarding instance numbers from the field name.
            if (RunSingleSubregisterLocatingPass(str, nameComponents, skippedComponents, SubregisterMatchingFlags.StripIndicies | SubregisterMatchingFlags.ExpandIndicies, out result))
                return true;

            //Pass 3. Try discarding 'x' and 'Sx' strings. E.g. 'DMA_SxCR_MBURST' becomes 'DMA_CR_MBURST' and will get matched to DMA->CR.
            if (RunSingleSubregisterLocatingPass(str, nameComponents, skippedComponents, SubregisterMatchingFlags.ReplaceXes, out result))
                return true;

            //Pass 4. Apply hardcoded name transformations that cannot be realistically guessed (e.g. OSPEEDR -> OSPEEDER). We should keep the amount of them to a minimum.
            if (RunSingleSubregisterLocatingPass(str, nameComponents, skippedComponents, SubregisterMatchingFlags.InvokeCustomRules, out result))
                return true;

            //Pass 5. Fix registers that don't have an underscore in the field names. APBx_FZ => APBxFZ
            if (RunSingleSubregisterLocatingPass(str, nameComponents, skippedComponents, SubregisterMatchingFlags.MergeAdjacentNameComponents, out result))
                return true;

            return false;
        }

        public void AttachSubregisterDefinitions(ParsedHeaderFile parsedFile, PeripheralRegisterGenerator2.DiscoveredPeripheral[] peripherals)
        {
            var peripheralsByName = peripherals.ToDictionary(p => p.Name);

            foreach (var grp in parsedFile.PreprocessorMacroGroups)
            {
                if (!grp.CommentText.Contains(" definition"))
                    continue;

                var newStyleMacros = grp.Macros.Where(m => m.Name.EndsWith("_Pos") || m.Name.EndsWith("_Msk")).ToArray();
                NamedSubregister[] subregisters;
                if (newStyleMacros.Length > 0)
                    subregisters = ExtractNewStyleSubregisters(parsedFile, newStyleMacros);
                else
                    subregisters = ExtractLegacySubregisters(parsedFile, grp.Macros);

                foreach (var subreg in subregisters)
                {
                    MatchedStructureField[] foundFields = LocateMatchingFields(parsedFile, subreg.Name, peripheralsByName, out var specificInstanceName);

                    if (foundFields == null || foundFields.Length == 0)
                    {
                        _ReportWriter.HandleOrphanedSubregisterMacro(grp, subreg);
                    }
                    else
                    {
                        foreach (var f in foundFields)
                        {
                            f.Entry.AddSubregister(subreg, f.StrippedIndex, specificInstanceName, f.SubregisterName);
                        }
                    }
                }
            }
        }

        private MatchedStructureField[] LocateMatchingFields(ParsedHeaderFile parsedFile,
                                                                    string subregisterName,
                                                                    Dictionary<string, PeripheralRegisterGenerator2.DiscoveredPeripheral> peripheralsByName,
                                                                    out string specificInstanceName)
        {
            ParsedStructure structureObj;
            MatchedStructureField[] foundFields = null;

            string[] components = subregisterName.Split('_');
            specificInstanceName = null;

            if (parsedFile.Structures.TryGetValue(components[0] + "_TypeDef", out structureObj) &&
                TryLocateFieldForSubregisterMacroName(structureObj, components, 1, out foundFields))
            {
                return foundFields;
            }

            if (peripheralsByName.TryGetValue(components[0], out var periph) &&
                TryLocateFieldForSubregisterMacroName(periph.Structure, components, 1, out foundFields))
            {
                specificInstanceName = periph.Name;
                return foundFields;
            }

            foreach (var s in parsedFile.Structures.Values)
                if (s.Name.StartsWith(components[0]))
                {
                    int prefixLen = CountMatchingItems(s.Name.Split('_'), components);
                    if (TryLocateFieldForSubregisterMacroName(s, components, prefixLen, out foundFields))
                        return foundFields;
                }

            return null;
        }

        /*
            This method extracts the subregister macros defined as follows

                #define ADC_ISR_ADRDY_Pos         (0U)                                         
                #define ADC_ISR_ADRDY_Msk         (0x1U << ADC_ISR_ADRDY_Pos)

         */
        private NamedSubregister[] ExtractNewStyleSubregisters(ParsedHeaderFile parsedFile, PreprocessorMacro[] newStyleMacros)
        {
            List<NamedSubregister> result = new List<NamedSubregister>();
            Dictionary<string, int> positionsByName = new Dictionary<string, int>();
            Dictionary<string, int> bitCountsByName = new Dictionary<string, int>();
            Dictionary<string, ulong> nonConsecutiveMasks = new Dictionary<string, ulong>();
            var resolver = new BasicExpressionResolver(false);

            foreach (var macro in newStyleMacros)
            {
                if (macro.ToString() == "#define GTZC_MPCBB_LCKVTR2_LCKSB32_Msk ( 0x01UL << GTZC_MPCBB_LCKVTR2_LCKSB32_Msk )")
                    continue;   //Bug in the STM32L5 family
                if (macro.ToString() == "#define TAMP_CR3_ITAMP7NOER_Msk ( 0x1UL << TAMP_CR3_ITAMP7NOER )")
                    continue;

                var expression = parsedFile.ResolveMacrosRecursively(macro.Value);
                var value = resolver.ResolveAddressExpression(expression);
                string key = macro.Name.Substring(0, macro.Name.Length - 4);

                if (value == null)
                    continue;

                if (macro.Name.EndsWith("_Pos"))
                    positionsByName[key] = (int)value.Value;
                else if (macro.Name.EndsWith("_Msk"))
                {
                    if (!ExtractFirstBitAndSize(value.Value, out var size, out var firstBit))
                        nonConsecutiveMasks.Add(key, value.Value);
                    else
                        bitCountsByName[key] = size;
                }
            }

            //Pass 2: resolve non-continuous bit ranges (note: STM does not follow a clear naming convention)
            foreach (var mask in nonConsecutiveMasks)
            {
                //Known case of simple masks
                if (mask.Key.StartsWith("EXTI_"))
                    continue;

                //Verify that no other bits with a possibly colliding name are defined
                string prefix = mask.Key + "_";
                if (Enumerable.Range(0, 32).Any(i => bitCountsByName.ContainsKey(prefix + i)))
                {
                    if (!char.IsDigit(mask.Key.Last()) && !Enumerable.Range(0, 32).Any(i => bitCountsByName.ContainsKey(mask.Key + i)))
                        prefix = mask.Key;
                    else
                        prefix = null;
                }

                if (prefix != null)
                {
                    int sequentialBitsInThisPart;

                    //Process each continuous bit subrange separately
                    for (int firstBitOfPart = 0, partNumber = 0; ; firstBitOfPart += sequentialBitsInThisPart)
                    {
                        while (firstBitOfPart < 64 && (mask.Value & (1UL << firstBitOfPart)) == 0)
                            firstBitOfPart++;

                        if (firstBitOfPart >= 64)
                            break;

                        sequentialBitsInThisPart = Enumerable.Range(0, 64)
                            .TakeWhile(i => (mask.Value & (1UL << (firstBitOfPart + i))) != 0)
                            .Count();

                        if (positionsByName.Any(kv => kv.Value == firstBitOfPart && bitCountsByName.ContainsKey(kv.Key)))
                        {
                            //This bit position is already known under a different name. Don't redefine it.
                            continue;
                        }

                        if (!bitCountsByName.ContainsKey(mask.Key)
                            && positionsByName.TryGetValue(mask.Key, out var origFirstBit)
                            && firstBitOfPart == origFirstBit)
                        {
                            //This subrange already has a known name, but no known size.
                            //Set the size to the number of bits we counted.
                            bitCountsByName[mask.Key] = sequentialBitsInThisPart;
                        }
                        else
                        {
                            //Create an entirely new entry for this bit range, as if it was a separate subregister
                            positionsByName.Add(prefix + partNumber, firstBitOfPart);
                            bitCountsByName.Add(prefix + partNumber, sequentialBitsInThisPart);
                        }

                        partNumber++;
                    }
                }
                else
                {
                    _ReportWriter.HandleInvalidNewStyleBitMask(mask.Key, mask.Value);
                }
            }

            foreach (var kv in positionsByName)
                if (bitCountsByName.TryGetValue(kv.Key, out var bitCount))
                    result.Add(new NamedSubregister { Name = kv.Key, Offset = kv.Value, BitCount = bitCount });

            return result.ToArray();
        }

        /*
            This method extracts the subregister macros for blocks that don't have any new-style (XXX_Pos/XXX_Msk) definitions.
         */
        private NamedSubregister[] ExtractLegacySubregisters(ParsedHeaderFile parsedFile, List<PreprocessorMacro> macros)
        {
            List<NamedSubregister> result = new List<NamedSubregister>();
            var resolver = new BasicExpressionResolver(false);

            foreach (var macro in macros)
            {
                BasicExpressionResolver.TypedInteger value;
                if (macro.Value.Length == 4 && macro.Value[0].Value == "B" && macro.Value[1].Value == "(" && macro.Value[3].Value == ")")
                {
                    //This is the B(number) macro used in STM32MP1 headers
                    value = new BasicExpressionResolver.TypedInteger { Value = 1U << int.Parse(macro.Value[2].Value) };
                }
                else
                {

                    var expression = parsedFile.ResolveMacrosRecursively(macro.Value);

                    if (expression.Length == 0)
                        continue;

                    //We are only interested in the ((type)0xVALUE) macros
                    if (expression.Count(t => t.Type != CppTokenizer.TokenType.Bracket && (t.Type != CppTokenizer.TokenType.Identifier)) > 0)
                        continue;

                    value = resolver.ResolveAddressExpression(expression);
                }

                if (value != null && ExtractFirstBitAndSize(value.Value, out var size, out var firstBit))
                {
                    result.Add(new NamedSubregister { Name = macro.Name, Offset = firstBit, BitCount = size });
                }
            }

            return result.ToArray();
        }
    }
}
