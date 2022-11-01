﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using stdole;

namespace HtmlForFSharp
{
    #region Classifier

    internal class HtmlClassifier : IClassifier
    {
        private readonly IClassificationType _htmlDelimiterType;
        private readonly IClassificationType _htmlElementType;
        private readonly IClassificationType _htmlAttributeNameType;
        private readonly IClassificationType _htmlQuoteType;
        private readonly IClassificationType _htmlAttributeValueType;
        private readonly IClassificationType _htmlTextType;
        private readonly IClassificationType _htmlLitAttributeNameType;
        private readonly IClassificationType _htmlLitAttributeValueType;
        private readonly IClassificationType _htmlComment;
        private readonly IClassifier _classifier;
        private readonly ITextBuffer _textBuffer;

        internal HtmlClassifier(IClassificationTypeRegistryService registry, IClassifier classifier, ITextBuffer textBuffer)
        {
            _htmlDelimiterType = registry.GetClassificationType(FormatNames.Delimiter);
            _htmlElementType = registry.GetClassificationType(FormatNames.Element);
            _htmlAttributeNameType = registry.GetClassificationType(FormatNames.AttributeName);
            _htmlQuoteType = registry.GetClassificationType(FormatNames.Quote);
            _htmlAttributeValueType = registry.GetClassificationType(FormatNames.AttributeValue);
            _htmlTextType = registry.GetClassificationType(FormatNames.Text);
            _htmlLitAttributeNameType = registry.GetClassificationType(FormatNames.LitAttributeName);
            _htmlLitAttributeValueType = registry.GetClassificationType(FormatNames.LitAttributeValue);
            _htmlComment = registry.GetClassificationType(FormatNames.Comment);

            _classifier = classifier;
            _textBuffer = textBuffer;
        }

        private static bool IsHtmlIdentifier(ClassificationSpan x) =>
                x.ClassificationType.Classification.ToLower() == "identifier"
                    && x.Span.GetText() == "html";

        private static Regex htmlTemplateBeginRegex = new Regex(@"((html((\n|\r|\r\n| )*)([$@]*)(""){3})|(html(\n|\r|\r\n| )+[$@""]+))", RegexOptions.Compiled);
        private static List<SnapshotSpan> GetHtmlTemplateSpans(ITextSnapshot snapshot)
        {
            var text = snapshot.GetText();
            return 
                htmlTemplateBeginRegex.Matches(text)
                .Cast<Match>()
                .Select(x=>
                {
                    try
                    {
                        var innerStringStart = x.Index + x.Length;
                        var innerStringEnd =
                            x.Value.Contains("\"\"\"")
                            ? text.Substring(innerStringStart).IndexOf("\"\"\"")
                            // end of line
                            : text.Substring(innerStringStart).IndexOf("\r\n");
                        var toTrimText = 
                            text.Substring(innerStringStart, innerStringEnd);
                        var removeCharsCount =
                            toTrimText.Trim().EndsWith("\"")
                            ? toTrimText.Length - toTrimText.LastIndexOf('"')
                            : 0;
                        innerStringEnd = innerStringEnd - removeCharsCount;
                        innerStringEnd = innerStringEnd < 0 ? text.Length - 1 : innerStringEnd;
                        return new SnapshotSpan(snapshot, innerStringStart, innerStringEnd);
                    }
                    catch (Exception)
                    {
                        return default;
                    }
                })
                .Where(x=>x!=default)
                .ToList();
        }



        public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
        {



            // Reset HTML State
            var htmlTemplateSpans = GetHtmlTemplateSpans(span.Snapshot);

            var stringSpans =
                _classifier.GetClassificationSpans(span)
                .Where(cs => cs.ClassificationType.Classification.ToLower() == "string")
                .Select(cs => cs.Span)
                .ToList();

            var effectedHtmlSpans =
                htmlTemplateSpans.
                    Where(hs =>
                        stringSpans.Any(sspan => hs.OverlapsWith(sspan))
                    )
                    .ToList();

            //var isInsideHtmlTemplate = htmlTemplateSpans.Any(x => x.OverlapsWith(span));
            //var htmlTemplatesAreCurrent = htmlTemplateSpans.Where(x => x.OverlapsWith(span));

            var classifications =
                    effectedHtmlSpans
                        .SelectMany(htmlSpans =>
                            Parser.HtmlParser.runHtmlParser(htmlSpans,
                                _htmlAttributeValueType,
                                _htmlLitAttributeValueType,
                                _htmlComment,
                                htmlSpans.GetText()
                    ))
                    .ToList();

            return classifications;
            
        }

        private enum State
        {
            Default,
            AfterOpenAngleBracket,
            ElementName,
            InsideAttributeList,
            AttributeName,
            AfterAttributeName,
            AfterAttributeEqualSign,
            AfterOpenDoubleQuote,
            AfterOpenSingleQuote,
            AttributeValue,
            InsideElement,
            AfterCloseAngleBracket,
            AfterOpenTagSlash,
            AfterCloseTagSlash,
            LitAttributeName,
            LitAttributeValue,
            Comment
        }

        private bool IsNameChar(char c)
        {
            return c =='-' || c == '_' || char.IsLetterOrDigit(c);
        }

        //
        private State state { get; set; } = State.Default;
        private StringType currentStringType = StringType.Unknown;


        private enum StringType
        {
            Unknown,
            SimpleOrMultiline,
            InterpolatedSimple,
            InterpolatedMultiLine
        } 
        private List<ClassificationSpan> ScanLiteral(ClassificationSpan cs)
        {
            var span = cs.Span;
            var result = new List<ClassificationSpan>();

            var literal = span.GetText();

            
            // Classify StringType
            // and set state. On a "second part of an interpolation
            // remember last state!
            (currentStringType,state) =
                literal.StartsWith("$\"\"\"")
                ? (StringType.InterpolatedMultiLine, State.Default)
                : literal.StartsWith("$\"")
                    ? (StringType.InterpolatedSimple, State.Default)
                    : currentStringType != StringType.InterpolatedMultiLine && (literal.StartsWith("\"\"\"") || literal.StartsWith("@\"") || literal.StartsWith("\""))
                        ? (StringType.SimpleOrMultiline, State.Default)
                        // next part of interpolated string
                        : literal.StartsWith("}")
                            ? (currentStringType,state)
                            // in case of a next line in a multiline string, continue with the last state
                            : currentStringType == StringType.InterpolatedMultiLine && !literal.StartsWith("\"\"\"")
                                ? (currentStringType, state)
                                : (StringType.Unknown, State.Default);


            var currentCharIndex = 0;

            int? continuousMark = null;
            var insideSingleQuote = false;
            var insideDoubleQuote = false;

            while (currentCharIndex < literal.Length)
            {
                var c = literal[currentCharIndex];
                var prevChar = currentCharIndex > 0 ? literal[currentCharIndex - 1] : '\0';
                char pChar(int idx) => currentCharIndex > idx - 1 ? literal[currentCharIndex - idx] : '\0';

                //check is quote of start and end
                if ((currentCharIndex == 0 || currentCharIndex == (literal.Length-1))  && IsQuote(c) && state != State.AttributeValue)
                {
                    currentCharIndex++;
                    continue;
                }

                if ((c=='{' || c=='}') 
                    && state != State.LitAttributeValue 
                    && state != State.AfterAttributeEqualSign
                    )
                {
                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), cs.ClassificationType));
                    currentCharIndex++;
                    continue;
                }

                
                switch (state)
                {
                    case State.Default:
                        {
                            if (c == '<')
                            {
                                state = State.AfterOpenAngleBracket;
                                continuousMark = null;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            break;
                        }
                    case State.AfterOpenAngleBracket:
                        {
                            if (IsNameChar(c))
                            {
                                continuousMark = currentCharIndex;
                                state = State.ElementName;

                                

                            }
                            else if (c == '/')
                            {
                                state = State.AfterCloseTagSlash;
                                continuousMark = null;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            // Possible Comment
                            else if (c == '!')
                            {
                                
                                state = State.Comment;
                                continuousMark = currentCharIndex;
                                var prevRes = result.Where(x=>x.Span.GetText()=="<").LastOrDefault();
                                if (prevRes != null)
                                    result.Remove(prevRes);
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex - 1, 2), _htmlComment));
                                
                            }
                            else
                            {
                                return null;
                            }
                            break;
                        }
                    case State.Comment:
                        {
                            // End Comment
                            if (c == '>')
                            {
                                

                                
                                if (pChar(1) == '-' && pChar(2) == '-')
                                {
                                    if (continuousMark.HasValue)
                                    {
                                        var attrNameStart = continuousMark.Value;
                                        var attrNameLength = currentCharIndex - attrNameStart;
                                        result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + attrNameStart, attrNameLength), _htmlComment));
                                    }

                                    state = State.AfterCloseAngleBracket;
                                    continuousMark = null;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex - 3, 4), _htmlComment));
                                }
                            }
                            else
                            {
                                // In case we have a comment on the next line an the literal is new
                                if (currentCharIndex == 0)
                                    continuousMark = currentCharIndex;
                            }
                            break;
                        }
                    case State.ElementName:
                        {
                            if (IsNameChar(c))
                            {

                            }
                            else if (char.IsWhiteSpace(c))
                            {
                                if (continuousMark.HasValue)
                                {
                                    var length = currentCharIndex - continuousMark.Value;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + continuousMark.Value, length), _htmlElementType));
                                    continuousMark = null;
                                }
                                state = State.InsideAttributeList;
                            }
                            else if (c == '>')
                            {
                                if (continuousMark.HasValue)
                                {
                                    var length = currentCharIndex - continuousMark.Value;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + continuousMark.Value, length), _htmlElementType));
                                    continuousMark = null;
                                }

                                state = State.AfterCloseAngleBracket;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            else if (c == '/')
                            {
                                if (continuousMark.HasValue)
                                {
                                    var length = currentCharIndex - continuousMark.Value;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + continuousMark.Value, length), _htmlElementType));
                                    continuousMark = null;
                                }

                                state = State.AfterOpenTagSlash;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            else
                            {
                                return null;
                            }
                            break;
                        }
                    case State.InsideAttributeList:
                        {
                            if (char.IsWhiteSpace(c) || c == '\r' || c == '\n')
                            {

                            }
                            else if (IsNameChar(c))
                            {
                                continuousMark = currentCharIndex;
                                state = State.AttributeName;
                            }
                            else if (c =='.' || c == '@' || c == '?')
                            {
                                state = State.LitAttributeName;
                                continuousMark = currentCharIndex;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlLitAttributeNameType));
                            }
                            else if (c == '>')
                            {
                                state = State.AfterCloseAngleBracket;
                                continuousMark = null;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            else if (c == '/')
                            {
                                state = State.AfterOpenTagSlash;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            else
                            {
                                return null;
                            }
                            break;
                        }
                    case State.AttributeName:
                        {
                            if (char.IsWhiteSpace(c))
                            {
                                if (continuousMark.HasValue)
                                {
                                    var length = currentCharIndex - continuousMark.Value;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + continuousMark.Value, length), _htmlAttributeNameType));
                                    continuousMark = null;
                                }
                                state = State.AfterAttributeName;
                            }
                            else if (IsNameChar(c))
                            {

                            }
                            else if (c == '=')
                            {
                                if (continuousMark.HasValue)
                                {
                                    var attrNameStart = continuousMark.Value;
                                    var attrNameLength = currentCharIndex - attrNameStart;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + attrNameStart, attrNameLength), _htmlAttributeNameType));
                                }

                                state = State.AfterAttributeEqualSign;
                                continuousMark = null;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            else if (c == '>')
                            {
                                if (continuousMark.HasValue)
                                {
                                    var attrNameStart = continuousMark.Value;
                                    var attrNameLength = currentCharIndex - attrNameStart;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + attrNameStart, attrNameLength), _htmlAttributeNameType));
                                }

                                state = State.AfterCloseAngleBracket;
                                continuousMark = null;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            else if (c == '/')
                            {
                                if (continuousMark.HasValue)
                                {
                                    var attrNameStart = continuousMark.Value;
                                    var attrNameLength = currentCharIndex - attrNameStart;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + attrNameStart, attrNameLength), _htmlAttributeNameType));
                                }

                                state = State.AfterOpenTagSlash;
                                continuousMark = null;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            else
                            {
                                return null;
                            }
                            break;
                        }
                    case State.LitAttributeName:
                        {
                            if (char.IsWhiteSpace(c))
                            {
                                if (continuousMark.HasValue)
                                {
                                    var length = currentCharIndex - continuousMark.Value;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + continuousMark.Value, length), _htmlLitAttributeNameType));
                                    continuousMark = null;
                                }
                                state = State.LitAttributeName;
                            }
                            else if (IsNameChar(c))
                            {
                                state = State.LitAttributeName;
                            }
                            else if (c == '=')
                            {
                                if (continuousMark.HasValue)
                                {
                                    var attrNameStart = continuousMark.Value;
                                    var attrNameLength = currentCharIndex - attrNameStart;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + attrNameStart, attrNameLength), _htmlLitAttributeNameType));
                                }
                                continuousMark = null;
                                state = State.LitAttributeValue;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlLitAttributeNameType));
                            }
                            else
                            {
                                return null;
                            }
                            break;
                        }
                    case State.LitAttributeValue:
                        {
                            if (char.IsWhiteSpace(c))
                            {
                                currentCharIndex++;
                                continue;
                            }
                            // in case of a string inside the interpolation
                            else if (IsNameChar(c) || c=='\"')
                            {
                                currentCharIndex++;
                                continue;
                            }
                            else if (c == '{')
                            {
                                if (continuousMark.HasValue)
                                {
                                    var attrNameStart = continuousMark.Value;
                                    var attrNameLength = currentCharIndex - attrNameStart;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + attrNameStart, attrNameLength), _htmlLitAttributeValueType));
                                }
                                state = State.LitAttributeValue;
                                continuousMark = currentCharIndex;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlLitAttributeValueType));
                            }
                            else if (c == '}')
                            {
                                if (continuousMark.HasValue)
                                {
                                    var attrNameStart = continuousMark.Value;
                                    var attrNameLength = currentCharIndex - attrNameStart;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + attrNameStart, attrNameLength), _htmlLitAttributeValueType));
                                }
                                state = State.InsideAttributeList;
                                continuousMark = null;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlLitAttributeValueType));
                            }
                            else
                            {
                                return null;
                            }
                            break;
                        }
                    case State.AfterAttributeName:
                        {
                            if (char.IsWhiteSpace(c))
                            {

                            }
                            else if (IsNameChar(c))
                            {
                                continuousMark = currentCharIndex;
                                state = State.AttributeName;
                            }
                            else if (c == '=')
                            {
                                state = State.AfterAttributeEqualSign;
                                continuousMark = null;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            else if (c == '.' || c == '@' || c == '?')
                            {
                                state = State.LitAttributeName;
                                continuousMark = currentCharIndex;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlLitAttributeNameType));
                            }
                            else if (c == '/')
                            {
                                state = State.AfterOpenTagSlash;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            else if (c == '>')
                            {
                                state = State.AfterCloseAngleBracket;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            else
                            {
                                return null;
                            }
                            break;
                        }
                    case State.AfterAttributeEqualSign:
                        {
                            if (char.IsWhiteSpace(c))
                            {

                            }
                            else if (IsNameChar(c))
                            {
                                continuousMark = currentCharIndex;
                                state = State.AttributeValue;
                            }
                            else if (c=='{')
                            {
                                continuousMark = currentCharIndex;
                                state = State.LitAttributeValue;
                            }
                            else if (c == '\"' && !literal.StartsWith("@"))
                            {
                                state = State.AfterOpenDoubleQuote;
                                insideDoubleQuote = true;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));
                            }
                            else if (c == '\\' && literal.StartsWith("\""))
                            {
                                state = State.AfterAttributeEqualSign;
                                insideDoubleQuote = true;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));
                            }
                            else if (c == '\"' && literal.StartsWith("\"") && prevChar == '\\')
                            {
                                state = State.AfterOpenDoubleQuote;
                                insideDoubleQuote = true;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));
                            }
                            else if (c == '\"' && literal.StartsWith("@") && prevChar != '\"')
                            {
                                state = State.AfterAttributeEqualSign;
                                insideDoubleQuote = true;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));
                            }
                            else if (c == '\"' && literal.StartsWith("@") && prevChar == '\"')
                            {
                                state = State.AfterOpenDoubleQuote;
                                insideDoubleQuote = true;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));
                            }
                            else if (c == '\"' && literal.StartsWith("\"") && prevChar == '\\')
                            {
                                state = State.AfterOpenDoubleQuote;
                                insideDoubleQuote = true;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));
                            }
                            else if (c == '\'')
                            {
                                state = State.AfterOpenSingleQuote;
                                insideSingleQuote = true;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));
                            }
                            else
                            {
                                return null;
                            }
                            break;
                        }
                    case State.AfterOpenDoubleQuote:
                        {
                            if (c == '\"' && literal.StartsWith("@") && prevChar != '\"')
                            {
                                state = State.AfterOpenDoubleQuote;
                                insideDoubleQuote = true;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));
                            }
                            if (c == '\"' && literal.StartsWith("@") && prevChar == '\"')
                            {
                                state = State.InsideAttributeList;
                                insideDoubleQuote = false;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));
                            }
                            if (c == '\"')
                            {
                                state = State.InsideAttributeList;
                                insideDoubleQuote = false;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));
                            }
                            else
                            {
                                continuousMark = currentCharIndex;
                                state = State.AttributeValue;
                            }
                            break;
                        }
                    case State.AfterOpenSingleQuote:
                        {
                            if (c == '\'')
                            {
                                state = State.InsideAttributeList;
                                insideSingleQuote = false;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));
                            }
                            else
                            {
                                continuousMark = currentCharIndex;
                                state = State.AttributeValue;
                            }
                            break;
                        }
                    case State.AttributeValue:
                        {
                            if (c == '\'')
                            {
                                if (insideSingleQuote)
                                {
                                    state = State.InsideAttributeList;
                                    insideSingleQuote = false;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));

                                    if (continuousMark.HasValue)
                                    {
                                        var start = continuousMark.Value;
                                        var length = currentCharIndex - start;
                                        continuousMark = null;

                                        result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + start, length), _htmlAttributeValueType));
                                    }
                                }
                            }
                            if (c == '\"' && literal.StartsWith("@") && prevChar != '\"')
                            {
                                state = State.AttributeValue;
                                insideDoubleQuote = true;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));
                            }
                            else if (c == '\"' && literal.Trim().StartsWith("@") && prevChar == '\"')
                            {
                                state = State.InsideAttributeList;
                                insideDoubleQuote = false;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));

                                if (continuousMark.HasValue)
                                {
                                    var start = continuousMark.Value;
                                    var length = currentCharIndex - start;
                                    continuousMark = null;

                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + start, length), _htmlAttributeValueType));
                                }
                            }
                            else if (c == '\"')
                            {
                                if (insideDoubleQuote)
                                {
                                    state = State.InsideAttributeList;
                                    insideDoubleQuote = false;
                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlQuoteType));

                                    if (continuousMark.HasValue)
                                    {
                                        var start = continuousMark.Value;
                                        var length = currentCharIndex - start;
                                        continuousMark = null;

                                        result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + start, length), _htmlAttributeValueType));
                                    }
                                }
                            }
                            else
                            {

                            }

                            break;
                        }


                    


                    case State.AfterCloseAngleBracket:
                        {
                            if (c == '<')
                            {
                                state = State.AfterOpenAngleBracket;
                                continuousMark = null;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            else
                            {
                                continuousMark = currentCharIndex;
                                state = State.InsideElement;
                            }
                            break;
                        }
                    case State.InsideElement:
                        {
                            if (c == '<')
                            {
                                state = State.AfterOpenAngleBracket;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));

                                if (continuousMark.HasValue)
                                {
                                    var start = continuousMark.Value;
                                    var length = currentCharIndex - start;
                                    continuousMark = null;

                                    result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + start, length), cs.ClassificationType));
                                }
                            }
                            else
                            {

                            }

                            break;
                        }
                    case State.AfterCloseTagSlash:
                        {
                            if (char.IsWhiteSpace(c))
                            {

                            }
                            else if (IsNameChar(c))
                            {
                                continuousMark = currentCharIndex;
                                state = State.ElementName;
                            }
                            else
                            {
                                return null;
                            }
                            break;
                        }
                    case State.AfterOpenTagSlash:
                        {
                            if (c == '>')
                            {
                                state = State.AfterCloseAngleBracket;
                                continuousMark = null;
                                result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + currentCharIndex, 1), _htmlDelimiterType));
                            }
                            else
                            {
                                return null;
                            }
                            break;
                        }
                    default:
                        break;
                }

                ++currentCharIndex;
            }

            // if the continuous span is stopped because of end of literal,
            // the span was not colored, handle it here
            if (currentCharIndex >= literal.Length)
            {
                if (continuousMark.HasValue)
                {
                    if (state == State.ElementName)
                    {
                        var start = continuousMark.Value;
                        var length = literal.Length - start;
                        result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + start, length), _htmlElementType));
                    }
                    else if (state == State.AttributeName)
                    {
                        var attrNameStart = continuousMark.Value;
                        var attrNameLength = literal.Length - attrNameStart;
                        result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + attrNameStart, attrNameLength), _htmlAttributeNameType));
                    }
                    else if (state == State.AttributeValue)
                    {
                        var start = continuousMark.Value;
                        var length = literal.Length - start;
                        result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + start, length), _htmlAttributeValueType));
                    }
                    else if (state == State.LitAttributeName)
                    {
                        var start = continuousMark.Value;
                        var length = literal.Length - start;
                        result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + start, length), _htmlLitAttributeNameType));
                    }
                    else if (state == State.LitAttributeValue)
                    {
                        var start = continuousMark.Value;
                        var length = literal.Length - start;
                        result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + start, length), _htmlLitAttributeNameType));
                    }
                    else if (state == State.InsideElement)
                    {
                        var start = continuousMark.Value;
                        var length = literal.Length - start;
                        result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + start, length), cs.ClassificationType));
                    }
                    else if (state == State.Comment)
                    {
                        var start = continuousMark.Value;
                        var length = literal.Length - start;
                        result.Add(new ClassificationSpan(new SnapshotSpan(span.Start + start, length), _htmlComment));
                    }
                }
            }


            // Reset State on end of strings
            (currentStringType, state) =
                literal.Trim().EndsWith("\"\"\"")
                ? (StringType.Unknown, State.Default)
                : currentStringType != StringType.InterpolatedMultiLine && literal.Trim().EndsWith("\"")
                    ? (StringType.Unknown, State.Default)
                    : (currentStringType, state);

            return result;
        }
        public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

        private bool IsQuote(char c)
        {
            return c == '\'' || c == '"' || c == '`';
        }
    }
    #endregion //Classifier
}
