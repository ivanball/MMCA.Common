namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>Default <see cref="ILocalCacheStore"/>: nothing is cached; reads return <see langword="default"/>.</summary>
public sealed class NullLocalCacheStore : ILocalCacheStore
{
    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<T?>(default);

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
