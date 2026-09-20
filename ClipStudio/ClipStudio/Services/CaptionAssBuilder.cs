using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClipStudio.Models;

namespace ClipStudio.Services
{
    public static class CaptionAssBuilder
    {
        public readonly record struct MappedWord(string Text, double Start, double End);

        public static List<MappedWord> MapWordsToOutputTimeline(
            IReadOnlyList<WordTiming> words,
            double clipStartSeconds,
            double clipEndSeconds,
            IReadOnlyList<(double Start, double End)>? keepSpans)
        {
            var mapped = new List<MappedWord>();
            var sortedKeepSpans = keepSpans?.OrderBy(s => s.Start).ToList();

            foreach (var word in words)
            {
                double wordStart = word.Start.TotalSeconds;
                double wordEnd = word.End.TotalSeconds;
                double wordMid = (wordStart + wordEnd) / 2.0;

                // Check overall bounds
                if (wordMid < clipStartSeconds || wordMid > clipEndSeconds)
                {
                    continue;
                }

                string text = word.Text.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                // Strip ASS special characters and control chars
                var sb = new StringBuilder();
                foreach (char c in text)
                {
                    if (c == '{' || c == '}' || c == '\\' || char.IsControl(c))
                        continue;
                    sb.Append(c);
                }
                text = sb.ToString();

                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                if (sortedKeepSpans == null)
                {
                    mapped.Add(new MappedWord(
                        Text: text,
                        Start: Math.Max(0, wordStart - clipStartSeconds),
                        End: Math.Max(0, wordEnd - clipStartSeconds)
                    ));
                }
                else
                {
                    // Find which keepSpan contains the midpoint
                    var span = sortedKeepSpans.FirstOrDefault(s => wordMid >= s.Start && wordMid <= s.End);
                    if (span == default)
                    {
                        continue; // Dropped (midpoint in a cut)
                    }

                    // Accumulate elapsed output time from previous keep spans
                    double elapsed = 0;
                    foreach (var s in sortedKeepSpans)
                    {
                        if (s == span) break;
                        elapsed += (s.End - s.Start);
                    }

                    // Clamp to span
                    double clampedStart = Math.Max(span.Start, wordStart);
                    double clampedEnd = Math.Min(span.End, wordEnd);

                    if (clampedEnd > clampedStart)
                    {
                        mapped.Add(new MappedWord(
                            Text: text,
                            Start: elapsed + (clampedStart - span.Start),
                            End: elapsed + (clampedEnd - span.Start)
                        ));
                    }
                }
            }

            // Sort and avoid overlaps
            mapped.Sort((a, b) => a.Start.CompareTo(b.Start));

            for (int i = 0; i < mapped.Count; i++)
            {
                var cur = mapped[i];
                if (i > 0 && cur.Start < mapped[i - 1].End)
                {
                    cur = cur with { Start = mapped[i - 1].End };
                }
                if (cur.End <= cur.Start)
                {
                    cur = cur with { End = cur.Start + 0.01 }; // Guarantee End > Start
                }
                mapped[i] = cur;
            }

            return mapped;
        }

        public static string Build(IReadOnlyList<MappedWord> words, CaptionStyle style, int outputWidth, int outputHeight)
        {
            var sb = new StringBuilder();
            WriteHeader(sb, outputWidth, outputHeight);
            WriteStyles(sb, style, outputWidth, outputHeight);
            WriteEvents(sb, words, style, outputWidth);
            return sb.ToString();
        }

        private static void WriteHeader(StringBuilder sb, int outputWidth, int outputHeight)
        {
            sb.AppendLine("[Script Info]");
            sb.AppendLine("ScriptType: v4.00+");
            sb.AppendLine($"PlayResX: {outputWidth}");
            sb.AppendLine($"PlayResY: {outputHeight}");
            sb.AppendLine("WrapStyle: 2");
            sb.AppendLine("ScaledBorderAndShadow: yes");
            sb.AppendLine();
        }

        private static string ConvertHexToAssColor(string hex)
        {
            // Hex format #RRGGBB -> ASS format &HAABBGGRR (AA=00 opaque)
            if (string.IsNullOrEmpty(hex) || !hex.StartsWith("#") || hex.Length != 7)
                return "&H00FFFFFF";

            string r = hex.Substring(1, 2);
            string g = hex.Substring(3, 2);
            string b = hex.Substring(5, 2);

            return $"&H00{b}{g}{r}";
        }

        private static void WriteStyles(StringBuilder sb, CaptionStyle style, int outputWidth, int outputHeight)
        {
            sb.AppendLine("[V4+ Styles]");
            sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");

            string primary = ConvertHexToAssColor(style.TextColor);
            string outline = ConvertHexToAssColor(style.OutlineColor);

            // OpaqueBox styling uses BorderStyle=3, otherwise 1
            int borderStyle = style.OpaqueBox ? 3 : 1;
            int bold = style.Bold ? -1 : 0;

            double scaleRatio = Math.Min(outputWidth, outputHeight) / 1080.0;
            double fontSize = style.FontSize * scaleRatio;

            int marginV = (int)Math.Round(style.BottomMarginFraction * outputHeight);
            int marginLR = (int)Math.Round(0.06 * outputWidth);

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "Style: Caption,{0},{1:F1},{2},{2},{3},&H80000000,{4},0,0,0,100,100,0,0,{5},{6:F1},{7:F1},2,{8},{8},{9},1",
                style.FontName,
                fontSize,
                primary,
                outline,
                bold,
                borderStyle,
                style.OutlineWidth,
                style.ShadowDepth,
                marginLR,
                marginV));

            sb.AppendLine();
        }

        private static string FormatAssTime(double seconds)
        {
            TimeSpan ts = TimeSpan.FromSeconds(seconds);
            int centiseconds = (int)Math.Round(ts.Milliseconds / 10.0);
            if (centiseconds == 100)
            {
                ts = ts.Add(TimeSpan.FromSeconds(0.01)); // round up
                centiseconds = 0;
            }
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:00}",
                Math.Floor(ts.TotalHours), ts.Minutes, ts.Seconds, centiseconds);
        }

        private static void WriteEvents(StringBuilder sb, IReadOnlyList<MappedWord> words, CaptionStyle style, int outputWidth)
        {
            sb.AppendLine("[Events]");
            sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

            if (words.Count == 0) return;

            var lines = new List<List<MappedWord>>();
            var currentLine = new List<MappedWord>();

            // Requirements say: estimated text width (chars * fontSize * 0.62) stays under 88% of outputWidth.
            double fontSizeScaled = style.FontSize * (outputWidth / 1080.0);

            for (int i = 0; i < words.Count; i++)
            {
                var word = words[i];
                if (currentLine.Count == 0)
                {
                    currentLine.Add(word);
                    continue;
                }

                var prevWord = currentLine.Last();
                double gap = word.Start - prevWord.End;

                // Line grouping conditions
                bool startNew = false;

                int currentChars = currentLine.Sum(w => w.Text.Length) + currentLine.Count - 1; // plus spaces
                int potentialChars = currentChars + 1 + word.Text.Length;

                double estimatedWidth = potentialChars * fontSizeScaled * 0.62;
                bool exceedsWidth = estimatedWidth > (0.88 * outputWidth);

                if (currentLine.Count >= style.MaxWordsPerLine) startNew = true;
                else if (potentialChars > style.MaxCharsPerLine) startNew = true;
                else if (exceedsWidth) startNew = true;
                else if (gap > 0.7) startNew = true;
                else
                {
                    string prevText = prevWord.Text;
                    if ((prevText.EndsWith(".") || prevText.EndsWith("?") || prevText.EndsWith("!")) && currentLine.Count >= 2)
                    {
                        startNew = true;
                    }
                }

                if (startNew)
                {
                    lines.Add(currentLine);
                    currentLine = new List<MappedWord> { word };
                }
                else
                {
                    currentLine.Add(word);
                }
            }

            if (currentLine.Count > 0)
            {
                lines.Add(currentLine);
            }

            string activeColorStr = ConvertHexToAssColor(style.ActiveColor).Substring(2); // remove &H for \c command which takes &H...& but wait, \c takes &HBBGGRR&. ConvertHex returns &H00BBGGRR.
            // Actually ASS tags: \c&HBBGGRR& (no alpha)
            // ConvertHexToAssColor returns &H00BBGGRR. We can substring:
            string activeColorBgr = ConvertHexToAssColor(style.ActiveColor).Substring(4); // BBGGRR
            activeColorBgr = $"&H{activeColorBgr}&";

            string activeScaleTag = "";
            if (style.ActiveScalePercent != 100.0)
            {
                string s = style.ActiveScalePercent.ToString("F1", CultureInfo.InvariantCulture);
                activeScaleTag = $"\\fscx{s}\\fscy{s}";
            }

            for (int lIndex = 0; lIndex < lines.Count; lIndex++)
            {
                var line = lines[lIndex];
                var nextLine = (lIndex + 1 < lines.Count) ? lines[lIndex + 1] : null;

                // For each word in the line, emit one dialogue event
                for (int wIndex = 0; wIndex < line.Count; wIndex++)
                {
                    var activeWord = line[wIndex];
                    double evStart = activeWord.Start;
                    double evEnd = (wIndex + 1 < line.Count) ? line[wIndex + 1].Start : (activeWord.End + 0.2);

                    if (wIndex == line.Count - 1 && nextLine != null && nextLine.Count > 0)
                    {
                        if (evEnd > nextLine[0].Start)
                        {
                            evEnd = nextLine[0].Start;
                        }
                    }

                    if (evEnd <= evStart)
                    {
                        evEnd = evStart + 0.01;
                    }

                    var textSb = new StringBuilder();
                    for (int j = 0; j < line.Count; j++)
                    {
                        string wText = line[j].Text;
                        if (style.AllCaps) wText = wText.ToUpperInvariant();

                        if (j > 0) textSb.Append(" ");

                        if (j == wIndex)
                        {
                            textSb.Append($"{{\\c{activeColorBgr}{activeScaleTag}}}{wText}{{\\r}}");
                        }
                        else
                        {
                            textSb.Append(wText);
                        }
                    }

                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "Dialogue: 0,{0},{1},Caption,,0,0,0,,{2}",
                        FormatAssTime(evStart),
                        FormatAssTime(evEnd),
                        textSb.ToString()));
                }
            }
        }
    }
}
