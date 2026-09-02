using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Diagnostics;
using EventLogViewer.Core.Hosting;
using EventLogViewer.Core.Presets;
using EventLogViewer.Core.Query;
using EventLogViewer.Wpf.ViewModels;
using EventLogViewer.Wpf.Views;

namespace EventLogViewer.Wpf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // An unhandled exception on a Backstage desktop would otherwise die silently or behind
            // a dialog nobody sees, leaving the technician with a window that just vanished.
            DispatcherUnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                ReportFatal(args.ExceptionObject as Exception);

            var options = CommandLineOptions.Parse(e.Args);

            if (options.TraceBindings) EnableBindingTrace();

            if (options.ShowHelp)
            {
                MessageBox.Show(CommandLineOptions.HelpText, "Windows Event Log Viewer",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            var host = HostEnvironment.Detect();
            var store = PresetStore.Load(PresetStore.ResolveSideCarPath(options.PresetsPath));
            var service = EventLogService.CreateDefault();

            var window = new MainWindow();
            var viewModel = new MainViewModel(service, store, host, window);
            window.DataContext = viewModel;

            MainWindow = window;
            window.Show();

            // Deferred so it can never delay the window appearing, and awaited nowhere - a failed
            // check is a non-event by design.
            _ = viewModel.CheckForUpdateAsync();
        }

        /// <summary>
        /// Routes WPF data-binding warnings to bindings.log next to the executable.
        /// </summary>
        /// <remarks>
        /// Attached in code rather than through app.config because WPF initialises the
        /// System.Windows.Data trace source before a config-declared listener gets a chance to
        /// attach, so the config route silently produces nothing. A broken binding otherwise fails
        /// invisibly - the control simply shows nothing - which is painful to diagnose over a
        /// remote session, hence keeping this as a supported flag rather than a debug-only hack.
        /// </remarks>
        private static void EnableBindingTrace()
        {
            try
            {
                var path = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? ".") ?? ".",
                    "bindings.log");

                var listener = new TextWriterTraceListener(path) { TraceOutputOptions = TraceOptions.None };

                PresentationTraceSources.Refresh();
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

                System.Diagnostics.Trace.AutoFlush = true;

                // Written immediately so the file always exists while tracing is on. Otherwise the
                // listener creates it lazily on first warning, and "no file" is indistinguishable
                // from "tracing never started" - which makes a clean run unprovable.
                listener.WriteLine("Binding trace started " +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - warnings follow, if any.");
                listener.Flush();
            }
            catch
            {
                // Diagnostics must never be the reason the app fails to start.
            }
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            ReportFatal(e.Exception);
        }

        private static void ReportFatal(Exception ex)
        {
            try
            {
                MessageBox.Show(
                    "The Event Log Viewer hit an unexpected error.\n\n" +
                    (ex?.Message ?? "Unknown error") +
                    "\n\n" + (ex?.StackTrace ?? ""),
                    "Unexpected Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // Nothing left to do if even the message box fails.
            }
        }
    }

    /// <summary>Command line handling. Deliberately tiny - one option and a help flag.</summary>
    public sealed class CommandLineOptions
    {
        public string PresetsPath { get; private set; }
        public bool ShowHelp { get; private set; }
        public bool TraceBindings { get; private set; }

        public const string HelpText =
            "Windows Event Log Viewer\n\n" +
            "Usage: EventLogViewer.exe [--presets <path>] [--trace-bindings]\n\n" +
            "  --presets <path>   Load preset overrides from the given presets.json.\n" +
            "                     Defaults to presets.json beside the executable, if present.\n" +
            "  --trace-bindings   Write WPF data-binding warnings to bindings.log, next to the\n" +
            "                     executable. Diagnostics only.\n" +
            "  --help             Show this message.\n\n" +
            "Built-in presets are embedded in the executable; a preset file only needs to contain\n" +
            "the presets it changes, adds, or disables.";

        public static CommandLineOptions Parse(string[] args)
        {
            var options = new CommandLineOptions();
            if (args == null) return options;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (Is(arg, "--help", "-h", "/?", "-?"))
                {
                    options.ShowHelp = true;
                }
                else if (Is(arg, "--trace-bindings"))
                {
                    options.TraceBindings = true;
                }
                else if (Is(arg, "--presets", "-p") && i + 1 < args.Length)
                {
                    options.PresetsPath = args[++i];
                }
                else if (arg != null && arg.StartsWith("--presets=", StringComparison.OrdinalIgnoreCase))
                {
                    options.PresetsPath = arg.Substring("--presets=".Length);
                }
            }

            return options;
        }

        private static bool Is(string arg, params string[] names) =>
            names.Any(n => string.Equals(arg, n, StringComparison.OrdinalIgnoreCase));
    }
}
