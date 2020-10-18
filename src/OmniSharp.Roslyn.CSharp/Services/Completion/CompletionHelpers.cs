#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
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
        private static readonly Dictionary<string, CompletionItemKind> s_roslynTagToCompletionItemKind = new Dictionary<string, CompletionItemKind>()
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
            bool useAsyncCompletion,
            ILogger logger)
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
                        position,
                        expectingImportedItems,
                        typedSpan,
                        typedText,
                        replacingSpanStartPosition,
                        replacingSpanEndPosition,
                        filteredItems,
                        logger);

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
            int position,
            bool expectingImportedItems,
            TextSpan typedSpan,
            string typedText,
            LinePosition replacingSpanStartPosition,
            LinePosition replacingSpanEndPosition,
            ImmutableArray<string> filteredItems,
            ILogger logger)
        {
            bool wasUnimported = false;
            var insertTextFormat = InsertTextFormat.PlainText;
            IReadOnlyList<LinePositionSpanTextChange>? additionalTextEdits = null;
            char sortTextPrepend = '0';
            var syntax = await document.GetSyntaxTreeAsync();

            if (!completion.TryGetInsertionText(out string insertText))
            {
                string providerName = completion.GetProviderName();
                switch (providerName)
                {
                    case CompletionItemExtensions.EmeddedLanguageCompletionProvider:
                        // The Regex completion provider can change escapes based on whether
                        // we're in a verbatim string or not
                        {
                            CompletionChange change = await completionService.GetChangeAsync(document, completion);
                            Debug.Assert(typedSpan == change.TextChange.Span);
                            insertText = change.TextChange.NewText!;
                        }
                        break;

                    case CompletionItemExtensions.InternalsVisibleToCompletionProvider:
                        // The IVT completer doesn't add extra things before the completion
                        // span, only assembly keys at the end if they exist.
                        {
                            CompletionChange change = await completionService.GetChangeAsync(document, completion);
                            Debug.Assert(typedSpan == change.TextChange.Span);
                            insertText = change.TextChange.NewText!;
                        }
                        break;

                    case CompletionItemExtensions.XmlDocCommentCompletionProvider:
                        {
                            // The doc comment completion might compensate for the < before
                            // the current word, if one exists. For these cases, if the token
                            // before the current location is a < and the text it's replacing starts
                            // with a <, erase the < from the given insertion text.
                            var change = await completionService.GetChangeAsync(document, completion);

                            bool trimFront = change.TextChange.NewText![0] == '<'
                                             && sourceText[change.TextChange.Span.Start] == '<';

                            Debug.Assert(!trimFront || change.TextChange.Span.Start + 1 == typedSpan.Start);

                            (insertText, insertTextFormat) = getAdjustedInsertTextWithPosition(change, position, newOffset: trimFront ? 1 : 0);
                        }
                        break;

                    case CompletionItemExtensions.OverrideCompletionProvider:
                    case CompletionItemExtensions.PartialMethodCompletionProvider:
                        {
                            // For these two, we potentially need to use additionalTextEdits. It's possible
                            // that override (or C# expanded partials) will cause the word or words before
                            // the cursor to be adjusted. For example:
                            //
                            // public class C {
                            //     override $0
                            // }
                            //
                            // Invoking completion and selecting, say Equals, wants to cause the line to be
                            // rewritten as this:
                            //
                            // public class C {
                            //     public override bool Equals(object other)
                            //     {
                            //         return base.Equals(other);$0
                            //     }
                            // }
                            //
                            // In order to handle this, we need to chop off the section of the completion
                            // before the cursor and bundle that into an additionalTextEdit. Then, we adjust
                            // the remaining bit of the change to put the cursor in the expected spot via
                            // snippets. We could leave the additionalTextEdit bit for resolve, but we already
                            // have the data do the change and we basically have to compute the whole thing now
                            // anyway, so it doesn't really save us anything.

                            var change = await completionService.GetChangeAsync(document, completion);

                            if (typedSpan == change.TextChange.Span)
                            {
                                // If the span we're using to key the completion off is the same as the replacement
                                // span, then we don't need to do anything special, just snippitize the text and
                                // exit
                                (insertText, insertTextFormat) = getAdjustedInsertTextWithPosition(change, position, newOffset: 0);
                                break;
                            }

                            if (change.TextChange.Span.Start > typedSpan.Start)
                            {
                                // If the span we're using to key the replacement span is within the original typed span
                                // span, we want to prepend the missing text from the original typed text to here. The
                                // reason is that some lsp clients, such as vscode, use the range from the text edit as
                                // the selector for what filter text to use. This can lead to odd scenarios where invoking
                                // completion and typing `EQ` will bring up the Equals override, but then dismissing and
                                // reinvoking completion will have a range that just replaces the Q. Vscode will then consider
                                // that capital Q to be the start of the filter text, and filter out the Equals overload
                                // leaving the user with no completion and no explanation
                                Debug.Assert(change.TextChange.Span.End == typedSpan.End);
                                var prefix = typedText.Substring(0, change.TextChange.Span.Start - typedSpan.Start);

                                (insertText, insertTextFormat) = getAdjustedInsertTextWithPosition(change, position, newOffset: 0, prefix);
                                break;
                            }

                            int additionalEditEndOffset;
                            (additionalTextEdits, additionalEditEndOffset) = GetAdditionalTextEdits(change, sourceText, (CSharpParseOptions)syntax!.Options, typedSpan, completion.DisplayText, providerName, logger);

                            if (additionalEditEndOffset < 0)
                            {
                                // We couldn't find the position of the change in the new text. This shouldn't happen in normal cases,
                                // but handle as best we can in release
                                Debug.Fail("Couldn't find the new cursor position in the replacement text!");
                                additionalTextEdits = null;
                                insertText = completion.DisplayText;
                                break;
                            }

                            // Now that we have the additional edit, adjust the rest of the new text
                            (insertText, insertTextFormat) = getAdjustedInsertTextWithPosition(change, position, additionalEditEndOffset);
                        }
                        break;

                    case CompletionItemExtensions.TypeImportCompletionProvider:
                    case CompletionItemExtensions.ExtensionMethodImportCompletionProvider:
                        // We did indeed find unimported types, the completion list can be considered complete.
                        // This is technically slightly incorrect: extension method completion can provide
                        // partial results. However, this should only affect the first completion session or
                        // two and isn't a big problem in practice.
                        wasUnimported = true;
                        sortTextPrepend = '1';
                        goto default;

                    default:
                        insertText = completion.DisplayText;
                        break;
                }
            }

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
                InsertTextFormat = insertTextFormat,
                AdditionalTextEdits = additionalTextEdits,
                // Ensure that unimported items are sorted after things already imported.
                SortText = expectingImportedItems ? sortTextPrepend + completion.SortText : completion.SortText,
                FilterText = completion.FilterText,
                Kind = GetCompletionItemKind(completion.Tags),
                Detail = completion.InlineDescription,
                Data = index,
                Preselect = completion.Rules.MatchPriority == MatchPriority.Preselect || filteredItems.Contains(completion.DisplayText),
                CommitCharacters = commitCharacters,
            }, wasUnimported);

            static (string, InsertTextFormat) getAdjustedInsertTextWithPosition(
                CompletionChange change,
                int originalPosition,
                int newOffset,
                string? prependText = null)
            {
                // We often have to trim part of the given change off the front, but we
                // still want to turn the resulting change into a snippet and control
                // the cursor location in the insertion text. We therefore need to compensate
                // by cutting off the requested portion of the text, finding the adjusted
                // position in the requested string, and snippetizing it.

                // NewText is annotated as nullable, but this is a misannotation that will be fixed.
                string newText = change.TextChange.NewText!;

                // Easy-out, either Roslyn doesn't have an opinion on adjustment, or the adjustment is after the
                // end of the new text. Just return a substring from the requested offset to the end
                if (!(change.NewPosition is int newPosition)
                    || newPosition >= (change.TextChange.Span.Start + newText.Length))
                {
                    return (prependText + newText.Substring(newOffset), InsertTextFormat.PlainText);
                }

                // Roslyn wants to move the cursor somewhere inside the result. Substring from the
                // requested start to the new position, and from the new position to the end of the
                // string.
                int midpoint = newPosition - change.TextChange.Span.Start;
                var beforeText = LspSnippetHelpers.Escape(prependText + newText.Substring(newOffset, midpoint - newOffset));
                var afterText = LspSnippetHelpers.Escape(newText.Substring(midpoint));

                return (beforeText + "$0" + afterText, InsertTextFormat.Snippet);
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
