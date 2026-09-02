using System;
using System.Windows;
using EventLogViewer.Wpf.ViewModels;

namespace EventLogViewer.Wpf.Views
{
    public partial class PresetEditorWindow : Window
    {
        public PresetEditorWindow()
        {
            InitializeComponent();
            SizeToOwner();
        }

        /// <summary>Keeps the dialog inside the desktop it opens on, same reasoning as MainWindow.</summary>
        private void SizeToOwner()
        {
            var available = SystemParameters.WorkArea;
            Width = Math.Min(900, Math.Max(MinWidth, available.Width - 80));
            Height = Math.Min(620, Math.Max(MinHeight, available.Height - 80));
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is PresetEditorViewModel vm)) return;
            if (!vm.Save()) return;      // the view model has already explained why

            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
