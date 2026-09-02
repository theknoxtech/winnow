using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Winnow.Core.Hosting;
using Winnow.Core.Presets;
using Winnow.Core.Query;
using Winnow.App;
using Winnow.App.ViewModels;
using Xunit;

namespace Winnow.Tests
{
    /// <summary>Records what the view model asked the view to do, and answers however the test wants.</summary>
    internal sealed class StubUserInteraction : IUserInteraction
    {
        public List<string> Errors { get; } = new List<string>();
        public List<string> Infos { get; } = new List<string>();
        public List<string> Clipboard { get; } = new List<string>();
        public List<string> OpenedUrls { get; } = new List<string>();

        public bool ConfirmResult { get; set; } = true;
        public int ConfirmCount { get; private set; }

        public string SaveFileResult { get; set; }
        public bool SaveDialogThrows { get; set; }
        public int SaveDialogCount { get; private set; }

        public void ShowError(string title, string message) => Errors.Add(title + ": " + message);
        public void ShowInfo(string title, string message) => Infos.Add(title + ": " + message);

        public bool Confirm(string title, string message)
        {
            ConfirmCount++;
            return ConfirmResult;
        }

        public string PromptSaveFile(string defaultFileName, string filter)
        {
            SaveDialogCount++;
            if (SaveDialogThrows) throw new InvalidOperationException("no shell on this desktop");
            return SaveFileResult;
        }

        public string PromptOpenFile(string filter) => SaveFileResult;
        public void CopyToClipboard(string text) => Clipboard.Add(text);
        public void OpenUrl(string url) => OpenedUrls.Add(url);
        public void RevealInExplorer(string path) { }
        public bool ShowPresetEditor(object viewModel) => false;
    }

    /// <summary>
    /// Drives the real view model.
    /// </summary>
    /// <remarks>
    /// Runs each case on an STA thread because the view model builds an ICollectionView over its
    /// results, and WPF collection views are thread-affine. Without this the tests fail on
    /// Refresh() rather than on anything to do with the behaviour under test.
    /// </remarks>
    public class MainViewModelTests
    {
        /// <summary>
        /// Runs an async test body on an STA thread with a real WPF dispatcher pumping.
        /// </summary>
        /// <remarks>
        /// Both halves matter. STA plus a dispatcher is needed because the view model builds an
        /// ICollectionView, which is thread-affine. A running dispatcher is needed because the
        /// view model awaits with ConfigureAwait(true): without a DispatcherSynchronizationContext
        /// the continuation resumes on a thread-pool thread and touches the collection view from
        /// the wrong thread. Pumping a real dispatcher makes the test resume exactly where the
        /// application would.
        /// </remarks>
        private static void OnStaThread(Func<Task> body)
        {
            Exception failure = null;

            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(dispatcher));

                dispatcher.InvokeAsync(async () =>
                {
                    try { await body(); }
                    catch (Exception ex) { failure = ex; }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
                });

                Dispatcher.Run();
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!thread.Join(TimeSpan.FromSeconds(30)))
                throw new Xunit.Sdk.XunitException("Test body did not complete within 30 seconds.");

            if (failure != null)
                throw new Xunit.Sdk.XunitException(failure.ToString());
        }

        private static readonly DateTime T0 = new DateTime(2024, 1, 1, 12, 0, 0);

        private static EventRow Row(string log, long id, string message = "msg", string provider = "P") =>
            new EventRow
            {
                LogName = log,
                RecordId = id,
                TimeCreated = T0.AddMinutes(-id),
                Message = message,
                ProviderName = provider,
                Id = 1000,
                Level = "Error"
            };

        private static HostEnvironment Interactive() => HostEnvironment.Detect();

        private static MainViewModel Build(IEventLogReader reader, StubUserInteraction ui,
                                           PresetStore store = null, HostEnvironment host = null)
        {
            return new MainViewModel(
                new EventLogService(reader),
                store ?? PresetStore.LoadDefaults(),
                host ?? Interactive(),
                ui);
        }

        [Fact]
        public void LoadsAllPresetsForTheStrip()
        {
            OnStaThread(async () =>
            {
                var vm = Build(new FakeEventLogReader(), new StubUserInteraction());
                Assert.Equal(36, vm.Presets.Count);
            });
        }

        [Fact]
        public void ASearchPopulatesTheGridAndTheCount()
        {
            OnStaThread(async () =>
            {
                var reader = new FakeEventLogReader()
                    .WithLog("Application", Row("Application", 1), Row("Application", 2));
                var vm = Build(reader, new StubUserInteraction());
                vm.SelectedLogSource = "Application";

                await vm.SearchAsync();

                Assert.Equal(2, vm.Rows.Count);
                Assert.Equal("2 record(s)", vm.CountText);
                Assert.Equal("Done", vm.StatusText);
                Assert.False(vm.IsSearching);
            });
        }

        [Fact]
        public void AnEmptyLogNameIsRejectedBeforeQuerying()
        {
            OnStaThread(async () =>
            {
                var ui = new StubUserInteraction();
                var vm = Build(new FakeEventLogReader(), ui);
                vm.SelectedLogSource = "   ";

                await vm.SearchAsync();

                Assert.Single(ui.Errors);
                Assert.Contains("Log Source", ui.Errors[0]);
            });
        }

        [Fact]
        public void AMalformedEventIdIsReportedNotSwallowed()
        {
            OnStaThread(async () =>
            {
                var ui = new StubUserInteraction();
                var vm = Build(new FakeEventLogReader(), ui);
                vm.SelectedLogSource = "Application";
                vm.EventIdText = "1000,abc";

                await vm.SearchAsync();

                Assert.Single(ui.Errors);
                Assert.Contains("Invalid Event ID", ui.Errors[0]);
                Assert.Empty(vm.Rows);
            });
        }

        [Fact]
        public void ClickingAPresetRunsItAndReflectsItsLog()
        {
            OnStaThread(async () =>
            {
                var reader = new FakeEventLogReader().WithLog("System", Row("System", 1));
                var vm = Build(reader, new StubUserInteraction());
                var preset = vm.Presets.Single(p => p.Id == "system.service-changes");

                await vm.PresetSearchAsync(preset);

                Assert.Equal("System", vm.SelectedLogSource);
                Assert.Single(vm.Rows);
            });
        }

        [Fact]
        public void AMissingLogReportsWhyRatherThanJustZero()
        {
            OnStaThread(async () =>
            {
                var reader = new FakeEventLogReader().WithMissingLog("Directory Service");
                var vm = Build(reader, new StubUserInteraction());
                var preset = vm.Presets.Single(p => p.Id == "ad.replication");

                await vm.PresetSearchAsync(preset);

                Assert.Empty(vm.Rows);
                Assert.Contains("not present on this machine", vm.StatusText);
                Assert.Contains("Directory Service", vm.StatusText);
            });
        }

        [Fact]
        public void AccessDeniedBecomesTheElevationMessage()
        {
            OnStaThread(async () =>
            {
                var reader = new FakeEventLogReader().WithDeniedLog("Security");
                var vm = Build(reader, new StubUserInteraction { ConfirmResult = true });
                var ui = new StubUserInteraction();
                vm = Build(reader, ui);
                vm.SelectedLogSource = "Security";

                await vm.SearchAsync();

                Assert.Single(ui.Errors);
                Assert.Contains("Administrator", ui.Errors[0]);
            });
        }

        [Fact]
        public void TheLiveFilterNarrowsWithoutRequerying()
        {
            OnStaThread(async () =>
            {
                var reader = new FakeEventLogReader().WithLog("Application",
                    Row("Application", 1, "disk failure"),
                    Row("Application", 2, "all good"));
                var vm = Build(reader, new StubUserInteraction());
                vm.SelectedLogSource = "Application";
                await vm.SearchAsync();

                vm.LiveFilter = "disk";

                // The underlying collection is untouched; only the view is filtered.
                Assert.Equal(2, vm.Rows.Count);
                Assert.Single(vm.RowsView.Cast<EventRow>());
                Assert.Equal("1 shown", vm.LiveCountText);
            });
        }

        [Fact]
        public void TheLiveFilterMatchesProviderAndEventId()
        {
            OnStaThread(async () =>
            {
                var reader = new FakeEventLogReader().WithLog("Application",
                    Row("Application", 1, "x", "Microsoft-Windows-Kernel-Power"),
                    Row("Application", 2, "y", "Something Else"));
                var vm = Build(reader, new StubUserInteraction());
                vm.SelectedLogSource = "Application";
                await vm.SearchAsync();

                vm.LiveFilter = "kernel";
                Assert.Single(vm.RowsView.Cast<EventRow>());

                vm.LiveFilter = "1000";           // the Event ID both rows share
                Assert.Equal(2, vm.RowsView.Cast<EventRow>().Count());
            });
        }

        [Fact]
        public void AnApostropheInTheLiveFilterIsHarmless()
        {
            // The WinForms version built a DataView RowFilter string, where an unescaped quote
            // produced a malformed expression. A predicate has no syntax to break.
            OnStaThread(async () =>
            {
                var reader = new FakeEventLogReader().WithLog("Application", Row("Application", 1, "Bob's file"));
                var vm = Build(reader, new StubUserInteraction());
                vm.SelectedLogSource = "Application";
                await vm.SearchAsync();

                vm.LiveFilter = "Bob's";

                Assert.Single(vm.RowsView.Cast<EventRow>());
            });
        }

        [Fact]
        public void SelectingARowShowsItsFullMessage()
        {
            OnStaThread(async () =>
            {
                var reader = new FakeEventLogReader().WithLog("Application",
                    Row("Application", 1, new string('x', 5000)));
                var vm = Build(reader, new StubUserInteraction());
                vm.SelectedLogSource = "Application";
                await vm.SearchAsync();

                vm.SelectedRow = vm.Rows[0];

                // Not truncated to 200 characters as the old grid did.
                Assert.Equal(5000, vm.DetailText.Length);
            });
        }

        [Fact]
        public void ClearResetsEverything()
        {
            OnStaThread(async () =>
            {
                var reader = new FakeEventLogReader().WithLog("Application", Row("Application", 1));
                var vm = Build(reader, new StubUserInteraction());
                vm.SelectedLogSource = "Application";
                vm.Keyword = "kw";
                vm.EventIdText = "1000";
                await vm.SearchAsync();

                vm.Clear();

                Assert.Empty(vm.Rows);
                Assert.Equal("", vm.Keyword);
                Assert.Equal("", vm.EventIdText);
                Assert.Equal("Ready", vm.StatusText);
                Assert.Equal("Application", vm.SelectedLogSource);   // back to the first source
            });
        }

        [Fact]
        public void ExportCannotRunWithNoResults()
        {
            OnStaThread(async () =>
            {
                var vm = Build(new FakeEventLogReader(), new StubUserInteraction());
                Assert.False(vm.ExportCommand.CanExecute(null));
            });
        }

        [Theory]
        [InlineData("1000", new[] { 1000 })]
        [InlineData("7045,7036", new[] { 7045, 7036 })]
        [InlineData(" 7045 , 7036 ", new[] { 7045, 7036 })]
        [InlineData("1,", new[] { 1 })]
        public void EventIdParsingAcceptsTheDocumentedForms(string input, int[] expected)
        {
            Assert.True(MainViewModel.TryParseIds(input, out var ids));
            Assert.Equal(expected, ids);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("1000,abc")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("1.5")]
        public void EventIdParsingRejectsTheRest(string input)
        {
            Assert.False(MainViewModel.TryParseIds(input, out _));
        }
    }
}
