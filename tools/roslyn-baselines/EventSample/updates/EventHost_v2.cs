namespace EventSample;

using System;

public class EventHost
{
    public event Action? OnChanged;
    public event Action<string>? OnStatus;

    public void Raise()
    {
        OnChanged?.Invoke();
        OnStatus?.Invoke("second update");
    }
}
