using System;
using System.Collections.Generic;

namespace ClipStudio.Models
{
    public sealed record CaptionStyle(
        string Name,
        string FontName,
        double FontSize,
        bool Bold,
        bool AllCaps,
        string TextColor,
        string ActiveColor,
        string OutlineColor,
        double OutlineWidth,
        double ShadowDepth,
        bool OpaqueBox,
        double ActiveScalePercent,
        int MaxWordsPerLine,
        int MaxCharsPerLine,
        double BottomMarginFraction
    );

    public static class CaptionStyles
    {
        // "Classic Pop" Arial Black, white text, black outline 6, active word yellow #FFD400, 112% scale, 3 words/line
        // "Bold Caps" Impact, ALL CAPS, white, black outline 8, active word green #00E676, 3 words/line
        // "Clean" Segoe UI (bold), text #D0D0D0, active word white, no outline, shadow 3, 4 words/line
        // "Box" Segoe UI (bold), white text on an opaque dark box (OpaqueBox = true), active word #FFD400, 3 words/line
        // "Neon" Bahnschrift, white text, dark navy outline 5, active word cyan #00E5FF, 3 words/line

        public static readonly IReadOnlyList<CaptionStyle> All = new List<CaptionStyle>
        {
            new CaptionStyle(
                Name: "Classic Pop",
                FontName: "Arial Black",
                FontSize: 85.0, // Baseline scale for 1080px short side
                Bold: false, // Arial Black is inherently bold
                AllCaps: false,
                TextColor: "#FFFFFF",
                ActiveColor: "#FFD400",
                OutlineColor: "#000000",
                OutlineWidth: 6.0,
                ShadowDepth: 0.0,
                OpaqueBox: false,
                ActiveScalePercent: 112.0,
                MaxWordsPerLine: 3,
                MaxCharsPerLine: 40,
                BottomMarginFraction: 0.24
            ),
            new CaptionStyle(
                Name: "Bold Caps",
                FontName: "Impact",
                FontSize: 90.0,
                Bold: false, // Impact is thick
                AllCaps: true,
                TextColor: "#FFFFFF",
                ActiveColor: "#00E676",
                OutlineColor: "#000000",
                OutlineWidth: 8.0,
                ShadowDepth: 0.0,
                OpaqueBox: false,
                ActiveScalePercent: 100.0, // no scaling requested
                MaxWordsPerLine: 3,
                MaxCharsPerLine: 40,
                BottomMarginFraction: 0.24
            ),
            new CaptionStyle(
                Name: "Clean",
                FontName: "Segoe UI",
                FontSize: 80.0,
                Bold: true,
                AllCaps: false,
                TextColor: "#D0D0D0",
                ActiveColor: "#FFFFFF",
                OutlineColor: "#000000", // Will be outline 0, but provide hex
                OutlineWidth: 0.0,
                ShadowDepth: 3.0,
                OpaqueBox: false,
                ActiveScalePercent: 100.0,
                MaxWordsPerLine: 4,
                MaxCharsPerLine: 40,
                BottomMarginFraction: 0.24
            ),
            new CaptionStyle(
                Name: "Box",
                FontName: "Segoe UI",
                FontSize: 80.0,
                Bold: true,
                AllCaps: false,
                TextColor: "#FFFFFF",
                ActiveColor: "#FFD400",
                OutlineColor: "#000000",
                OutlineWidth: 0.0, // Assuming box styling handles background, usually outline is 0 for box
                ShadowDepth: 0.0,
                OpaqueBox: true,
                ActiveScalePercent: 100.0,
                MaxWordsPerLine: 3,
                MaxCharsPerLine: 40,
                BottomMarginFraction: 0.24
            ),
            new CaptionStyle(
                Name: "Neon",
                FontName: "Bahnschrift",
                FontSize: 85.0,
                Bold: false, // Bahnschrift can be bolded or kept normal, preset didn't specify bold, but usually standard
                AllCaps: false,
                TextColor: "#FFFFFF",
                ActiveColor: "#00E5FF",
                OutlineColor: "#000080", // dark navy
                OutlineWidth: 5.0,
                ShadowDepth: 0.0,
                OpaqueBox: false,
                ActiveScalePercent: 100.0,
                MaxWordsPerLine: 3,
                MaxCharsPerLine: 40,
                BottomMarginFraction: 0.24
            )
        }.AsReadOnly();

        public static CaptionStyle Default => All[0];
    }
}
