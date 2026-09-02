using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using EventLogViewer.Core.Query;

namespace EventLogViewer.Core.Export
{
    /// <summary>
    /// Writes result rows as RFC 4180 CSV.
    /// </summary>
    /// <remarks>
    /// Event messages routinely contain commas, quotes and embedded newlines, so the quoting here
    /// is load-bearing - the PowerShell version got this for free from Export-Csv and a
    /// hand-rolled writer must not regress it.
    /// </remarks>
    public static class CsvExporter
    {
        private static readonly string[] Headers = { "TimeCreated", "Level", "ProviderName", "Id", "Message" };

        public static void Write(string path, IEnumerable<EventRow> rows)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("An output path is required.", nameof(path));

            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // UTF-8 with BOM: without it Excel opens the file as the ANSI code page and mangles
            // any non-ASCII text in event messages.
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
                Write(writer, rows);
        }

        public static void Write(TextWriter writer, IEnumerable<EventRow> rows)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));

            writer.Write(string.Join(",", Headers));
            writer.Write("\r\n");

            if (rows == null) return;

            foreach (var row in rows)
            {
                writer.Write(string.Join(",", new[]
                {
                    Escape(row.TimeCreated.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    Escape(row.Level),
                    Escape(row.ProviderName),
                    Escape(row.Id.ToString(CultureInfo.InvariantCulture)),
                    Escape(row.Message)
                }));
                writer.Write("\r\n");
            }
        }

        public static string ToCsv(IEnumerable<EventRow> rows)
        {
            using (var writer = new StringWriter(CultureInfo.InvariantCulture))
            {
                Write(writer, rows);
                return writer.ToString();
            }
        }

        /// <summary>
        /// Quotes a field when it contains a comma, quote, CR or LF, doubling any embedded quote.
        /// A leading space after a comma is also significant to some readers, so fields with
        /// leading or trailing whitespace are quoted too.
        /// </summary>
        internal static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var mustQuote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                            || value != value.Trim();

            if (!mustQuote) return value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>Timestamped default filename, matching the old app's EventLog_yyyyMMdd_HHmmss.csv.</summary>
        public static string DefaultFileName() =>
            "EventLog_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv";
    }
}
