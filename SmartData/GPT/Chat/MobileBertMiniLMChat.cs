using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using SmartData.Attributes;
using SmartData.Configurations;
using SmartData.GPT.Embedder;
using SmartData.GPT.Search;
using SmartData.GPT.Tokenizer;
using System.Reflection;
using System.Text;

namespace SmartData.GPT.Chat
{
    public partial class MobileBertMiniLMChat : IDisposable
    {
        private readonly MLContext _mlContext;
        private readonly PredictionEngine<OnnxInput, OnnxOutput> _mobileBertEngine;
        private readonly FaissNetSearch _faissIndex;
        private readonly List<string> _conversationHistory;
        private readonly int _maxTokens;
        private readonly int _maxHistoryLength;
        private readonly string _tempModelPath;
        private bool _disposed;
        private readonly IEmbedder _embedder;
        private readonly ITokenizer _tokenizer;
        private readonly SqlData _sqlData;
        private readonly string _tableName;
        private readonly ILogger<MobileBertMiniLMChat> _logger;
        private readonly IServiceProvider _serviceProvider; // NEW: Injected IServiceProvider

        public MobileBertMiniLMChat(PredictionEngine<OnnxInput, OnnxOutput> mobileBertEngine, IEmbedder embedder, SqlData sqlData, IServiceProvider serviceProvider, string tableName, ILogger<MobileBertMiniLMChat> logger = null, int maxTokens = 128, int maxHistoryLength = 512)
        {
            _mlContext = new MLContext();
            _faissIndex = new FaissNetSearch(dimension: 384, logger: logger);
            _conversationHistory = new List<string>();
            _maxTokens = maxTokens;
            _maxHistoryLength = maxHistoryLength;
            _mobileBertEngine = mobileBertEngine ?? throw new ArgumentNullException(nameof(mobileBertEngine));
            _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
            _sqlData = sqlData ?? throw new ArgumentNullException(nameof(sqlData));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
            _logger = logger;

            _tokenizer = embedder.GetType()
                .GetField("_tokenizer", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(embedder) as ITokenizer
                ?? throw new InvalidOperationException("Tokenizer not accessible from IEmbedder.");

            _tempModelPath = LoadModelFromResource();
            _logger?.LogDebug("Initialized MobileBertMiniLMChat for table {TableName}", _tableName);
        }

        private string LoadModelFromResource()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "SmartData.GPT.Chat.model.mobilebert_squad11_int8_qdq_89.4f1.onnx";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                _logger?.LogError("Embedded resource '{ResourceName}' not found", resourceName);
                throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");
            }

            string tempPath = Path.GetTempFileName();
            using (var fileStream = File.Create(tempPath))
            {
                stream.CopyTo(fileStream);
            }

            _logger?.LogDebug("Loaded model to temporary path {TempPath}", tempPath);
            return tempPath;
        }

        public async Task<string> Chat(string userInput, int topK = 1)
        {
            if (_disposed)
            {
                _logger?.LogError("Attempted to chat after disposal");
                throw new ObjectDisposedException(nameof(MobileBertMiniLMChat));
            }

            _conversationHistory.Add($"[CLS] User: {userInput} [SEP]");

            float[] queryEmbedding = _embedder.GenerateEmbedding(userInput).ToArray();
            _logger?.LogDebug("Generated query embedding for input: {UserInput}", userInput);

            // Query FAISS index for top matching embeddings
            Guid[] retrievedContextIds = _faissIndex.Search(queryEmbedding, topK);
            StringBuilder contextBuilder = new StringBuilder();
            using (var scope = _serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
                var dataSet = new DataSet<object>(_sqlData, _serviceProvider, _faissIndex, _tableName); // NEW: Pass _serviceProvider
                foreach (var contextId in retrievedContextIds)
                {
                    var entity = await dataSet.FindByIdAsync(contextId.ToString());
                    if (entity != null)
                    {
                        var paragraph = GenerateParagraph(entity, dbContext);
                        if (!string.IsNullOrEmpty(paragraph))
                        {
                            contextBuilder.Append(paragraph).Append(" ");
                            _logger?.LogDebug("Retrieved context for EntityId {ContextId}: {Paragraph}", contextId, paragraph);
                        }
                    }
                }
            }

            string retrievedContext = contextBuilder.Length > 0
                ? contextBuilder.ToString().Trim()
                : "Sorry, I couldn't find a relevant answer.";
            _logger?.LogDebug("Constructed context: {RetrievedContext}", retrievedContext);

            string prompt = $"[CLS] User: {userInput} [SEP] Context: {retrievedContext} [SEP]";

            var encodedTokens = _tokenizer.Encode(_tokenizer.Tokenize(prompt).Count(), prompt);
            var bertInput = new BertInput
            {
                InputIds = encodedTokens.Select(t => t.InputIds).ToArray(),
                AttentionMask = encodedTokens.Select(t => t.AttentionMask).ToArray(),
                TypeIds = encodedTokens.Select(t => t.TokenTypeIds).ToArray()
            };

            var chatInput = new OnnxInput
            {
                InputIds = bertInput.InputIds,
                AttentionMask = bertInput.AttentionMask,
                TokenTypeIds = bertInput.TypeIds
            };

            var chatPrediction = _mobileBertEngine.Predict(chatInput);
            string response = ProcessLogits(chatPrediction, bertInput.InputIds);
            _logger?.LogDebug("Generated response: {Response}", response);

            _conversationHistory.Add($"Assistant: {response}");

            TrimHistory();

            return response;
        }

        private string GenerateParagraph(object entity, SmartDataContext dbContext)
        {
            var type = entity.GetType();
            var embeddableProperties = type.GetProperties()
                .Select(p => new { Property = p, Attribute = p.GetCustomAttribute<EmbeddableAttribute>() })
                .Where(x => x.Attribute != null)
                .OrderBy(x => x.Attribute.Priority)
                .ToList();

            if (!embeddableProperties.Any()) return string.Empty;

            var sb = new StringBuilder();
            foreach (var item in embeddableProperties)
            {
                var value = item.Property.GetValue(entity)?.ToString();
                if (!string.IsNullOrEmpty(value))
                {
                    var formatted = string.Format(item.Attribute.Format, value);
                    sb.Append(formatted + " ");
                }
            }
            return sb.ToString().Trim();
        }

        public void AddEmbedding(Guid entityId, float[] embedding)
        {
            if (_disposed)
            {
                _logger?.LogError("Attempted to add embedding after disposal");
                throw new ObjectDisposedException(nameof(MobileBertMiniLMChat));
            }

            _faissIndex.AddEmbedding(entityId, embedding);
        }

        public void UpdateEmbedding(Guid entityId, float[] embedding)
        {
            if (_disposed)
            {
                _logger?.LogError("Attempted to update embedding after disposal");
                throw new ObjectDisposedException(nameof(MobileBertMiniLMChat));
            }

            _faissIndex.UpdateEmbedding(entityId, embedding);
        }

        public void RemoveEmbedding(Guid entityId)
        {
            if (_disposed)
            {
                _logger?.LogError("Attempted to remove embedding after disposal");
                throw new ObjectDisposedException(nameof(MobileBertMiniLMChat));
            }

            _faissIndex.RemoveEmbedding(entityId);
        }

        public Guid[] Search(float[] queryEmbedding, int topK)
        {
            if (_disposed)
            {
                _logger?.LogError("Attempted to search after disposal");
                throw new ObjectDisposedException(nameof(MobileBertMiniLMChat));
            }

            return _faissIndex.Search(queryEmbedding, topK);
        }

        private string ProcessLogits(OnnxOutput output, long[] inputIds)
        {
            int startIndex = Array.IndexOf(output.StartLogits, output.StartLogits.Max());
            int endIndex = Array.IndexOf(output.EndLogits, output.EndLogits.Max());

            if (startIndex >= endIndex || startIndex < 0 || endIndex >= inputIds.Length)
            {
                _logger?.LogWarning("Invalid logits: startIndex={StartIndex}, endIndex={EndIndex}, inputIdsLength={InputIdsLength}", startIndex, endIndex, inputIds.Length);
                return "Sorry, I couldn't understand that.";
            }

            long[] answerTokens = inputIds.Skip(startIndex).Take(endIndex - startIndex + 1).ToArray();
            string answer = _tokenizer.Decode(answerTokens);
            return answer.Trim();
        }

        private void TrimHistory()
        {
            var prompt = string.Join(" ", _conversationHistory);
            var tokenCount = _tokenizer.Tokenize(prompt).Count();
            while (tokenCount > _maxHistoryLength && _conversationHistory.Count > 1)
            {
                _conversationHistory.RemoveAt(0);
                prompt = string.Join(" ", _conversationHistory);
                tokenCount = _tokenizer.Tokenize(prompt).Count();
            }
            _logger?.LogDebug("Trimmed conversation history to {Count} entries", _conversationHistory.Count);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _faissIndex?.Dispose();
                if (!string.IsNullOrEmpty(_tempModelPath) && File.Exists(_tempModelPath))
                {
                    try
                    {
                        File.Delete(_tempModelPath);
                        _logger?.LogDebug("Deleted temporary model file {TempPath}", _tempModelPath);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to delete temporary model file {TempPath}", _tempModelPath);
                    }
                }
                _disposed = true;
                _logger?.LogDebug("Disposed MobileBertMiniLMChat");
            }
        }
    }
}