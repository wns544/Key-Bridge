using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace KeyboardPadBridge;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\KeyboardPadBridge.KeyBridge";
    private const string ActivationPipeName = "KeyboardPadBridge.KeyBridge.Activate";

    private Mutex? singleInstanceMutex;
    private CancellationTokenSource? activationPipeCancellation;

    protected override void OnStartup(StartupEventArgs e)
    {
        var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _ = SignalExistingInstanceAsync();
            mutex.Dispose();
            Shutdown();
            return;
        }

        singleInstanceMutex = mutex;
        base.OnStartup(e);
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
        StartActivationPipeServer(mainWindow);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        activationPipeCancellation?.Cancel();
        activationPipeCancellation?.Dispose();
        activationPipeCancellation = null;
        singleInstanceMutex?.ReleaseMutex();
        singleInstanceMutex?.Dispose();
        singleInstanceMutex = null;
        base.OnExit(e);
    }

    private static async Task SignalExistingInstanceAsync()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", ActivationPipeName, PipeDirection.Out);
                await pipe.ConnectAsync(180);
                await pipe.WriteAsync(new byte[] { 1 });
                await pipe.FlushAsync();
                return;
            }
            catch (TimeoutException)
            {
                await Task.Delay(80);
            }
            catch (IOException)
            {
                await Task.Delay(80);
            }
        }
    }

    private void StartActivationPipeServer(MainWindow mainWindow)
    {
        activationPipeCancellation = new CancellationTokenSource();
        var cancellationToken = activationPipeCancellation.Token;

        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(
                        ActivationPipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(cancellationToken);
                    await Dispatcher.InvokeAsync(mainWindow.ShowFromExternalActivation);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(80, cancellationToken);
                }
            }
        }, cancellationToken);
    }
}
