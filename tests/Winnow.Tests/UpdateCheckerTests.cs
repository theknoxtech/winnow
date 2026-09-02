using System;
using Winnow.Core.Update;
using Xunit;

namespace Winnow.Tests
{
    public class UpdateCheckerTests
    {
        private static string Release(string tag, string url = "https://example.invalid/r") =>
            "{ \"tag_name\": \"" + tag + "\", \"html_url\": \"" + url + "\" }";

        [Theory]
        [InlineData("v1.2.0", "1.2.0")]
        [InlineData("1.2.0", "1.2.0")]
        [InlineData("V1.2.0", "1.2.0")]
        [InlineData("v1.2", "1.2")]
        [InlineData("v1.2.0.4", "1.2.0.4")]
        [InlineData("v1.3.0-beta.1", "1.3.0")]
        // The SDK stamps AssemblyInformationalVersion as "<version>+<commit sha>", and that is the
        // exact string CurrentVersion() has to read back to know what is running. Parsing it as
        // null would make the app think it had no version and never report an update.
        [InlineData("1.3.0+98b6a3589e88542f6f301c157957f1b3f118c9f3", "1.3.0")]
        [InlineData("v1.3.0-beta.1+a1b2c3d", "1.3.0")]
        public void ParsesVersionTags(string tag, string expected)
        {
            Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(tag));
        }

        [Fact]
        public void TheRunningVersionIsNotZero()
        {
            // Catches the whole chain being broken - a version that fails to parse degrades to
            // 0.0.0.0, which would silently make every release look newer.
            var version = UpdateChecker.CurrentVersion();

            Assert.NotNull(version);
            Assert.True(version > new Version(0, 0, 0, 0),
                "CurrentVersion() returned " + version + "; the assembly version is not being read.");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-version")]
        [InlineData("v")]
        [InlineData("release-candidate")]
        public void RejectsNonVersionTags(string tag)
        {
            Assert.Null(UpdateChecker.ParseVersion(tag));
        }

        [Fact]
        public void ReportsANewerRelease()
        {
            var info = UpdateChecker.Parse(Release("v1.3.0"), new Version(1, 2, 0));

            Assert.NotNull(info);
            Assert.Equal("v1.3.0", info.TagName);
            Assert.Equal(new Version(1, 3, 0), info.Version);
            Assert.Equal("https://example.invalid/r", info.ReleaseUrl);
        }

        [Fact]
        public void SaysNothingWhenCurrent()
        {
            Assert.Null(UpdateChecker.Parse(Release("v1.2.0"), new Version(1, 2, 0)));
        }

        [Fact]
        public void SaysNothingWhenRunningAheadOfTheRelease()
        {
            // A local build newer than the published release must not prompt to "update" backwards.
            Assert.Null(UpdateChecker.Parse(Release("v1.2.0"), new Version(1, 3, 0)));
        }

        [Fact]
        public void ComparesNumericallyNotLexically()
        {
            // The bug a string comparison would introduce: "1.10.0" sorting below "1.9.0".
            Assert.NotNull(UpdateChecker.Parse(Release("v1.10.0"), new Version(1, 9, 0)));
            Assert.Null(UpdateChecker.Parse(Release("v1.9.0"), new Version(1, 10, 0)));
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("{}")]
        [InlineData("{ \"message\": \"API rate limit exceeded\" }")]
        [InlineData("[]")]
        [InlineData("")]
        public void SwallowsUnusablePayloads(string json)
        {
            // Rate limiting and error payloads are normal here and must never surface to the user.
            Assert.Null(UpdateChecker.Parse(json, new Version(1, 0, 0)));
        }

        [Fact]
        public void SwallowsAnUnknownCurrentVersion()
        {
            Assert.Null(UpdateChecker.Parse(Release("v9.9.9"), null));
        }

        [Fact]
        public void CurrentVersionIsReadableFromTheAssembly()
        {
            // Version comes from assembly metadata rather than a hand-maintained constant, so the
            // release tag stays the single source of truth.
            Assert.NotNull(UpdateChecker.CurrentVersion());
        }
    }
}
