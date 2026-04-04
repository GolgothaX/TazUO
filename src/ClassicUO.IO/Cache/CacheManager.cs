using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using ClassicUO.Utility.Logging;

namespace ClassicUO.IO.Cache;

public enum CacheType
{
    Font
}

public abstract class CacheItemDefinition
{
    public abstract CacheType Key { get; }
    public abstract Type ValueType { get; }
}

public abstract class CacheItemDefinition<TValue> : CacheItemDefinition where TValue : class, new()
{
    public sealed override Type ValueType => typeof(TValue);
}

public sealed class CacheManager
{
    private static readonly Lazy<CacheManager> _instance = new(() => new CacheManager(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static CacheManager Instance => _instance.Value;

    private readonly ConcurrentDictionary<CacheType, Lazy<object>> _cache = new();
    private readonly FrozenDictionary<CacheType, string> _filePaths;
    private readonly string _cacheDirectory;

    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private CacheManager(string cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache");

        if (!Directory.Exists(_cacheDirectory))
            Directory.CreateDirectory(_cacheDirectory);

        var filePaths = new Dictionary<CacheType, string>();

        foreach (CacheType cacheType in Enum.GetValues<CacheType>())
            filePaths[cacheType] = GetCacheFilePath(cacheType);

        _filePaths = filePaths.ToFrozenDictionary();
    }

    public TValue Get<TValue>(CacheItemDefinition<TValue> definition)
        where TValue : class, new()
    {
        CacheType key = definition.Key;

        Lazy<object> lazy = _cache.GetOrAdd(
            key,
            static _ => new Lazy<object>(() => null, LazyThreadSafetyMode.PublicationOnly)
        );

        object cachedValue = lazy.Value;
        if (cachedValue is TValue typedValue)
            return typedValue;

        lazy = _cache.AddOrUpdate(
            key,
            _ => new Lazy<object>(() => LoadOrCreate(definition), LazyThreadSafetyMode.ExecutionAndPublication),
            (_, existing) =>
            {
                if (existing.IsValueCreated && existing.Value is TValue)
                    return existing;

                return new Lazy<object>(() => LoadOrCreate(definition), LazyThreadSafetyMode.ExecutionAndPublication);
            });

        return (TValue)lazy.Value;
    }

    public bool Set<TValue>(CacheItemDefinition<TValue> definition, TValue data) where TValue : class, new()
    {
        if (data == null)
        {
            Log.Warn($"Attempted to set null data for cache type: {definition.Key}");
            return false;
        }

        _cache[definition.Key] = new Lazy<object>(() => data, LazyThreadSafetyMode.PublicationOnly);
        return SaveToFile(definition, data);
    }

    private string GetCacheFilePath(CacheType cacheType) =>
        Path.Combine(_cacheDirectory, $"{cacheType.ToString().ToLowerInvariant()}.json");

    private TValue LoadOrCreate<TValue>(CacheItemDefinition<TValue> definition)
        where TValue : class, new()
    {
        TValue data = LoadFromFile(definition) ?? new TValue();
        return data;
    }

    private TValue LoadFromFile<TValue>(CacheItemDefinition<TValue> definition)
        where TValue : class, new()
    {
        string filePath = _filePaths[definition.Key];

        if (!File.Exists(filePath))
            return null;

        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<TValue>(json, _jsonSerializerOptions);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load cache file for {definition.Key}: {ex.Message}");
            return null;
        }
    }

    private bool SaveToFile<TValue>(CacheItemDefinition<TValue> definition, TValue data) where TValue : class, new()
    {
        string filePath = _filePaths[definition.Key];
        try
        {
            string json = JsonSerializer.Serialize(data, _jsonSerializerOptions);
            File.WriteAllText(filePath, json);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save cache file for {definition.Key}: {ex.Message}");
            return false;
        }
    }

    public void Clear(CacheType cacheType) => _cache.TryRemove(cacheType, out _);

    public void ClearAll() => _cache.Clear();
}
