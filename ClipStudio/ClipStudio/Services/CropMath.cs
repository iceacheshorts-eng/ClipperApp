namespace ClipStudio.Services
{
    using System;

    public static class CropMath
    {
        public const int OutputWidth = 1080;
        public const int OutputHeight = 1920;
        public const int PanelHeight = 960;

        public static int TargetWidth(int sourceHeight)
        {
            int w = (int)(sourceHeight * 9.0 / 16.0);
            return w % 2 == 0 ? w : w - 1;
        }

        public static bool CanStack(int srcW, int srcH)
        {
            return srcH * (OutputWidth / (double)PanelHeight) <= srcW;
        }

        public static int StackedRegionWidth(int srcH)
        {
            return (int)Math.Round(srcH * (OutputWidth / (double)PanelHeight));
        }
    }
}
