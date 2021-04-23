#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using OmniSharp.Models;
using OmniSharp.Models.v1.Completion;
using OmniSharp.Roslyn.CSharp.Helpers;
using OmniSharp.Roslyn.CSharp.Services.Intellisense;
using OmniSharp.Utilities;
using CompletionItem = OmniSharp.Models.v1.Completion.CompletionItem;
using CSharpCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using CSharpCompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;
using CSharpCompletionService = Microsoft.CodeAnalysis.Completion.CompletionService;

namespace OmniSharp.Roslyn.CSharp.Services.Completion
{
    internal static class CompletionHelpers
    {
        private static readonly Dictionary<string, CompletionItemKind> s_roslynTagToCompletionItemKind = new()
        {
            { WellKnownTags.Public, CompletionItemKind.Keyword },
            { WellKnownTags.Protected, CompletionItemKind.Keyword },
            { WellKnownTags.Private, CompletionItemKind.Keyword },
            { WellKnownTags.Internal, CompletionItemKind.Keyword },
            { WellKnownTags.File, CompletionItemKind.File },
            { WellKnownTags.Project, CompletionItemKind.File },
            { WellKnownTags.Folder, CompletionItemKind.Folder },
            { WellKnownTags.Assembly, CompletionItemKind.File },
            { WellKnownTags.Class, CompletionItemKind.Class },
            { WellKnownTags.Constant, CompletionItemKind.Constant },
            { WellKnownTags.Delegate, CompletionItemKind.Function },
            { WellKnownTags.Enum, CompletionItemKind.Enum },
            { WellKnownTags.EnumMember, CompletionItemKind.EnumMember },
            { WellKnownTags.Event, CompletionItemKind.Event },
            { WellKnownTags.ExtensionMethod, CompletionItemKind.Method },
            { WellKnownTags.Field, CompletionItemKind.Field },
            { WellKnownTags.Interface, CompletionItemKind.Interface },
            { WellKnownTags.Intrinsic, CompletionItemKind.Text },
            { WellKnownTags.Keyword, CompletionItemKind.Keyword },
            { WellKnownTags.Label, CompletionItemKind.Text },
            { WellKnownTags.Local, CompletionItemKind.Variable },
            { WellKnownTags.Namespace, CompletionItemKind.Module },
            { WellKnownTags.Method, CompletionItemKind.Method },
            { WellKnownTags.Module, CompletionItemKind.Module },
            { WellKnownTags.Operator, CompletionItemKind.Operator },
            { WellKnownTags.Parameter, CompletionItemKind.Variable },
            { WellKnownTags.Property, CompletionItemKind.Property },
            { WellKnownTags.RangeVariable, CompletionItemKind.Variable },
            { WellKnownTags.Reference, CompletionItemKind.Reference },
            { WellKnownTags.Structure, CompletionItemKind.Struct },
            { WellKnownTags.TypeParameter, CompletionItemKind.TypeParameter },
            { WellKnownTags.Snippet, CompletionItemKind.Snippet },
            { WellKnownTags.Error, CompletionItemKind.Text },
            { WellKnownTags.Warning, CompletionItemKind.Text },
        };

        /// <summary>
        /// Completion providers that must be eagerly resolved, regardless of whether async completion is turned on.
        /// </summary>
        private static readonly HashSet<string> s_forceEagerResolveProviders = new HashSet<string>()
        {
            // Attempting to re-complete will result in inserting a new completion item, leaving the old text
            CompletionItemExtensions.EmeddedLanguageCompletionProvider,
            // Attempting to re-complete will result in inserting a new completion item, leaving the old text
            CompletionItemExtensions.XmlDocCommentCompletionProvider
        };

        internal static async ValueTask<(IReadOnlyList<CompletionItem>, bool SawUnimportedItems)> BuildCompletionList(
            CSharpCompletionService completionService,
            CSharpCompletionList completions,
            Document document,
            SourceText sourceText,
            int position,
            bool expectingImportedItems,
            bool useAsyncCompletion)
        {
            var finalCompletionList = new List<CompletionItem>(completions.Items.Length);
            bool sawUnimportedItems = false;

            var typedSpan = completionService.GetDefaultCompletionListSpan(sourceText, position);
            string typedText = sourceText.GetSubText(typedSpan).ToString();

            ImmutableArray<string> filteredItems = typedText != string.Empty
                ? completionService.FilterItems(document, completions.Items, typedText).SelectAsArray(i => i.DisplayText)
                : ImmutableArray<string>.Empty;

            var replacingSpanStartPosition = sourceText.Lines.GetLinePosition(typedSpan.Start);
            var replacingSpanEndPosition = sourceText.Lines.GetLinePosition(typedSpan.End);

            for (int i = 0; i < completions.Items.Length; i++)
            {
                var completion = completions.Items[i];

                // if the completion is for the hidden Misc files project, skip it
                string providerName = completion.GetProviderName();
                if (providerName == CompletionItemExtensions.InternalsVisibleToCompletionProvider
                    && completion.DisplayText == Configuration.OmniSharpMiscProjectName)
                {
                    continue;
                }

                CompletionItem? finalCompletion;
                bool wasUnimported;
                if (useAsyncCompletion && !s_forceEagerResolveProviders.Contains(providerName))
                {
                    (finalCompletion, wasUnimported) = BuildCompletionForAsync(
                        completions,
                        completion,
                        i,
                        replacingSpanStartPosition,
                        replacingSpanEndPosition,
                        filteredItems,
                        expectingImportedItems);
                }
                else
                {
                    (finalCompletion, wasUnimported) = await BuildCompletionForSync(
                        completionService,
                        completions,
                        completion,
                        i,
                        document,
                        sourceText,
                        expectingImportedItems,
                        position,
                        typedSpan,
                        typedText,
                        filteredItems);

                }

                if (finalCompletion is null)
                {
                    continue;
                }

                sawUnimportedItems = sawUnimportedItems || wasUnimported;
                finalCompletionList.Add(finalCompletion);
            }

            return (finalCompletionList, sawUnimportedItems);
        }

        private static (CompletionItem, bool SawUnimportedItems) BuildCompletionForAsync(
            CSharpCompletionList completions,
            CSharpCompletionItem completion,
            int index,
            LinePosition replacingSpanStartPosition,
            LinePosition replacingSpanEndPosition,
            ImmutableArray<string> filteredItems,
            bool expectingImportedItems)
        {
            var (sawUnimportedCompletions, sortTextPrepend) = completion.GetProviderName() switch
            {
                CompletionItemExtensions.ExtensionMethodImportCompletionProvider => (true, "1"),
                CompletionItemExtensions.TypeImportCompletionProvider => (true, "1"),
                _ => (false, "0"),
            };

            // We use FilterText rather than DisplayText because the DisplayText can have characters that will
            // break the afterInsert text, such as the method arguments for override completion
            var insertText = completion.TryGetInsertionText(out var i) ? i : completion.FilterText;

            var commitCharacters = BuildCommitCharacters(completions, completion.Rules.CommitCharacterRules);

            return (new CompletionItem
            {
                Label = completion.DisplayTextPrefix + completion.DisplayText + completion.DisplayTextSuffix,
                TextEdit = new LinePositionSpanTextChange
                {
                    NewText = insertText,
                    StartLine = replacingSpanStartPosition.Line,
                    StartColumn = replacingSpanStartPosition.Character,
                    EndLine = replacingSpanEndPosition.Line,
                    EndColumn = replacingSpanEndPosition.Character
                },
                InsertTextFormat = InsertTextFormat.PlainText,
                // Ensure that unimported items are sorted after things already imported.
                SortText = expectingImportedItems ? sortTextPrepend + completion.SortText : completion.SortText,
                FilterText = completion.FilterText,
                Kind = GetCompletionItemKind(completion.Tags),
                Detail = completion.InlineDescription,
                Data = index,
                Preselect = completion.Rules.MatchPriority == MatchPriority.Preselect || filteredItems.Contains(completion.DisplayText),
                CommitCharacters = commitCharacters,
            }, sawUnimportedCompletions);
        }

        private static async ValueTask<(CompletionItem, bool WasUnimported)> BuildCompletionForSync(
            CSharpCompletionService completionService,
            CSharpCompletionList completions,
            CSharpCompletionItem completion,
            int index,
            Document document,
            SourceText sourceText,
            bool expectingImportedItems,
            int position,
            TextSpan typedSpan,
            string typedText,
            ImmutableArray<string> filteredItems)
        {
            bool wasUnimported = false;
            var insertTextFormat = InsertTextFormat.PlainText;

            // Except for import completion, we just resolve the change up front in the sync version. It's only expensive
            // for override completion, but there's not a heck of a lot we can do about that for the sync scenario
            TextSpan changeSpan = typedSpan;
            string labelText = completion.DisplayTextPrefix + completion.DisplayText + completion.DisplayTextSuffix;
            List<LinePositionSpanTextChange>? additionalTextEdits = null;
            string? insertText = null;
            string? filterText = null;
            string? sortText = null;
            if (completion.GetProviderName() is CompletionItemExtensions.TypeImportCompletionProvider or CompletionItemExtensions.ExtensionMethodImportCompletionProvider)
            {
                changeSpan = typedSpan;
                insertText = completion.DisplayText;
                wasUnimported = true;
                sortText = '1' + completion.SortText;
                filterText = null;
            }
            else
            {
                var change = await completionService.GetChangeAsync(document, completion);

                // There must be at least one change that affects the current location, or something is seriously wrong
                Debug.Assert(change.TextChanges.Any(change => change.Span.IntersectsWith(position)));

                foreach (var textChange in change.TextChanges)
                {
                    if (!textChange.Span.IntersectsWith(position))
                    {
                        additionalTextEdits ??= new();
                        additionalTextEdits.Add(getChangeForTextAndSpan(textChange.NewText!, textChange.Span, sourceText));
                    }
                    else
                    {
                        changeSpan = textChange.Span;
                        insertText = textChange.NewText!;

                        // If we're expecting there to be unimported types, put in an explicit sort text to put things already in scope first.
                        // Otherwise, omit the sort text if it's the same as the label to save on space.
                        sortText = expectingImportedItems
                            ? '0' + completion.SortText
                            : labelText == completion.SortText ? null : completion.SortText;

                        // If the completion is replacing a bigger range than the previously-typed word, we need to have the filter
                        // text compensate. Clients will use the range of the text edit to determine the thing that is being filtered
                        // against. For example, override completion:
                        //
                        //    override $$
                        //    |--------| Replacing Range
                        //
                        // That means vscode will consider "override <additional user input>" when looking to see whether the item
                        // still matches. To compensate, we add the start of the replacing range, up to the start of the current word,
                        // to ensure the item isn't silently filtered out.

                        if (changeSpan != typedSpan)
                        {
                            Debug.Assert(string.IsNullOrEmpty(typedText) || completion.FilterText.StartsWith(typedText));

                            var prefix = sourceText.GetSubText(TextSpan.FromBounds(changeSpan.Start, typedSpan.Start)).ToString();
                            filterText = prefix + completion.FilterText;
                        }
                        else
                        {
                            filterText = labelText == completion.FilterText ? null : completion.FilterText;
                        }
                    }
                }
            }

            Debug.Assert(insertText != null);
            var commitCharacters = BuildCommitCharacters(completions, completion.Rules.CommitCharacterRules);

            return (new CompletionItem
            {
                Label = labelText,
                TextEdit = getChangeForTextAndSpan(insertText!, changeSpan, sourceText),
                InsertTextFormat = insertTextFormat,
                AdditionalTextEdits = additionalTextEdits,
                SortText = sortText,
                FilterText = filterText,
                Kind = GetCompletionItemKind(completion.Tags),
                Detail = completion.InlineDescription,
                Data = index,
                Preselect = completion.Rules.MatchPriority == MatchPriority.Preselect || filteredItems.Contains(completion.DisplayText),
                CommitCharacters = commitCharacters,
            }, wasUnimported);

            static LinePositionSpanTextChange getChangeForTextAndSpan(string insertText, TextSpan changeSpan, SourceText sourceText)
            {
                var changeLinePositionSpan = sourceText.Lines.GetLinePositionSpan(changeSpan);
                return new()
                {
                    NewText = insertText,
                    StartLine = changeLinePositionSpan.Start.Line,
                    StartColumn = changeLinePositionSpan.Start.Character,
                    EndLine = changeLinePositionSpan.End.Line,
                    EndColumn = changeLinePositionSpan.End.Character
                };
            }
        }

        internal static bool HasAsyncInsertStep(this CSharpCompletionItem item)
        {
            var provider = item.GetProviderName();
            return provider != CompletionItemExtensions.ExtensionMethodImportCompletionProvider
                   && provider != CompletionItemExtensions.TypeImportCompletionProvider
                   && !s_forceEagerResolveProviders.Contains(provider);
        }

        private static IReadOnlyList<char> BuildCommitCharacters(CSharpCompletionList completions, ImmutableArray<CharacterSetModificationRule> characterRules)
        {
            var triggerCharacters = new List<char>(completions.Rules.DefaultCommitCharacters.Length);
            triggerCharacters.AddRange(completions.Rules.DefaultCommitCharacters);

            foreach (var modifiedRule in characterRules)
            {
                switch (modifiedRule.Kind)
                {
                    case CharacterSetModificationKind.Add:
                        triggerCharacters.AddRange(modifiedRule.Characters);
                        break;

                    case CharacterSetModificationKind.Remove:
                        for (int i = 0; i < triggerCharacters.Count; i++)
                        {
                            if (modifiedRule.Characters.Contains(triggerCharacters[i]))
                            {
                                triggerCharacters.RemoveAt(i);
                                i--;
                            }
                        }

                        break;

                    case CharacterSetModificationKind.Replace:
                        triggerCharacters.Clear();
                        triggerCharacters.AddRange(modifiedRule.Characters);
                        break;
                }
            }

            // VS has a more complex concept of a commit mode vs suggestion mode for intellisense.
            // LSP doesn't have this, so mock it as best we can by removing space ` ` from the list
            // of commit characters if we're in suggestion mode.
            if (completions.SuggestionModeItem is object)
            {
                triggerCharacters.Remove(' ');
            }

            return triggerCharacters;
        }

        internal static (IReadOnlyList<LinePositionSpanTextChange>? edits, int endOffset) GetAdditionalTextEdits(
            CompletionChange change,
            SourceText sourceText,
            CSharpParseOptions parseOptions,
            TextSpan typedSpan,
            string completionDisplayText,
            string providerName,
            ILogger logger)
        {
            // We know the span starts before the text we're keying off of. So, break that
            // out into a separate edit. We need to cut out the space before the current word,
            // as the additional edit is not allowed to overlap with the insertion point.
            var additionalEditStartPosition = sourceText.Lines.GetLinePosition(change.TextChange.Span.Start);
            var additionalEditEndPosition = sourceText.Lines.GetLinePosition(typedSpan.Start - 1);
            int additionalEditEndOffset = getAdditionalTextEditEndOffset(change, sourceText, parseOptions, completionDisplayText, providerName);
            if (additionalEditEndOffset < 1)
            {
                // The first index of this was either 0 and the edit span was wrong,
                // or it wasn't found at all. In this case, just do the best we can:
                // send the whole string wtih no additional edits and log a warning.
                logger.LogWarning("Could not find the first index of the display text.\nDisplay text: {0}.\nCompletion Text: {1}",
                    completionDisplayText, change.TextChange.NewText);
                return (null, -1);
            }

            return (ImmutableArray.Create(new LinePositionSpanTextChange
            {
                // Again, we cut off the space at the end of the offset
                NewText = change.TextChange.NewText!.Substring(0, additionalEditEndOffset - 1),
                StartLine = additionalEditStartPosition.Line,
                StartColumn = additionalEditStartPosition.Character,
                EndLine = additionalEditEndPosition.Line,
                EndColumn = additionalEditEndPosition.Character,
            }), additionalEditEndOffset);

            static int getAdditionalTextEditEndOffset(CompletionChange change, SourceText sourceText, CSharpParseOptions parseOptions, string completionDisplayText, string providerName)
            {
                if (providerName == CompletionItemExtensions.ExtensionMethodImportCompletionProvider ||
                    providerName == CompletionItemExtensions.TypeImportCompletionProvider)
                {
                    return change.TextChange.NewText!.LastIndexOf(completionDisplayText);
                }

                // The DisplayText wasn't in the final string. This can happen in a few cases:
                //  * The override or partial method completion is involving types that need
                //    to have a using added in the final version, and won't be fully qualified
                //    as they were in the DisplayText
                //  * Nullable context differences, such as if the thing you're overriding is
                //    annotated but the final context being generated into does not have
                //    annotations enabled.
                // For these cases, we currently should only be seeing override or partial
                // completions, as import completions don't have nullable annotations or
                // fully-qualified types in their DisplayTexts. If that ever changes, we'll have
                // to adjust the API here.
                //
                // In order to find the correct location here, we parse the change. The location
                // of the method or property that contains the new cursor position is the location
                // of the new changes
                Debug.Assert(providerName == CompletionItemExtensions.OverrideCompletionProvider ||
                             providerName == CompletionItemExtensions.PartialMethodCompletionProvider);
                Debug.Assert(change.NewPosition.HasValue);

                var parsedTree = CSharpSyntaxTree.ParseText(sourceText.WithChanges(change.TextChange).ToString(), parseOptions);

                var tokenOfNewPosition = parsedTree.GetRoot().FindToken(change.NewPosition!.Value);
                var finalNode = tokenOfNewPosition.Parent;
                while (finalNode != null)
                {
                    switch (finalNode)
                    {
                        case MethodDeclarationSyntax decl:
                            return decl.Identifier.SpanStart - change.TextChange.Span.Start;
                        case PropertyDeclarationSyntax prop:
                            return prop.Identifier.SpanStart - change.TextChange.Span.Start;
                    }

                    finalNode = finalNode.Parent;
                }

                return -1;
            }
        }

        private static CompletionItemKind GetCompletionItemKind(ImmutableArray<string> tags)
        {
            foreach (var tag in tags)
            {
                if (s_roslynTagToCompletionItemKind.TryGetValue(tag, out var itemKind))
                {
                    return itemKind;
                }
            }

            return CompletionItemKind.Text;
        }
    }
}
