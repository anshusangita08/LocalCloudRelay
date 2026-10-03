namespace LocalCloudRelay;

internal static class Program
{
    private static Mutex? _singleInstance;
    private static EventWaitHandle? _showSignal;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // A second launch would fail to bind 8787 and race the first instance on
        // settings.dat, silently swapping the stored credentials.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\LocalCloudRelay", out var isFirstInstance);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LocalCloudRelayShow");

        if (!isFirstInstance)
        {
            // Signal the running instance to raise its window, then exit. A MessageBox here
            // blocks until dismissed, which hangs any launcher that does not expect one.
            try { _showSignal.Set(); } catch { }
            return;
        }

        // Without these, any unhandled exception on the UI thread silently kills the
        // process - including the relay host running inside it.
        Application.ThreadException += (_, e) =>
            MessageBox.Show($"Unexpected error:\n\n{e.Exception.Message}", "Local Cloud Relay",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show($"Fatal error:\n\n{e.ExceptionObject}", "Local Cloud Relay",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        // Application.Run owns the form's lifetime and disposes it when the message loop
        // ends; disposing here would be wrong.
#pragma warning disable CA2000 // false positive: Application.Run owns the form
        var form = new MainForm();
#pragma warning restore CA2000
        _ = Task.Run(() =>
        {
            while (_showSignal?.WaitOne() == true)
            {
                if (form.IsDisposed) return;
                if (!form.IsHandleCreated) continue;
                try { form.BeginInvoke(form.ShowWindow); } catch { return; }
            }
        });

        try
        {
            Application.Run(form);
        }
        finally
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }
    }
}
