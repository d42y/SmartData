namespace SmartData.Vectorizer.Tokenizer
{
    using System.IO;
    using System.Reflection;
    using System.Text.RegularExpressions;

    public class BertTokenizer : ITokenizer
    {
        private readonly IDictionary<string, int> _vocab;
        private readonly BasicTokenizer _basicTokenizer;
        private readonly WordpieceTokenizer _wordpieceTokenizer;
        private readonly IDictionary<int, string> _invVocab;

        public BertTokenizer(bool isLowerCase = true, string unknownToken = Tokens.UNKNOWN_TOKEN, int maxInputCharsPerWord = 200)
        {
            // Load vocab.txt from embedded resource
            _vocab = LoadVocabFromResource();
            _invVocab = new Dictionary<int, string>();
            foreach (KeyValuePair<string, int> kv in _vocab)
            {
                _invVocab.Add(kv.Value, kv.Key);
            }
            _basicTokenizer = new BasicTokenizer(isLowerCase);
            _wordpieceTokenizer = new WordpieceTokenizer(_vocab, unknownToken, maxInputCharsPerWord);
        }

        // Constructor for custom vocab (optional, for flexibility)
        public BertTokenizer(IDictionary<string, int> vocab, bool isLowerCase = true, string unknownToken = Tokens.UNKNOWN_TOKEN, int maxInputCharsPerWord = 200)
        {
            _vocab = vocab ?? throw new ArgumentNullException(nameof(vocab));
            _invVocab = new Dictionary<int, string>();
            foreach (KeyValuePair<string, int> kv in _vocab)
            {
                _invVocab.Add(kv.Value, kv.Key);
            }
            _basicTokenizer = new BasicTokenizer(isLowerCase);
            _wordpieceTokenizer = new WordpieceTokenizer(_vocab, unknownToken, maxInputCharsPerWord);
        }

        private static IDictionary<string, int> LoadVocabFromResource()
        {
            //ListEmbeddedResources();
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "SmartData.Vectorizer.Embedder.Model.vocab.txt";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");
            }

            return VocabLoader.Load(stream);
        }

        private static void ListEmbeddedResources()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string[] resourceNames = assembly.GetManifestResourceNames();

            Console.WriteLine("Embedded Resources:");
            foreach (string name in resourceNames)
            {
                Console.WriteLine(name);
            }
        }

        public IEnumerable<Token> Tokenize(string text)
        {
            List<Token> outputTokens = new List<Token>()
            {
                new Token(Tokens.CLS_TOKEN, 0, _vocab[Tokens.CLS_TOKEN])
            };

            int segmentIndex = 0;
            foreach (string token in _basicTokenizer.Tokenize(text))
            {
                foreach (string subToken in _wordpieceTokenizer.Tokenize(token))
                {
                    var outputToken = new Token(subToken, segmentIndex, _vocab[subToken]);
                    outputTokens.Add(outputToken);

                    if (token == Tokens.SEPARATOR_TOKEN)
                    {
                        segmentIndex++;
                    }
                }
            }

            outputTokens.Add(new Token(Tokens.SEPARATOR_TOKEN, segmentIndex++, _vocab[Tokens.SEPARATOR_TOKEN]));

            return outputTokens;
        }

        public IEnumerable<EncodedToken> Encode(int sequenceLength, string text)
        {
            IEnumerable<Token> tokens = Tokenize(text);

            if (tokens.Count() > sequenceLength)
            {
                tokens = tokens.Take(sequenceLength);
            }

            IEnumerable<long> padding = Enumerable.Repeat(0L, sequenceLength - tokens.Count());
            return tokens
                .Select(token => new EncodedToken { InputIds = token.VocabularyIndex, TokenTypeIds = token.SegmentIndex, AttentionMask = 1L })
                .Concat(padding.Select(p => new EncodedToken { InputIds = p, TokenTypeIds = p, AttentionMask = p }));
        }

        public string Decode(long[] tokenIds)
        {
            if (tokenIds == null || tokenIds.Length == 0)
                return string.Empty;

            // Convert token IDs to tokens, skipping special tokens and handling subwords
            List<string> tokens = new List<string>();
            foreach (long id in tokenIds)
            {
                if (_invVocab.TryGetValue((int)id, out string token))
                {
                    // Skip special tokens like [CLS], [SEP], [PAD]
                    if (token == Tokens.CLS_TOKEN || token == Tokens.SEPARATOR_TOKEN || token == "[PAD]")
                        continue;

                    // Remove ## prefix for subword tokens
                    if (token.StartsWith("##"))
                        token = token.Substring(2);

                    tokens.Add(token);
                }
                else
                {
                    tokens.Add(Tokens.UNKNOWN_TOKEN);
                }
            }

            // Join tokens, handling spaces appropriately
            string decodedText = string.Empty;
            for (int i = 0; i < tokens.Count; i++)
            {
                // Avoid space before contractions or punctuation
                if (i > 0 && !tokens[i].StartsWith("'") && !Regex.IsMatch(tokens[i], @"^[\.,!?;]"))
                    decodedText += " ";
                decodedText += tokens[i];
            }

            return decodedText.Trim();
        }

    }


}