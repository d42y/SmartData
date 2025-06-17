namespace SmartData.GPT.Chat
{
    using Microsoft.ML.Data;

    
        // Input schema for MobileBERT
        public class OnnxInput
        {
            [ColumnName("input_ids")]
            public long[] InputIds { get; set; }

            [ColumnName("attention_mask")]
            public long[] AttentionMask { get; set; }

            [ColumnName("token_type_ids")]
            public long[] TokenTypeIds { get; set; }
        }
    
}