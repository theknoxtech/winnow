using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EventLogViewer.Core.Export;
using EventLogViewer.Core.Query;
using Xunit;

namespace EventLogViewer.Tests
{
    public class CsvExporterTests
    {
        private static EventRow Row(string message = "hello", string provider = "Test", int id = 1) =>
            new EventRow
            {
                TimeCreated = new DateTime(2024, 5, 6, 7, 8, 9),
                Level = "Error",
                ProviderName = provider,
                Id = id,
                Message = message
            };

        private static string[] Lines(string csv) =>
            csv.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

        [Fact]
        public void WritesHeaderEvenWithNoRows()
        {
            Assert.Equal("TimeCreated,Level,ProviderName,Id,Message",
                Lines(CsvExporter.ToCsv(new EventRow[0])).Single());
        }

        [Fact]
        public void WritesOneLinePerRow()
        {
            var csv = CsvExporter.ToCsv(new[] { Row(), Row() });
            Assert.Equal(3, Lines(csv).Length);   // header + 2
        }

        [Fact]
        public void FormatsTimestampSortably()
        {
            Assert.Contains("2024-05-06 07:08:09", CsvExporter.ToCsv(new[] { Row() }));
        }

        [Fact]
        public void QuotesFieldsContainingCommas()
        {
            Assert.Contains("\"Faulting application, version 1.0\"",
                CsvExporter.ToCsv(new[] { Row("Faulting application, version 1.0") }));
        }

        [Fact]
        public void DoublesEmbeddedQuotes()
        {
            Assert.Contains("\"He said \"\"boom\"\"\"",
                CsvExporter.ToCsv(new[] { Row("He said \"boom\"") }));
        }

        [Fact]
        public void QuotesFieldsContainingNewlines()
        {
            // Event messages are routinely multi-line; an unquoted newline would split the record.
            var csv = CsvExporter.ToCsv(new[] { Row("line one\r\nline two") });

            Assert.Contains("\"line one\r\nline two\"", csv);
            // Header + one record, even though the message itself spans two lines.
            Assert.Equal(3, csv.Split(new[] { "\r\n" }, StringSplitOptions.None).Length - 1);
        }

        [Fact]
        public void QuotesFieldsWithSignificantWhitespace()
        {
            Assert.Contains("\" leading\"", CsvExporter.ToCsv(new[] { Row(" leading") }));
        }

        [Fact]
        public void LeavesPlainFieldsUnquoted()
        {
            Assert.Contains(",Error,Test,1,plain", CsvExporter.ToCsv(new[] { Row("plain") }));
        }

        [Fact]
        public void HandlesNullMessage()
        {
            var csv = CsvExporter.ToCsv(new[] { Row(null) });
            Assert.EndsWith(",", Lines(csv)[1]);
        }

        [Theory]
        [InlineData("a,b", "\"a,b\"")]
        [InlineData("a\"b", "\"a\"\"b\"")]
        [InlineData("a\nb", "\"a\nb\"")]
        [InlineData("plain", "plain")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void EscapeHandlesEachCase(string input, string expected)
        {
            Assert.Equal(expected, CsvExporter.Escape(input));
        }

        [Fact]
        public void RoundTripsThroughAFileWithBom()
        {
            var path = Path.Combine(Path.GetTempPath(), "elv-csv-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                CsvExporter.Write(path, new[] { Row("with, comma") });

                var bytes = File.ReadAllBytes(path);
                // UTF-8 BOM, without which Excel misreads non-ASCII text in event messages.
                Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
                Assert.Contains("\"with, comma\"", File.ReadAllText(path, Encoding.UTF8));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void DefaultFileNameMatchesTheOldNamingScheme()
        {
            var name = CsvExporter.DefaultFileName();
            Assert.StartsWith("EventLog_", name);
            Assert.EndsWith(".csv", name);
            Assert.Equal("EventLog_yyyyMMdd_HHmmss.csv".Length, name.Length);
        }
    }
}
