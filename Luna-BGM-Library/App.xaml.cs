using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace LunaBgmLibrary
{
    public partial class App : Application
    {
        private static string LogPath =>
            Path.Combine(AppContext.BaseDirectory, "startup-errors.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;

            try
            {
                File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now:O} App starting{Environment.NewLine}");
            }
            catch {}

            base.OnStartup(e);
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogException("DispatcherUnhandledException", e.Exception);
            MessageBox.Show(
                $"Unhandled UI exception:\n{e.Exception.Message}\n\nSee startup-errors.log",
                "Luna-BGM-Library", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogException("DomainUnhandledException", e.ExceptionObject as Exception);
        }

        private static void LogException(string kind, Exception? ex)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:O}] {kind}");
                if (ex != null)
                {
                    sb.AppendLine(ex.ToString());
                    if (ex.InnerException != null)
                    {
                        sb.AppendLine("--- Inner ---");
                        sb.AppendLine(ex.InnerException.ToString());
                    }
                }
                sb.AppendLine();
                File.AppendAllText(LogPath, sb.ToString());
            }
            catch {}
        }
    }
}
