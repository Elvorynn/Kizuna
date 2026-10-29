using Newtonsoft.Json;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Kizuna
{
    /// <summary>
    /// Persists <see cref="AppSettings"/> to a DPAPI-encrypted file under the current
    /// user's Application Data folder (%AppData%\Kizuna\settings.dat).
    /// </summary>
    public static class SettingsStorage
    {
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kizuna",
            "settings.dat");

        /// <summary>True if a settings file is present on disk.</summary>
        public static bool FileExists => File.Exists(SettingsFilePath);

        /// <summary>
        /// Loads and decrypts settings from disk. Returns <c>null</c> if the file does
        /// not exist or cannot be decrypted (e.g. created by a different Windows user).
        /// Retries a few times so a transient read (e.g. mid-write on another thread)
        /// doesn't masquerade as "no settings".
        /// </summary>
        public static AppSettings Load()
        {
            if (!File.Exists(SettingsFilePath))
                return null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    byte[] encrypted  = File.ReadAllBytes(SettingsFilePath);
                    byte[] plain      = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    string json       = Encoding.UTF8.GetString(plain);
                    return JsonConvert.DeserializeObject<AppSettings>(json);
                }
                catch
                {
                    System.Threading.Thread.Sleep(50);
                }
            }
            return null;
        }

        /// <summary>Encrypts and writes <paramref name="settings"/> to disk atomically
        /// (temp file + swap) so a concurrent reader can never observe a half-written,
        /// undecryptable file.</summary>
        public static void Save(AppSettings settings)
        {
            string dir = Path.GetDirectoryName(SettingsFilePath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string json      = JsonConvert.SerializeObject(settings);
            byte[] plain     = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

            string tmp = SettingsFilePath + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            if (File.Exists(SettingsFilePath))
                File.Replace(tmp, SettingsFilePath, null);
            else
                File.Move(tmp, SettingsFilePath);
        }

        /// <summary>
        /// Safely applies a change: loads the current settings, mutates them, and saves.
        /// If a settings file exists but could not be read, the change is skipped rather
        /// than overwriting existing data with a blank object — this prevents a transient
        /// read failure from wiping unrelated settings. Returns true if it was saved.
        /// </summary>
        public static bool Update(Action<AppSettings> mutate)
        {
            AppSettings s = Load();
            if (s == null)
            {
                if (FileExists) return false;   // exists but unreadable → never clobber it
                s = new AppSettings();          // genuine first run
            }
            mutate(s);
            Save(s);
            return true;
        }
    }
}
