﻿#region License Header
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using PrettyPrompt.Consoles;
using PrettyPrompt.Highlighting;
using PrettyPrompt.Rendering;

namespace PrettyPrompt.Documents;

internal static class WordWrapping
{
    /// <summary>
    /// Wraps editable input, contained in the string builder, to the supplied width.
    /// The caret index (as the input is a 1 dimensional string of text) is converted
    /// to a 2 dimensional coordinate in the wrapped text.
    /// </summary>
    public static WordWrappedText WrapEditableCharacters(ReadOnlyStringBuilder input, int caret, int width)
    {
        Debug.Assert(caret >= 0 && caret <= input.Length);

        if (input.Length == 0)
        {
            return new WordWrappedText(
                new[] { WrappedLine.Empty(startIndex: 0) },
                new ConsoleCoordinate(0, caret));
        }

        var lines = new List<WrappedLine>();
        int currentLineLength = 0;
        var line = StringBuilderPool.Shared.Get(width);
        int textIndex = 0;
        int cursorColumn = 0;
        int cursorRow = 0;
        bool implicitNewLineUsed = false;
        foreach (var chunkMemory in input.GetChunks())
        {
            var chunk = chunkMemory.Span;
            for (var i = 0; i < chunk.Length; i++)
            {
                char character = chunk[i];
                Debug.Assert(character != '\t', "tabs should be replaced by spaces");
                
                bool isCursorPastCharacter = caret > textIndex;
                textIndex++;

                if (implicitNewLineUsed && character == '\n')
                {
                    //https://github.com/waf/PrettyPrompt/issues/256
                    //we want to skip this new-line character because line is already wrapped because of console buffer width
                    implicitNewLineUsed = false;
                    continue;
                }

                line.Append(character);

                int unicodeWidth = UnicodeWidth.GetWidth(character);
                if (unicodeWidth < 1)
                {
                    Debug.Fail("such character should not be present");
                    continue;
                }
                currentLineLength += unicodeWidth;

                if (isCursorPastCharacter && !char.IsControl(character))
                {
                    cursorColumn++;
                }

                implicitNewLineUsed = currentLineLength == width;
                if (character == '\n' || implicitNewLineUsed ||
                    NextCharacterIsFullWidthAndWillWrap(width, currentLineLength, chunk, i))
                {
                    if (isCursorPastCharacter)
                    {
                        cursorRow++;
                        cursorColumn = 0;
                    }
                    lines.Add(new WrappedLine(textIndex - line.Length, line.ToString(), currentLineLength));
                    line.Clear();
                    currentLineLength = 0;
                }
            }
        }

        if (currentLineLength > 0 || input[^1] == '\n')
        {
            lines.Add(new WrappedLine(textIndex - line.Length, line.ToString()));
        }

        Debug.Assert(textIndex == input.Length);
        if (cursorRow >= lines.Count)
        {
            Debug.Assert(cursorRow == lines.Count);
            lines.Add(WrappedLine.Empty(startIndex: textIndex));
        }

        StringBuilderPool.Shared.Put(line);
        return new WordWrappedText(lines, new ConsoleCoordinate(cursorRow, cursorColumn));

        static bool NextCharacterIsFullWidthAndWillWrap(int width, int currentLineLength, ReadOnlySpan<char> chunk, int i)
            => chunk.Length > i + 1 && UnicodeWidth.GetWidth(chunk[i + 1]) > 1 && currentLineLength + 1 == width;
    }

    /// <summary>
    /// Wrap words into lines of at most maxLength long. Split on spaces
    /// where possible, otherwise split by character if a single word is
    /// greater than maxLength.
    /// </summary>
    public static List<FormattedString> WrapWords(FormattedString input, int maxLength, int? maxLines = null)
    {
        Debug.Assert(maxLength >= 0);
        Debug.Assert(maxLines.GetValueOrDefault() >= 0);

        if (input.Length == 0 || maxLength == 0 || maxLines == 0)
        {
            return new List<FormattedString>();
        }

        var lines = new List<FormattedString>();
        var currentLine = new FormattedStringBuilder();
        foreach (var line in input.Split('\n'))
        {
            currentLine.Clear();

            int currentLineWidth = 0;
            foreach (var word in line.Split(' '))
            {
                if (word.Length <= maxLength)
                {
                    if (!ProcessWord(word))
                    {
                        return lines;
                    }
                }
                else
                {
                    //slow path
                    foreach (var currentWord in word.SplitIntoChunks(maxLength))
                    {
                        if (!ProcessWord(currentWord))
                        {
                            return lines;
                        }
                    }
                }

                bool ProcessWord(FormattedString currentWord)
                {
                    var wordLength = currentWord.GetUnicodeWidth();
                    var wordWithSpaceLength = currentLineWidth == 0 ? wordLength : wordLength + 1;

                    bool result = true;
                    if (currentLineWidth > maxLength ||
                        currentLineWidth + wordWithSpaceLength > maxLength)
                    {
                        result = AddLine(currentLine.ToFormattedString());
                        currentLine.Clear();
                        currentLineWidth = 0;
                    }

                    if (currentLineWidth == 0)
                    {
                        currentLine.Append(currentWord);
                        currentLineWidth += wordLength;
                    }
                    else
                    {
                        currentLine.Append(" ");
                        currentLine.Append(currentWord);
                        currentLineWidth += wordLength + 1;
                    }
                    return result;
                }
            }

            if (!AddLine(currentLine.ToFormattedString()))
            {
                return lines;
            }
        }

        bool AddLine(FormattedString line)
        {
            if (lines.Count == maxLines)
            {
                var lastLine = lines[^1];
                FormattedString lastLineModified;
                lines.RemoveAt(lines.Count - 1);
                if (maxLength > 3)
                {
                    Debug.Assert(lastLine.Length <= maxLength);
                    lastLine = lastLine.Substring(0, Math.Min(maxLength - 3, lastLine.Length));
                    lastLineModified = lastLine + new string('.', Math.Min(3, maxLength - lastLine.Length));
                }
                else
                {
                    lastLineModified = new string('.', maxLength);
                }
                lines.Add(lastLineModified);
                return false;
            }

            lines.Add(line);
            return true;
        }

        return lines;
    }
}

internal struct WordWrappedText
{
    public IReadOnlyList<WrappedLine> WrappedLines { get; }
    private ConsoleCoordinate cursor;

    public ConsoleCoordinate Cursor
    {
        get => cursor;
        set
        {
            Debug.Assert(value.Row < WrappedLines.Count);
            Debug.Assert(value.Column <= WrappedLines[value.Row].Content.Length);
            cursor = value;
        }
    }

    public WordWrappedText(IReadOnlyList<WrappedLine> wrappedLines, ConsoleCoordinate cursor)
    {
        Debug.Assert(!wrappedLines[^1].Content.EndsWith('\n'));

        WrappedLines = wrappedLines;

        this.cursor = default;
        Cursor = cursor;
    }
}

[DebuggerDisplay("{Content}")]
internal readonly struct WrappedLine
{
    public readonly int StartIndex;
    public readonly string Content;
    public readonly int UnicodeWidth;

    public WrappedLine(int startIndex, string content)
        : this(startIndex, content, Rendering.UnicodeWidth.GetWidth(content))
    { }

    public WrappedLine(int startIndex, string content, int unicodeWidth)
    {
        Debug.Assert(startIndex >= 0);
        Debug.Assert(unicodeWidth == Rendering.UnicodeWidth.GetWidth(content));

        StartIndex = startIndex;
        Content = content;
        UnicodeWidth = unicodeWidth;
    }

    public static WrappedLine Empty(int startIndex) => new(startIndex, string.Empty, 0);
}