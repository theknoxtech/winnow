using EventLogViewer.Core.Hosting;
using Xunit;

namespace EventLogViewer.Tests
{
    /// <summary>
    /// The window has to fit whatever desktop it lands on. These cases are the reason the rewrite
    /// exists at all, so they are pinned rather than left to be discovered in a Backstage session.
    /// </summary>
    public class WindowSizingTests
    {
        [Fact]
        public void OnALargeMonitor_UsesThePreferredSize()
        {
            var size = WindowSizing.Compute(1920, 1032);

            Assert.Equal(WindowSizing.PreferredWidth, size.Width);
            Assert.Equal(WindowSizing.PreferredHeight, size.Height);
        }

        [Fact]
        public void OnATypicalBackstageDesktop_FitsWithin1024x768()
        {
            // The exact case the WinForms version failed: its 1150x780 default did not fit, and its
            // 900x600 minimum stopped the user shrinking it back into view.
            var size = WindowSizing.Compute(1024, 768);

            Assert.True(size.Width <= 1024, "width " + size.Width + " overflows a 1024px desktop");
            Assert.True(size.Height <= 768, "height " + size.Height + " overflows a 768px desktop");
            Assert.True(size.Width >= WindowSizing.MinWidth);
            Assert.True(size.Height >= WindowSizing.MinHeight);
        }

        [Theory]
        [InlineData(1024, 768)]
        [InlineData(1280, 720)]
        [InlineData(1024, 640)]
        [InlineData(800, 600)]
        public void NeverOpensLargerThanTheDesktop(double w, double h)
        {
            var size = WindowSizing.Compute(w, h);

            Assert.True(size.Width <= w, "width " + size.Width + " > " + w);
            Assert.True(size.Height <= h, "height " + size.Height + " > " + h);
        }

        [Fact]
        public void OnADesktopSmallerThanTheMinimum_ClampsToTheMinimumNotToZero()
        {
            // A window slightly larger than a tiny desktop is recoverable; a collapsed one is not.
            var size = WindowSizing.Compute(640, 400);

            Assert.Equal(WindowSizing.MinWidth, size.Width);
            Assert.Equal(WindowSizing.MinHeight, size.Height);
            Assert.Equal(WindowSizing.MinWidth, size.MaxWidth);
            Assert.Equal(WindowSizing.MinHeight, size.MaxHeight);
        }

        [Fact]
        public void MaxSizeAllowsUsingTheWholeWorkArea()
        {
            var size = WindowSizing.Compute(1024, 768);

            Assert.Equal(1024, size.MaxWidth);
            Assert.Equal(768, size.MaxHeight);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(-1, -1)]
        public void ADegenerateWorkAreaFallsBackToThePreferredSize(double w, double h)
        {
            // SystemParameters can report nothing useful on a desktop that is not fully set up yet.
            var size = WindowSizing.Compute(w, h);

            Assert.Equal(WindowSizing.PreferredWidth, size.Width);
            Assert.Equal(WindowSizing.PreferredHeight, size.Height);
        }

        [Fact]
        public void TheMinimumIsSmallEnoughForACommonBackstageDesktop()
        {
            // Guards the constant itself: the old 900x600 minimum is what made 1024x768 unusable
            // once window chrome and the taskbar were accounted for.
            Assert.True(WindowSizing.MinWidth <= 800);
            Assert.True(WindowSizing.MinHeight <= 600);
        }
    }
}
