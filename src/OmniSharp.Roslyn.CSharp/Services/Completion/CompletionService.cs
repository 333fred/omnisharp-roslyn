#nullable enable

using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions;
using OmniSharp.Mef;
using OmniSharp.Models;
using OmniSharp.Models.v1.Completion;
using OmniSharp.Options;
using OmniSharp.Roslyn.CSharp.Helpers;
using OmniSharp.Roslyn.CSharp.Services.Intellisense;
using CompletionItem = OmniSharp.Models.v1.Completion.CompletionItem;
using CompletionTriggerKind = OmniSharp.Models.v1.Completion.CompletionTriggerKind;
using CSharpCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using CSharpCompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;
using CSharpCompletionService = Microsoft.CodeAnalysis.Completion.CompletionService;

namespace OmniSharp.Roslyn.CSharp.Services.Completion
{
    [Shared]
    [OmniSharpHandler(OmniSharpEndpoints.Completion, LanguageNames.CSharp)]
    [OmniSharpHandler(OmniSharpEndpoints.CompletionResolve, LanguageNames.CSharp)]
    [OmniSharpHandler(OmniSharpEndpoints.CompletionAfterInsert, LanguageNames.CSharp)]
    public class CompletionService :
        IRequestHandler<CompletionRequest, CompletionResponse>,
        IRequestHandler<CompletionResolveRequest, CompletionResolveResponse>,
        IRequestHandler<CompletionAfterInsertRequest, CompletionAfterInsertResponse>
    {
        private readonly OmniSharpWorkspace _workspace;
        private readonly FormattingOptions _formattingOptions;
        private readonly ILogger _logger;

        private readonly object _lock = new object();
        private (CSharpCompletionList Completions, string FileName, int position)? _lastCompletion = null;

        [ImportingConstructor]
        public CompletionService(OmniSharpWorkspace workspace, FormattingOptions formattingOptions, ILoggerFactory loggerFactory)
        {
            _workspace = workspace;
            _formattingOptions = formattingOptions;
            _logger = loggerFactory.CreateLogger<CompletionService>();
        }

        public async Task<CompletionResponse> Handle(CompletionRequest request)
        {
            _logger.LogTrace("Completions requested");
            lock (_lock)
            {
                _lastCompletion = null;
            }

            var document = _workspace.GetDocument(request.FileName);
            if (document is null)
            {
                _logger.LogInformation("Could not find document for file {0}", request.FileName);
                return new CompletionResponse { Items = ImmutableArray<CompletionItem>.Empty };
            }

            var sourceText = await document.GetTextAsync();
            var position = sourceText.GetTextPosition(request);

            var completionService = CSharpCompletionService.GetService(document);
            Debug.Assert(request.TriggerCharacter != null || request.CompletionTrigger != CompletionTriggerKind.TriggerCharacter);

            if (request.CompletionTrigger == CompletionTriggerKind.TriggerCharacter &&
                !completionService.ShouldTriggerCompletion(sourceText, position, getCompletionTrigger(includeTriggerCharacter: true)))
            {
                _logger.LogTrace("Should not insert completions here.");
                return new CompletionResponse { Items = ImmutableArray<CompletionItem>.Empty };
            }

            var (completions, expandedItemsAvailable) = await completionService.GetCompletionsInternalAsync(
                document,
                position,
                getCompletionTrigger(includeTriggerCharacter: false));
            _logger.LogTrace("Found {0} completions for {1}:{2},{3}",
                             completions?.Items.IsDefaultOrEmpty != true ? 0 : completions.Items.Length,
                             request.FileName,
                             request.Line,
                             request.Column);

            if (completions is null || completions.Items.Length == 0)
            {
                return new CompletionResponse { Items = ImmutableArray<CompletionItem>.Empty };
            }

            if (request.TriggerCharacter == ' ' && !completions.Items.Any(c =>
            {
                var providerName = c.GetProviderName();
                return providerName == CompletionItemExtensions.OverrideCompletionProvider ||
                       providerName == CompletionItemExtensions.PartialMethodCompletionProvider ||
                       providerName == CompletionItemExtensions.ObjectCreationCompletionProvider;
            }))
            {
                // Only trigger on space if there is an object creation completion
                return new CompletionResponse { Items = ImmutableArray<CompletionItem>.Empty };
            }


            lock (_lock)
            {
                _lastCompletion = (completions, request.FileName, position);
            }

            // If we don't encounter any unimported types, and the completion context thinks that some would be available, then
            // that completion provider is still creating the cache. We'll mark this completion list as not completed, and the
            // editor will ask again when the user types more. By then, hopefully the cache will have populated and we can mark
            // the completion as done.
            bool expectingImportedItems = expandedItemsAvailable && _workspace.Options.GetOption(CompletionItemExtensions.ShowItemsFromUnimportedNamespaces, LanguageNames.CSharp) == true;

            var (finalCompletionItems, sawUnimportedCompletions) = await CompletionHelpers.BuildCompletionList(
                completionService,
                completions,
                document,
                sourceText,
                position,
                expectingImportedItems,
                request.UseAsyncCompletion ?? false,
                _logger);


            _logger.LogTrace("Completions filled in");

            return new CompletionResponse
            {
                IsIncomplete = !sawUnimportedCompletions && expectingImportedItems,
                Items = finalCompletionItems
            };

            CompletionTrigger getCompletionTrigger(bool includeTriggerCharacter)
                => request.CompletionTrigger switch
                {
                    CompletionTriggerKind.Invoked => CompletionTrigger.Invoke,
                    // https://github.com/dotnet/roslyn/issues/42982: Passing a trigger character
                    // to GetCompletionsAsync causes a null ref currently.
                    CompletionTriggerKind.TriggerCharacter when includeTriggerCharacter => CompletionTrigger.CreateInsertionTrigger((char)request.TriggerCharacter!),
                    _ => CompletionTrigger.Invoke,
                };
        }

        public async Task<CompletionResolveResponse> Handle(CompletionResolveRequest request)
        {
            if (_lastCompletion is null)
            {
                _logger.LogError("Cannot call completion/resolve before calling completion!");
                return new CompletionResolveResponse { Item = request.Item };
            }

            var (completions, fileName, position) = _lastCompletion.Value;

            if (request.Item is null
                || request.Item.Data >= completions.Items.Length
                || request.Item.Data < 0)
            {
                _logger.LogError("Received invalid completion resolve!");
                return new CompletionResolveResponse { Item = request.Item };
            }

            var lastCompletionItem = completions.Items[request.Item.Data];
            if (lastCompletionItem.DisplayTextPrefix + lastCompletionItem.DisplayText + lastCompletionItem.DisplayTextSuffix != request.Item.Label)
            {
                _logger.LogError($"Inconsistent completion data. Requested data on {request.Item.Label}, but found completion item {lastCompletionItem.DisplayText}");
                return new CompletionResolveResponse { Item = request.Item };
            }


            var document = _workspace.GetDocument(fileName);
            if (document is null)
            {
                _logger.LogInformation("Could not find document for file {0}", fileName);
                return new CompletionResolveResponse { Item = request.Item };
            }

            var completionService = CSharpCompletionService.GetService(document);

            var description = await completionService.GetDescriptionAsync(document, lastCompletionItem);

            StringBuilder textBuilder = new StringBuilder();
            MarkdownHelpers.TaggedTextToMarkdown(description.TaggedParts, textBuilder, _formattingOptions, MarkdownFormat.FirstLineAsCSharp, out _);

            request.Item.Documentation = textBuilder.ToString();

            string providerName = lastCompletionItem.GetProviderName();
            switch (providerName)
            {
                case CompletionItemExtensions.ExtensionMethodImportCompletionProvider:
                case CompletionItemExtensions.TypeImportCompletionProvider:
                    var syntax = await document.GetSyntaxTreeAsync();
                    var sourceText = await document.GetTextAsync();
                    var typedSpan = completionService.GetDefaultCompletionListSpan(sourceText, position);
                    var change = await completionService.GetChangeAsync(document, lastCompletionItem, typedSpan);
                    var (additionalTextEdits, offset) = CompletionHelpers.GetAdditionalTextEdits(change, sourceText, (CSharpParseOptions)syntax!.Options, typedSpan, lastCompletionItem.DisplayText, providerName, _logger);
                    if (offset > 0)
                    {
                        Debug.Assert(additionalTextEdits is object);
                        request.Item.AdditionalTextEdits = additionalTextEdits;
                    }
                    break;
            }

            return new CompletionResolveResponse
            {
                Item = request.Item
            };
        }

        public async Task<CompletionAfterInsertResponse> Handle(CompletionAfterInsertRequest request)
        {
            _logger.LogTrace("AfterInsert requested for {0}", request.FileName);

            var document = _workspace.GetDocument(request.FileName);
            if (document is null)
            {
                _logger.LogInformation("Could not find document for file {0}", request.FileName);
                return new CompletionAfterInsertResponse { Change = null, Column = null, Line = null };
            }

            var sourceText = await document.GetTextAsync();
            var position = sourceText.GetTextPosition(request);

            var completionService = CSharpCompletionService.GetService(document);

            var (completions, _) = await completionService.GetCompletionsInternalAsync(
                document,
                position);
            _logger.LogTrace("Found {0} completions for {1}:{2},{3}",
                             completions?.Items.IsDefaultOrEmpty != true ? 0 : completions.Items.Length,
                             request.FileName,
                             request.Line,
                             request.Column);

            if (completions is null || completions.Items.Length == 0)
            {
                _logger.LogWarning("Could not find completions after inserting the text");
                return new CompletionAfterInsertResponse { Change = null, Column = null, Line = null };
            }

            CSharpCompletionItem? resolvedCompletion = null;
            foreach (var c in completions.Items)
            {
                if (c.FilterText == request.Item.TextEdit.NewText)
                {
                    resolvedCompletion = c;
                    break;
                }
            }

            if (resolvedCompletion is null)
            {
                _logger.LogWarning("Could not find completion item for {0}", request.Item.Label);
                return new CompletionAfterInsertResponse { Change = null, Column = null, Line = null };
            }

            if (!resolvedCompletion.HasAsyncInsertStep())
            {
                _logger.LogTrace("Completion {0} has no async insert step", request.Item.Label);
                return new CompletionAfterInsertResponse { Change = null, Column = null, Line = null };
            }

            var resolvedChange = await completionService.GetChangeAsync(document, resolvedCompletion);

            // If there is no change other than the text that was inserted by the item, no need to do
            // anything else on the client side
            if (resolvedChange.TextChange.NewText == request.Item.TextEdit.NewText)
            {
                _logger.LogTrace("Completion {0} has no additional changes", request.Item.Label);
                return new CompletionAfterInsertResponse { Change = null, Column = null, Line = null };
            }

            _logger.LogTrace("Completion {0} has additional changes, formatting", request.Item.Label);
            SourceText finalText = sourceText.WithChanges(resolvedChange.TextChange);
            var finalPosition = finalText.Lines.GetLinePosition(resolvedChange.NewPosition ?? position);
            var replacingSpanStartPosition = sourceText.Lines.GetLinePosition(resolvedChange.TextChange.Span.Start);
            var replacingSpanEndPosition = sourceText.Lines.GetLinePosition(resolvedChange.TextChange.Span.End);

            return new CompletionAfterInsertResponse
            {
                Change = new LinePositionSpanTextChange
                {
                    NewText = resolvedChange.TextChange.NewText,
                    StartLine = replacingSpanStartPosition.Line,
                    StartColumn = replacingSpanStartPosition.Character,
                    EndLine = replacingSpanEndPosition.Line,
                    EndColumn = replacingSpanEndPosition.Character
                },
                Line = finalPosition.Line,
                Column = finalPosition.Character
            };
        }
    }
}
