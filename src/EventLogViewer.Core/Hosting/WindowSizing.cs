using System;

namespace EventLogViewer.Core.Hosting
{
    /// <summary>Window size chosen for a particular desktop.</summary>
    public struct WindowSize
    {
        public double Width;
        public double Height;
        public double MaxWidth;
        public double MaxHeight;
    }

    /// <summary>
    /// Works out how large the main window should open, given the desktop it is on.
    /// </summary>
    /// <remarks>
    /// Pure arithmetic and deliberately outside the view, because this is the single most
    /// important Backstage fix and cannot otherwise be tested without a small screen to test on.
    /// The WinForms version opened at a fixed 1150x780 with a 900x600 minimum. A ScreenConnect
    /// Backstage desktop is commonly 1024x768 and can be smaller, so that window opened with its
    /// lower portion off-screen - including the status bar and part of the detail pane - and its
    /// minimum size prevented shrinking it back into view.
    /// </remarks>
    public static class WindowSizing
    {
        public const double PreferredWidth = 1150;
        public const double PreferredHeight = 780;
        public const double MinWidth = 720;
        public const double MinHeight = 480;

        /// <summary>Gap left around the window so it never sits flush against the screen edges.</summary>
        private const double Margin = 40;

        public static WindowSize Compute(double availableWidth, double availableHeight) =>
            Compute(availableWidth, availableHeight, PreferredWidth, PreferredHeight, MinWidth, MinHeight);

        public static WindowSize Compute(double availableWidth, double availableHeight,
                                         double preferredWidth, double preferredHeight,
                                         double minWidth, double minHeight)
        {
            // A degenerate work area (a metrics call that failed, or a desktop not yet sized)
            // should not produce a zero-sized window. Fall back to the preferred size outright
            // rather than to a preferred-sized "screen", which would then have the edge margin
            // subtracted from it and open the window slightly smaller than intended.
            if (availableWidth <= 0 || availableHeight <= 0)
            {
                return new WindowSize
                {
                    Width = preferredWidth,
                    Height = preferredHeight,
                    MaxWidth = double.PositiveInfinity,
                    MaxHeight = double.PositiveInfinity
                };
            }

            var fitWidth = Math.Max(minWidth, availableWidth - Margin);
            var fitHeight = Math.Max(minHeight, availableHeight - Margin);

            return new WindowSize
            {
                Width = Math.Min(preferredWidth, fitWidth),
                Height = Math.Min(preferredHeight, fitHeight),

                // Capped at the work area rather than the fitted size: on a desktop smaller than
                // the minimum, the window is allowed to reach the screen edges, since being
                // slightly oversized is recoverable and being clipped with no way back is not.
                MaxWidth = Math.Max(minWidth, availableWidth),
                MaxHeight = Math.Max(minHeight, availableHeight)
            };
        }
    }
}
