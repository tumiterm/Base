using System.Globalization;

namespace Base.Web.Theming;

/// <summary>
/// Derives the accent ramp from one tenant colour: hover/active shades, a subtle tint for selected states, a ring for
/// focus, and the text colour that meets WCAG contrast on it. Dark mode gets a lifted variant so the accent stays legible.
/// </summary>
public sealed record ThemePalette(string Accent)
{
    public static ThemePalette From(string? hex) => new(IsHex(hex) ? hex!.ToLowerInvariant() : "#4f5bd5");

    public static bool IsHex(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).ToString().All(Uri.IsHexDigit);

    public string Css()
    {
        var accent = Rgb.Parse(Accent);
        var dark = Rgb.Mix(accent, Rgb.White, 0.22);
        return string.Create(CultureInfo.InvariantCulture, $$"""
            /* Generated per tenant from Branding.PrimaryColor. */
            :root {
              --accent: {{accent}};
              --accent-rgb: {{accent.R}}, {{accent.G}}, {{accent.B}};
              --accent-hover: {{Rgb.Mix(accent, Rgb.Black, 0.1)}};
              --accent-active: {{Rgb.Mix(accent, Rgb.Black, 0.2)}};
              --accent-contrast: {{ContrastText(accent)}};
              --accent-subtle: {{Rgb.Mix(accent, Rgb.White, 0.9)}};
              --accent-subtle-text: {{Rgb.Mix(accent, Rgb.Black, 0.28)}};
              --accent-ring: rgb({{accent.R}} {{accent.G}} {{accent.B}} / 0.32);
            }
            [data-bs-theme="dark"] {
              --accent: {{dark}};
              --accent-rgb: {{dark.R}}, {{dark.G}}, {{dark.B}};
              --accent-hover: {{Rgb.Mix(dark, Rgb.White, 0.1)}};
              --accent-active: {{Rgb.Mix(dark, Rgb.White, 0.18)}};
              --accent-contrast: {{ContrastText(dark)}};
              --accent-subtle: rgb({{dark.R}} {{dark.G}} {{dark.B}} / 0.16);
              --accent-subtle-text: {{Rgb.Mix(dark, Rgb.White, 0.35)}};
              --accent-ring: rgb({{dark.R}} {{dark.G}} {{dark.B}} / 0.4);
            }

            """);
    }

    /// <summary>White or near-black, whichever contrasts more with the background (WCAG relative luminance).</summary>
    internal static string ContrastText(Rgb background) =>
        Rgb.Contrast(background, Rgb.White) >= Rgb.Contrast(background, Rgb.Ink) ? "#ffffff" : "#0b0d12";

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
    internal readonly record struct Rgb(int R, int G, int B)
    {
        public static readonly Rgb White = new(255, 255, 255);
        public static readonly Rgb Black = new(0, 0, 0);
        public static readonly Rgb Ink = new(11, 13, 18);

        public static Rgb Parse(string hex) => new(
            int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        public static Rgb Mix(Rgb a, Rgb b, double amount) => new(
            (int)Math.Round(a.R + ((b.R - a.R) * amount)),
            (int)Math.Round(a.G + ((b.G - a.G) * amount)),
            (int)Math.Round(a.B + ((b.B - a.B) * amount)));

        public static double Contrast(Rgb a, Rgb b)
        {
            var (l1, l2) = (a.Luminance(), b.Luminance());
            return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        }

        public double Luminance() => (0.2126 * Channel(R)) + (0.7152 * Channel(G)) + (0.0722 * Channel(B));

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}");

        private static double Channel(int value)
        {
            var c = value / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }
}
