using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SmartData.Vectorizer.Tokenizer;
using SmartData.Vectorizer.Extensions;
using System.Reflection;

namespace SmartData.Vectorizer
{
    public interface IEmbeddingProvider
    {
        float[] GenerateEmbedding(string sentence);
        float[][] GenerateEmbeddings(IEnumerable<string> sentences);
    }

    public class AllMiniLmL6V2Embedder : IEmbeddingProvider, IDisposable
    {
        private readonly int _tokenSize = 256;
        private readonly ITokenizer _tokenizer;
        private readonly bool _truncate = true;
        private readonly InferenceSession _inferenceSession;
        private readonly RunOptions _runOptions;
        private bool _disposed;

        public AllMiniLmL6V2Embedder(ITokenizer tokenizer = null)
        {
            _tokenizer = tokenizer ?? new BertTokenizer();
            _inferenceSession = LoadModelFromResource();
            _runOptions = new RunOptions();
        }

        private InferenceSession LoadModelFromResource()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "SmartData.Vectorizer.Model.model.onnx";
            using var stream = assembly.GetManifestResourceStream(resourceName) ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            return new InferenceSession(memoryStream.ToArray());
        }

        public float[] GenerateEmbedding(string sentence)
        {
            var tokens = _tokenizer.Tokenize(sentence).ToList();
            if (_truncate && tokens.Count > _tokenSize)
            {
                var tokenChunks = new List<List<Token>>();
                for (int i = 0; i < tokens.Count; i += _tokenSize)
                    tokenChunks.Add(tokens.Skip(i).Take(_tokenSize).ToList());

                var embeddings = new List<float[]>();
                foreach (var chunk in tokenChunks)
                {
                    var encodedTokens = _tokenizer.Encode(chunk.Count, string.Join(" ", chunk.Select(t => t.Value)));
                    var bertInput = new BertInput
                    {
                        InputIds = encodedTokens.Select(t => t.InputIds).ToArray(),
                        TypeIds = encodedTokens.Select(t => t.TokenTypeIds).ToArray(),
                        AttentionMask = encodedTokens.Select(t => t.AttentionMask).ToArray()
                    };

                    using var inputIdsOrtValue = OrtValue.CreateTensorValueFromMemory(bertInput.InputIds, new long[] { 1, bertInput.InputIds.Length });
                    using var attMaskOrtValue = OrtValue.CreateTensorValueFromMemory(bertInput.AttentionMask, new long[] { 1, bertInput.AttentionMask.Length });
                    using var typeIdsOrtValue = OrtValue.CreateTensorValueFromMemory(bertInput.TypeIds, new long[] { 1, bertInput.TypeIds.Length });

                    var inputs = new Dictionary<string, OrtValue>
                    {
                        { "input_ids", inputIdsOrtValue },
                        { "attention_mask", attMaskOrtValue },
                        { "token_type_ids", typeIdsOrtValue }
                    };

                    using var output = _inferenceSession.Run(_runOptions, inputs, _inferenceSession.OutputNames);
                    var pooled = SingleMeanPooling(output.First(), attMaskOrtValue);
                    embeddings.Add(pooled.ToArray());
                }

                var aggregated = AggregateEmbeddings(embeddings);
                return aggregated.Normalize(p: 2, dim: 1).ToArray();
            }
            else
            {
                if (_truncate && tokens.Count > _tokenSize)
                    tokens = tokens.Take(_tokenSize).ToList();

                var encodedTokens = _tokenizer.Encode(tokens.Count, sentence);
                var bertInput = new BertInput
                {
                    InputIds = encodedTokens.Select(t => t.InputIds).ToArray(),
                    TypeIds = encodedTokens.Select(t => t.TokenTypeIds).ToArray(),
                    AttentionMask = encodedTokens.Select(t => t.AttentionMask).ToArray()
                };

                using var inputIdsOrtValue = OrtValue.CreateTensorValueFromMemory(bertInput.InputIds, new long[] { 1, bertInput.InputIds.Length });
                using var attMaskOrtValue = OrtValue.CreateTensorValueFromMemory(bertInput.AttentionMask, new long[] { 1, bertInput.AttentionMask.Length });
                using var typeIdsOrtValue = OrtValue.CreateTensorValueFromMemory(bertInput.TypeIds, new long[] { 1, bertInput.TypeIds.Length });

                var inputs = new Dictionary<string, OrtValue>
                {
                    { "input_ids", inputIdsOrtValue },
                    { "attention_mask", attMaskOrtValue },
                    { "token_type_ids", typeIdsOrtValue }
                };

                using var output = _inferenceSession.Run(_runOptions, inputs, _inferenceSession.OutputNames);
                var pooled = SingleMeanPooling(output.First(), attMaskOrtValue);
                return pooled.Normalize(p: 2, dim: 1).ToArray();
            }
        }

        public float[][] GenerateEmbeddings(IEnumerable<string> sentences)
        {
            var allTokens = new List<IEnumerable<Token>>();
            var allEncoded = new List<IEnumerable<EncodedToken>>();

            foreach (var sentence in sentences)
            {
                var tokens = _tokenizer.Tokenize(sentence);
                if (_truncate && tokens.Count() > _tokenSize)
                    tokens = tokens.Take(_tokenSize);
                allTokens.Add(tokens);
            }

            int maxSequence = allTokens.Max(t => t.Count());
            foreach (var sentence in sentences)
                allEncoded.Add(_tokenizer.Encode(maxSequence, sentence));

            var inputs = allEncoded.Select(e => new BertInput
            {
                InputIds = e.Select(t => t.InputIds).ToArray(),
                TypeIds = e.Select(t => t.TokenTypeIds).ToArray(),
                AttentionMask = e.Select(t => t.AttentionMask).ToArray()
            });

            var size = inputs.Count();
            var inputIds = inputs.SelectMany(i => i.InputIds).ToArray();
            using var inputIdsOrtValue = OrtValue.CreateTensorValueFromMemory(inputIds, new long[] { size, maxSequence });
            var attentionMask = inputs.SelectMany(i => i.AttentionMask).ToArray();
            using var attMaskOrtValue = OrtValue.CreateTensorValueFromMemory(attentionMask, new long[] { size, inputs.First().AttentionMask.Length });
            var typeIds = inputs.SelectMany(i => i.TypeIds).ToArray();
            using var typeIdsOrtValue = OrtValue.CreateTensorValueFromMemory(typeIds, new long[] { size, maxSequence });

            var ortInputs = new Dictionary<string, OrtValue>
            {
                { "input_ids", inputIdsOrtValue },
                { "attention_mask", attMaskOrtValue },
                { "token_type_ids", typeIdsOrtValue }
            };

            using var output = _inferenceSession.Run(_runOptions, ortInputs, _inferenceSession.OutputNames);
            return MultiplePostProcess(output.First(), attMaskOrtValue);
        }

        private float[][] MultiplePostProcess(OrtValue modelOutput, OrtValue attentionMask)
        {
            var results = new List<float[]>();
            var output = modelOutput.GetTensorDataAsSpan<float>().ToArray();
            var dimensions = modelOutput.GetTensorTypeAndShape().Shape.Select(s => (int)s).ToArray();
            dimensions[0] = 1;
            long shape = dimensions[0] * dimensions[1] * dimensions[2];

            var mask = attentionMask.GetTensorDataAsSpan<long>().ToArray();
            var maskDimensions = attentionMask.GetTensorTypeAndShape().Shape.Select(s => (int)s).ToArray();
            maskDimensions[0] = 1;
            long maskShape = maskDimensions[0] * maskDimensions[1];
            int indices = (int)Math.Floor(output.Length / (double)shape);

            for (long i = 0; i < indices; i++)
            {
                long sourceIndex = shape * i;
                var buffer = new float[shape];
                Array.Copy(output, sourceIndex, buffer, 0, shape);
                var tokenTensor = new DenseTensor<float>(buffer, dimensions);

                var maskBuffer = new long[maskShape];
                long maskIndex = maskShape * i;
                Array.Copy(mask, maskIndex, maskBuffer, 0, maskShape);
                var maskTensor = new DenseTensor<float>(maskBuffer.Select(x => (float)x).ToArray(), maskDimensions);

                var pooled = MeanPooling(tokenTensor, maskTensor);
                var normalized = pooled.Normalize(p: 2, dim: 1);
                results.Add(normalized.ToArray());
            }
            return results.ToArray();
        }

        private DenseTensor<float> SingleMeanPooling(OrtValue modelOutput, OrtValue attentionMask)
        {
            var tokenTensor = OrtToTensor<float>(modelOutput);
            var maskTensor = AttentionMaskToTensor(attentionMask);
            return MeanPooling(tokenTensor, maskTensor);
        }

        private static DenseTensor<float> AttentionMaskToTensor(OrtValue attentionMask)
        {
            var maskIntTensor = OrtToTensor<long>(attentionMask);
            var maskFloatData = maskIntTensor.Select(x => (float)x).ToArray();
            return new DenseTensor<float>(maskFloatData, maskIntTensor.Dimensions);
        }

        private DenseTensor<float> MeanPooling(DenseTensor<float> tokenTensor, DenseTensor<float> maskTensor)
        {
            var expanded = maskTensor.Unsqueeze(-1).Expand(tokenTensor.Dimensions.ToArray());
            var multiplied = TensorExtensions.ElementWiseMultiply(tokenTensor, expanded); // Explicitly use TensorExtensions
            var sum = multiplied.Sum(1);
            var sumMask = expanded.Sum(1);
            var clampedMask = sumMask.Clamp(min: 1e-9f);
            var result = TensorExtensions.ElementWiseDivide(sum, clampedMask); // Explicitly use TensorExtensions
            return result;
        }

        private static DenseTensor<T> OrtToTensor<T>(OrtValue value) where T : unmanaged
        {
            var typeAndShape = value.GetTensorTypeAndShape();
            var tokenShape = new ReadOnlySpan<int>(typeAndShape.Shape.Select(s => (int)s).ToArray());
            var tokenEmbeddings = value.GetTensorDataAsSpan<T>();
            var tokenTensor = new DenseTensor<T>(tokenShape);
            tokenEmbeddings.CopyTo(tokenTensor.Buffer.Span);
            return tokenTensor;
        }

        private DenseTensor<float> AggregateEmbeddings(List<float[]> embeddings)
        {
            if (embeddings == null || !embeddings.Any())
                throw new ArgumentException("Embeddings list cannot be empty.");

            int embeddingSize = embeddings.First().Length;
            var aggregated = new float[embeddingSize];
            foreach (var embedding in embeddings)
                for (int i = 0; i < embeddingSize; i++)
                    aggregated[i] += embedding[i];

            int count = embeddings.Count;
            for (int i = 0; i < embeddingSize; i++)
                aggregated[i] /= count;

            return new DenseTensor<float>(aggregated, new[] { 1, embeddingSize });
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _inferenceSession?.Dispose();
                _runOptions?.Dispose();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }
}