using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SmartData.GPT.Tokenizer;
using SmartData.GPT.Extensions;

namespace SmartData.GPT.Embedder
{
    /// <summary>
    /// Generate Embeddings via All-MiniLM-L6-v2
    /// </summary>
    public class AllMiniLmL6V2Embedder : IEmbedder, IDisposable
    {
        private readonly int _tokenSize = 256; // Default token size for All-MiniLM-L6-v2
        private readonly ITokenizer _tokenizer;
        private readonly bool _truncate = true;
        private readonly InferenceSession _inferenceSession;
        private readonly RunOptions _runOptions;
        private bool disposedValue;

        public AllMiniLmL6V2Embedder()
        {
            _tokenizer = new BertTokenizer();
            _inferenceSession = LoadModelFromResource();
            _runOptions = new RunOptions();
        }

        private InferenceSession LoadModelFromResource()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "SmartData.GPT.Embedder.Model.model.onnx";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");
            }

            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            byte[] modelBytes = memoryStream.ToArray();

            return new InferenceSession(modelBytes);
        }

        /// <summary>
        /// Generates an embedding array for the given sentence by splitting into tokenized chunks if necessary.
        /// </summary>
        /// <param name="sentence">Text to embed.</param>
        /// <returns>Sentence embeddings</returns>
        public IEnumerable<float> GenerateEmbedding(string sentence)
        {
            // Tokenize Input
            IEnumerable<Token> tokens = _tokenizer.Tokenize(sentence);
            var tokenList = tokens.ToList();

            // If truncation is enabled and tokens exceed tokenSize, process in chunks
            if (_truncate && tokenList.Count > _tokenSize)
            {
                // Split tokens into chunks of tokenSize
                var tokenChunks = new List<List<Token>>();
                for (int i = 0; i < tokenList.Count; i += _tokenSize)
                {
                    tokenChunks.Add(tokenList.Skip(i).Take(_tokenSize).ToList());
                }

                var embeddings = new List<float[]>();

                // Generate embeddings for each chunk
                foreach (var chunk in tokenChunks)
                {
                    IEnumerable<EncodedToken> encodedTokens = _tokenizer.Encode(chunk.Count, string.Join(" ", chunk.Select(t => t.Value)));

                    BertInput bertInput = new BertInput
                    {
                        InputIds = encodedTokens.Select(t => t.InputIds).ToArray(),
                        TypeIds = encodedTokens.Select(t => t.TokenTypeIds).ToArray(),
                        AttentionMask = encodedTokens.Select(t => t.AttentionMask).ToArray()
                    };

                    // Create input tensors
                    using OrtValue inputIdsOrtValue = OrtValue.CreateTensorValueFromMemory(bertInput.InputIds,
                        new long[] { 1, bertInput.InputIds.Length });

                    using OrtValue attMaskOrtValue = OrtValue.CreateTensorValueFromMemory(bertInput.AttentionMask,
                        new long[] { 1, bertInput.AttentionMask.Length });

                    using OrtValue typeIdsOrtValue = OrtValue.CreateTensorValueFromMemory(bertInput.TypeIds,
                        new long[] { 1, bertInput.TypeIds.Length });

                    IReadOnlyDictionary<string, OrtValue> inputs = new Dictionary<string, OrtValue>
                    {
                        { "input_ids", inputIdsOrtValue },
                        { "attention_mask", attMaskOrtValue },
                        { "token_type_ids", typeIdsOrtValue }
                    };

                    // Run inference
                    using IDisposableReadOnlyCollection<OrtValue> output = _inferenceSession.Run(_runOptions, inputs, _inferenceSession.OutputNames);

                    // Perform pooling
                    var pooled = SingleMeanPooling(output.First(), attMaskOrtValue);

                    // Store embedding
                    embeddings.Add(pooled.ToArray());
                }

                // Aggregate embeddings (mean pooling)
                var aggregated = AggregateEmbeddings(embeddings);

                // Normalize aggregated embedding
                var normalized = aggregated.Normalize(p: 2, dim: 1);

                return normalized.ToArray();
            }
            else
            {
                // Original logic for non-chunked input
                if (_truncate && tokenList.Count > _tokenSize)
                {
                    tokens = tokenList.Take(_tokenSize);
                }

                IEnumerable<EncodedToken> encodedTokens = _tokenizer.Encode(tokenList.Count, sentence);

                BertInput bertInput = new BertInput
                {
                    InputIds = encodedTokens.Select(t => t.InputIds).ToArray(),
                    TypeIds = encodedTokens.Select(t => t.TokenTypeIds).ToArray(),
                    AttentionMask = encodedTokens.Select(t => t.AttentionMask).ToArray()
                };

                using OrtValue inputIdsOrtValue = OrtValue.CreateTensorValueFromMemory(bertInput.InputIds,
                    new long[] { 1, bertInput.InputIds.Length });

                using OrtValue attMaskOrtValue = OrtValue.CreateTensorValueFromMemory(bertInput.AttentionMask,
                    new long[] { 1, bertInput.AttentionMask.Length });

                using OrtValue typeIdsOrtValue = OrtValue.CreateTensorValueFromMemory(bertInput.TypeIds,
                    new long[] { 1, bertInput.TypeIds.Length });

                IReadOnlyDictionary<string, OrtValue> inputs = new Dictionary<string, OrtValue>
                {
                    { "input_ids", inputIdsOrtValue },
                    { "attention_mask", attMaskOrtValue },
                    { "token_type_ids", typeIdsOrtValue }
                };

                using IDisposableReadOnlyCollection<OrtValue> output = _inferenceSession.Run(_runOptions, inputs, _inferenceSession.OutputNames);

                var pooled = SingleMeanPooling(output.First(), attMaskOrtValue);

                var normalized = pooled.Normalize(p: 2, dim: 1);

                return normalized.ToArray();
            }
        }

        /// <summary>
        /// Aggregates multiple embeddings by computing their mean.
        /// </summary>
        /// <param name="embeddings">List of embeddings to aggregate.</param>
        /// <returns>Aggregated embedding.</returns>
        private DenseTensor<float> AggregateEmbeddings(List<float[]> embeddings)
        {
            if (embeddings == null || !embeddings.Any())
            {
                throw new ArgumentException("Embeddings list cannot be empty.");
            }

            int embeddingSize = embeddings.First().Length;
            float[] aggregated = new float[embeddingSize];

            // Sum all embeddings
            foreach (var embedding in embeddings)
            {
                for (int i = 0; i < embeddingSize; i++)
                {
                    aggregated[i] += embedding[i];
                }
            }

            // Divide by number of embeddings to get mean
            int count = embeddings.Count;
            for (int i = 0; i < embeddingSize; i++)
            {
                aggregated[i] /= count;
            }

            // Create tensor for aggregated embedding
            return new DenseTensor<float>(aggregated, new[] { 1, embeddingSize });
        }

        /// <summary>
        /// Generates an embedding array for the given sentences.
        /// </summary>
        /// <param name="sentences">Text to embed.</param>
        /// <returns>An enumerable of embeddings.</returns>
        public IEnumerable<IEnumerable<float>> GenerateEmbeddings(IEnumerable<string> sentences)
        {
            // Tokenize Input
            IEnumerable<IEnumerable<Token>> allTokens = new List<IEnumerable<Token>>();
            IEnumerable<IEnumerable<EncodedToken>> allEncoded = new List<IEnumerable<EncodedToken>>();

            foreach (var sentence in sentences)
            {
                IEnumerable<Token> tokens = _tokenizer.Tokenize(sentence);

                if (_truncate && tokens.Count() > _tokenSize)
                {
                    tokens = tokens.Take(_tokenSize);
                }

                allTokens = allTokens.Append(tokens);
            }

            int maxSequence = allTokens.Max(t => t.Count());

            foreach (var sentence in sentences)
            {
                IEnumerable<EncodedToken> encodedTokens = _tokenizer.Encode(maxSequence, sentence);
                allEncoded = allEncoded.Append(encodedTokens);
            }

            // Compute Token Embeddings
            IEnumerable<BertInput> inputs = allEncoded.Select(e => new BertInput
            {
                InputIds = e.Select(t => t.InputIds).ToArray(),
                TypeIds = e.Select(t => t.TokenTypeIds).ToArray(),
                AttentionMask = e.Select(t => t.AttentionMask).ToArray()
            });

            // Create input tensors over the input data.
            var size = inputs.Count();
            var inputIds = inputs.SelectMany(i => i.InputIds).ToArray();
            using OrtValue inputIdsOrtValue = OrtValue.CreateTensorValueFromMemory(inputIds,
                  new long[] { size, maxSequence });

            var attentionMask = inputs.SelectMany(i => i.AttentionMask).ToArray();
            using OrtValue attMaskOrtValue = OrtValue.CreateTensorValueFromMemory(attentionMask,
                  new long[] { size, inputs.First().AttentionMask.Length });

            var typeIds = inputs.SelectMany(i => i.TypeIds).ToArray();
            using OrtValue typeIdsOrtValue = OrtValue.CreateTensorValueFromMemory(typeIds,
                  new long[] { size, maxSequence });

            // Create input data for session. Request all outputs in this case.
            IReadOnlyDictionary<string, OrtValue> ortInputs = new Dictionary<string, OrtValue>
            {
                { "input_ids", inputIdsOrtValue },
                { "attention_mask", attMaskOrtValue },
                { "token_type_ids", typeIdsOrtValue }
            };

            using IDisposableReadOnlyCollection<OrtValue> output = _inferenceSession.Run(_runOptions, ortInputs, _inferenceSession.OutputNames);

            // For now, perform this separately for each output value.
            return MultiplePostProcess(output.First(), attMaskOrtValue);
        }

        private float[][] MultiplePostProcess(OrtValue modelOutput, OrtValue attentionMask)
        {
            List<float[]> results = new List<float[]>();
            float[] output = modelOutput.GetTensorDataAsSpan<float>().ToArray();
            int[] dimensions = modelOutput.GetTensorTypeAndShape().Shape.Select(s => (int)s).ToArray();
            dimensions[0] = 1; // Since only processing 1 row at a time, set to 1.
            long shape = dimensions[0] * dimensions[1] * dimensions[2];

            long[] mask = attentionMask.GetTensorDataAsSpan<long>().ToArray();
            int[] maskDimensions = attentionMask.GetTensorTypeAndShape().Shape.Select(s => (int)s).ToArray();
            maskDimensions[0] = 1; // Since only processing 1 row at a time, set to 1.
            long maskShape = maskDimensions[0] * maskDimensions[1];
            int indicies = (int)Math.Floor(output.Length / (double)shape);

            for (long i = 0; i < indicies; i++)
            {
                long sourceIndex = shape * i;
                float[] buffer = new float[shape];
                Array.Copy(output, sourceIndex, buffer, 0, shape);
                DenseTensor<float> tokenTensor = new DenseTensor<float>(buffer, dimensions);

                long[] maskBuffer = new long[maskShape];
                long maskIndex = maskShape * i;
                Array.Copy(mask, maskIndex, maskBuffer, 0, maskShape);

                DenseTensor<float> maskTensor = new DenseTensor<float>(maskBuffer.Select(x => (float)x).ToArray(), maskDimensions);

                var pooled = MeanPooling(tokenTensor, maskTensor);
                // Normalize Embeddings
                var normalized = pooled.Normalize(p: 2, dim: 1);
                results.Add(normalized.ToArray());
            }

            return results.ToArray();
        }

        private DenseTensor<float> SingleMeanPooling(OrtValue modelOutput, OrtValue attentionMask)
        {
            DenseTensor<float> tokenTensor = OrtToTensor<float>(modelOutput);
            DenseTensor<float> maskTensor = AttentionMaskToTensor(attentionMask);
            return MeanPooling(tokenTensor, maskTensor);
        }

        private static DenseTensor<float> AttentionMaskToTensor(OrtValue attentionMask)
        {
            DenseTensor<long> maskIntTensor = OrtToTensor<long>(attentionMask);
            var maskFloatData = maskIntTensor.Select(x => (float)x).ToArray();
            DenseTensor<float> maskTensor = new DenseTensor<float>(maskFloatData, maskIntTensor.Dimensions);
            return maskTensor;
        }

        private DenseTensor<float> MeanPooling(DenseTensor<float> tokenTensor, DenseTensor<float> maskTensor)
        {
            DenseTensor<float> maskedSum = ApplyMaskAndSum(tokenTensor, maskTensor);
            return maskedSum;
        }

        private DenseTensor<float> ApplyMaskAndSum(DenseTensor<float> tokenTensor, DenseTensor<float> maskTensor)
        {
            var expanded = maskTensor.Unsqueeze(-1).Expand(tokenTensor.Dimensions.ToArray());

            var multiplied = tokenTensor.ElementWiseMultiply(expanded);

            var sum = multiplied.Sum(1);

            var sumMask = expanded.Sum(1);

            var clampedMask = sumMask.Clamp(min: 1e-9f);

            var result = sum.ElementWiseDivide(clampedMask);

            return result;
        }

        private static DenseTensor<T> OrtToTensor<T>(OrtValue value) where T : unmanaged
        {
            var typeAndShape = value.GetTensorTypeAndShape();
            var tokenShape = new ReadOnlySpan<int>(typeAndShape.Shape.Select(s => (int)s).ToArray());
            var tokenEmbeddings = value.GetTensorDataAsSpan<T>();
            DenseTensor<T> tokenTensor = new DenseTensor<T>(tokenShape);
            tokenEmbeddings.CopyTo(tokenTensor.Buffer.Span);
            return tokenTensor;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _inferenceSession?.Dispose();
                    _runOptions?.Dispose();
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}