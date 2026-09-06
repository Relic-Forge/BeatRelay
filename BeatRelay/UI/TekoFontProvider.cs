#if NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BeatRelay.UI;

internal static class TekoFontProvider
{
    private const string TekoResourceName = "BeatRelay.Resources.Fonts.Teko[wght].ttf";
    private const string TekoRegularResourceName = "BeatRelay.Resources.Fonts.Teko-Regular.ttf";
    private const string TekoSemiBoldResourceName = "BeatRelay.Resources.Fonts.Teko-SemiBold.ttf";
    private const string PrewarmCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789#.,:+-()/[] ";
    private static readonly object Sync = new();
    private static readonly Dictionary<FontStyle, Font> CachedRuntimeFonts = new();
    private static readonly Dictionary<string, string> ExtractedPathsByResource = new(StringComparer.Ordinal);
    private static bool attemptedPrivateFontRegistration;

    public static Font? GetFont(int size, FontStyle style = FontStyle.Normal)
    {
        lock (Sync)
        {
            try
            {
                if (!CachedRuntimeFonts.TryGetValue(style, out var cachedRuntimeFont))
                {
                    EnsurePrivateFontRegistered();
                    cachedRuntimeFont = TryCreateFromBundledFontFile(style)
                        ?? TryCreateFromKnownFontFile(style)
                        ?? TryCreateFromOsFont(size, style);

                    if (cachedRuntimeFont != null)
                    {
                        CachedRuntimeFonts[style] = cachedRuntimeFont;
                    }
                }

                if (cachedRuntimeFont != null)
                {
                    // Desktop IMGUI needs the glyphs requested for the exact style it will draw.
                    cachedRuntimeFont.RequestCharactersInTexture(
                        PrewarmCharacters,
                        Math.Max(12, size),
                        style);
                }

                return cachedRuntimeFont;
            }
            catch
            {
                CachedRuntimeFonts.Remove(style);
            }

            return null;
        }
    }

    public static string? GetFontFilePath(FontStyle style = FontStyle.Normal)
    {
        lock (Sync)
        {
            EnsurePrivateFontRegistered();
            var useSemiBold = style is FontStyle.Bold or FontStyle.BoldAndItalic;
            var bundledPath = EnsureExtracted(
                useSemiBold ? TekoSemiBoldResourceName : TekoRegularResourceName,
                useSemiBold ? "Teko-SemiBold.ttf" : "Teko-Regular.ttf");
            if (!string.IsNullOrWhiteSpace(bundledPath) && File.Exists(bundledPath))
            {
                return bundledPath;
            }

            var knownPath = ResolveKnownFontFile(style);
            return !string.IsNullOrWhiteSpace(knownPath) && File.Exists(knownPath) ? knownPath : null;
        }
    }

    private static string? EnsureExtracted(string resourceName, string outputFileName)
    {
        if (ExtractedPathsByResource.TryGetValue(resourceName, out var cachedPath) && File.Exists(cachedPath))
        {
            return cachedPath;
        }

        var assembly = Assembly.GetExecutingAssembly();
        var resolvedResourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => string.Equals(name, resourceName, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(resolvedResourceName))
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(resolvedResourceName);
        if (stream == null)
        {
            return null;
        }

        var outputDirectory = Path.Combine(Path.GetTempPath(), "BeatRelay", "Fonts");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, outputFileName);
        using var file = File.Create(outputPath);
        stream.CopyTo(file);
        ExtractedPathsByResource[resourceName] = outputPath;
        return outputPath;
    }

    private static void EnsurePrivateFontRegistered()
    {
        if (attemptedPrivateFontRegistration)
        {
            return;
        }

        attemptedPrivateFontRegistration = true;
        var fontPaths = new[]
        {
            EnsureExtracted(TekoRegularResourceName, "Teko-Regular.ttf"),
            EnsureExtracted(TekoSemiBoldResourceName, "Teko-SemiBold.ttf"),
            EnsureExtracted(TekoResourceName, "Teko[wght].ttf")
        };

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (var fontPath in fontPaths)
                {
                    if (!string.IsNullOrWhiteSpace(fontPath) && File.Exists(fontPath))
                    {
                        AddFontResourceEx(fontPath!, 0x10, IntPtr.Zero);
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static Font? TryCreateFromKnownFontFile(FontStyle style)
    {
        var fontPath = ResolveKnownFontFile(style);
        return string.IsNullOrWhiteSpace(fontPath) ? null : TryCreateFromFile(fontPath!);
    }

    private static Font? TryCreateFromBundledFontFile(FontStyle style)
    {
        var useSemiBold = style is FontStyle.Bold or FontStyle.BoldAndItalic;
        var resourceName = useSemiBold ? TekoSemiBoldResourceName : TekoRegularResourceName;
        var fileName = useSemiBold ? "Teko-SemiBold.ttf" : "Teko-Regular.ttf";
        var fontPath = EnsureExtracted(resourceName, fileName);
        return string.IsNullOrWhiteSpace(fontPath) ? null : TryCreateFromFile(fontPath!);
    }

    private static Font? TryCreateFromFile(string fontPath)
    {
        try
        {
            if (!File.Exists(fontPath))
            {
                return null;
            }

            var font = new Font(fontPath);
            return font != null && font.dynamic ? font : null;
        }
        catch
        {
        }

        return null;
    }

    private static string? ResolveKnownFontFile(FontStyle style)
    {
        try
        {
            var useSemiBold = style is FontStyle.Bold or FontStyle.BoldAndItalic;
            var fileName = useSemiBold ? "Teko-SemiBold.ttf" : "Teko-Regular.ttf";
            var localFonts = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "Windows",
                "Fonts",
                fileName);
            if (File.Exists(localFonts))
            {
                return localFonts;
            }

            var windowsFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", fileName);
            return File.Exists(windowsFonts) ? windowsFonts : null;
        }
        catch
        {
            return null;
        }
    }

    private static Font? TryCreateFromOsFont(int size, FontStyle style)
    {
        try
        {
            var requestedSize = Math.Max(12, size);
            var fallbackNames = style switch
            {
                FontStyle.Bold => new[] { "Teko SemiBold", "Teko-SemiBold", "Teko Bold", "Teko-Bold", "Teko Medium", "Teko-Medium", "Teko" },
                FontStyle.Italic => new[] { "Teko Italic", "Teko-Italic", "Teko Medium", "Teko-Medium", "Teko" },
                FontStyle.BoldAndItalic => new[] { "Teko SemiBold", "Teko-SemiBold", "Teko Bold Italic", "Teko-BoldItalic", "Teko Bold", "Teko Medium", "Teko-Medium", "Teko" },
                _ => new[] { "Teko", "Teko Regular", "Teko-Regular", "Teko Medium", "Teko-Medium" }
            };

            var font = Font.CreateDynamicFontFromOSFont(fallbackNames, requestedSize);
            return font != null && font.dynamic ? font : null;
        }
        catch
        {
        }

        return null;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceEx(string name, uint flags, IntPtr reserved);
}
#endif
