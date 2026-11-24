using Denon.App.Desktop.Controls;
using Denon.App.Desktop.Infrastructure.Theming;
using Xunit;
using Color = System.Windows.Media.Color;

namespace Denon.App.Core.Tests;

/// <summary>
/// Meter materials (docs/theming.md): every skin has a dark set for dark themes and a light set for light themes,
/// and the print on each dial and LED screen stays readable.
/// </summary>
public sealed class MeterPaletteTests
{
    public static TheoryData<string, bool> Sets()
    {
        var data = new TheoryData<string, bool>();
        foreach (var skin in ThemeManager.Skins)
        {
            data.Add(skin.Id, true);
            data.Add(skin.Id, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void The_theme_picks_a_face_of_its_own_brightness(string skinId, bool dark)
    {
        var p = Palette(skinId, dark);
        foreach (var face in new[] { p.FaceTop, p.FaceMiddle, p.FaceBottom, p.LedScreen })
        {
            Assert.True(dark ? Luminance(face) < 0.05 : Luminance(face) > 0.6, $"{skinId} {(dark ? "dark" : "light")}: {face}");
        }
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void Print_stays_readable_on_its_face(string skinId, bool dark)
    {
        var p = Palette(skinId, dark);
        (string Name, Color Ink, Color Face, double Floor)[] pairs =
        [
            ("PaperInk", p.PaperInk, p.FaceMiddle, 7),
            ("LeftAccent", p.LeftAccent, p.FaceMiddle, 4.5),
            ("RightAccent", p.RightAccent, p.FaceMiddle, 4.5),
            ("RedZoneInk", p.RedZoneInk, p.FaceMiddle, 4.5),
            ("SourceDenon", p.SourceDenon, p.FaceMiddle, 3),
            ("SourcePc", p.SourcePc, p.FaceMiddle, 3),
            ("SourceMuted", p.SourceMuted, p.FaceMiddle, 3),
            ("SourceSimulated", p.SourceSimulated, p.FaceMiddle, 3),
            ("SourceIdle", p.SourceIdle, p.FaceMiddle, 3),
            ("LedLabel", p.LedLabel, p.LedScreen, 3),
        ];

        var failures = pairs.Select(x => (x, Ratio: Contrast(x.Ink, x.Face)))
            .Where(r => r.Ratio < r.x.Floor)
            .Select(r => $"{r.x.Name}: {r.Ratio:0.00} < {r.x.Floor}")
            .ToList();
        Assert.True(failures.Count == 0, $"{skinId} {(dark ? "dark" : "light")}:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Each_skin_uses_its_dark_set_in_dark_themes_and_its_light_set_in_light_themes()
    {
        var dark = ThemeManager.Themes.Single(t => t.IsDark);
        var light = ThemeManager.Themes.Single(t => !t.IsDark);
        Assert.All(ThemeManager.Skins, s =>
        {
            Assert.Same(s.Meters, s.MetersFor(dark));
            Assert.Same(s.LightMeters, s.MetersFor(light));
            Assert.NotSame(s.Meters, s.LightMeters);
        });
    }

    private static MeterPalette Palette(string skinId, bool dark) =>
        dark ? ThemeManager.ResolveSkin(skinId).Meters : ThemeManager.ResolveSkin(skinId).LightMeters;

    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }
}
