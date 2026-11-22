using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Infrastructure;

/// <summary>An options monitor whose value a test can replace, to prove that a service reads its settings again on every use.</summary>
public sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T> where T : class
{
    public MutableOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; set; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
