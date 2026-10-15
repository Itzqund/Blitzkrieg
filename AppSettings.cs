using System;
using System.Drawing;
using System.IO;
using System.Xml.Serialization;

namespace BlitzkriegWPF
{
    [Serializable]
    public class AppSettings
    {
        public string Theme { get; set; }
        public string Language { get; set; }
        public double Zoom { get; set; } = 1.0;
        // Эти два свойства оставлены для чтения настроек из старых версий приложения.
        public Point Location { get; set; }
        public int[] Size { get; set; }

        // Координаты и размеры окна в обычном, не развёрнутом состоянии.
        public bool HasWindowPlacement { get; set; }
        public double WindowLeft { get; set; }
        public double WindowTop { get; set; }
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public bool WasMaximized { get; set; }
    }

    public static class AppSettingsService
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlitzkriegWPF");

        private static readonly string SettingsPath = Path.Combine(
            SettingsDirectory,
            "settings.xml");

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new AppSettings();

                using (var stream = File.OpenRead(SettingsPath))
                {
                    var serializer = new XmlSerializer(typeof(AppSettings));
                    return serializer.Deserialize(stream) as AppSettings ?? new AppSettings();
                }
            }
            catch
            {
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);

                using (var stream = File.Create(SettingsPath))
                {
                    var serializer = new XmlSerializer(typeof(AppSettings));
                    serializer.Serialize(stream, settings ?? new AppSettings());
                }
            }
            catch
            {
                
            }
        }
    }
}
