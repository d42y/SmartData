using Microsoft.Extensions.Logging;
using SmartData;
using SmartData.Attributes;
using SmartData.Configurations;
using SmartData.GPT.Embedder;
using SmartData.GPT.Search;
using System.Reflection;
using System.Text;

namespace DemoEmbedding
{
    public class ChatService
    {
        private readonly IEmbedder _embedder;
        private readonly FaissNetSearch _faissIndex;
        private readonly ILogger<ChatService> _logger;

        public ChatService(IEmbedder embedder, FaissNetSearch faissIndex, ILogger<ChatService> logger = null)
        {
            _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
            _faissIndex = faissIndex ?? throw new ArgumentNullException(nameof(faissIndex));
            _logger = logger;
        }

        public async Task<string> ChatAsync<T>(string query, SdSet<T> dataSet, int topK = 1) where T : class
        {
            if (string.IsNullOrEmpty(query))
            {
                _logger?.LogError("Query cannot be empty");
                throw new ArgumentException("Query cannot be empty.", nameof(query));
            }

            var queryEmbedding = _embedder.GenerateEmbedding(query).ToArray();
            _logger?.LogDebug("Generated query embedding for query: {Query}", query);

            var contextIds = _faissIndex.Search(queryEmbedding, topK);
            var contextBuilder = new StringBuilder();
            foreach (var contextId in contextIds)
            {
                var entity = await dataSet.FindByIdAsync(contextId.ToString());
                if (entity != null)
                {
                    var paragraph = GenerateParagraph(entity);
                    if (!string.IsNullOrEmpty(paragraph))
                    {
                        contextBuilder.Append(paragraph).Append(" ");
                        _logger?.LogDebug("Retrieved context for EntityId {ContextId}: {Paragraph}", contextId, paragraph);
                    }
                }
            }

            string context = contextBuilder.Length > 0
                ? contextBuilder.ToString().Trim()
                : "No relevant information found.";
            _logger?.LogDebug("Constructed context: {Context}", context);

            // Simple response generation (replace with advanced model if needed)
            string response = $"Based on the context: {context}, the answer to '{query}' is inferred as: {context.Split(' ').LastOrDefault() ?? "unknown"}.";
            _logger?.LogDebug("Generated response: {Response}", response);

            return response;
        }

        private string GenerateParagraph(object entity)
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
    }
}