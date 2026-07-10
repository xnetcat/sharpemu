// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace SharpEmu.Libs.Kernel;

internal static class KernelVirtualRangeAllocator
{
    private static readonly ConcurrentDictionary<Type, Accessor> _accessors = new();

    public static bool TryReserve(
        CpuContext ctx,
        ulong desiredAddress,
        ulong length,
        bool executable,
        ulong alignment,
        bool allowSearch,
        bool allowAllocateAtAlternative,
        string traceName,
        out ulong mappedAddress)
    {
        mappedAddress = 0;
        if (length == 0)
        {
            return false;
        }

        try
        {
            if (!TryResolveAccessor(ctx.Memory, out var target, out var accessor))
            {
                Console.Error.WriteLine($"[LOADER][TRACE] {traceName}: AllocateAt missing on {ctx.Memory.GetType().FullName}");
                return false;
            }

            if (allowSearch && accessor.AllocateAtOrAbove is not null)
            {
                var searchArgs = new object[] { desiredAddress, length, executable, alignment, 0UL };
                var searchResult = accessor.AllocateAtOrAbove.Invoke(target, searchArgs);
                if (searchResult is bool trueValue && trueValue &&
                    searchArgs[4] is ulong searchedAddress && searchedAddress != 0)
                {
                    mappedAddress = searchedAddress;
                    return true;
                }
            }

            if (accessor.AllocateAt is null)
            {
                Console.Error.WriteLine($"[LOADER][TRACE] {traceName}: AllocateAt missing on {target.GetType().FullName}");
                return false;
            }

            var invokeArgs = accessor.AllocateAtHasAllowAlternativeArg
                ? new object[] { desiredAddress, length, executable, allowAllocateAtAlternative }
                : new object[] { desiredAddress, length, executable };
            var result = accessor.AllocateAt.Invoke(target, invokeArgs);
            if (result is not ulong allocated || allocated == 0)
            {
                var resultType = result?.GetType().FullName ?? "null";
                Console.Error.WriteLine($"[LOADER][TRACE] {traceName}: AllocateAt returned {resultType} value={result ?? "null"}");
                return false;
            }

            mappedAddress = allocated;
            return true;
        }
        catch
        {
            // Expected when a fixed-address request cannot be satisfied on
            // this host; the caller falls back or reports the failure.
            Console.Error.WriteLine(
                $"[LOADER][TRACE] {traceName}: no host mapping at 0x{desiredAddress:X16} len=0x{length:X}");
            return false;
        }
    }

    private static bool TryResolveAccessor(object rootMemory, out object target, out Accessor accessor)
    {
        target = rootMemory;
        accessor = default;

        for (var depth = 0; depth < 4; depth++)
        {
            accessor = _accessors.GetOrAdd(target.GetType(), DiscoverAccessor);
            if (accessor.AllocateAt is not null || accessor.AllocateAtOrAbove is not null)
            {
                return true;
            }

            if (accessor.InnerProperty is null)
            {
                break;
            }

            var innerValue = accessor.InnerProperty.GetValue(target);
            if (innerValue is null || ReferenceEquals(innerValue, target))
            {
                break;
            }

            target = innerValue;
        }

        return false;
    }

    private static Accessor DiscoverAccessor(Type type)
    {
        MethodInfo? allocateAt = null;
        MethodInfo? allocateAtOrAbove = null;
        var allocateAtHasAllowAlternativeArg = false;

        foreach (var candidate in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = candidate.GetParameters();
            if (string.Equals(candidate.Name, "TryAllocateAtOrAbove", StringComparison.Ordinal) &&
                parameters.Length == 5 &&
                parameters[0].ParameterType == typeof(ulong) &&
                parameters[1].ParameterType == typeof(ulong) &&
                parameters[2].ParameterType == typeof(bool) &&
                parameters[3].ParameterType == typeof(ulong) &&
                parameters[4].ParameterType == typeof(ulong).MakeByRefType())
            {
                allocateAtOrAbove = candidate;
            }
            else if (string.Equals(candidate.Name, "AllocateAt", StringComparison.Ordinal))
            {
                if (parameters.Length == 3 &&
                    parameters[0].ParameterType == typeof(ulong) &&
                    parameters[1].ParameterType == typeof(ulong) &&
                    parameters[2].ParameterType == typeof(bool))
                {
                    allocateAt = candidate;
                    allocateAtHasAllowAlternativeArg = false;
                }
                else if (parameters.Length == 4 &&
                    parameters[0].ParameterType == typeof(ulong) &&
                    parameters[1].ParameterType == typeof(ulong) &&
                    parameters[2].ParameterType == typeof(bool) &&
                    parameters[3].ParameterType == typeof(bool))
                {
                    allocateAt = candidate;
                    allocateAtHasAllowAlternativeArg = true;
                }
            }
        }

        var innerProperty = type.GetProperty("Inner", BindingFlags.Public | BindingFlags.Instance);
        return new Accessor(allocateAt, allocateAtOrAbove, allocateAtHasAllowAlternativeArg, innerProperty);
    }

    private readonly record struct Accessor(
        MethodInfo? AllocateAt,
        MethodInfo? AllocateAtOrAbove,
        bool AllocateAtHasAllowAlternativeArg,
        PropertyInfo? InnerProperty);
}
