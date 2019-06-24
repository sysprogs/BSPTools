using System.Collections.Generic;

namespace BSPGenerationTools.Parsing
{
    class SimpleTokenReader
    {
        private int _Index;
        private List<SimpleToken> _Tokens;

        public SimpleTokenReader(List<SimpleToken> tokens)
        {
            _Index = -1;
            _Tokens = tokens;
        }

        public bool EOF => _Index >= _Tokens.Count;

        public SimpleToken ReadNext(bool skipComments)
        {
            if (!EOF)
            {
                _Index++;
                while (skipComments && Current.Type == CppTokenizer.TokenType.Comment)
                {
                    _Index++;
                }
            }

            return Current;
        }

        public SimpleToken Current
        {
            get
            {
                if (EOF)
                    return new SimpleToken();
                else
                    return _Tokens[_Index];
            }
        }
    }

}
