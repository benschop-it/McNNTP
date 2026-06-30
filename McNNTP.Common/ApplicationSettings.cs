namespace McNNTP.Common {
    public static class ApplicationSettings {
        private static String _settingsFolder;

        public static string SettingsFolder {
            get {
                if (_settingsFolder == null) {
                    string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _settingsFolder = Path.Combine(localApplicationData, "McNNTP");
                    if (!Directory.Exists(_settingsFolder)) {
                        Directory.CreateDirectory(_settingsFolder);
                    }
                }
                return _settingsFolder;
            }
        }
    }
}
