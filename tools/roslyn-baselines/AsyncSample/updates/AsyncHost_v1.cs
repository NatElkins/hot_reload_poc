namespace AsyncSample;

public static class AsyncHost
{
    public static string RunAsync(int token)
    {
        return token switch
        {
            < 0 => "negative",
            _ => "updated"
        };
    }
}
