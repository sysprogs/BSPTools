using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BSPGenerationTools.Parsing
{
    public struct SimpleToken
    {
        public CppTokenizer.TokenType Type;
        public string Value;
        public int Line;

        public SimpleToken(CppTokenizer.TokenType type, string value, int line)
        {
            Type = type;
            Value = value;
            Line = line;
        }

        public override string ToString()
        {
            return Value;
        }

        public SimpleToken WithAppendedText(string text)
        {
            SimpleToken result = this;
            result.Value += text;
            return result;
        }
    }

    public struct PreprocessorMacro
    {
        public string Name;
        public SimpleToken[] Value;
        public string CombinedComments;

        public override string ToString()
        {
            return $"#define {Name} " + string.Join(" ", Value.Select(v => v.ToString()));
        }
    }

    public struct NamedSubregister
    {
        public string Name;
        public int Offset, BitCount;

        public ulong Mask => ((1UL << BitCount) - 1) << Offset;

        public override string ToString()
        {
            return Name;
        }
    }

    public struct SubregisterWithConstraints
    {
        public NamedSubregister Subregister;
        public int? RegisterInstanceFilter;
        public string PeripheralInstanceFilter;
        public string SubregisterName;

        public override string ToString()
        {
            string r = Subregister.Name;
            if (RegisterInstanceFilter != null)
                r += $" [reg#={RegisterInstanceFilter}]";
            if (PeripheralInstanceFilter != null)
                r += $" [periph={PeripheralInstanceFilter}]";

            return r;
        }
    }


    public class ParsedStructure
    {
        public class Entry
        {
            public string Name;
            public SimpleToken[] Type;

            public string TrailingComment;
            public int ArraySize;

            public List<SubregisterWithConstraints> Subregisters = new List<SubregisterWithConstraints>();

            public override string ToString()
            {
                return Name;
            }

            public void AddSubregister(NamedSubregister subreg, int? strippedIndex, string specificInstanceName, string subregisterName)
            {
                Subregisters.Add(new SubregisterWithConstraints
                {
                    Subregister = subreg,
                    SubregisterName = subregisterName,
                    PeripheralInstanceFilter = specificInstanceName,
                    RegisterInstanceFilter = strippedIndex,
                });
            }
        }

        public readonly string Name;
        public readonly Entry[] Entries;

        public readonly Dictionary<string, Entry> EntriesByName;
        public readonly Dictionary<string, IGrouping<string, Entry>> EntriesByNameWithoutTrailingIndex;

        public ParsedStructure(string name, Entry[] entries)
        {
            Name = name;
            Entries = entries;
            EntriesByName = entries.ToDictionary(e => e.Name);
            EntriesByNameWithoutTrailingIndex = entries.GroupBy(e => e.Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9')).ToDictionary(g => g.Key);
        }

        public override string ToString()
        {
            return Name;
        }
    }

    public class PreprocessorMacroGroup
    {
        public List<string> HeaderComments = new List<string>();
        public string CommentText => string.Join("\n", HeaderComments);

        public List<PreprocessorMacro> Macros = new List<PreprocessorMacro>();

        public override string ToString()
        {
            return HeaderComments.FirstOrDefault() ?? "";
        }
    }

    public class PreprocessorMacroCollection
    {
        public Dictionary<string, PreprocessorMacro> PreprocessorMacros = new Dictionary<string, PreprocessorMacro>();
        public PreprocessorMacroGroup[] PreprocessorMacroGroups;

        public SimpleToken[] ResolveMacrosRecursively(SimpleToken[] tokens, int maxLevel = 100)
        {
            if (maxLevel < 0)
                throw new Exception("Possible circular reference while resolving macros. Please check the call stack. for the macro name.");

            List<SimpleToken> result = new List<SimpleToken>();
            foreach (var token in tokens)
                if (token.Type == CppTokenizer.TokenType.Identifier && PreprocessorMacros.TryGetValue(token.Value, out var macro))
                    result.AddRange(ResolveMacrosRecursively(macro.Value, maxLevel - 1));
                else
                    result.Add(token);

            return result.ToArray();
        }
    }

    public class ParsedHeaderFile : PreprocessorMacroCollection
    {
        public string Path;
        public Dictionary<string, ParsedStructure> Structures = new Dictionary<string, ParsedStructure>();
    }

    public class HeaderFileParser
    {
        private string _FilePath;
        private readonly ParseReportWriter.SingleDeviceFamilyHandle _ReportWriter;

        public HeaderFileParser(string fn, ParseReportWriter.SingleDeviceFamilyHandle reportWriter)
        {
            _FilePath = fn;
            _ReportWriter = reportWriter;
        }

        class PreprocessorMacroGroupBuilder
        {
            List<PreprocessorMacroGroup> _Groups = new List<PreprocessorMacroGroup>();

            PreprocessorMacroGroup _ConstructedGroup;

            public void OnPreprocessorMacroDefined(PreprocessorMacro macro)
            {
                _ConstructedGroup?.Macros?.Add(macro);
            }

            public void OnTokenizedLineProcessed(CppTokenizer.Token[] tokens, string line)
            {
                if (tokens.Length == 0)
                    return;

                if (tokens.Length == 1 && tokens[0].Type == CppTokenizer.TokenType.Comment)
                {
                    var text = tokens[0].GetText(line);
                    if (_ConstructedGroup == null || _ConstructedGroup.Macros.Count > 0)
                        _Groups.Add(_ConstructedGroup = new PreprocessorMacroGroup());
                    _ConstructedGroup.HeaderComments.Add(text);
                }
                else
                {
                    _ConstructedGroup = null;
                }
            }

            public PreprocessorMacroGroup[] ExportGroups() => _Groups.ToArray();
        }

        List<SimpleToken> TokenizeFileAndFillPreprocessorMacroCollection(string[] lines, PreprocessorMacroCollection collection)
        {
            List<SimpleToken> result = new List<SimpleToken>();
            var tt = CppTokenizer.TokenType.Whitespace;
            var tokenizer = new CppTokenizer("");

            PreprocessorMacroGroupBuilder builder = new PreprocessorMacroGroupBuilder();

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                var tokens = tokenizer.TokenizeLine(line, ref tt, false);
                if (tokens.Length == 0)
                    continue;

                if (tokens[0].Type == CppTokenizer.TokenType.PreprocessorDirective)
                {
                    if (line.Substring(tokens[0].Start, tokens[0].Length) == "#define")
                    {
                        while (tokens.Last().Type == CppTokenizer.TokenType.Operator &&
                               line.Substring(tokens.Last().Start, tokens.Last().Length) == "\\")
                        {
                            //This is a multi-line #define statement. Combine it with the next line until we reach a line without a trailing backslash
                            var newTokens = tokenizer.TokenizeLine(lines[++lineIndex], ref tt, false);
                            tokens = tokens.Take(tokens.Length - 1).Concat(newTokens).ToArray();
                        }

                        List<SimpleToken> macroTokens = new List<SimpleToken>();
                        List<string> comment = new List<string>();

                        foreach (var token in tokens.Skip(2))
                        {
                            if (token.Type == CppTokenizer.TokenType.Comment)
                                comment.Add(line.Substring(token.Start, token.Length));
                            else
                                macroTokens.Add(new SimpleToken(token.Type, line.Substring(token.Start, token.Length), lineIndex));
                        }

                        PreprocessorMacro macro = new PreprocessorMacro
                        {
                            Name = line.Substring(tokens[1].Start, tokens[1].Length),
                            Value = macroTokens.ToArray(),
                            CombinedComments = comment.Count == 0 ? null : string.Join(" ", comment.ToArray())
                        };

                        builder.OnPreprocessorMacroDefined(macro);
                        collection.PreprocessorMacros[macro.Name] = macro;
                    }
                }
                else
                {
                    bool isFirstToken = true;
                    builder.OnTokenizedLineProcessed(tokens, line);

                    foreach (var token in tokens)
                    {

                        if (token.Type == CppTokenizer.TokenType.Comment && result.Count > 0 && result[result.Count - 1].Type == CppTokenizer.TokenType.Comment)
                        {
                            //Merge adjacent comments
                            string separator = isFirstToken ? "\n" : " ";
                            result[result.Count - 1] = result[result.Count - 1].WithAppendedText(separator + line.Substring(token.Start, token.Length));
                        }
                        else
                            result.Add(new SimpleToken(token.Type, line.Substring(token.Start, token.Length), lineIndex));

                        isFirstToken = false;
                    }
                }
            }

            collection.PreprocessorMacroGroups = builder.ExportGroups();
            return result;
        }

        public ParsedHeaderFile ParseHeaderFile()
        {
            ParsedHeaderFile result = new ParsedHeaderFile { Path = _FilePath };
            var tokens = TokenizeFileAndFillPreprocessorMacroCollection(File.ReadAllLines(_FilePath), result);
            ExtractStructureDefinitions(tokens, result.Structures);
            return result;
        }

        private void ExtractStructureDefinitions(List<SimpleToken> tokens, Dictionary<string, ParsedStructure> structures)
        {
            SimpleTokenReader reader = new SimpleTokenReader(tokens);

            while (!reader.EOF)
            {
                var token = reader.ReadNext(true);
                if (token.Type != CppTokenizer.TokenType.Identifier || token.Value != "typedef")
                    continue;

                token = reader.ReadNext(true);
                if (token.Type != CppTokenizer.TokenType.Identifier || token.Value != "struct")
                    continue;

                token = reader.ReadNext(true);
                if (token.Type == CppTokenizer.TokenType.Identifier)
                    token = reader.ReadNext(true);  //Skip through the struct name definition, as we will use the typedef name

                if (token.Type != CppTokenizer.TokenType.Bracket || token.Value != "{")
                {
                    ReportUnexpectedToken(token);
                    continue;
                }

                List<ParsedStructure.Entry> entries = new List<ParsedStructure.Entry>();
                List<SimpleToken> tokensInThisStatement = new List<SimpleToken>();

                while (!reader.EOF)
                {
                    token = reader.ReadNext(false);
                    if (token.Type == CppTokenizer.TokenType.Comment)
                    {
                        if (entries.Count > 0)
                            entries[entries.Count - 1].TrailingComment = token.Value;
                    }
                    if (token.Type == CppTokenizer.TokenType.Bracket && token.Value == "{")
                    {
                        //Nested structs are not supported
                        ReportUnexpectedToken(token);
                        break;
                    }
                    else if (token.Type == CppTokenizer.TokenType.Bracket && token.Value == "}")
                    {
                        token = reader.ReadNext(true);
                        if (token.Type != CppTokenizer.TokenType.Identifier)
                            ReportUnexpectedToken(token);
                        else
                            structures[token.Value] = new ParsedStructure(token.Value, entries.ToArray());

                        break;
                    }
                    else if (token.Type == CppTokenizer.TokenType.Operator && token.Value == ";")
                    {
                        entries.Add(ParseSingleStructureMember(tokensInThisStatement));
                        tokensInThisStatement.Clear();
                    }
                    else
                        tokensInThisStatement.Add(token);
                }

            }

        }

        private ParsedStructure.Entry ParseSingleStructureMember(List<SimpleToken> tokensInThisStatement)
        {
            int idx = tokensInThisStatement.Count - 1;
            if (tokensInThisStatement.Count == 0)
                throw new Exception("Empty structure member");

            int arraySize = 1;

            if (idx >= 2 && tokensInThisStatement[idx].Type == CppTokenizer.TokenType.Bracket)
            {
                if (tokensInThisStatement[idx].Value != "]")
                    throw new Exception("Unexpected bracket at the end of a structure statement");

                if (tokensInThisStatement[idx - 2].Value == "[")
                {
                    //Simple "Type Name[Size];" statement.
                    arraySize = (int)ParseMaybeHex(tokensInThisStatement[idx - 1].Value);
                    idx -= 3;
                }
                else
                {
                    int start = idx - 1;
                    while (start > 0 && tokensInThisStatement[start].Value != "[")
                        start--;

                    if (start <= 0)
                        throw new Exception("Could not find '[' for array size");

                    arraySize = (int)new BasicExpressionResolver(true).ResolveAddressExpression(tokensInThisStatement.Skip(start + 1).Take(idx - start - 1).ToArray()).Value;
                    idx = start - 1;
                }
            }

            if (tokensInThisStatement[idx].Type != CppTokenizer.TokenType.Identifier)
            {
                ReportUnexpectedToken(tokensInThisStatement[idx]);
                throw new Exception("Unexpected token");
            }

            return new ParsedStructure.Entry
            {
                Name = tokensInThisStatement[idx].Value,
                Type = tokensInThisStatement.Take(idx).ToArray(),
                ArraySize = arraySize
            };
        }

        public static ulong ParseMaybeHex(string text)
        {
            text = text.TrimEnd('U', 'L');
            if (text.StartsWith("0x"))
                return ulong.Parse(text.Substring(2), NumberStyles.AllowHexSpecifier, null);
            else
                return ulong.Parse(text);
        }

        public static ulong? TryParseMaybeHex(string text)
        {
            text = text.TrimEnd('U', 'L');
            bool done;
            ulong result;
            if (text.StartsWith("0x"))
                done = ulong.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, null, out result);
            else
                done = ulong.TryParse(text, out result);

            if (done)
                return result;
            else
                return null;
        }

        private void ReportUnexpectedToken(SimpleToken token)
        {
            _ReportWriter.HandleUnexpectedToken(token);
        }

    }
}
