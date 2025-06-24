namespace SmartData.Vectorizer.Tokenizer
{
    public interface ITokenizer
    {
        IEnumerable<Token> Tokenize(string text);
        IEnumerable<EncodedToken> Encode(int sequenceLength, string text);
        string Decode(long[] tokenIds); // Added Decode method

    }
}
