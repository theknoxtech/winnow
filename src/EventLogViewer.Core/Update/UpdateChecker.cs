using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace EventLogViewer.Core.Update
{
    public sealed class UpdateInfo
    {
        public string TagName { get; set; }
        public Version Version { get; set; }
        public string ReleaseUrl { get; set; }
    }

    /// <summary>
    /// Notify-only update check against GitHub Releases.
    /// </summary>
    /// <remarks>
    /// Deliberately does not download or apply anything - it reports that a newer release exists
    /// and where to get it, and that is all.
    ///
    /// Any failure is swallowed. Offline machines, outbound-blocked networks and GitHub rate
    /// limits are all normal in this app's environment, and none of them are worth interrupting a
    /// technician mid-investigation.
    /// </remarks>
    public sealed class UpdateChecker
    {
        public const string DefaultApiUrl =
            "https://api.github.com/repos/theknoxtech/PowerShell-Event-Log-Viewer/releases/latest";

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

        private readonly string _apiUrl;

        public UpdateChecker(string apiUrl = null)
        {
            _apiUrl = string.IsNullOrWhiteSpace(apiUrl) ? DefaultApiUrl : apiUrl;
        }

        /// <summary>
        /// Returns details of a newer release, or null if current, unreachable, or anything at
        /// all went wrong.
        /// </summary>
        public async Task<UpdateInfo> CheckAsync(Version currentVersion,
                                                 CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                ConfigureTransport();

                using (var handler = new HttpClientHandler())
                {
                    // Running as SYSTEM there is no per-user proxy configuration, so the check
                    // would silently never fire on a proxied network unless the system proxy is
                    // used explicitly with the machine's credentials.
                    try
                    {
                        handler.Proxy = WebRequest.GetSystemWebProxy();
                        handler.UseProxy = true;
                        handler.UseDefaultCredentials = true;
                    }
                    catch
                    {
                        // No resolvable proxy configuration; a direct connection is fine.
                    }

                    using (var client = new HttpClient(handler) { Timeout = Timeout })
                    {
                        // GitHub rejects requests with no User-Agent.
                        client.DefaultRequestHeaders.Add("User-Agent", "EventLogViewer");
                        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

                        var json = await client.GetStringAsync(_apiUrl).ConfigureAwait(false);
                        return Parse(json, currentVersion);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Parses the release payload and compares versions. Internal for testing.</summary>
        internal static UpdateInfo Parse(string json, Version currentVersion)
        {
            try
            {
                var release = JObject.Parse(json);
                var tag = (string)release["tag_name"];
                var latest = ParseVersion(tag);

                if (latest == null || currentVersion == null || latest <= currentVersion)
                    return null;

                return new UpdateInfo
                {
                    TagName = tag,
                    Version = latest,
                    ReleaseUrl = (string)release["html_url"]
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Turns a "v1.2.0" style tag or informational version into a Version.
        /// Null when it is not a version at all.
        /// </summary>
        /// <remarks>
        /// Handles both semver suffixes, because both actually occur here. A release tag may carry
        /// a pre-release part ("v1.3.0-beta.1"), and the SDK appends build metadata to the
        /// assembly's informational version ("1.3.0+a1b2c3d") - which is the very string this has
        /// to parse to know what version is running. Version.TryParse rejects either one.
        /// </remarks>
        internal static Version ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;

            var text = tag.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(1);

            // Build metadata first: "1.3.0-beta.1+sha" puts the '+' after the '-'.
            var plus = text.IndexOf('+');
            if (plus > 0) text = text.Substring(0, plus);

            var dash = text.IndexOf('-');
            if (dash > 0) text = text.Substring(0, dash);

            return Version.TryParse(text, out var version) ? version : null;
        }

        private static void ConfigureTransport()
        {
            try
            {
                // .NET Framework 4.8 defaults to the system TLS configuration, but being explicit
                // costs nothing and avoids a handshake failure on machines still pinned to an
                // older default.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch
            {
                // Older platform without TLS 1.2 in the enum; nothing useful to do.
            }
        }

        /// <summary>
        /// The running version, from the assembly rather than a hand-maintained constant, so the
        /// release tag is the single source of truth and cannot drift out of sync.
        /// </summary>
        public static Version CurrentVersion()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

                var informational = asm
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                var parsed = ParseVersion(StripBuildMetadata(informational));
                if (parsed != null) return parsed;

                return asm.GetName().Version ?? new Version(0, 0, 0, 0);
            }
            catch
            {
                return new Version(0, 0, 0, 0);
            }
        }

        /// <summary>The SDK appends "+&lt;commit sha&gt;" to the informational version.</summary>
        private static string StripBuildMetadata(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var plus = value.IndexOf('+');
            return plus > 0 ? value.Substring(0, plus) : value;
        }
    }
}
