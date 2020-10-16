#nullable enable

using Newtonsoft.Json;

namespace OmniSharp.Models.v1.Completion
{
    public class CompletionAfterInsertResponse
    {
        /// <summary>
        /// Text change to be applied to the document.
        /// </summary>
        public LinePositionSpanTextChange? Change { get; set; }
        /// <summary>
        /// Line to position the cursor on after applying <see cref="Change"/>.
        /// </summary>
        [JsonConverter(typeof(ZeroBasedIndexConverter))]
        public int? Line { get; set; }
        /// <summary>
        /// Column to position the cursor on after applying <see cref="Change"/>.
        /// </summary>
        [JsonConverter(typeof(ZeroBasedIndexConverter))]
        public int? Column { get; set; }
    }
}
