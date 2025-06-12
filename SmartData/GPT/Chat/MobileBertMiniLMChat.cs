using Microsoft.ML;
using SmartData.GPT.Embedder;
using SmartData.GPT.Tokenizer;
using System.Reflection;
namespace d42y.SmartData.GPT.Chat
{
    

    public partial class MobileBertMiniLMChat : IDisposable
    {
        private readonly MLContext _mlContext;
        private readonly PredictionEngine<OnnxInput, OnnxOutput> _mobileBertEngine;
        private readonly FaissNetSearch _faissIndex; // Updated to FaissNetSearch
        private readonly List<string> _conversationHistory;
        private readonly int _maxTokens;
        private readonly int _maxHistoryLength;
        private readonly string _tempModelPath;
        private bool _disposed;

        private static readonly Lazy<MobileBertMiniLMChat> _instance = new Lazy<MobileBertMiniLMChat>(() =>
            new MobileBertMiniLMChat(), true);

        public static MobileBertMiniLMChat Instance => _instance.Value;

        private MobileBertMiniLMChat(int maxTokens = 128, int maxHistoryLength = 512)
        {
            _mlContext = new MLContext();
            _faissIndex = new FaissNetSearch(dimension: 384); // 384 for all-MiniLM-L6-v2
            _conversationHistory = new List<string>();
            _maxTokens = maxTokens;
            _maxHistoryLength = maxHistoryLength;

            _tempModelPath = LoadModelFromResource();

            var pipeline = _mlContext.Transforms.ApplyOnnxModel(
                modelFile: _tempModelPath,
                outputColumnNames: new[] { "start_logits", "end_logits" },
                inputColumnNames: new[] { "input_ids", "attention_mask", "token_type_ids" });

            var emptyData = _mlContext.Data.LoadFromEnumerable(new OnnxInput[] { });
            var transformer = pipeline.Fit(emptyData);
            _mobileBertEngine = _mlContext.Model.CreatePredictionEngine<OnnxInput, OnnxOutput>(transformer);
        }

        private string LoadModelFromResource()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "SmartData.GPT.Chat.model.mobilebert_squad11_int8_qdq_89.4f1.onnx";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");
            }

            string tempPath = Path.GetTempFileName();
            using (var fileStream = File.Create(tempPath))
            {
                stream.CopyTo(fileStream);
            }

            return tempPath;
        }

        //public string Chat(string userInput, DataCore data)
        //{
        //    if (_disposed)
        //        throw new ObjectDisposedException(nameof(MobileBertMiniLMChat));

        //    _conversationHistory.Add($"[CLS] User: {userInput} [SEP]");

        //    float[] queryEmbedding = AllMiniLmL6V2Embedder.Instance.GenerateEmbedding(userInput).ToArray();

        //    // Query FAISS index for specific database
        //    Guid[] retrievedContexts = _faissIndex.Search(data.Name, queryEmbedding, 1);
        //    var textId = retrievedContexts.FirstOrDefault();
        //    string retrievedContext = data.GetParagraph(textId) ?? "Sorry, I couldn't find a relevant answer.";

        //    string prompt = $"[CLS] User: {userInput} [SEP] Context: {retrievedContext} [SEP]";

        //    var tokens = AllMiniLmL6V2Embedder.Instance.GetType()
        //        .GetField("_tokenizer", BindingFlags.NonPublic | BindingFlags.Instance)
        //        ?.GetValue(AllMiniLmL6V2Embedder.Instance) as ITokenizer;

        //    if (tokens == null)
        //        throw new InvalidOperationException("Tokenizer not accessible.");

        //    var encodedTokens = tokens.Encode(tokens.Tokenize(prompt).Count(), prompt);
        //    var bertInput = new BertInput
        //    {
        //        InputIds = encodedTokens.Select(t => t.InputIds).ToArray(),
        //        AttentionMask = encodedTokens.Select(t => t.AttentionMask).ToArray(),
        //        TypeIds = encodedTokens.Select(t => t.TokenTypeIds).ToArray()
        //    };

        //    var chatInput = new OnnxInput
        //    {
        //        InputIds = bertInput.InputIds,
        //        AttentionMask = bertInput.AttentionMask,
        //        TokenTypeIds = bertInput.TypeIds
        //    };

        //    var chatPrediction = _mobileBertEngine.Predict(chatInput);
        //    string response = ProcessLogits(chatPrediction, bertInput.InputIds);

        //    _conversationHistory.Add($"Assistant: {response}");

        //    TrimHistory();

        //    return response;
        //}

        //private string QueryEmbeddingDatabase(float[] queryEmbedding)
        //{
        //    // Replaced with FAISS search, default database
        //    var results = _faissIndex.Search("default", queryEmbedding, 1);
        //    return results.FirstOrDefault() ?? "Sorry, I couldn't find a relevant answer.";
        //}

        //private float CosineSimilarity(float[] a, float[] b)
        //{
        //    float dotProduct = 0, normA = 0, normB = 0;
        //    for (int i = 0; i < a.Length; i++)
        //    {
        //        dotProduct += a[i] * b[i];
        //        normA += a[i] * a[i];
        //        normB += b[i] * b[i];
        //    }
        //    return dotProduct / (float)(Math.Sqrt(normA) * Math.Sqrt(normB));
        //}

        private string ProcessLogits(OnnxOutput output, long[] inputIds)
        {
            int startIndex = Array.IndexOf(output.StartLogits, output.StartLogits.Max());
            int endIndex = Array.IndexOf(output.EndLogits, output.EndLogits.Max());

            if (startIndex >= endIndex || startIndex < 0 || endIndex >= inputIds.Length)
                return "Sorry, I couldn't understand that.";

            var tokens = AllMiniLmL6V2Embedder.Instance.GetType()
                .GetField("_tokenizer", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(AllMiniLmL6V2Embedder.Instance) as ITokenizer;

            if (tokens == null)
                throw new InvalidOperationException("Tokenizer not accessible.");

            long[] answerTokens = inputIds.Skip(startIndex).Take(endIndex - startIndex + 1).ToArray();
            string answer = tokens.Decode(answerTokens);
            return answer.Trim();
        }

        private void TrimHistory()
        {
            var tokens = AllMiniLmL6V2Embedder.Instance.GetType()
                .GetField("_tokenizer", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(AllMiniLmL6V2Embedder.Instance) as ITokenizer;

            if (tokens == null)
                return;

            var prompt = string.Join(" ", _conversationHistory);
            var tokenCount = tokens.Tokenize(prompt).Count();
            while (tokenCount > _maxHistoryLength && _conversationHistory.Count > 1)
            {
                _conversationHistory.RemoveAt(0);
                prompt = string.Join(" ", _conversationHistory);
                tokenCount = tokens.Tokenize(prompt).Count();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _mobileBertEngine?.Dispose();
                _faissIndex?.Dispose();
                if (!string.IsNullOrEmpty(_tempModelPath) && File.Exists(_tempModelPath))
                {
                    try
                    {
                        File.Delete(_tempModelPath);
                    }
                    catch
                    {
                        // Log or handle cleanup failure
                    }
                }
                _disposed = true;
            }
        }
    }
}