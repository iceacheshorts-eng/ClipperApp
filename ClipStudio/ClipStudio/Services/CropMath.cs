namespace ClipStudio.Services
{
    public static class CropMath
    {
        public static int TargetWidth(int sourceHeight)
        {
            int w = (int)(sourceHeight * 9.0 / 16.0);
            return w % 2 == 0 ? w : w - 1;
        }
    }
}
