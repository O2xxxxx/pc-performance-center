namespace Ppc.Core.Services;

public static class Guard
{
    public static string Safe(Func<string> factory, string fallback = "未知")
    {
        try
        {
            var value = factory();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }
}
