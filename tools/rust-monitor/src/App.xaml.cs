using System.Windows;

namespace RustMonitor;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var index = Array.IndexOf(e.Args, "--data-dir");
        var root = index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1]
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RustMonitor");
        try
        {
            var window = new MainWindow(System.IO.Path.GetFullPath(root));
            MainWindow = window;
            window.Show();
            if (e.Args.Contains("--smoke-test"))
                window.Dispatcher.BeginInvoke(async () => { await Task.Delay(600); window.Close(); });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Rust Monitor"); Shutdown(1); }
    }
}
