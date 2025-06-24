namespace SmartData.Vectorizer.Tokenizer
{
    public class VocabLoader
    {
        public static IDictionary<string, int> Load(string path)
        {
            IDictionary<string, int> vocab = new Dictionary<string, int>();
            int index = 0;
            IEnumerable<string> lines = File.ReadLines(path);
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line)) break;
                string trimmedLine = line.Trim();
                vocab.Add(trimmedLine, index++);
            }

            return vocab;
        }

        public static IDictionary<string, int> Load(Stream stream)
        {
            var vocab = new Dictionary<string, int>();
            using var reader = new StreamReader(stream);
            int index = 0;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string token = line.Trim();
                if (!string.IsNullOrEmpty(token))
                {
                    vocab[token] = index++;
                }
            }
            return vocab;
        }
    }
}
