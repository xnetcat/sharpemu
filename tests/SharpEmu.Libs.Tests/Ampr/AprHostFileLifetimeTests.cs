// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Ampr;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

public sealed class AprHostFileLifetimeTests
{
    [Fact]
    public void EvictionKeepsHandleOpenUntilAllReadersFinish()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            using var file = new AmprExports.CachedHostFile(path);
            file.AcquireRead();
            file.AcquireRead();
            file.Dispose(); // Cache eviction while two reads still own the file.
            file.Dispose(); // Repeated disposal must not release a reader.
            Assert.False(file.Handle.IsClosed);
            Span<byte> first = stackalloc byte[2];
            Assert.Equal(2, RandomAccess.Read(file.Handle, first, 0));
            Assert.Equal(new byte[] { 1, 2 }, first.ToArray());
            file.ReleaseRead();
            Assert.False(file.Handle.IsClosed);
            Assert.Equal(2, RandomAccess.Read(file.Handle, first, 2));
            Assert.Equal(new byte[] { 3, 4 }, first.ToArray());
            file.ReleaseRead();
            Assert.True(file.Handle.IsClosed);
            Assert.Throws<ObjectDisposedException>(() => file.AcquireRead());
        }
        finally { File.Delete(path); }
    }
}
