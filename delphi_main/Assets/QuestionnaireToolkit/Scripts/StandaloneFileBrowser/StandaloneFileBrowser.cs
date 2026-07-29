using System;

namespace QuestionnaireToolkit.Scripts.StandaloneFileBrowser {
    public struct ExtensionFilter {
        public string Name;
        public string[] Extensions;

        public ExtensionFilter(string filterName, params string[] filterExtensions) {
            Name = filterName;
            Extensions = filterExtensions;
        }
    }

    public class StandaloneFileBrowser {
        private static IStandaloneFileBrowser _platformWrapper = null;

        static StandaloneFileBrowser() {
            // UNITY_EDITOR checked FIRST, not last: UNITY_STANDALONE_OSX (etc.)
            // is ALSO defined inside the Editor whenever the active build
            // target is that standalone platform, so the old #elif ordering
            // picked the native macOS plugin wrapper even in-Editor. That
            // native .bundle is x86_64-only and can't load into an arm64
            // (Apple Silicon) Editor process — the dialog silently failed to
            // open. The Editor wrapper below uses managed
            // UnityEditor.EditorUtility.OpenFilePanel instead, no native
            // plugin involved, so it works regardless of CPU architecture.
            // Real standalone BUILDS never define UNITY_EDITOR, so they still
            // correctly fall through to the native per-platform wrappers.
            //
            // There is deliberately NO Windows branch. The Windows wrapper
            // needs Ookii.Dialogs and a .NET Framework 2.0 System.Windows.Forms
            // whose simple name collides with the 4.0 one Unity's own Mono
            // profile ships — and that duplicate name crashed Unity 6's
            // assembly lifecycle sort on every domain reload. Both plugins are
            // now disabled on all platforms and the wrapper is compiled out;
            // see the header of StandaloneFileBrowserWindows for the details.
            // A Windows build therefore has no wrapper, and every entry point
            // below degrades to a warning instead of a NullReferenceException.
#if UNITY_EDITOR
            _platformWrapper = new StandaloneFileBrowserEditor();
#elif UNITY_STANDALONE_OSX
            _platformWrapper = new StandaloneFileBrowserMac();
#elif UNITY_STANDALONE_LINUX
            _platformWrapper = new StandaloneFileBrowserLinux();
#endif
        }

        /// <summary>True when this build has no native dialog wrapper, logging
        /// why on the way. Import/export are authoring-time features, so a
        /// build that reaches them should report it plainly and carry on
        /// rather than throw in the participant's face.</summary>
        private static bool NoDialogs {
            get {
                if (_platformWrapper != null) return false;
                UnityEngine.Debug.LogWarning(
                    "[StandaloneFileBrowser] Native file dialogs are unavailable in this build. " +
                    "Import/export questionnaires from the Unity Editor instead.");
                return true;
            }
        }

        /// <summary>
        /// Native open file dialog
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="directory">Root directory</param>
        /// <param name="extension">Allowed extension</param>
        /// <param name="multiselect">Allow multiple file selection</param>
        /// <returns>Returns array of chosen paths. Zero length array when cancelled</returns>
        public static string[] OpenFilePanel(string title, string directory, string extension, bool multiselect) {
            var extensions = string.IsNullOrEmpty(extension) ? null : new [] { new ExtensionFilter("", extension) };
            return OpenFilePanel(title, directory, extensions, multiselect);
        }

        /// <summary>
        /// Native open file dialog
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="directory">Root directory</param>
        /// <param name="extensions">List of extension filters. Filter Example: new ExtensionFilter("Image Files", "jpg", "png")</param>
        /// <param name="multiselect">Allow multiple file selection</param>
        /// <returns>Returns array of chosen paths. Zero length array when cancelled</returns>
        public static string[] OpenFilePanel(string title, string directory, ExtensionFilter[] extensions, bool multiselect) {
            if (NoDialogs) return new string[0];
            return _platformWrapper.OpenFilePanel(title, directory, extensions, multiselect);
        }

        /// <summary>
        /// Native open file dialog async
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="directory">Root directory</param>
        /// <param name="extension">Allowed extension</param>
        /// <param name="multiselect">Allow multiple file selection</param>
        /// <param name="cb">Callback")</param>
        public static void OpenFilePanelAsync(string title, string directory, string extension, bool multiselect, Action<string[]> cb) {
            var extensions = string.IsNullOrEmpty(extension) ? null : new [] { new ExtensionFilter("", extension) };
            OpenFilePanelAsync(title, directory, extensions, multiselect, cb);
        }

        /// <summary>
        /// Native open file dialog async
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="directory">Root directory</param>
        /// <param name="extensions">List of extension filters. Filter Example: new ExtensionFilter("Image Files", "jpg", "png")</param>
        /// <param name="multiselect">Allow multiple file selection</param>
        /// <param name="cb">Callback")</param>
        public static void OpenFilePanelAsync(string title, string directory, ExtensionFilter[] extensions, bool multiselect, Action<string[]> cb) {
            if (NoDialogs) { cb?.Invoke(new string[0]); return; }
            _platformWrapper.OpenFilePanelAsync(title, directory, extensions, multiselect, cb);
        }

        /// <summary>
        /// Native open folder dialog
        /// NOTE: Multiple folder selection doesn't supported on Windows
        /// </summary>
        /// <param name="title"></param>
        /// <param name="directory">Root directory</param>
        /// <param name="multiselect"></param>
        /// <returns>Returns array of chosen paths. Zero length array when cancelled</returns>
        public static string[] OpenFolderPanel(string title, string directory, bool multiselect) {
            if (NoDialogs) return new string[0];
            return _platformWrapper.OpenFolderPanel(title, directory, multiselect);
        }

        /// <summary>
        /// Native open folder dialog async
        /// NOTE: Multiple folder selection doesn't supported on Windows
        /// </summary>
        /// <param name="title"></param>
        /// <param name="directory">Root directory</param>
        /// <param name="multiselect"></param>
        /// <param name="cb">Callback")</param>
        public static void OpenFolderPanelAsync(string title, string directory, bool multiselect, Action<string[]> cb) {
            if (NoDialogs) { cb?.Invoke(new string[0]); return; }
            _platformWrapper.OpenFolderPanelAsync(title, directory, multiselect, cb);
        }

        /// <summary>
        /// Native save file dialog
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="directory">Root directory</param>
        /// <param name="defaultName">Default file name</param>
        /// <param name="extension">File extension</param>
        /// <returns>Returns chosen path. Empty string when cancelled</returns>
        public static string SaveFilePanel(string title, string directory, string defaultName , string extension) {
            var extensions = string.IsNullOrEmpty(extension) ? null : new [] { new ExtensionFilter("", extension) };
            return SaveFilePanel(title, directory, defaultName, extensions);
        }

        /// <summary>
        /// Native save file dialog
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="directory">Root directory</param>
        /// <param name="defaultName">Default file name</param>
        /// <param name="extensions">List of extension filters. Filter Example: new ExtensionFilter("Image Files", "jpg", "png")</param>
        /// <returns>Returns chosen path. Empty string when cancelled</returns>
        public static string SaveFilePanel(string title, string directory, string defaultName, ExtensionFilter[] extensions) {
            if (NoDialogs) return "";
            return _platformWrapper.SaveFilePanel(title, directory, defaultName, extensions);
        }

        /// <summary>
        /// Native save file dialog async
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="directory">Root directory</param>
        /// <param name="defaultName">Default file name</param>
        /// <param name="extension">File extension</param>
        /// <param name="cb">Callback")</param>
        public static void SaveFilePanelAsync(string title, string directory, string defaultName , string extension, Action<string> cb) {
            var extensions = string.IsNullOrEmpty(extension) ? null : new [] { new ExtensionFilter("", extension) };
            SaveFilePanelAsync(title, directory, defaultName, extensions, cb);
        }

        /// <summary>
        /// Native save file dialog async
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="directory">Root directory</param>
        /// <param name="defaultName">Default file name</param>
        /// <param name="extensions">List of extension filters. Filter Example: new ExtensionFilter("Image Files", "jpg", "png")</param>
        /// <param name="cb">Callback")</param>
        public static void SaveFilePanelAsync(string title, string directory, string defaultName, ExtensionFilter[] extensions, Action<string> cb) {
            if (NoDialogs) { cb?.Invoke(""); return; }
            _platformWrapper.SaveFilePanelAsync(title, directory, defaultName, extensions, cb);
        }
    }
}