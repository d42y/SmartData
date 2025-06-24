namespace SmartData.Vectorizer.Tokenizer
{
    public class EncodedToken
    {
        public long InputIds { get; set; }
        public long TokenTypeIds { get; set; }
        public long AttentionMask { get; set; }
    }
}
