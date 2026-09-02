using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Winnow.App
{
    /// <summary>
    /// Maps a preset's group to its button colour, carrying over the palette from the PowerShell
    /// version's $script:GroupColors.
    /// </summary>
    /// <remarks>
    /// Groups are user-extensible now that presets come from a file, so an unknown group gets a
    /// stable pastel derived from its name rather than falling back to a single grey. Deriving it
    /// from a hash keeps a custom group's colour consistent between launches.
    /// </remarks>
    public sealed class GroupColorConverter : IValueConverter
    {
        private static readonly Dictionary<string, Color> Known =
            new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
            {
                ["System Changes"]   = Color.FromRgb(220, 235, 252),
                ["Account/Policy"]   = Color.FromRgb(255, 235, 220),
                ["App Health"]       = Color.FromRgb(255, 220, 220),
                ["Resources"]        = Color.FromRgb(255, 248, 210),
                ["Printing"]         = Color.FromRgb(230, 245, 230),
                ["Networking"]       = Color.FromRgb(235, 225, 255),
                ["Active Directory"] = Color.FromRgb(225, 225, 235),
                ["Hardware"]         = Color.FromRgb(210, 240, 240),
            };

        private static readonly Dictionary<string, SolidColorBrush> Cache =
            new Dictionary<string, SolidColorBrush>(StringComparer.OrdinalIgnoreCase);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var group = value as string ?? string.Empty;

            lock (Cache)
            {
                if (Cache.TryGetValue(group, out var cached)) return cached;

                var color = Known.TryGetValue(group, out var known) ? known : Derive(group);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                Cache[group] = brush;
                return brush;
            }
        }

        /// <summary>A light, readable pastel from the group name, stable across runs.</summary>
        private static Color Derive(string group)
        {
            unchecked
            {
                var hash = 17;
                foreach (var c in group ?? string.Empty)
                    hash = hash * 31 + char.ToUpperInvariant(c);

                // Keep every channel high so black label text stays legible.
                var r = (byte)(210 + Math.Abs(hash % 40));
                var g = (byte)(210 + Math.Abs((hash / 40) % 40));
                var b = (byte)(210 + Math.Abs((hash / 1600) % 40));
                return Color.FromRgb(r, g, b);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Colours the Level column so errors stand out in a dense grid.</summary>
    public sealed class LevelColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush Critical = Frozen(Color.FromRgb(168, 0, 0));
        private static readonly SolidColorBrush Error = Frozen(Color.FromRgb(200, 32, 32));
        private static readonly SolidColorBrush Warning = Frozen(Color.FromRgb(160, 100, 0));
        private static readonly SolidColorBrush Normal = Frozen(Color.FromRgb(32, 32, 32));

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            switch (value as string)
            {
                case "Critical": return Critical;
                case "Error": return Error;
                case "Warning": return Warning;
                default: return Normal;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var flag = value is bool b && b;
            if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
                flag = !flag;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Collapses an element when its bound string is null or empty.</summary>
    public sealed class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
