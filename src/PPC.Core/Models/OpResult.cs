namespace Ppc.Core.Models;

/// <summary>Outcome of a hardware operation. Never claims success that did not happen.</summary>
public readonly record struct OpResult(bool Success, string Message)
{
    public static OpResult Ok(string message = "已完成") => new(true, message);
    public static OpResult Fail(string message) => new(false, message);
    public static OpResult Unsupported(string message) => new(false, message);
}
