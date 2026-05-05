using KeyboardPadBridge.Services;

var probe = new BluetoothCapabilityProbe();
var result = await probe.RunAsync();

Console.WriteLine(result.Summary);
Console.WriteLine(new string('-', result.Summary.Length));

foreach (var message in result.Messages)
{
    Console.WriteLine(message);
}

Environment.ExitCode = 0;
