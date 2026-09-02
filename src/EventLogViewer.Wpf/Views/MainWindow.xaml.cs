using System;
using System.Diagnostics;
using System.Windows;
using EventLogViewer.Core.Hosting;
using EventLogViewer.Wpf.ViewModels;
using Microsoft.Win32;

namespace EventLogViewer.Wpf.Views
{
    public partial class MainWindow : Window, IUserInteraction
    {
        public MainWindow()
        {
            InitializeComponent();
            SizeToWorkArea();
        }

        /// <summary>
        /// Sizes the window against the desktop it is actually on. The arithmetic lives in
        /// <see cref="WindowSizing"/> so it can be tested without a small screen to test on.
        /// </summary>
        private void SizeToWorkArea()
        {
            var available = SystemParameters.WorkArea;
            var size = WindowSizing.Compute(available.Width, available.Height);

            Width = size.Width;
            Height = size.Height;
            MaxWidth = size.MaxWidth;
            MaxHeight = size.MaxHeight;
        }

        #region IUserInteraction

        public void ShowError(string title, string message) =>
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

        public void ShowInfo(string title, string message) =>
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        public bool Confirm(string title, string message) =>
            MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                == MessageBoxResult.Yes;

        public string PromptSaveFile(string defaultFileName, string filter)
        {
            var dialog = new SaveFileDialog { FileName = defaultFileName, Filter = filter };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        public string PromptOpenFile(string filter)
        {
            var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        public void CopyToClipboard(string text)
        {
            try
            {
                Clipboard.SetText(text ?? string.Empty);
            }
            catch (Exception)
            {
                // The clipboard is owned per desktop and can be locked by another process; on an
                // alternate desktop it may not be usable at all. The caller always shows the value
                // on screen too, so failing quietly here still leaves the user something to work with.
            }
        }

        public void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowError("Could not open link", url + "\n\n" + ex.Message);
            }
        }

        public void RevealInExplorer(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception)
            {
                // No shell available (the Backstage case) - the caller has already shown the path.
            }
        }

        public bool ShowPresetEditor(object viewModel)
        {
            var editor = new PresetEditorWindow
            {
                Owner = this,
                DataContext = viewModel
            };
            return editor.ShowDialog() == true;
        }

        #endregion
    }
}
