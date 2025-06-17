namespace SmartData.GPT.Chat
{
    using Microsoft.ML.Data;

    
        // Output schema for MobileBERT (question answering)
        public class OnnxOutput
        {
            [ColumnName("start_logits")]
            public float[] StartLogits { get; set; }

            [ColumnName("end_logits")]
            public float[] EndLogits { get; set; }
        }
    
}