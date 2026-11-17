using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AudioPicker
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, args) =>
            {
                Log(args.Exception);
                args.Handled = false;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                Log(args.ExceptionObject as Exception);
        }

        private static void Log(Exception ex)
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AudioPicker");
                Directory.CreateDirectory(path);
                File.AppendAllText(Path.Combine(path, "crash.log"),
                    DateTime.Now + " " + ex + Environment.NewLine);
            }
            catch { }
        }
    }
}
