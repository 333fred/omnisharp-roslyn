#nullable enable

using OmniSharp.Mef;

namespace OmniSharp.Models.v1.Completion
{
    [OmniSharpEndpoint(OmniSharpEndpoints.CompletionAfterInsert, typeof(CompletionAfterInsertRequest), typeof(CompletionAfterInsertResponse))]
    public class CompletionAfterInsertRequest : Request
    {
        public CompletionItem Item { get; set; } = null!;
    }
}
