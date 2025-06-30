namespace SmartData.Vectorizer.Tokenizer
{
    public interface ITokenizer
    {
        IEnumerable<Token> Tokenize(string sentence);
        IEnumerable<EncodedToken> Encode(int sequenceLength, string sentence);
    }

    public class Token
    {
        public string Value { get; set; }
    }

    public class EncodedToken
    {
        public long InputIds { get; set; }
        public long TokenTypeIds { get; set; }
        public long AttentionMask { get; set; }
    }

    public class BertInput
    {
        public long[] InputIds { get; set; }
        public long[] TypeIds { get; set; }
        public long[] AttentionMask { get; set; }
    }

    public class BertTokenizer : ITokenizer
    {
        public IEnumerable<Token> Tokenize(string sentence)
        {
            // Placeholder implementation (replace with actual BERT tokenization logic)
            return sentence.Split(' ').Select(word => new Token { Value = word });
        }

        public IEnumerable<EncodedToken> Encode(int sequenceLength, string sentence)
        {
            // Placeholder implementation (replace with actual BERT encoding logic)
            var tokens = Tokenize(sentence).ToList();
            var encoded = new List<EncodedToken>();
            for (int i = 0; i < sequenceLength; i++)
            {
                if (i < tokens.Count)
                    encoded.Add(new EncodedToken { InputIds = i + 1, TokenTypeIds = 0, AttentionMask = 1 });
                else
                    encoded.Add(new EncodedToken { InputIds = 0, TokenTypeIds = 0, AttentionMask = 0 });
            }
            return encoded;
        }
    }
}