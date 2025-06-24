using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartData.Vectorizer.Tokenizer
{
    public class Token
    {
        public Token(string value, long segmentIndex, long vocabularyIndex)
        {
            Value = value;
            VocabularyIndex = vocabularyIndex;
            SegmentIndex = segmentIndex;
        }

        public string Value { get; set; } = string.Empty;
        public long VocabularyIndex { get; set; }
        public long SegmentIndex { get; set; }
    }
}
