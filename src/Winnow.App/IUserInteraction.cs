namespace Winnow.App
{
    /// <summary>
    /// The view's side of anything the view model needs from the user or the shell.
    /// </summary>
    /// <remarks>
    /// Kept as an interface so the view model holds no direct reference to MessageBox, the save
    /// dialog, the clipboard or Process.Start - all four of which behave differently, or not at
    /// all, on a Backstage desktop. The decision about which to use lives in the view model where
    /// it can be reasoned about; this is only the mechanism.
    /// </remarks>
    public interface IUserInteraction
    {
        void ShowError(string title, string message);
        void ShowInfo(string title, string message);

        /// <summary>Yes/no prompt. Returns true for yes.</summary>
        bool Confirm(string title, string message);

        /// <summary>
        /// Shows a save dialog and returns the chosen path, or null if cancelled.
        /// Throws if the shell dialog cannot run - the caller is expected to fall back.
        /// </summary>
        string PromptSaveFile(string defaultFileName, string filter);

        /// <summary>
        /// Shows an open dialog and returns the chosen existing path, or null if cancelled.
        /// Throws if the shell dialog cannot run.
        /// </summary>
        string PromptOpenFile(string filter);

        void CopyToClipboard(string text);

        /// <summary>Opens a URL in the default browser.</summary>
        void OpenUrl(string url);

        /// <summary>Opens a folder in Explorer, if there is a shell to do it with.</summary>
        void RevealInExplorer(string path);

        /// <summary>Shows the preset editor. Returns true if presets were saved.</summary>
        bool ShowPresetEditor(object viewModel);
    }
}
