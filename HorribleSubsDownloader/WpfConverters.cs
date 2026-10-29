using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Kizuna
{
    // Shared helper: returns true for paths that must never be checked synchronously.
    // Directory.Exists on an unreachable UNC share blocks the Windows SMB stack and can
    // freeze the entire process. DriveInfo.DriveType is fast (reads cached kernel data).
    internal static class PathHelper
    {
        public static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith(@"\\")) return true;
            if (path.Length >= 2 && path[1] == ':')
            {
                try { return new DriveInfo(path.Substring(0, 1)).DriveType == DriveType.Network; }
                catch { return true; } // if we can't determine, treat as network to be safe
            }
            return false;
        }
    }

    public sealed class PathMissingToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string path = value as string;
            if (string.IsNullOrEmpty(path)) return Visibility.Collapsed;
            if (PathHelper.IsNetworkPath(path)) return Visibility.Collapsed; // checked async during scan
            try { return Directory.Exists(path) ? Visibility.Collapsed : Visibility.Visible; }
            catch { return Visibility.Collapsed; }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public sealed class PathExistsToForegroundConverter : IValueConverter
    {
        private static readonly Brush _ok      = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        private static readonly Brush _missing = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string path = value as string;
            if (string.IsNullOrEmpty(path)) return _ok;
            if (PathHelper.IsNetworkPath(path)) return _ok; // checked async during scan
            try { return Directory.Exists(path) ? _ok : _missing; }
            catch { return _ok; }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public sealed class BoolToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public sealed class InverseBoolToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
