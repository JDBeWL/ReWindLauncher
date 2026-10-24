using System.Drawing;

namespace ReWindLauncher.UI
{
    internal static class MainLayout
    {
        public const int Width = 720;
        public const int Height = 450;

        public static readonly Rectangle LaunchButton = new Rectangle(431, 311, 147, 72);
        public static readonly Rectangle UpdateButton = new Rectangle(591, 311, 97, 32);
        public static readonly Rectangle SettingsButton = new Rectangle(591, 351, 97, 32);
        public static readonly Rectangle VersionBox = new Rectangle(432, 392, 181, 26);
        public static readonly Rectangle OpenDirText = new Rectangle(616, 388, 74, 22);
        public static readonly Rectangle CloseButton = new Rectangle(686, 2, 24, 24);
        public static readonly Rectangle Footer = new Rectangle(14, 424, 430, 18);
    }

    internal static class SettingsLayout
    {
        public const int Width = 686;
        public const int Height = 256;

        public static readonly Rectangle LeftGroup = new Rectangle(11, 31, 387, 181);
        public static readonly Rectangle RightGroup = new Rectangle(411, 31, 266, 181);
        public static readonly Rectangle CloseButton = new Rectangle(664, 2, 22, 22);
    }

    internal static class UpdateLayout
    {
        public const int Width = 706;
        public const int Height = 436;
        public const int TitleHeight = 28;

        public static readonly Rectangle CloseButton = new Rectangle(672, 3, 22, 22);
        public static readonly Rectangle List = new Rectangle(8, 36, 690, 320);
        public static readonly Rectangle SourceRow = new Rectangle(8, 372, 690, 56);
    }
}
