using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using EventLogViewer.Core.Hosting;
using EventLogViewer.Core.Presets;
using EventLogViewer.Core.Query;
using EventLogViewer.Wpf.ViewModels;
using Xunit;

namespace EventLogViewer.Tests
{
    /// <summary>
    /// The behaviours that differ under ScreenConnect Backstage.
    /// </summary>
    /// <remarks>
    /// These are the promises of the rewrite that are hardest to check by hand - verifying them
    /// for real needs a Backstage session against a customer machine. Pinning them here means the
    /// manual session is confirming a decision that is already known to be wired up, rather than
    /// discovering it was never wired up at all.
    /// </remarks>
    public class BackstageBehaviourTests
    {
        private static HostEnvironment Backstage() =>
            HostEnvironment.CreateForTesting(isSystemAccount: true, isElevated: true,
                                             isAlternateDesktop: true, desktopName: "Backstage");

        private static HostEnvironment Interactive() =>
            HostEnvironment.CreateForTesting(isSystemAccount: false, isElevated: false,
                                             isAlternateDesktop: false, desktopName: "Default",
                                             userName: "CONTOSO\\jdoe");

        private static HostEnvironment ElevatedInteractive() =>
            HostEnvironment.CreateForTesting(isSystemAccount: false, isElevated: true,
                                             isAlternateDesktop: false, desktopName: "Default",
                                             userName: "CONTOSO\\jdoe");

        private static void OnStaThread(Func<Task> body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
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
            if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        }

        private static EventRow Row(long id) => new EventRow
        {
            LogName = "Application",
            RecordId = id,
            TimeCreated = new DateTime(2024, 1, 1, 12, 0, 0).AddMinutes(-id),
            Message = "msg " + id,
            ProviderName = "P",
            Id = 1000,
            Level = "Error"
        };

        private static MainViewModel Build(StubUserInteraction ui, HostEnvironment host)
        {
            var reader = new FakeEventLogReader().WithLog("Application", Row(1), Row(2));
            return new MainViewModel(new EventLogService(reader), PresetStore.LoadDefaults(), host, ui);
        }

        #region Detection

        [Fact]
        public void SystemAccountAloneIsEnoughToTreatAsBackstage()
        {
            // A SYSTEM process has the browser and profile problems regardless of which desktop
            // it is on.
            var env = HostEnvironment.CreateForTesting(true, true, isAlternateDesktop: false);
            Assert.True(env.IsBackstageLikely);
        }

        [Fact]
        public void AnAlternateDesktopAloneIsEnoughToTreatAsBackstage()
        {
            // An alternate desktop has the shell-dialog problems regardless of which account it runs as.
            var env = HostEnvironment.CreateForTesting(false, false, isAlternateDesktop: true, desktopName: "Winlogon");
            Assert.True(env.IsBackstageLikely);
        }

        [Fact]
        public void AnOrdinaryInteractiveSessionIsNotBackstage()
        {
            Assert.False(Interactive().IsBackstageLikely);
        }

        [Fact]
        public void DescribeNamesTheModeForTheStatusBar()
        {
            Assert.Equal("SYSTEM · Backstage desktop", Backstage().Describe());
            Assert.Equal("CONTOSO\\jdoe", Interactive().Describe());
            Assert.Equal("CONTOSO\\jdoe (elevated)", ElevatedInteractive().Describe());
        }

        [Fact]
        public void TheFallbackExportDirectoryIsUnderTheWindowsTempPath()
        {
            var dir = Backstage().FallbackExportDirectory;

            Assert.EndsWith("EventLogViewer", dir);
            Assert.True(Path.IsPathRooted(dir));
        }

        #endregion

        #region Export

        [Fact]
        public void InBackstage_ExportSkipsTheShellDialogEntirely()
        {
            OnStaThread(async () =>
            {
                var ui = new StubUserInteraction();
                var vm = Build(ui, Backstage());
                vm.SelectedLogSource = "Application";
                await vm.SearchAsync();

                vm.Export();

                // The shell save dialog is unreliable on an alternate desktop under SYSTEM, so it
                // must not even be attempted.
                Assert.Equal(0, ui.SaveDialogCount);
                Assert.Single(ui.Infos);
                Assert.Contains("Export Complete", ui.Infos[0]);

                CleanUpExports(vm);
            });
        }

        [Fact]
        public void InBackstage_ExportWritesAFileAndReportsItsPath()
        {
            OnStaThread(async () =>
            {
                var ui = new StubUserInteraction();
                var vm = Build(ui, Backstage());
                vm.SelectedLogSource = "Application";
                await vm.SearchAsync();

                vm.Export();

                var path = ui.Clipboard.Single();
                Assert.True(File.Exists(path), "export file was not written to " + path);
                Assert.Contains(path, ui.Infos[0]);

                // The path is copied to the clipboard so it can be pasted into a file transfer.
                Assert.StartsWith(vm.Host.FallbackExportDirectory, path);

                var csv = File.ReadAllText(path);
                Assert.Contains("TimeCreated,Level,ProviderName,Id,Message", csv);
                Assert.Contains("msg 1", csv);

                CleanUpExports(vm);
            });
        }

        [Fact]
        public void Interactive_ExportUsesTheSaveDialog()
        {
            OnStaThread(async () =>
            {
                var target = Path.Combine(Path.GetTempPath(), "elv-" + Guid.NewGuid().ToString("N") + ".csv");
                var ui = new StubUserInteraction { SaveFileResult = target };
                var vm = Build(ui, Interactive());
                vm.SelectedLogSource = "Application";
                await vm.SearchAsync();

                vm.Export();

                Assert.Equal(1, ui.SaveDialogCount);
                Assert.True(File.Exists(target));
                try { File.Delete(target); } catch { }
            });
        }

        [Fact]
        public void Interactive_CancellingTheSaveDialogWritesNothing()
        {
            OnStaThread(async () =>
            {
                var ui = new StubUserInteraction { SaveFileResult = null };   // cancelled
                var vm = Build(ui, Interactive());
                vm.SelectedLogSource = "Application";
                await vm.SearchAsync();

                vm.Export();

                Assert.Equal(1, ui.SaveDialogCount);
                Assert.Empty(ui.Infos);
                Assert.Empty(ui.Errors);
            });
        }

        [Fact]
        public void Interactive_AFailingSaveDialogFallsBackRatherThanLosingTheExport()
        {
            // Covers a shell dialog that throws even though the environment did not look like
            // Backstage - the export still lands somewhere and the user is told where.
            OnStaThread(async () =>
            {
                var ui = new StubUserInteraction { SaveDialogThrows = true };
                var vm = Build(ui, Interactive());
                vm.SelectedLogSource = "Application";
                await vm.SearchAsync();

                vm.Export();

                Assert.Equal(1, ui.SaveDialogCount);
                var path = ui.Clipboard.Single();
                Assert.True(File.Exists(path));

                CleanUpExports(vm);
            });
        }

        [Fact]
        public void ExportWritesWhatTheLiveFilterShowsNotEverythingLoaded()
        {
            OnStaThread(async () =>
            {
                var ui = new StubUserInteraction();
                var vm = Build(ui, Backstage());
                vm.SelectedLogSource = "Application";
                await vm.SearchAsync();

                vm.LiveFilter = "msg 1";
                vm.Export();

                var csv = File.ReadAllText(ui.Clipboard.Single());
                Assert.Contains("msg 1", csv);
                Assert.DoesNotContain("msg 2", csv);

                CleanUpExports(vm);
            });
        }

        private static void CleanUpExports(MainViewModel vm)
        {
            try { Directory.Delete(vm.Host.FallbackExportDirectory, true); } catch { }
        }

        #endregion

        #region Elevation prompts

        [Fact]
        public void InBackstage_TheSecurityLogPromptIsSuppressed()
        {
            OnStaThread(async () =>
            {
                var ui = new StubUserInteraction();
                var reader = new FakeEventLogReader().WithLog("Security", Row(1));
                var vm = new MainViewModel(new EventLogService(reader), PresetStore.LoadDefaults(),
                                           Backstage(), ui);
                vm.SelectedLogSource = "Security";

                await vm.SearchAsync();

                // Already SYSTEM - the prompt would be noise, and awkward to dismiss on that desktop.
                Assert.Equal(0, ui.ConfirmCount);
                Assert.Single(vm.Rows);
            });
        }

        [Fact]
        public void Unelevated_TheSecurityLogStillPrompts()
        {
            OnStaThread(async () =>
            {
                var ui = new StubUserInteraction { ConfirmResult = false };
                var reader = new FakeEventLogReader().WithLog("Security", Row(1));
                var vm = new MainViewModel(new EventLogService(reader), PresetStore.LoadDefaults(),
                                           Interactive(), ui);
                vm.SelectedLogSource = "Security";

                await vm.SearchAsync();

                Assert.Equal(1, ui.ConfirmCount);
                Assert.Empty(vm.Rows);      // declined, so nothing ran
            });
        }

        [Fact]
        public void ElevatedInteractive_DoesNotPrompt()
        {
            OnStaThread(async () =>
            {
                var ui = new StubUserInteraction();
                var reader = new FakeEventLogReader().WithLog("Security", Row(1));
                var vm = new MainViewModel(new EventLogService(reader), PresetStore.LoadDefaults(),
                                           ElevatedInteractive(), ui);
                vm.SelectedLogSource = "Security";

                await vm.SearchAsync();

                Assert.Equal(0, ui.ConfirmCount);
            });
        }

        [Fact]
        public void ASecurityPresetPromptsTheSameWayAManualSearchDoes()
        {
            OnStaThread(async () =>
            {
                var ui = new StubUserInteraction { ConfirmResult = false };
                var reader = new FakeEventLogReader().WithLog("Security", Row(1));
                var vm = new MainViewModel(new EventLogService(reader), PresetStore.LoadDefaults(),
                                           Interactive(), ui);

                await vm.PresetSearchAsync(vm.Presets.Single(p => p.Id == "account.logon-events"));

                Assert.Equal(1, ui.ConfirmCount);
                Assert.Empty(vm.Rows);
            });
        }

        [Fact]
        public void ANonSecurityPresetNeverPrompts()
        {
            OnStaThread(async () =>
            {
                var ui = new StubUserInteraction();
                var vm = Build(ui, Interactive());

                await vm.PresetSearchAsync(vm.Presets.Single(p => p.Id == "apphealth.app-crashes"));

                Assert.Equal(0, ui.ConfirmCount);
            });
        }

        #endregion

        #region Preset warning surfacing

        [Fact]
        public void ABrokenPresetFileIsSurfacedInTheUiRatherThanIgnored()
        {
            OnStaThread(async () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "elv-bad-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(path, "{ not json");

                try
                {
                    var store = PresetStore.Load(path);
                    var vm = new MainViewModel(new EventLogService(new FakeEventLogReader()),
                                               store, Interactive(), new StubUserInteraction());

                    // The app still works, on built-in presets, and says so.
                    Assert.Equal(36, vm.Presets.Count);
                    Assert.NotNull(vm.PresetWarning);
                    Assert.Contains("built-in presets", vm.PresetWarning);
                }
                finally
                {
                    try { File.Delete(path); } catch { }
                }
            });
        }

        #endregion
    }
}
