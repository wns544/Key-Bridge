namespace KeyboardPadBridge.Models;

public sealed record ActivityEvent(DateTime Timestamp, string Channel, string Message)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss");
}
