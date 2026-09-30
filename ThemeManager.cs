using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace BlitzkriegWPF
{
    /// <summary>
    /// Быстро переключает тему без замены ResourceDictionary.
    /// Кисти никогда не изменяются после создания: WPF может их заморозить,
    /// поэтому при смене темы ресурс просто заменяется новым SolidColorBrush.
    /// </summary>
    public static class ThemeManager
    {
        private static readonly string[] BrushKeys =
        {
            "WindowBackgroundBrush",
            "ControlBackgroundBrush",
            "PanelBackgroundBrush",
            "GridRowBrush",
            "GridAltRowBrush",
            "TextPrimaryBrush",
            "TextSecondaryBrush",
            "BorderBrush",
            "AccentBrush",
            "AccentHoverBrush"
        };

        private static readonly Dictionary<string, string> ThemePaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Blue"] = "Themes/BlueTheme.xaml",
                ["Charcoal"] = "Themes/CharcoalTheme.xaml",
                ["Emerald"] = "Themes/EmeraldTheme.xaml"
            };

        private static readonly Dictionary<string, Dictionary<string, Color>> Palettes =
            new Dictionary<string, Dictionary<string, Color>>(StringComparer.OrdinalIgnoreCase);

        private static bool isInitialized;

        public static void Initialize(string selectedTheme)
        {
            if (!isInitialized)
            {
                LoadPalettes();
                isInitialized = true;
            }

            Apply(selectedTheme);
        }

        public static void Apply(string theme)
        {
            if (!isInitialized)
                Initialize(theme);

            if (!Palettes.TryGetValue(theme ?? string.Empty, out var palette))
            {
                theme = "Blue";
                if (!Palettes.TryGetValue(theme, out palette))
                    return;
            }

            var resources = Application.Current.Resources;

            foreach (var pair in palette)
            {
                resources[pair.Key] = new SolidColorBrush(pair.Value);
            }
        }

        private static void LoadPalettes()
        {
            foreach (var theme in ThemePaths)
            {
                try
                {
                    var dictionary = new ResourceDictionary
                    {
                        Source = new Uri(
                            $"pack://application:,,,/{theme.Value}",
                            UriKind.Absolute)
                    };

                    var palette = new Dictionary<string, Color>(StringComparer.Ordinal);

                    foreach (string key in BrushKeys)
                    {
                        if (dictionary[key] is SolidColorBrush sourceBrush)
                            palette[key] = sourceBrush.Color;
                    }

                    Palettes[theme.Key] = palette;
                }
                catch
                {
                    // Не загружаемая дополнительная тема не должна ломать запуск.
                }
            }
        }
    }
}
