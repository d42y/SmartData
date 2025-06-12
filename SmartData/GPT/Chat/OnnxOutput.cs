namespace d42y.SmartData.GPT.Chat
{
    using Microsoft.ML.Data;

    public partial class MobileBertMiniLMChat
    {
        // Output schema for MobileBERT (question answering)
        public class OnnxOutput
        {
            [ColumnName("start_logits")]
            public float[] StartLogits { get; set; }

            [ColumnName("end_logits")]
            public float[] EndLogits { get; set; }
        }
    }
}