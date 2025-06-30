using Microsoft.ML.OnnxRuntime.Tensors;

namespace SmartData.Vectorizer.Extensions
{
    public static class TensorExtensions
    {
        public static DenseTensor<float> ElementWiseMultiply(this DenseTensor<float> tensor, DenseTensor<float> other)
        {
            if (tensor.Dimensions.SequenceEqual(other.Dimensions))
            {
                var result = new DenseTensor<float>(tensor.Dimensions);
                for (int i = 0; i < tensor.Length; i++)
                    result.Buffer.Span[i] = tensor.Buffer.Span[i] * other.Buffer.Span[i];
                return result;
            }
            throw new ArgumentException("Tensors must have the same dimensions.");
        }

        public static DenseTensor<float> ElementWiseDivide(this DenseTensor<float> tensor, DenseTensor<float> other)
        {
            if (tensor.Dimensions.SequenceEqual(other.Dimensions))
            {
                var result = new DenseTensor<float>(tensor.Dimensions);
                for (int i = 0; i < tensor.Length; i++)
                    result.Buffer.Span[i] = tensor.Buffer.Span[i] / other.Buffer.Span[i];
                return result;
            }
            throw new ArgumentException("Tensors must have the same dimensions.");
        }

        public static DenseTensor<float> Sum(this DenseTensor<float> tensor, int dimension)
        {
            var shape = tensor.Dimensions.ToArray();
            if (dimension < 0 || dimension >= shape.Length)
                throw new ArgumentException("Invalid dimension for sum operation.");

            shape[dimension] = 1;
            var result = new DenseTensor<float>(shape);
            // Simplified sum implementation (replace with actual tensor summation logic if available)
            for (int i = 0; i < tensor.Length; i++)
                result.Buffer.Span[i % shape[0]] += tensor.Buffer.Span[i];
            return result;
        }

        public static DenseTensor<float> Unsqueeze(this DenseTensor<float> tensor, int dimension)
        {
            var shape = tensor.Dimensions.ToArray().ToList();
            if (dimension < 0 || dimension > shape.Count)
                throw new ArgumentException("Invalid dimension for unsqueeze operation.", nameof(dimension));

            shape.Insert(dimension, 1);
            var newShape = shape.ToArray();
            var buffer = tensor.Buffer.ToArray(); // Copy the data buffer
            return new DenseTensor<float>(buffer, newShape);
        }

        public static DenseTensor<float> Expand(this DenseTensor<float> tensor, int[] newShape)
        {
            // Simplified expand implementation (replace with actual tensor expansion logic if available)
            var buffer = tensor.Buffer.ToArray();
            return new DenseTensor<float>(buffer, newShape);
        }

        public static DenseTensor<float> Clamp(this DenseTensor<float> tensor, float min)
        {
            var result = new DenseTensor<float>(tensor.Dimensions);
            for (int i = 0; i < tensor.Length; i++)
                result.Buffer.Span[i] = Math.Max(tensor.Buffer.Span[i], min);
            return result;
        }

        public static DenseTensor<float> Normalize(this DenseTensor<float> tensor, float p = 2, int dim = 1)
        {
            var result = new DenseTensor<float>(tensor.Dimensions);
            var norm = (float)Math.Sqrt(tensor.Buffer.Span.ToArray().Sum(x => x * x));
            for (int i = 0; i < tensor.Length; i++)
                result.Buffer.Span[i] = tensor.Buffer.Span[i] / (norm == 0 ? 1 : norm);
            return result;
        }
    }
}