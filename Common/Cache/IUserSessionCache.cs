using System.Collections.Concurrent;
using tdtd_be.DTOs.Auth;

namespace tdtd_be.Common.Cache;

public interface IUserSessionCache
{
    Task<MeResponse?> GetMeAsync(string userId);
    Task SetMeAsync(MeResponse me);
    Task DeleteMeAsync(string userId);
    Task<long> GetTokenVersionAsync(string userId);
    Task EnsureTokenVersionAsync(string userId);
    Task<long> BumpTokenVersionAsync(string userId);
}

public sealed class InMemoryUserSessionCache : IUserSessionCache
{
    private readonly ConcurrentDictionary<string, MeResponse> _me = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _tokenVersions = new(StringComparer.Ordinal);

    public Task<MeResponse?> GetMeAsync(string userId)
        => Task.FromResult(_me.TryGetValue(userId, out var value) ? value : null);

    public Task SetMeAsync(MeResponse me)
    {
        _me[me.Id] = me;
        return Task.CompletedTask;
    }

    public Task DeleteMeAsync(string userId)
    {
        _me.TryRemove(userId, out _);
        return Task.CompletedTask;
    }

    public Task<long> GetTokenVersionAsync(string userId)
        => Task.FromResult(_tokenVersions.TryGetValue(userId, out var value) ? value : 0L);

    public Task EnsureTokenVersionAsync(string userId)
    {
        _tokenVersions.TryAdd(userId, 0L);
        return Task.CompletedTask;
    }

    public Task<long> BumpTokenVersionAsync(string userId)
        => Task.FromResult(_tokenVersions.AddOrUpdate(userId, 1L, static (_, current) => current + 1L));
}
