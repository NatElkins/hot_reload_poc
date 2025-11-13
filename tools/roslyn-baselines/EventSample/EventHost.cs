namespace EventSample;

using System;

public class EventHost
{
    public event Action? OnChanged;

    public void Raise()
    {
        OnChanged?.Invoke();
    }
}
