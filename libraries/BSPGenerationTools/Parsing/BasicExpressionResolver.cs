using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BSPGenerationTools.Parsing
{
    public class BasicExpressionResolver
    {
        private readonly bool _ThrowOnFailure;

        public BasicExpressionResolver(bool throwOnFailure)
        {
            _ThrowOnFailure = throwOnFailure;
        }

        public TypedInteger ResolveAddressExpression(SimpleToken[] addressExpression)
        {
            var reader = new SimpleTokenReader(addressExpression.ToList());
            reader.ReadNext(true);
            return ResolveAddressExpressionRecursively(reader);
        }

        public class TypedInteger
        {
            public ulong Value;
            public SimpleToken[] Type;

            public bool IsAPointer => Type?.Count(t => t.Value == "*") > 0;
        }

        TypedInteger ResolveAddressExpressionRecursively(SimpleTokenReader reader)
        {
            TypedInteger value = null;

            for (; ; )
            {
                if (value == null)
                    value = ReadSingleAtom(reader);

                if (reader.EOF || reader.Current.Value == ")")
                    return value;

                if (reader.Current.Type != CppTokenizer.TokenType.Operator)
                    throw new Exception("Expected an operator");

                var op = reader.Current.Value;
                reader.ReadNext(true);
                var next = ReadSingleAtom(reader);

                if (value == null || next == null)
                    return null;

                switch(op)
                {
                    case "+":
                        if (next.IsAPointer)
                            throw new Exception("Cannot add pointers");

                        if (value.IsAPointer)
                        {
                            //We need to multiply this by the pointer size
                            throw new NotImplementedException();
                        }

                        value = new TypedInteger { Value = value.Value + next.Value, Type = value.Type };
                        break;
                    case "<<":
                        if (next.IsAPointer || value.IsAPointer)
                            throw new Exception("Cannot shift pointers");

                        value = new TypedInteger { Value = value.Value << (int)next.Value, Type = value.Type };
                        break;
                    default:
                        throw new NotImplementedException();
                }

            }

        }

        //On entry, reader should point to the first available token. On exit, it will point to the first token AFTER the read expression.
        TypedInteger ReadSingleAtom(SimpleTokenReader reader)
        {
            if (reader.Current.Type == CppTokenizer.TokenType.Bracket && reader.Current.Value == "(")
            {
                var token = reader.ReadNext(true);
                if (token.Type == CppTokenizer.TokenType.Identifier && !char.IsNumber(token.Value[0]))
                {
                    //This is a cast operator (e.g. "(void *)0x1234").
                    List<SimpleToken> typeTokens = new List<SimpleToken>();
                    typeTokens.Add(token);
                    int level = 0;
                    for (; ; )
                    {
                        token = reader.ReadNext(true);
                        if (token.Type == CppTokenizer.TokenType.Bracket)
                        {
                            if (token.Value == "(")
                                level++;
                            else if (token.Value == ")")
                                level--;
                        }

                        if (level < 0)  //Found a closing bracket for the cast
                        {
                            reader.ReadNext(true);
                            break;
                        }

                        typeTokens.Add(token);
                    }

                    TypedInteger value = ReadSingleAtom(reader);
                    if (value != null)
                        value.Type = typeTokens.ToArray();
                    return value;
                }
                else
                {
                    //Other bracketed expression
                    var value = ResolveAddressExpressionRecursively(reader);
                    if (reader.Current.Value != ")")
                        throw new Exception("Expected ')'");
                    reader.ReadNext(true);
                    return value;
                }
            }
            else if (reader.Current.Type == CppTokenizer.TokenType.Identifier)
            {
                var token = reader.Current;
                ulong? value = HeaderFileParser.TryParseMaybeHex(token.Value);

                reader.ReadNext(true);

                if (!value.HasValue)
                {
                    if (_ThrowOnFailure)
                        throw new UnexpectedNonNumberException(token);
                    else
                        return null;
                }

                return new TypedInteger { Value = value.Value };
            }
            else
            {
                if (_ThrowOnFailure)
                    throw new Exception("Invalid atom: " + reader.Current.Value);
                else
                    return null;
            }
        }

        public class UnexpectedNonNumberException : Exception
        {
            public readonly SimpleToken Token;

            public UnexpectedNonNumberException(SimpleToken token)
                : base("Expected a number, found " + token.Value)
            {
                Token = token;
            }
        }
    }
}
