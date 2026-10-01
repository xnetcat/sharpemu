// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Diagnostics;
using SharpEmu.Libs.Gpu.Pipelines;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private sealed class DriverCacheShard
        {
            public required PipelineCache Cache;
            public required string Path;
            public bool Dirty;
        }

        private string? _pipelineCacheShardDirectory;
        private readonly Dictionary<string, DriverCacheShard> _pipelineCacheShards = new();

        // MoltenVK recompiles cached MSL libraries during vkCreatePipelineCache.
        // Loading only a requested shader group prevents a mature cache from
        // compiling every previously visited scene before the first frame.
        // Vulkan still validates the complete shader/layout key inside each blob;
        // stage hashes only select a storage bucket, never a pipeline to reuse.
        private PipelineCache GetGuestPipelineCache(string key)
        {
            if (_pipelineCacheShardDirectory is null) return _pipelineCache;
            if (_pipelineCacheShards.TryGetValue(key, out var existing))
            {
                existing.Dirty = true;
                return existing.Cache;
            }

            var path = Path.Combine(_pipelineCacheShardDirectory, key + ".bin");
            byte[] data = [];
            try
            {
                if (File.Exists(path) &&
                    !PipelineCacheSignature.TryUnwrap(DriverCacheSignature(), File.ReadAllBytes(path), out data))
                    Console.Error.WriteLine($"[LOADER][INFO] Vulkan cache shard invalidated: path={path}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Vulkan cache shard read failed: {exception.Message}");
            }

            var started = Stopwatch.GetTimestamp();
            var result = TryCreatePipelineCache(data, out var cache);
            if (result != Result.Success && data.Length != 0)
                result = TryCreatePipelineCache([], out cache);
            if (result != Result.Success)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Vulkan cache shard unavailable: key={key} result={result}");
                return _pipelineCache;
            }

            _pipelineCacheShards.Add(key, new DriverCacheShard { Cache = cache, Path = path, Dirty = true });
            if (data.Length != 0)
                Console.Error.WriteLine($"[LOADER][INFO] Vulkan cache shard loaded: key={key} bytes={data.Length} ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
            return cache;
        }

        private void SaveGuestPipelineCaches()
        {
            foreach (var shard in _pipelineCacheShards.Values)
            {
                if (shard.Dirty && SaveDriverPipelineCache(shard.Cache, shard.Path))
                    shard.Dirty = false;
            }
        }

        private void DestroyGuestPipelineCaches()
        {
            foreach (var shard in _pipelineCacheShards.Values)
                _vk.DestroyPipelineCache(_device, shard.Cache, null);
            _pipelineCacheShards.Clear();
        }
    }
}
