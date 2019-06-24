using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BSPGenerationTools.Parsing;

namespace stm32_bsp_generator
{
    public class PossibleSubregisterDefinition
    {
        public struct PossibleName
        {
            public string Peripheral;
            public string Register;
        }

        public struct Subregister
        {
            public string Name;
            public int FirstBit;
            public int BitCount;
        }

        public PossibleName[] PossibleNames;
        public Subregister[] Subregisters;
    }

    public struct PossibleSubregisterSet
    {
        public List<PossibleSubregisterDefinition> Subregisters;

    }

    static class PeripheralSubregisterParser
    {
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
            ReplaceXes = 2,
            InvokeCustomRules = 4,
        }

        static bool RunSingleSubregisterLocatingPass(ParsedStructure str,
                                                     string[] nameComponents,
                                                     int skippedComponents,
                                                     SubregisterMatchingFlags flags,
                                                     out ParsedStructure.Entry entry,
                                                     out string subregisterName,
                                                     out int strippedIndex)
        {
            strippedIndex = -1;
            for (int i = 1; i < (nameComponents.Length - skippedComponents); i++)
            {
                string[] nameForMatching = nameComponents.Skip(skippedComponents).Take(i).ToArray();

                if (flags.HasFlag(SubregisterMatchingFlags.ReplaceXes))
                    nameForMatching = nameForMatching.Select(s => s.Replace("Sx", "").Replace("Lx", "").Replace("x", "")).ToArray();

                int discardedIndex = -1;
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

                if (str.EntriesByName.TryGetValue(string.Join("_", nameForMatching), out entry))
                {
                    subregisterName = string.Join("_", nameComponents.Skip(skippedComponents + 1));
                    strippedIndex = discardedIndex;
                    return true;
                }
            }

            entry = null;
            subregisterName = null;
            return false;
        }
        static bool TryLocateFieldForSubregisterMacroName(ParsedStructure str, string[] nameComponents, int skippedComponents, out ParsedStructure.Entry entry, out string subregisterName, out int strippedIndex)
        {
            //Pass 1. Try locating a structure field with the exact name of the macro (e.g. MODER for GPIO_MODER_xxx). 
            //We also iterate multi-component names for rare special cases (e.g. UCPD_TX_ORDSET_TXORDSET would split into UCPD->TX_ORDSET, not UCPD->TX)
            if (RunSingleSubregisterLocatingPass(str, nameComponents, skippedComponents, SubregisterMatchingFlags.None, out entry, out subregisterName, out strippedIndex))
                return true;

            //Pass 2. Try discarding instance numbers from the field name.
            if (RunSingleSubregisterLocatingPass(str, nameComponents, skippedComponents, SubregisterMatchingFlags.StripIndicies, out entry, out subregisterName, out strippedIndex))
                return true;

            //Pass 3. Try discarding 'x' and 'Sx' strings. E.g. 'DMA_SxCR_MBURST' becomes 'DMA_CR_MBURST' and will get matched to DMA->CR.
            if (RunSingleSubregisterLocatingPass(str, nameComponents, skippedComponents, SubregisterMatchingFlags.ReplaceXes, out entry, out subregisterName, out strippedIndex))
                return true;

            //Pass 4. Apply hardcoded name transformations that cannot be realistically guessed (e.g. OSPEEDR -> OSPEEDER). We should keep the amount of them to a minimum.
            if (RunSingleSubregisterLocatingPass(str, nameComponents, skippedComponents, SubregisterMatchingFlags.InvokeCustomRules, out entry, out subregisterName, out strippedIndex))
                return true;

            return false;
        }

        public static PossibleSubregisterSet LocatePossibleSubregisterDefinitions(ParsedHeaderFile parsedFile)
        {
            PossibleSubregisterSet result = new PossibleSubregisterSet
            {
                Subregisters = new List<PossibleSubregisterDefinition>()
            };

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
                    string[] components = subreg.Name.Split('_');

                    ParsedStructure structureObj;

                    ParsedStructure.Entry entry = null;
                    string subregisterName = null;
                    int strippedIndex = -1;

                    if (!parsedFile.Structures.TryGetValue(components[0] + "_TypeDef", out structureObj) ||
                        !TryLocateFieldForSubregisterMacroName(structureObj, components, 1, out entry, out subregisterName, out strippedIndex))
                    {
                        foreach (var s in parsedFile.Structures.Values)
                            if (s.Name.StartsWith(components[0]))
                            {
                                int prefixLen = CountMatchingItems(s.Name.Split('_'), components);
                                if (TryLocateFieldForSubregisterMacroName(s, components, prefixLen, out entry, out subregisterName, out strippedIndex))
                                {
                                    structureObj = s;
                                    break;
                                }
                            }
                    }

                    if (entry != null)
                    {
                        entry.Subregisters.Add(subreg);
                    }
                    else
                    {
                    }
                }
            }

            return result;
        }

        /*
            This method extracts the subregister macros defined as follows

                #define ADC_ISR_ADRDY_Pos         (0U)                                         
                #define ADC_ISR_ADRDY_Msk         (0x1U << ADC_ISR_ADRDY_Pos)

         */
        private static NamedSubregister[] ExtractNewStyleSubregisters(ParsedHeaderFile parsedFile, PreprocessorMacro[] newStyleMacros)
        {
            List<NamedSubregister> result = new List<NamedSubregister>();
            Dictionary<string, int> positionsByName = new Dictionary<string, int>();
            Dictionary<string, int> bitCountsByName = new Dictionary<string, int>();
            var resolver = new BasicExpressionResolver(false);

            foreach (var macro in newStyleMacros)
            {
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
                    {
                        //TODO: warning
                    }
                    else
                        bitCountsByName[key] = size;
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
        private static NamedSubregister[] ExtractLegacySubregisters(ParsedHeaderFile parsedFile, List<PreprocessorMacro> macros)
        {
            List<NamedSubregister> result = new List<NamedSubregister>();
            var resolver = new BasicExpressionResolver(false);

            foreach (var macro in macros)
            {
                var expression = parsedFile.ResolveMacrosRecursively(macro.Value);

                if (expression.Length == 0)
                    continue;

                //We are only interested in the ((type)0xVALUE) macros
                if (expression.Count(t => t.Type != CppTokenizer.TokenType.Bracket && (t.Type != CppTokenizer.TokenType.Identifier)) > 0)
                    continue;

                var value = resolver.ResolveAddressExpression(expression);

                if (value != null && ExtractFirstBitAndSize(value.Value, out var size, out var firstBit))
                {
                    result.Add(new NamedSubregister { Name = macro.Name, Offset = firstBit, BitCount = size });
                }
            }

            return result.ToArray();
        }
    }
}
