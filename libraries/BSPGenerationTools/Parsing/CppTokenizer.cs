using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace BSPGenerationTools.Parsing
{
    //Not thread-safe
    public class CppTokenizer
    {
        Dictionary<string, bool> _Keywords = new Dictionary<string, bool>();

        const int CharMask = 0xFF;

        public enum TokenType
        {
            Invalid = 0,
            Whitespace,
            Identifier,
            Operator,
            Bracket,
            Keyword,
            CharacterLiteral,
            StringLiteral,
            IncludeDirectiveArgument,
            PreprocessorDirective,
            PreprocessorMacro,
            Comment,

            FirstRawLiteral,
            LastRawLiteral = FirstRawLiteral + 200,
        }

        readonly string[] _RawLiteralTable;

        TokenType[] _CharacterClassMap;
        public CppTokenizer(string annotatedKeywords, bool supportsRawLiterals = false)
        {
            if (supportsRawLiterals)
                _RawLiteralTable = new string[TokenType.LastRawLiteral - TokenType.FirstRawLiteral + 1];

            if (annotatedKeywords != null)
            {
                foreach (var kw in annotatedKeywords.Split('\n'))
                {
                    //"+keyword" <=> C++ keyword; "-keyword" <=> C keyword
                    if (kw.Length < 2)
                        continue;
                    switch (kw[0])
                    {
                        case '+':
                            _Keywords[kw.Substring(1)] = true;
                            break;
                        case '-':
                            _Keywords[kw.Substring(1)] = false;
                            break;
                    }
                }
            }

            _CharacterClassMap = new TokenType[CharMask + 1];
            for (int i = 0; i < _CharacterClassMap.Length; i++)
                _CharacterClassMap[i] = TokenType.Identifier;

            _CharacterClassMap[' '] = _CharacterClassMap['\t'] = _CharacterClassMap['\r'] = _CharacterClassMap['\n'] = TokenType.Whitespace;
            foreach (char ch in "!@#$%^&*,./~+-=:;|?<>")
                _CharacterClassMap[ch] = TokenType.Operator;
            foreach (char ch in "()[]{}")
                _CharacterClassMap[ch] = TokenType.Bracket;
            _CharacterClassMap['\"'] = TokenType.StringLiteral;
            _CharacterClassMap['\''] = TokenType.CharacterLiteral;
        }


        public struct Token
        {
            public TokenType Type;
            public int Start, Limit;

            public int Length { get { return Limit - Start; } }

            public string GetText(string line)
            {
                if (Start >= 0 && Limit >= 0 && Start < line.Length && Limit <= line.Length && Limit >= Start)
                    return line.Substring(Start, Limit - Start);
                else
                    return null;
            }
        }

        Token MakeToken(TokenType type, string line, int start, int limit, bool cppMode)
        {
            if (type == TokenType.Identifier)
            {
                bool isCpp;
                if (_Keywords.TryGetValue(line.Substring(start, limit - start), out isCpp))
                {
                    if (!isCpp || cppMode)
                        type = TokenType.Keyword;
                }
            }
            return new Token { Type = type, Start = start, Limit = limit };
        }

        public static bool IsOpeningBracket(char ch)
        {
            return ch == '(' || ch == '<' || ch == '{' || ch == '[';
        }

        public Token[] TokenizeLine(string line, ref TokenType tt, bool cpp)
        {
            List<Token> result = new List<Token>();
            if (line == null)
                return result.ToArray();

            result.Capacity = line.Length / 2;

            switch (tt)
            {
                case TokenType.CharacterLiteral:
                case TokenType.StringLiteral:
                case TokenType.Comment:
                case TokenType.Whitespace:
                    break;
                default:
                    if (tt >= TokenType.FirstRawLiteral && tt <= TokenType.LastRawLiteral)
                    {

                    }
                    else
                        tt = TokenType.Whitespace;
                    break;
            }

            bool nonWhitespaceEncountered = (tt != TokenType.Whitespace);
            int tokenStart = 0;
            bool thisTokenIsSingleCharToken = false;
            bool literalEscape = false;
            bool expectIncludeDirectiveArgument = false, expectPreprocessorMacro = false;
            int preprocessorDirectiveStart = -1;
            bool thisIdentifierIsNumber = false;
            bool firstIdentifierAfterPragma = false;

            for (int i = 0; i < line.Length; i++)
            {
                if (_RawLiteralTable != null && tt >= TokenType.FirstRawLiteral && tt <= TokenType.LastRawLiteral)
                {
                    string delimiter = _RawLiteralTable[tt - TokenType.FirstRawLiteral];
                    if (delimiter != null)
                    {
                        int endIndex = line.IndexOf(")" + delimiter + "\"", i);
                        if (endIndex == -1)
                        {
                            i = line.Length;
                            result.Add(MakeToken(tt, line, tokenStart, line.Length, cpp));
                            return result.ToArray();
                        }
                        else
                        {
                            i = endIndex + delimiter.Length + 2;
                            if (i >= line.Length)
                            {
                                result.Add(MakeToken(tt, line, tokenStart, line.Length, cpp));
                                tt = TokenType.Whitespace;
                                return result.ToArray();
                            }
                        }
                    }
                }

                int delta = 0;
                bool forceSingleCharToken = false, forceTokenBreak = false;

                char ch = line[i];
                TokenType thisTT = _CharacterClassMap[ch & CharMask];
                if (tt == TokenType.Whitespace)
                {
                    if (!nonWhitespaceEncountered && ch == '#')
                    {
                        thisTT = TokenType.PreprocessorDirective;
                        preprocessorDirectiveStart = -1;
                    }
                    if (expectIncludeDirectiveArgument && ((ch == '<') || (ch == '\"')))
                    {
                        thisTT = TokenType.IncludeDirectiveArgument;
                        forceSingleCharToken = true;
                    }
                    if (expectPreprocessorMacro && thisTT == TokenType.Identifier)
                        thisTT = TokenType.PreprocessorMacro;
                }

                if (thisTT != TokenType.Whitespace)
                {
                    nonWhitespaceEncountered = true;
                    expectIncludeDirectiveArgument = false;
                    expectPreprocessorMacro = false;
                }

                switch (tt)
                {
                    case TokenType.PreprocessorDirective:
                        if (thisTT == TokenType.Whitespace && preprocessorDirectiveStart == -1)
                            thisTT = tt;
                        else if (thisTT == TokenType.Identifier)
                        {
                            thisTT = tt;
                            if (preprocessorDirectiveStart == -1)
                                preprocessorDirectiveStart = i;
                        }
                        else if (preprocessorDirectiveStart != -1)
                        {
                            string directive = line.Substring(preprocessorDirectiveStart, i - preprocessorDirectiveStart);

                            if (directive == "include")
                            {
                                delta = -1;
                                expectIncludeDirectiveArgument = true;
                                break;
                            }
                            else if (directive == "define" || directive == "ifdef" || directive == "ifndef" || directive == "undef")
                            {
                                delta = -1;
                                expectPreprocessorMacro = true;
                                break;
                            }
                            else if (directive == "pragma")
                                firstIdentifierAfterPragma = true;
                        }
                        goto default;   //Handle potential beginning of comment without preceding spaces.
                    case TokenType.IncludeDirectiveArgument:
                        if (ch == '\"' || ch == '>')
                        {
                            i++;
                            delta = -1;
                            thisTT = TokenType.Whitespace;
                        }
                        else
                        {
                            thisTT = tt;
                            if (ch == '/' || ch == '\\')
                                forceSingleCharToken = true;
                        }
                        goto default;   //Handle potential beginning of comment without preceding spaces.
                    case TokenType.PreprocessorMacro:
                        if (thisTT == TokenType.Identifier)
                            thisTT = TokenType.PreprocessorMacro;
                        goto default;   //Handle potential beginning of comment without preceding spaces.
                    case TokenType.StringLiteral:
                    case TokenType.CharacterLiteral:
                        if (!literalEscape && (ch == '\'' || ch == '\"') && ((ch == '\'') == (tt == TokenType.CharacterLiteral)))
                        {
                            i++;
                            delta = -1;
                            thisTT = TokenType.Whitespace;
                        }
                        else
                            thisTT = tt;

                        if (ch == '\\' && !literalEscape)
                            literalEscape = true;
                        else
                            literalEscape = false;

                        break;
                    case TokenType.Comment:
                        if (ch == '*' && i < (line.Length - 1) && line[i + 1] == '/')
                        {
                            i += 2;
                            delta = -1;
                            thisTT = TokenType.Whitespace;
                        }
                        else
                            thisTT = tt;
                        break;
                    default:
                        //WARNING: This code is also invoked for several other token types (e.g. preprocessor directive or macro). Don't add any non-generic handling here.
                        if (ch == '/' && i < (line.Length - 1))
                        {
                            if (line[i + 1] == '/')
                            {
                                if (tt != TokenType.Comment)
                                    result.Add(MakeToken(tt, line, tokenStart, i, cpp));

                                //Special case: whole-line comment
                                result.Add(new Token { Start = i, Limit = line.Length, Type = TokenType.Comment });

                                tt = TokenType.Whitespace;
                                return result.ToArray();
                            }
                            else if (line[i + 1] == '*')
                            {
                                thisTT = TokenType.Comment;
                                delta++;  //Don't treat the '*' character as a potential end-of-comment as it's a part of the start-of-comment marker
                            }
                        }
                        break;
                }

                switch (thisTT)
                {
                    case TokenType.Bracket:
                        if (ch == '>' && tt == TokenType.Operator && i > 0 && line[i - 1] == '-')
                            thisTT = TokenType.Operator;
                        else if ((ch == '<' || ch == '>') && IsFollowedByEqualsSign(line, i))
                            thisTT = TokenType.Operator;
                        else
                            forceSingleCharToken = true;
                        break;
                    case TokenType.Operator:
                        if (ch == ',' || ch == ';' || (ch == '*' && (i >= (line.Length - 1) || line[i + 1] != '=')))
                            forceSingleCharToken = true;
                        else if (ch == '.' && tt == TokenType.Identifier && thisIdentifierIsNumber)
                            thisTT = TokenType.Identifier;
                        else if (ch == '-' && tt == TokenType.Identifier && i > 0 && thisIdentifierIsNumber && (line[i - 1] == 'e' || line[i - 1] == 'E'))
                            thisTT = tt;    //This is a part of the '123e-456' definition
                        else if (i != tokenStart && (ch == '-' || ch == '!' || ch == '~' || ch == '&' || ch == '*' || ch == '.') && ch != line[tokenStart])
                            forceTokenBreak = true;
                        else if ((i - tokenStart) >= 3)
                            forceTokenBreak = true;
                        break;
                    case TokenType.CharacterLiteral:
                        if (tt == TokenType.Identifier && thisIdentifierIsNumber)
                            thisTT = tt;    //Handle the "0xFFFF'FFFF'FFFF'FFFF" syntax
                        break;
                }

                if (thisTT != tt || forceSingleCharToken || thisTokenIsSingleCharToken || forceTokenBreak)
                {
                    if (firstIdentifierAfterPragma && tt == TokenType.Identifier)
                    {
                        tt = TokenType.Keyword;
                        firstIdentifierAfterPragma = false;
                    }

                    if (_RawLiteralTable != null && tt != TokenType.StringLiteral && thisTT == TokenType.StringLiteral && tokenStart == i - 1 && i > 0 && line[tokenStart] == 'R')
                    {
                        //This is a raw string literal
                        int bracket = line.IndexOf('(', i);
                        if (bracket > 0)
                        {
                            string delimiter = line.Substring(i + 1, bracket - i - 1);
                            int foundIndex = -1;
                            for (int j = 0; j < _RawLiteralTable.Length; j++)
                                if (_RawLiteralTable[j] == null)
                                {
                                    _RawLiteralTable[j] = delimiter;
                                    foundIndex = j;
                                    break;
                                }
                                else if (_RawLiteralTable[j] == delimiter)
                                {
                                    foundIndex = j;
                                    break;
                                }

                            if (foundIndex >= 0)
                            {
                                tt = TokenType.FirstRawLiteral + foundIndex;
                                i += delimiter.Length + 1;
                                continue;
                            }
                        }
                    }

                    if (tt != TokenType.Whitespace)
                        result.Add(MakeToken(tt, line, tokenStart, i, cpp));
                    tt = thisTT;
                    tokenStart = i;
                    thisTokenIsSingleCharToken = forceSingleCharToken;
                    literalEscape = false;

                    if (thisTT == TokenType.Identifier)
                        thisIdentifierIsNumber = (ch >= '0' && ch <= '9');
                }

                i += delta;
            }

            //WARNING! The code below is skipped if the raw string literal logic discards the remainder of the string and returns.
            if (tt != TokenType.Whitespace)
            {
                if (firstIdentifierAfterPragma && tt == TokenType.Identifier)
                    tt = TokenType.Keyword;

                result.Add(MakeToken(tt, line, tokenStart, line.Length, cpp));
            }

            switch (tt)
            {
                case TokenType.CharacterLiteral:
                case TokenType.StringLiteral:
                    if (!literalEscape)
                        tt = TokenType.Whitespace;
                    break;
            }

            return result.ToArray();
        }

        //Returns 'true' for '<' within '<<<<=' and '<='. I.e. followed by 0 or more instances of itself and then the '=' sign;
        static bool IsFollowedByEqualsSign(string line, int idx)
        {
            for (int i = idx + 1; i < line.Length; i++)
                if (line[i] == '=')
                    return true;
                else if (line[i] != line[idx])
                    return false;

            return false;
        }

        public static char GetMatchingBracket(char ch)
        {
            switch (ch)
            {
                case '(':
                    return ')';
                case ')':
                    return '(';
                case '[':
                    return ']';
                case ']':
                    return '[';
                case '{':
                    return '}';
                case '}':
                    return '{';
                case '<':
                    return '>';
                case '>':
                    return '<';
                default:
                    return '\0';
            }
        }

        public static bool IsMatchingBracket(char ch, char originalCh)
        {
            return ch == GetMatchingBracket(originalCh);
        }

        public static bool IsIdentifierChar(char ch)
        {
            return (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || ch == '_';
        }

        public static bool IsTemplateBracket(char ch)
        {
            return ch == '<' || ch == '>';
        }
    }
}
