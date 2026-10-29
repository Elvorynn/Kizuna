using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Kizuna
{
    /// <summary>
    /// Manages the Windows system-tray icon for Kizuna.
    ///
    /// The icon is redrawn whenever the indicator state changes:
    ///   • Green dot (bottom-left)  — unrecognized files are present in the backend queue.
    ///   • Red dot   (bottom-right) — items are waiting in the download queue.
    /// Both dots can appear simultaneously.
    /// </summary>
    internal sealed class TrayIconManager : IDisposable
    {
        private readonly NotifyIcon _icon;
        private bool _hasDownloadItems;
        private bool _hasUnrecognizedItems;
        private bool _disposed;

        /// <summary>Fired when the user double-clicks the tray icon or chooses "Show".</summary>
        public event Action ShowRequested;

        /// <summary>Fired when the user chooses "Exit" from the tray context menu.</summary>
        public event Action ExitRequested;

        public TrayIconManager()
        {
            _icon = new NotifyIcon { Text = "Kizuna", Visible = true };
            _icon.DoubleClick += (s, e) => ShowRequested?.Invoke();

            _icon.ContextMenu = new ContextMenu(new[]
            {
                new MenuItem("Show", (s, e) => ShowRequested?.Invoke()),
                new MenuItem("-"),
                new MenuItem("Exit", (s, e) => ExitRequested?.Invoke())
            });

            RedrawIcon();
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void UpdateIndicators(bool hasDownloadItems, bool hasUnrecognizedItems)
        {
            if (_hasDownloadItems == hasDownloadItems &&
                _hasUnrecognizedItems == hasUnrecognizedItems) return;

            _hasDownloadItems     = hasDownloadItems;
            _hasUnrecognizedItems = hasUnrecognizedItems;
            RedrawIcon();
        }

        public void ShowBalloon(string title, string message, ToolTipIcon tipIcon = ToolTipIcon.Info)
        {
            if (_disposed) return;
            _icon.ShowBalloonTip(3000, title, message, tipIcon);
        }

        // ── Icon drawing ──────────────────────────────────────────────────────

        private void RedrawIcon()
        {
            if (_disposed) return;

            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(0x1E, 0x1E, 0x1E));

                // "K" badge
                using (var font  = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.FromArgb(0x9C, 0xDC, 0xFE)))
                {
                    SizeF sz = g.MeasureString("K", font);
                    g.DrawString("K", font, brush,
                        (32f - sz.Width) / 2f - 1f,
                        (32f - sz.Height) / 2f - 1f);
                }

                // Green dot — unrecognized files (bottom-left)
                if (_hasUnrecognizedItems)
                {
                    using (var fill = new SolidBrush(Color.FromArgb(0x3C, 0xC8, 0x3C)))
                    using (var edge = new Pen(Color.FromArgb(0x1E, 0x1E, 0x1E), 1.5f))
                    {
                        g.FillEllipse(fill, 1f, 22f, 9f, 9f);
                        g.DrawEllipse(edge, 1f, 22f, 9f, 9f);
                    }
                }

                // Red dot — download queue items (bottom-right)
                if (_hasDownloadItems)
                {
                    using (var fill = new SolidBrush(Color.FromArgb(0xF0, 0x50, 0x50)))
                    using (var edge = new Pen(Color.FromArgb(0x1E, 0x1E, 0x1E), 1.5f))
                    {
                        g.FillEllipse(fill, 22f, 22f, 9f, 9f);
                        g.DrawEllipse(edge, 22f, 22f, 9f, 9f);
                    }
                }

                // Convert Bitmap → Icon; clone so we own the handle independently.
                IntPtr hIcon = bmp.GetHicon();
                Icon newIcon = (Icon)Icon.FromHandle(hIcon).Clone();
                DestroyIcon(hIcon);

                _icon.Icon?.Dispose();
                _icon.Icon = newIcon;
            }
        }

        // ── Cleanup ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _icon.Visible = false;
            _icon.Icon?.Dispose();
            _icon.Dispose();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
