using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartData.Vectorizer.Tokenizer
{
    public class EncodedToken
    {
        public long InputIds { get; set; }
        public long TokenTypeIds { get; set; }
        public long AttentionMask { get; set; }
    }
}
