using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartData.GPT.Tokenizer
{
    public interface ITokenizer
    {
        IEnumerable<Token> Tokenize(string text);
        IEnumerable<EncodedToken> Encode(int sequenceLength, string text);
        string Decode(long[] tokenIds); // Added Decode method

    }
}
