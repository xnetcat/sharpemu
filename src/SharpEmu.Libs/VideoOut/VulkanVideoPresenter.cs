// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Bink;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Windowing;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.VideoOut;

internal enum GuestDrawKind
{
    None,
    FullscreenBarycentric,
}

internal sealed record VulkanGuestDrawTexture(
    ulong Address,
    uint Width,
    uint Height,
    uint Format,
    uint NumberType,
    byte[] RgbaPixels,
    bool IsFallback,
    bool IsStorage,
    uint MipLevels = 1,
    uint MipLevel = 0,
    uint BaseMipLevel = 0,
    uint ResourceMipLevels = 1,
    uint Pitch = 0,
    uint TileMode = 0,
    uint DstSelect = 0xFAC,
    VulkanGuestSampler Sampler = default,
    ulong DeferredDescriptorAddress = 0);

internal readonly record struct VulkanGuestSampler(
    uint Word0,
    uint Word1,
    uint Word2,
    uint Word3);

// Data arrays flagged Pooled are rented from ArrayPool (and may be larger
// than Length); the presenter returns them to the pool right after copying
// them into host-visible Vulkan buffers. Pooled buffers must therefore be
// uploaded exactly once — reusable draws (the flip-time present path, which
// can also be re-created on swapchain retry) must pass Pooled=false.
internal sealed record VulkanGuestMemoryBuffer(
    ulong BaseAddress,
    byte[] Data,
    int Length,
    bool Pooled,
    bool Writable = false,
    bool WriteBackToGuest = true);

internal sealed record VulkanGuestVertexBuffer(
    uint Location,
    uint ComponentCount,
    uint DataFormat,
    uint NumberFormat,
    ulong BaseAddress,
    uint Stride,
    uint OffsetBytes,
    byte[] Data,
    int Length,
    bool Pooled,
    ulong DeferredDescriptorAddress = 0,
    int RequiredRecords = 0);

internal sealed record VulkanGuestIndexBuffer(
    byte[] Data,
    int Length,
    bool Is32Bit,
    bool Pooled);

internal readonly record struct VulkanGuestRect(
    int X,
    int Y,
    uint Width,
    uint Height);

internal readonly record struct VulkanGuestViewport(
    float X,
    float Y,
    float Width,
    float Height,
    float MinDepth,
    float MaxDepth);

internal readonly record struct VulkanGuestBlendState(
    bool Enable,
    uint ColorSrcFactor,
    uint ColorDstFactor,
    uint ColorFunc,
    uint AlphaSrcFactor,
    uint AlphaDstFactor,
    uint AlphaFunc,
    bool SeparateAlphaBlend,
    uint WriteMask)
{
    public static VulkanGuestBlendState Default { get; } = new(
        Enable: false,
        ColorSrcFactor: 1,
        ColorDstFactor: 0,
        ColorFunc: 0,
        AlphaSrcFactor: 1,
        AlphaDstFactor: 0,
        AlphaFunc: 0,
        SeparateAlphaBlend: false,
        WriteMask: 0xFu);
}

internal readonly record struct VulkanGuestRasterState(
    bool CullFront,
    bool CullBack,
    bool FrontFaceClockwise,
    bool Wireframe)
{
    public static VulkanGuestRasterState Default { get; } = new(false, false, false, false);
}

// Depth test/write state from DB_DEPTH_CONTROL. CompareOp uses the GCN 3-bit
// ZFUNC encoding (0=Never, 1=Less, 2=Equal, 3=LEqual, 4=Greater, 5=NotEqual,
// 6=GEqual, 7=Always), which matches Vulkan's CompareOp ordering.
internal readonly record struct VulkanGuestDepthState(
    bool TestEnable,
    bool WriteEnable,
    uint CompareOp)
{
    public static VulkanGuestDepthState Default { get; } = new(false, false, 7);
}

internal sealed record VulkanGuestRenderState(
    VulkanGuestBlendState Blend,
    VulkanGuestRect? Scissor,
    VulkanGuestViewport? Viewport,
    VulkanGuestRasterState Raster,
    VulkanGuestDepthState Depth)
{
    public static VulkanGuestRenderState Default { get; } = new(
        VulkanGuestBlendState.Default,
        Scissor: null,
        Viewport: null,
        VulkanGuestRasterState.Default,
        VulkanGuestDepthState.Default);
}

internal sealed record VulkanGuestRenderTarget(
    ulong Address,
    uint Width,
    uint Height,
    uint Format,
    uint NumberType,
    uint MipLevels = 1);

// Guest DB (depth-buffer) surface bound alongside a color render target.  The
// read and write bases are retained separately because GFX10 can bind distinct
// surfaces, although games normally use the same allocation for both.  Vulkan
// uses a host-native depth format; GuestFormat and SwizzleMode describe the
// original DB_Z_INFO fields and keep the cache identity honest.
internal sealed record VulkanGuestDepthTarget(
    ulong ReadAddress,
    ulong WriteAddress,
    uint Width,
    uint Height,
    uint GuestFormat,
    uint SwizzleMode,
    float ClearDepth,
    bool ReadOnly)
{
    public ulong Address => WriteAddress != 0 ? WriteAddress : ReadAddress;
}

internal sealed record VulkanTranslatedGuestDraw(
    byte[] VertexSpirv,
    byte[] PixelSpirv,
    IReadOnlyList<VulkanGuestDrawTexture> Textures,
    IReadOnlyList<VulkanGuestMemoryBuffer> GlobalMemoryBuffers,
    IReadOnlyList<VulkanGuestVertexBuffer> VertexBuffers,
    uint AttributeCount,
    uint VertexCount,
    uint InstanceCount,
    uint PrimitiveType,
    VulkanGuestIndexBuffer? IndexBuffer,
    VulkanGuestRenderState RenderState);

internal sealed record VulkanOffscreenGuestDraw(
    VulkanTranslatedGuestDraw Draw,
    VulkanGuestRenderTarget Target,
    VulkanGuestDepthTarget? DepthTarget,
    bool PublishTarget);

internal sealed record VulkanComputeGuestDispatch(
    ulong ShaderAddress,
    byte[] ComputeSpirv,
    IReadOnlyList<VulkanGuestDrawTexture> Textures,
    IReadOnlyList<VulkanGuestMemoryBuffer> GlobalMemoryBuffers,
    uint GroupCountX,
    uint GroupCountY,
    uint GroupCountZ,
    uint BaseGroupX,
    uint BaseGroupY,
    uint BaseGroupZ,
    uint LocalSizeX,
    uint LocalSizeY,
    uint LocalSizeZ,
    bool IsIndirect,
    bool WritesGlobalMemory,
    uint ThreadCountX = uint.MaxValue,
    uint ThreadCountY = uint.MaxValue,
    uint ThreadCountZ = uint.MaxValue);

internal sealed record VulkanOrderedGuestAction(
    Action Action,
    string DebugName);

internal static unsafe class VulkanVideoPresenter
{
    private const uint DefaultWindowWidth = 1280;
    private const uint DefaultWindowHeight = 720;
    // Vulkan's portable upper bound for minStorageBufferOffsetAlignment is
    // 256 bytes. Using that fixed power of two (instead of racing the render
    // thread's physical-device query) gives shader translation and descriptor
    // creation one stable aliasing contract on every conformant device.
    internal const ulong GuestStorageBufferOffsetAlignment = 256;
    // Terrarium benefits from immutable per-draw snapshots for read-only
    // buffers, but keep a diagnostic escape hatch while other games are being
    // validated. Setting SHARPEMU_SHARED_READONLY_BUFFERS=1 restores the
    // original shared guest-VA aliasing path and its descriptor byte bias.
    internal static readonly bool UseTransientReadOnlyGuestBuffers =
        !string.Equals(
            Environment.GetEnvironmentVariable(
                "SHARPEMU_SHARED_READONLY_BUFFERS"),
            "1",
            StringComparison.Ordinal);
    // silent-hill-dev removed the guest-queue-identity tracking that this scope
    // used to drive. aa7b519's ParseSubmittedDcbSnapshot still brackets its
    // captured-command replay with EnterGuestQueue, so keep a no-op scope that
    // satisfies that contract without reintroducing the removed per-queue state.
    internal static IDisposable EnterGuestQueue(string queueName, ulong submissionId)
        => GuestQueueScope.Instance;

    private sealed class GuestQueueScope : IDisposable
    {
        public static readonly GuestQueueScope Instance = new();

        public void Dispose()
        {
        }
    }
    // The pending queue and per-render drain budget bound how much guest GPU
    // work can be buffered ahead of the presenter. Draws are batched into
    // shared command buffers, so draining a large batch per render tick is
    // cheap; small caps here throttle games that issue more than a handful
    // of draws per frame to a fraction of the display rate. The pending cap
    // stays tighter than the drain budget because queued draws pin their
    // pooled guest-data arrays until the render thread uploads them.
    private static readonly int _maxPendingGuestWork =
        int.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_PENDING_GUEST_WORK_COUNT"),
            out var pendingGuestWorkCount) && pendingGuestWorkCount > 0
                ? pendingGuestWorkCount
                : 64;
    // A count-only queue bound is not a memory bound: one compute dispatch can
    // carry dozens of full-resolution texture snapshots.  At 4K, 64 queued
    // dispatches retained more than 12 GiB of managed byte arrays before the
    // render thread could upload them.  Keep the count cap for small work and
    // independently apply backpressure to the actual retained payload.
    private static readonly ulong _maxPendingGuestWorkBytes =
        (ulong.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_PENDING_GUEST_WORK_MB"),
             out var pendingGuestWorkMb) && pendingGuestWorkMb > 0
            ? pendingGuestWorkMb
            : 256UL) * 1024UL * 1024UL;
    private const int MaxGuestWorkPerRender = 256;
    // On macOS the whole window loop — including Render() and its guest-work
    // drain — runs on the process main thread, so draining a large backlog of
    // slow guest work (heavy compute) blocks the Cocoa event pump and marks the
    // window "Not Responding" while starving the swapchain present. Cap the
    // wall-clock time spent draining per Render() call; leftover work stays
    // queued for the next frame. SHARPEMU_RENDER_WORK_BUDGET_MS overrides
    // (0 disables the cap); default 12ms keeps the window interactive at ~60Hz.
    private static readonly long _renderWorkBudgetTicks =
        (long.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_RENDER_WORK_BUDGET_MS"),
             out var renderBudgetMs) && renderBudgetMs >= 0
            ? renderBudgetMs
            : 12L) * System.Diagnostics.Stopwatch.Frequency / 1000L;
    // Max time the main-thread Render() will block waiting for a frame slot's
    // GPU fence before skipping the frame and returning to the event pump.
    // Prevents the window freezing behind a slow-compute GPU backlog.
    // SHARPEMU_FRAME_WAIT_BUDGET_MS overrides; default 8ms.
    private static readonly ulong _frameSlotWaitBudgetNs =
        (ulong.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_FRAME_WAIT_BUDGET_MS"),
             out var frameWaitMs) && frameWaitMs > 0
            ? frameWaitMs
            : 8UL) * 1_000_000UL;
    // Cap the guest-submission fence wait so a GPU submission whose fence never
    // signals (a mistranslated compute shader that hangs the Metal queue) cannot
    // freeze the render thread forever and starve the swapchain present.
    // SHARPEMU_FENCE_WAIT_TIMEOUT_MS overrides; default 3s.
    private static readonly ulong _guestFenceWaitTimeoutNs =
        ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FENCE_WAIT_TIMEOUT_MS"), out var fenceMs) && fenceMs > 0
            ? fenceMs * 1_000_000UL
            : 3_000_000_000UL;
    // When making room in the in-flight submission queue from the macOS MAIN
    // thread (Render() -> guest-work drain), block only this long per attempt
    // instead of the full fence timeout. If a slow/capped compute submission
    // isn't done yet, proceed anyway: the in-flight cap is soft, the fence and
    // command-buffer pools are dynamic so a brief overshoot is safe, and the
    // queue drains as GPU completions land on later frames. This keeps the
    // window responsive (event pump runs) under a heavy compute backlog instead
    // of the main thread sitting in vkWaitForFences for up to 3s per chunk.
    // SHARPEMU_SUBMISSION_CAPACITY_WAIT_MS overrides; default 100ms; 0 restores
    // the full blocking wait.
    private static readonly ulong _submissionCapacityWaitNs =
        ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SUBMISSION_CAPACITY_WAIT_MS"), out var capMs)
            ? capMs * 1_000_000UL
            : 100_000_000UL;
    private static readonly HashSet<string> _tracedFenceTimeouts = new();
    // Diagnostic: skip every compute dispatch (mistranslated compute shaders
    // run long / GPU-hang and starve the present). Isolates whether the
    // geometry+composite path renders on its own.
    private static readonly bool _skipAllCompute =
        Environment.GetEnvironmentVariable("SHARPEMU_SKIP_ALL_COMPUTE") == "1";
    // Diagnostic: skip compute dispatches whose GroupCountZ is at least this,
    // to isolate a specific tall dispatch (e.g. Demon's Souls' 27x15x72 froxel
    // shader that hangs the Metal queue) without needing its ASLR-varying
    // address. 0 disables.
    private static readonly uint _skipTallComputeZ =
        uint.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SKIP_TALL_COMPUTE_Z"), out var z)
            ? z
            : 0;
    private const uint GuestPrimitiveRectList = 0x11;

    private static readonly object _gate = new();
    private readonly record struct PendingGuestWork(object Work, ulong PayloadBytes);

    private static readonly Queue<PendingGuestWork> _pendingGuestWork = new();
    private static ulong _pendingGuestWorkBytes;
    private static string? _activeGuestWorkDescription;
    // A flip names an image that was rendered earlier in the command stream.
    // Keep a small FIFO of those flips instead of replacing an incomplete one
    // with the next frame: the guest can enqueue the next frame before the
    // render thread reaches the previous image, which otherwise starves
    // presentation indefinitely.
    private static readonly Queue<Presentation> _pendingGuestImagePresentations = new();
    private static readonly Dictionary<ulong, long> _guestImageWorkSequences = new();
    private static readonly Dictionary<ulong, uint> _availableGuestImages = new();
    // Storage-image initialization is copied only by the first queued writer.
    // Later dispatches targeting the same image must not each retain another
    // multi-megabyte guest-memory snapshot while waiting for that first writer
    // to reach the presenter.  Reference counts let failed/completed work
    // retire its reservation without leaving a permanent false cache hit.
    private static readonly Dictionary<(ulong Address, uint Format), int>
        _pendingGuestImageUploads = new();
    private static readonly Dictionary<ulong, byte[]> _pendingGuestImageInitialData = new();
    private static readonly Dictionary<ulong, (uint Width, uint Height, ulong ByteCount)>
        _guestImageExtents = new();
    private static readonly bool _traceGuestImageEvents =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DRAWS"),
            "1",
            StringComparison.Ordinal) ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_EVENTS"),
            "1",
            StringComparison.Ordinal);
    private static readonly HashSet<(ulong Address, uint Width, uint Height)>
        _tracedGuestImageSubmissions = [];
    private static Thread? _thread;
    private static Presentation? _latestPresentation;
    private static byte[]? _copyFragmentSpirv;
    private static uint _windowWidth;
    private static uint _windowHeight;
    private static bool _closed;
    private static bool _presenterCloseRequested;
    private const string DebugUtilsExtensionName = "VK_EXT_debug_utils";
    private const string PortabilityEnumerationExtensionName = "VK_KHR_portability_enumeration";
    private const string PortabilitySubsetExtensionName = "VK_KHR_portability_subset";
    private static bool _splashHidden;
    private static long _enqueuedGuestWorkSequence;
    private static long _completedGuestWorkSequence;

    public static void EnsureStarted(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed || _thread is not null)
            {
                return;
            }
        }

        var hasSplash = PngSplashLoader.TryLoad(
            out var splashPixels,
            out var splashWidth,
            out var splashHeight);
        lock (_gate)
        {
            if (_closed || _thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            _latestPresentation ??= _splashHidden
                ? new Presentation(
                    CreateBlackFrame(width, height),
                    width,
                    height,
                    1,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                    IsSplash: false)
                : hasSplash
                ? new Presentation(
                    splashPixels,
                    splashWidth,
                    splashHeight,
                    1,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                    IsSplash: true)
                : new Presentation(
                    null,
                    width,
                    height,
                    0,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                    IsSplash: false);
            StartPresenterLocked();
        }
    }

    public static void HideSplashScreen()
    {
        lock (_gate)
        {
            _splashHidden = true;
            if (_closed || _latestPresentation is not { IsSplash: true } latest)
            {
                return;
            }

            var sequence = latest.Sequence + 1;
            _latestPresentation = new Presentation(
                CreateBlackFrame(latest.Width, latest.Height),
                latest.Width,
                latest.Height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false);
            Console.Error.WriteLine("[LOADER][INFO] Vulkan VideoOut hid splash");
        }
    }

    public static void Submit(byte[] bgraFrame, uint width, uint height)
    {
        if (bgraFrame.Length != checked((int)(width * height * 4)))
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                bgraFrame,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    public static void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height)
    {
        if (drawKind == GuestDrawKind.None || width == 0 || height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed ||
                _latestPresentation is { Pixels: null } latest &&
                latest.DrawKind == drawKind &&
                latest.Width == width &&
                latest.Height == height)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                null,
                width,
                height,
                sequence,
                drawKind,
                TranslatedDraw: null,
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    public static void SubmitTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<VulkanGuestDrawTexture> textures,
        IReadOnlyList<VulkanGuestMemoryBuffer> globalMemoryBuffers,
        uint width,
        uint height,
        uint attributeCount,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        VulkanGuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<VulkanGuestVertexBuffer>? vertexBuffers = null,
        VulkanGuestRenderState? renderState = null)
    {
        if (pixelSpirv.Length == 0 || width == 0 || height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                null,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                new VulkanTranslatedGuestDraw(
                    vertexSpirv ?? [],
                    pixelSpirv,
                    textures.ToArray(),
                    globalMemoryBuffers.ToArray(),
                    vertexBuffers?.ToArray() ?? [],
                    attributeCount,
                    vertexCount,
                    instanceCount,
                    primitiveType,
                    indexBuffer,
                    renderState ?? VulkanGuestRenderState.Default),
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    /// <summary>
    /// Marks a guest range as GPU-written (CP DMA destinations, shader UAVs).
    /// Read-only bindings intersecting such ranges must stay live-aliased:
    /// their contents are produced on the GPU timeline, so a parse-time CPU
    /// snapshot would bake stale data (Unity's autoexposure constant, written
    /// by DMA from a compute result, otherwise reads zero and the frame goes
    /// black).
    /// </summary>
    public static void NotifyGpuWritesGuestRange(ulong start, ulong end) =>
        Presenter.RecordGpuWrittenGuestRange(start, end);

    public static bool GpuWritesIntersectGuestRange(ulong start, ulong end) =>
        Presenter.IntersectsGpuWrittenGuestRange(start, end);

    public static void SubmitOffscreenTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<VulkanGuestDrawTexture> textures,
        IReadOnlyList<VulkanGuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        VulkanGuestRenderTarget target,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        VulkanGuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<VulkanGuestVertexBuffer>? vertexBuffers = null,
        VulkanGuestRenderState? renderState = null,
        VulkanGuestDepthTarget? depthTarget = null)
    {
        if (pixelSpirv.Length == 0 ||
            target.Address == 0 ||
            target.Width == 0 ||
            target.Height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var guestTextureFormat = GetGuestTextureFormat(
                target.Format,
                target.NumberType);
            if (guestTextureFormat != 0)
            {
                _availableGuestImages[target.Address] = guestTextureFormat;
            }

            var workSequence = EnqueueGuestWorkLocked(
                new VulkanOffscreenGuestDraw(
                    new VulkanTranslatedGuestDraw(
                        vertexSpirv ?? [],
                        pixelSpirv,
                        textures.ToArray(),
                        globalMemoryBuffers.ToArray(),
                        vertexBuffers?.ToArray() ?? [],
                        attributeCount,
                        vertexCount,
                        instanceCount,
                        primitiveType,
                        indexBuffer,
                        renderState ?? VulkanGuestRenderState.Default),
                    target,
                    depthTarget,
                    PublishTarget: true));
            _guestImageWorkSequences[target.Address] = workSequence;
        }
    }

    public static void SubmitDepthOnlyTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<VulkanGuestDrawTexture> textures,
        IReadOnlyList<VulkanGuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        VulkanGuestDepthTarget depthTarget,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        VulkanGuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<VulkanGuestVertexBuffer>? vertexBuffers = null,
        VulkanGuestRenderState? renderState = null)
    {
        if (pixelSpirv.Length == 0 ||
            depthTarget.Address == 0 ||
            depthTarget.Width == 0 ||
            depthTarget.Height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            EnqueueGuestWorkLocked(
                new VulkanOffscreenGuestDraw(
                    new VulkanTranslatedGuestDraw(
                        vertexSpirv ?? [],
                        pixelSpirv,
                        textures.ToArray(),
                        globalMemoryBuffers.ToArray(),
                        vertexBuffers?.ToArray() ?? [],
                        attributeCount,
                        vertexCount,
                        instanceCount,
                        primitiveType,
                        indexBuffer,
                        renderState ?? VulkanGuestRenderState.Default),
                    new VulkanGuestRenderTarget(
                        Address: 0,
                        depthTarget.Width,
                        depthTarget.Height,
                        Format: 10,
                        NumberType: 0),
                    depthTarget,
                    PublishTarget: false));
        }
    }

    private sealed record VulkanGuestImageWrite(
        ulong Address,
        byte[]? Pixels,
        uint FillValue);

    /// <summary>
    /// Reports the extent of a live guest image so DMA writes to its backing
    /// memory can be mirrored into the Vulkan image (PS5 render targets alias
    /// guest memory, so CP DMA fills/copies are visible to later GPU reads).
    /// </summary>
    internal static bool TryGetGuestImageExtent(
        ulong address,
        out uint width,
        out uint height,
        out ulong byteCount)
    {
        lock (_gate)
        {
            if (_guestImageExtents.TryGetValue(address, out var extent))
            {
                (width, height, byteCount) = extent;
                return true;
            }
        }

        width = 0;
        height = 0;
        byteCount = 0;
        return false;
    }

    internal static void SubmitGuestImageFill(ulong address, uint fillValue)
    {
        lock (_gate)
        {
            if (_closed || !_guestImageExtents.ContainsKey(address))
            {
                return;
            }

            _guestImageWorkSequences[address] = EnqueueGuestWorkLocked(
                new VulkanGuestImageWrite(address, null, fillValue));
        }
    }

    internal static void SubmitGuestImageWrite(ulong address, byte[] pixels)
    {
        lock (_gate)
        {
            if (_closed || !_guestImageExtents.ContainsKey(address))
            {
                return;
            }

            _guestImageWorkSequences[address] = EnqueueGuestWorkLocked(
                new VulkanGuestImageWrite(address, pixels, 0));
        }
    }

    private static long _perfDrawCount;
    private static long _perfDrawTicks;
    private static long _perfPipelineCreations;
    private static long _perfSpirvCompilations;

    internal static (long Draws, double DrawMs, long Pipelines, long SpirvCompilations)
        ReadAndResetPerfCounters()
    {
        var draws = Interlocked.Exchange(ref _perfDrawCount, 0);
        var ticks = Interlocked.Exchange(ref _perfDrawTicks, 0);
        var pipelines = Interlocked.Exchange(ref _perfPipelineCreations, 0);
        var spirv = Interlocked.Exchange(ref _perfSpirvCompilations, 0);
        return (draws, ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, pipelines, spirv);
    }

    internal static void CountSpirvCompilation() =>
        Interlocked.Increment(ref _perfSpirvCompilations);

    internal static IReadOnlyList<(ulong Address, uint Width, uint Height, ulong ByteCount)> GetGuestImageExtents()
    {
        lock (_gate)
        {
            return _guestImageExtents
                .Select(entry => (
                    entry.Key,
                    entry.Value.Width,
                    entry.Value.Height,
                    entry.Value.ByteCount))
                .ToArray();
        }
    }

    public static void SubmitStorageTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<VulkanGuestDrawTexture> textures,
        IReadOnlyList<VulkanGuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        uint width,
        uint height)
    {
        if (pixelSpirv.Length == 0 ||
            width == 0 ||
            height == 0 ||
            textures.All(texture => !texture.IsStorage))
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            EnqueueGuestWorkLocked(
                new VulkanOffscreenGuestDraw(
                    new VulkanTranslatedGuestDraw(
                        [],
                        pixelSpirv,
                        textures.ToArray(),
                        globalMemoryBuffers.ToArray(),
                        [],
                        attributeCount,
                        3,
                        1,
                        4,
                        null,
                        VulkanGuestRenderState.Default),
                    new VulkanGuestRenderTarget(
                        Address: 0,
                        width,
                        height,
                        Format: 12,
                        NumberType: 7),
                    DepthTarget: null,
                    PublishTarget: false));
        }
    }

    public static long SubmitComputeDispatch(
        ulong shaderAddress,
        byte[] computeSpirv,
        IReadOnlyList<VulkanGuestDrawTexture> textures,
        IReadOnlyList<VulkanGuestMemoryBuffer> globalMemoryBuffers,
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ,
        uint baseGroupX,
        uint baseGroupY,
        uint baseGroupZ,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        bool isIndirect,
        bool writesGlobalMemory,
        uint threadCountX = uint.MaxValue,
        uint threadCountY = uint.MaxValue,
        uint threadCountZ = uint.MaxValue)
    {
        if (computeSpirv.Length == 0 ||
            groupCountX == 0 ||
            groupCountY == 0 ||
            groupCountZ == 0 ||
            textures.All(texture => !texture.IsStorage) &&
            !writesGlobalMemory)
        {
            return 0;
        }

        long workSequence;
        lock (_gate)
        {
            if (_closed)
            {
                return 0;
            }

            workSequence = EnqueueGuestWorkLocked(
                new VulkanComputeGuestDispatch(
                    shaderAddress,
                    computeSpirv,
                    textures.ToArray(),
                    globalMemoryBuffers.ToArray(),
                    groupCountX,
                    groupCountY,
                    groupCountZ,
                    baseGroupX,
                    baseGroupY,
                    baseGroupZ,
                    localSizeX,
                    localSizeY,
                    localSizeZ,
                    isIndirect,
                    writesGlobalMemory,
                    threadCountX,
                    threadCountY,
                    threadCountZ));
            foreach (var key in GetStorageImageUploadKeys(textures))
            {
                _pendingGuestImageUploads[key] =
                    _pendingGuestImageUploads.TryGetValue(key, out var count)
                        ? checked(count + 1)
                        : 1;
            }

            foreach (var texture in textures)
            {
                if (texture.IsStorage && texture.Address != 0)
                {
                    _guestImageWorkSequences[texture.Address] = workSequence;
                }
            }
        }

        return workSequence;
    }

    /// <summary>
    /// Enqueues a CPU-visible PM4 side effect behind all GPU work submitted
    /// before it. The render thread flushes its open batch and waits for the
    /// corresponding guest fences before invoking the action.
    /// </summary>
    public static long SubmitOrderedGuestAction(Action action, string debugName)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            return _closed || _thread is null
                ? 0
                : EnqueueGuestWorkLocked(new VulkanOrderedGuestAction(action, debugName));
        }
    }

    public static bool WaitForGuestWork(
        long workSequence,
        int timeoutMilliseconds = System.Threading.Timeout.Infinite)
    {
        if (workSequence <= 0)
        {
            return false;
        }

        var waitIndefinitely = timeoutMilliseconds == System.Threading.Timeout.Infinite;
        var deadline = waitIndefinitely
            ? long.MaxValue
            : Environment.TickCount64 + Math.Max(timeoutMilliseconds, 1);
        lock (_gate)
        {
            while (!_closed && _completedGuestWorkSequence < workSequence)
            {
                if (!waitIndefinitely)
                {
                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] Vulkan guest work wait timed out " +
                            $"sequence={workSequence} completed={_completedGuestWorkSequence}");
                        return false;
                    }

                    System.Threading.Monitor.Wait(
                        _gate,
                        checked((int)Math.Min(remaining, 1_000)));
                    continue;
                }

                // CPU-visible GPU writes are ordering points in the guest
                // command stream. First-use shader compilation can take more
                // than a minute on MoltenVK; timing out would let the guest
                // consume stale zero-filled buffers and permanently corrupt
                // the frame. Closing the presenter pulses this monitor, so an
                // unbounded correctness wait remains interruptible.
                System.Threading.Monitor.Wait(_gate, 1_000);
            }

            return _completedGuestWorkSequence >= workSequence;
        }
    }

    public static bool TrySubmitGuestImage(
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel)
    {
        var traceSubmission = false;
        lock (_gate)
        {
            if (_closed ||
                !_availableGuestImages.ContainsKey(address))
            {
                return false;
            }

            traceSubmission =
                _tracedGuestImageSubmissions.Add((address, width, height));
            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            var requiredWorkSequence = _guestImageWorkSequences.TryGetValue(
                address,
                out var imageWorkSequence)
                ? imageWorkSequence
                : _completedGuestWorkSequence;
            var presentation = new Presentation(
                null,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                // Wait only for the work that last wrote this image, not for
                // every later command the guest has already queued. Requiring
                // the global tail makes a fast guest permanently outrun the
                // renderer and turns every flip into a dropped black frame.
                RequiredGuestWorkSequence: requiredWorkSequence,
                IsSplash: false,
                GuestImageAddress: address);
            _latestPresentation = presentation;
            _pendingGuestImagePresentations.Enqueue(presentation);
            while (_pendingGuestImagePresentations.Count > _maxPendingGuestWork)
            {
                _pendingGuestImagePresentations.Dequeue();
            }
        }

        if (traceSubmission)
        {
            var effectivePitch = pitchInPixel == 0 ? width : pitchInPixel;
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.submit_guest_image addr=0x{address:X16} " +
                $"size={width}x{height} pitch={effectivePitch}");
        }

        return true;
    }

    /// <summary>
    /// On PS5 a render target aliases guest memory, so CPU-prefilled pixels are
    /// visible before the first draw. Our Vulkan images start undefined, so the
    /// first draw into a new address must seed the image from guest memory.
    /// </summary>
    internal static bool GuestImageWantsInitialData(ulong address)
    {
        if (address == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return !_availableGuestImages.ContainsKey(address) &&
                !_pendingGuestImageInitialData.ContainsKey(address);
        }
    }

    internal static void ProvideGuestImageInitialData(ulong address, byte[] rgbaPixels)
    {
        lock (_gate)
        {
            _pendingGuestImageInitialData[address] = rgbaPixels;
        }
    }

    private static byte[]? TakeGuestImageInitialData(ulong address)
    {
        lock (_gate)
        {
            if (!_pendingGuestImageInitialData.TryGetValue(address, out var data))
            {
                return null;
            }

            _pendingGuestImageInitialData.Remove(address);
            return data;
        }
    }

    // Mirror of the render thread's texture-cache identities, readable from
    // the guest submit thread. The AGC translator used to allocate and copy
    // every referenced texture's texels out of guest memory on every draw,
    // only for the presenter to discard the bytes on a cache hit — for a
    // scene sampling large textures this was by far the dominant CPU cost
    // (gigabytes/second of allocation, page faults and GC pressure).
    // ConcurrentDictionary keyed set: reads happen per texture per draw on
    // the guest submit thread and must not contend with the render thread's
    // mutations.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        TextureContentIdentity, byte> _cachedTextureIdentities = new();

    internal readonly record struct TextureContentIdentity(
        ulong Address,
        uint Width,
        uint Height,
        uint Format,
        uint NumberType,
        uint DstSelect,
        uint TileMode,
        uint Pitch,
        VulkanGuestSampler Sampler);

    // Guest memory handle for render-thread self-healing: when a draw whose
    // texel copy was skipped misses the texture cache (eviction, cache
    // clear, or any other race), the presenter re-reads the texels itself
    // instead of showing a fallback pattern.
    private static volatile SharpEmu.HLE.ICpuMemory? _guestMemory;

    internal static void AttachGuestMemory(SharpEmu.HLE.ICpuMemory memory) =>
        _guestMemory = memory;

    internal static bool IsTextureContentCached(in TextureContentIdentity identity) =>
        _cachedTextureIdentities.ContainsKey(identity);

    private static void MarkTextureContentCached(in TextureContentIdentity identity) =>
        _cachedTextureIdentities.TryAdd(identity, 0);

    private static void UnmarkTextureContentCached(in TextureContentIdentity identity) =>
        _cachedTextureIdentities.TryRemove(identity, out _);

    private static void ClearCachedTextureIdentities() =>
        _cachedTextureIdentities.Clear();

    internal static bool IsGuestImageAvailable(
        ulong address,
        uint format,
        uint numberType)
    {
        var guestFormat = GetGuestTextureFormat(format, numberType);
        if (address == 0 || guestFormat == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _availableGuestImages.TryGetValue(address, out var availableFormat) &&
                availableFormat == guestFormat;
        }
    }

    /// <summary>
    /// Returns whether a storage image already exists on the presenter or an
    /// earlier queued dispatch owns its one-time guest-memory initialization.
    /// This is intentionally separate from <see cref="IsGuestImageAvailable"/>:
    /// a pending image may skip a duplicate upload but is not yet safe for a
    /// flip/presentation lookup.
    /// </summary>
    internal static bool IsGuestImageUploadKnown(
        ulong address,
        uint format,
        uint numberType)
    {
        var guestFormat = GetGuestTextureFormat(format, numberType);
        if (address == 0 || guestFormat == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _availableGuestImages.TryGetValue(address, out var availableFormat) &&
                    availableFormat == guestFormat ||
                _pendingGuestImageUploads.ContainsKey((address, guestFormat));
        }
    }

    public static bool TrySubmitGuestImageBlit(
        ulong sourceAddress,
        uint sourceWidth,
        uint sourceHeight,
        uint sourceFormat,
        ulong destinationAddress,
        uint destinationWidth,
        uint destinationHeight,
        uint destinationFormat) =>
        TrySubmitGuestImageBlit(
            sourceAddress,
            sourceWidth,
            sourceHeight,
            sourceFormat,
            sourceNumberType: 0,
            destinationAddress,
            destinationWidth,
            destinationHeight,
            destinationFormat,
            destinationNumberType: 0);

    public static bool TrySubmitGuestImageBlit(
        ulong sourceAddress,
        uint sourceWidth,
        uint sourceHeight,
        uint sourceFormat,
        uint sourceNumberType,
        ulong destinationAddress,
        uint destinationWidth,
        uint destinationHeight,
        uint destinationFormat,
        uint destinationNumberType)
    {
        if (sourceAddress == 0 ||
            destinationAddress == 0 ||
            sourceWidth == 0 ||
            sourceHeight == 0 ||
            destinationWidth == 0 ||
            destinationHeight == 0 ||
            !TryGetCopyFragmentShader(out var fragmentSpirv))
        {
            return false;
        }

        lock (_gate)
        {
            if (_closed ||
                !_availableGuestImages.ContainsKey(sourceAddress) ||
                GetGuestTextureFormat(destinationFormat, destinationNumberType) == 0)
            {
                return false;
            }
        }

        SubmitOffscreenTranslatedDraw(
            fragmentSpirv,
            [
                new VulkanGuestDrawTexture(
                    sourceAddress,
                    sourceWidth,
                    sourceHeight,
                    sourceFormat,
                    sourceNumberType,
                    [],
                    IsFallback: false,
                    IsStorage: false),
            ],
            [],
            attributeCount: 1,
            new VulkanGuestRenderTarget(
                destinationAddress,
                destinationWidth,
                destinationHeight,
                destinationFormat,
                destinationNumberType));
        return true;
    }

    private static bool TryGetCopyFragmentShader(out byte[] spirv)
    {
        lock (_gate)
        {
            if (_copyFragmentSpirv is not null)
            {
                spirv = _copyFragmentSpirv;
                return true;
            }
        }

        spirv = SpirvFixedShaders.CreateCopyFragment();

        lock (_gate)
        {
            _copyFragmentSpirv ??= spirv;
            spirv = _copyFragmentSpirv;
        }

        return true;
    }

    private static uint GetGuestTextureFormat(uint format, uint numberType) =>
        IsKnownGuestTextureFormat(format)
            ? 0x8000_0000u | ((format & 0x1FFu) << 8) | (numberType & 0xFFu)
            : 0;

    private static bool IsKnownGuestTextureFormat(uint format) =>
        format is >= 1 and <= 19 or 34 or >= 169 and <= 182;

    private static byte[] CreateBlackFrame(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > 8192 || height > 8192)
        {
            width = 1;
            height = 1;
        }

        var pixels = GC.AllocateUninitializedArray<byte>(checked((int)(width * height * 4)));
        pixels.AsSpan().Clear();
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0xFF;
        }

        return pixels;
    }

    private static void StartPresenterLocked()
    {
        if (HostMainThread.IsAvailable)
        {
            // AppKit (and therefore GLFW) traps when touched off the process
            // main thread on macOS, so hand the whole window loop to the
            // main-thread pump the CLI parked for us. _thread only marks the
            // presenter as running; Run() clears it on exit either way.
            _thread = Thread.CurrentThread;
            HostMainThread.SetShutdownRequestHandler(RequestClose);
            HostMainThread.Post(Run);
            return;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SharpEmu Vulkan VideoOut",
        };
        _thread.Start();
    }

    /// <summary>
    /// Asks a running presenter to close its window; used at emulator
    /// shutdown so a main-thread-hosted window loop returns to the pump.
    /// </summary>
    public static void RequestClose()
    {
        Volatile.Write(ref _presenterCloseRequested, true);
    }

    /// <summary>
    /// GLFW resolves Vulkan with dlopen("libvulkan.1.dylib"), which cannot
    /// find the app-local MoltenVK on macOS (Homebrew's Vulkan libraries are
    /// arm64-only and this is an x86-64 process). GLFW 3.4 accepts the
    /// loader entry point directly instead, so hand it MoltenVK's
    /// vkGetInstanceProcAddr before any window exists.
    /// </summary>
    private static unsafe void InitializeMacVulkanLoader()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            nint vulkan = 0;
            foreach (var candidate in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "libvulkan.1.dylib"),
                Path.Combine(AppContext.BaseDirectory, "libMoltenVK.dylib"),
                "libvulkan.1.dylib",
                "libMoltenVK.dylib",
            })
            {
                if (System.Runtime.InteropServices.NativeLibrary.TryLoad(candidate, out vulkan))
                {
                    break;
                }
            }

            if (vulkan == 0 ||
                !System.Runtime.InteropServices.NativeLibrary.TryGetExport(
                    vulkan, "vkGetInstanceProcAddr", out var procAddr))
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] No Vulkan loader for GLFW; place a universal libMoltenVK.dylib " +
                    "next to SharpEmu as libvulkan.1.dylib.");
                return;
            }

            var glfw = System.Runtime.InteropServices.NativeLibrary.Load(
                Path.Combine(AppContext.BaseDirectory, "libglfw.3.dylib"));
            var initVulkanLoader = (delegate* unmanaged<nint, void>)
                System.Runtime.InteropServices.NativeLibrary.GetExport(glfw, "glfwInitVulkanLoader");
            initVulkanLoader(procAddr);
            Console.Error.WriteLine("[LOADER][INFO] GLFW Vulkan loader wired to MoltenVK.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] GLFW Vulkan loader setup failed: {exception.Message}");
        }
    }

    private static void Run()
    {
        uint width;
        uint height;
        lock (_gate)
        {
            width = _windowWidth == 0 ? _latestPresentation?.Width ?? 1280 : _windowWidth;
            height = _windowHeight == 0 ? _latestPresentation?.Height ?? 720 : _windowHeight;
        }

        InitializeMacVulkanLoader();

        try
        {
            using var presenter = new Presenter(width, height);
            presenter.Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][ERROR] Vulkan VideoOut presenter failed: {exception}");
        }
        finally
        {
            lock (_gate)
            {
                _closed = true;
                _thread = null;
                System.Threading.Monitor.PulseAll(_gate);
            }
        }
    }

    private static bool TryTakePresentation(long presentedSequence, out Presentation presentation)
    {
        lock (_gate)
        {
            // Guest flips are retained in submission order. The renderer is
            // deliberately allowed to lag a frame or two behind the guest
            // while it drains expensive work, so use the first completed flip
            // rather than repeatedly asking only for the newest one.
            while (_pendingGuestImagePresentations.Count > 0 &&
                   _pendingGuestImagePresentations.Peek().Sequence <= presentedSequence)
            {
                _pendingGuestImagePresentations.Dequeue();
            }

            if (_pendingGuestImagePresentations.Count > 0)
            {
                var pending = _pendingGuestImagePresentations.Peek();
                if (pending.RequiredGuestWorkSequence <= _completedGuestWorkSequence)
                {
                    presentation = _pendingGuestImagePresentations.Dequeue();
                    TryReplaceWithBinkFrame(ref presentation);
                    return true;
                }

                presentation = default;
                return false;
            }

            if (_latestPresentation is not { } latest ||
                latest.Sequence == presentedSequence ||
                latest.RequiredGuestWorkSequence > _completedGuestWorkSequence)
            {
                if (_latestPresentation is { } rej &&
                    rej.GuestImageAddress != 0 &&
                    _tracedGuestImagePresentRejections.Add(rej.Sequence))
                {
                    var reason = rej.Sequence == presentedSequence
                        ? "already-presented(seq==presented)"
                        : rej.RequiredGuestWorkSequence > _completedGuestWorkSequence
                            ? $"work-not-done(req={rej.RequiredGuestWorkSequence}>done={_completedGuestWorkSequence})"
                            : "unknown";
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] vk.guest_present_rejected addr=0x{rej.GuestImageAddress:X16} " +
                        $"seq={rej.Sequence} presentedSeq={presentedSequence} reason={reason}");
                }

                presentation = default;
                return false;
            }

            presentation = latest;
            TryReplaceWithBinkFrame(ref presentation);
            return true;
        }
    }

    private static void TryReplaceWithBinkFrame(ref Presentation presentation)
    {
        if (!Bink2MovieBridge.TryDecodeNextFrame(out var pixels, out var width, out var height))
        {
            return;
        }

        presentation = new Presentation(
            pixels,
            width,
            height,
            presentation.Sequence,
            GuestDrawKind.None,
            TranslatedDraw: null,
            presentation.RequiredGuestWorkSequence,
            IsSplash: false);
    }

    private static readonly HashSet<long> _tracedGuestImagePresentRejections = new();

    private static long EnqueueGuestWorkLocked(object work)
    {
        var payloadBytes = GetGuestWorkPayloadBytes(work);
        var backpressureLogged = false;
        var backpressureStarted = 0L;
        while (!_closed &&
               _thread is not null &&
               (_pendingGuestWork.Count >= _maxPendingGuestWork ||
                // Always admit one item when no payload is outstanding, even
                // when that single item exceeds the configured budget. This
                // avoids an impossible wait while still bounding the normal
                // multi-item backlog.
                (_pendingGuestWorkBytes != 0 &&
                 payloadBytes > _maxPendingGuestWorkBytes -
                     Math.Min(_pendingGuestWorkBytes, _maxPendingGuestWorkBytes))))
        {
            if (!backpressureLogged)
            {
                backpressureLogged = true;
                backpressureStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.guest_queue_backpressure " +
                    $"queued={_pendingGuestWork.Count} " +
                    $"count_budget={_maxPendingGuestWork} " +
                    $"retained_mb={_pendingGuestWorkBytes / (1024 * 1024)} " +
                    $"incoming_mb={payloadBytes / (1024 * 1024)} " +
                    $"budget_mb={_maxPendingGuestWorkBytes / (1024 * 1024)} " +
                    $"work={work.GetType().Name} " +
                    $"active='{Volatile.Read(ref _activeGuestWorkDescription) ?? "none"}'");
            }

            System.Threading.Monitor.Wait(_gate);
        }

        if (backpressureLogged)
        {
            var waitMilliseconds =
                (System.Diagnostics.Stopwatch.GetTimestamp() - backpressureStarted) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency;
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.guest_queue_backpressure_released " +
                $"wait_ms={waitMilliseconds:F1} " +
                $"queued={_pendingGuestWork.Count} " +
                $"work={work.GetType().Name}");
        }

        if (_closed)
        {
            return 0;
        }

        _pendingGuestWork.Enqueue(new PendingGuestWork(work, payloadBytes));
        _pendingGuestWorkBytes = SaturatingAdd(_pendingGuestWorkBytes, payloadBytes);
        return ++_enqueuedGuestWorkSequence;
    }

    private static bool TryTakeGuestWork(out PendingGuestWork work)
    {
        lock (_gate)
        {
            return _pendingGuestWork.TryDequeue(out work!);
        }
    }

    private static void CompleteGuestWork(in PendingGuestWork pending)
    {
        lock (_gate)
        {
            _pendingGuestWorkBytes = pending.PayloadBytes >= _pendingGuestWorkBytes
                ? 0
                : _pendingGuestWorkBytes - pending.PayloadBytes;
            ReleasePendingGuestImageUploadsLocked(pending.Work);
            _completedGuestWorkSequence++;
            System.Threading.Monitor.PulseAll(_gate);
        }
    }

    private static ulong GetGuestWorkPayloadBytes(object work) => work switch
    {
        VulkanComputeGuestDispatch compute => SaturatingAdd(
            GetTexturePayloadBytes(compute.Textures),
            GetGlobalBufferPayloadBytes(compute.GlobalMemoryBuffers)),
        VulkanOffscreenGuestDraw offscreen => GetDrawPayloadBytes(offscreen.Draw),
        VulkanGuestImageWrite { Pixels: { } pixels } => (ulong)pixels.LongLength,
        _ => 0,
    };

    private static ulong GetDrawPayloadBytes(VulkanTranslatedGuestDraw draw)
    {
        var bytes = GetTexturePayloadBytes(draw.Textures);
        bytes = SaturatingAdd(bytes, GetGlobalBufferPayloadBytes(draw.GlobalMemoryBuffers));
        foreach (var vertex in draw.VertexBuffers)
        {
            bytes = SaturatingAdd(bytes, (ulong)vertex.Data.LongLength);
        }

        if (draw.IndexBuffer is { } index)
        {
            bytes = SaturatingAdd(bytes, (ulong)index.Data.LongLength);
        }

        return bytes;
    }

    private static ulong GetTexturePayloadBytes(
        IReadOnlyList<VulkanGuestDrawTexture> textures)
    {
        var bytes = 0UL;
        foreach (var texture in textures)
        {
            bytes = SaturatingAdd(bytes, (ulong)texture.RgbaPixels.LongLength);
        }

        return bytes;
    }

    private static ulong GetGlobalBufferPayloadBytes(
        IReadOnlyList<VulkanGuestMemoryBuffer> buffers)
    {
        var bytes = 0UL;
        foreach (var buffer in buffers)
        {
            bytes = SaturatingAdd(bytes, (ulong)buffer.Data.LongLength);
        }

        return bytes;
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static void ReleasePendingGuestImageUploadsLocked(object work)
    {
        if (work is not VulkanComputeGuestDispatch compute)
        {
            return;
        }

        foreach (var key in GetStorageImageUploadKeys(compute.Textures))
        {
            if (!_pendingGuestImageUploads.TryGetValue(key, out var count))
            {
                continue;
            }

            if (count <= 1)
            {
                _pendingGuestImageUploads.Remove(key);
            }
            else
            {
                _pendingGuestImageUploads[key] = count - 1;
            }
        }
    }

    private static HashSet<(ulong Address, uint Format)> GetStorageImageUploadKeys(
        IReadOnlyList<VulkanGuestDrawTexture> textures)
    {
        var keys = new HashSet<(ulong Address, uint Format)>();
        foreach (var texture in textures)
        {
            if (!texture.IsStorage || texture.Address == 0)
            {
                continue;
            }

            var format = GetGuestTextureFormat(texture.Format, texture.NumberType);
            if (format != 0)
            {
                keys.Add((texture.Address, format));
            }
        }

        return keys;
    }

    private readonly record struct Presentation(
        byte[]? Pixels,
        uint Width,
        uint Height,
        long Sequence,
        GuestDrawKind DrawKind,
        VulkanTranslatedGuestDraw? TranslatedDraw,
        long RequiredGuestWorkSequence,
        bool IsSplash,
        ulong GuestImageAddress = 0);

    private sealed class Presenter : IDisposable
    {
        private const string FullscreenBarycentricVertexSpirv =
            "AwIjBwAAAQALAAgAMgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ACAAAAAAABAAAAG1haW4AAAAADQAAABoAAAApAAAAAwADAAIAAADCAQAABQAEAAQAAABtYWluAAAAAAUABgALAAAAZ2xfUGVyVmVydGV4AAAAAAYABgALAAAAAAAAAGdsX1Bvc2l0aW9uAAYABwALAAAAAQAAAGdsX1BvaW50U2l6ZQAAAAAGAAcACwAAAAIAAABnbF9DbGlwRGlzdGFuY2UABgAHAAsAAAADAAAAZ2xfQ3VsbERpc3RhbmNlAAUAAwANAAAAAAAAAAUABgAaAAAAZ2xfVmVydGV4SW5kZXgAAAUABQAdAAAAaW5kZXhhYmxlAAAABQAFACkAAABiYXJ5Y2VudHJpYwAFAAUALwAAAGluZGV4YWJsZQAAAEcAAwALAAAAAgAAAEgABQALAAAAAAAAAAsAAAAAAAAASAAFAAsAAAABAAAACwAAAAEAAABIAAUACwAAAAIAAAALAAAAAwAAAEgABQALAAAAAwAAAAsAAAAEAAAARwAEABoAAAALAAAAKgAAAEcABAApAAAAHgAAAAAAAAATAAIAAgAAACEAAwADAAAAAgAAABYAAwAGAAAAIAAAABcABAAHAAAABgAAAAQAAAAVAAQACAAAACAAAAAAAAAAKwAEAAgAAAAJAAAAAQAAABwABAAKAAAABgAAAAkAAAAeAAYACwAAAAcAAAAGAAAACgAAAAoAAAAgAAQADAAAAAMAAAALAAAAOwAEAAwAAAANAAAAAwAAABUABAAOAAAAIAAAAAEAAAArAAQADgAAAA8AAAAAAAAAFwAEABAAAAAGAAAAAgAAACsABAAIAAAAEQAAAAMAAAAcAAQAEgAAABAAAAARAAAAKwAEAAYAAAATAAAAAACAvywABQAQAAAAFAAAABMAAAATAAAAKwAEAAYAAAAVAAAAAABAQCwABQAQAAAAFgAAABUAAAATAAAALAAFABAAAAAXAAAAEwAAABUAAAAsAAYAEgAAABgAAAAUAAAAFgAAABcAAAAgAAQAGQAAAAEAAAAOAAAAOwAEABkAAAAaAAAAAQAAACAABAAcAAAABwAAABIAAAAgAAQAHgAAAAcAAAAQAAAAKwAEAAYAAAAhAAAAAAAAACsABAAGAAAAIgAAAAAAgD8gAAQAJgAAAAMAAAAHAAAAIAAEACgAAAADAAAAEAAAADsABAAoAAAAKQAAAAMAAAAsAAUAEAAAACoAAAAiAAAAIQAAACwABQAQAAAAKwAAACEAAAAiAAAALAAFABAAAAAsAAAAIQAAACEAAAAsAAYAEgAAAC0AAAAqAAAAKwAAACwAAAA2AAUAAgAAAAQAAAAAAAAAAwAAAPgAAgAFAAAAOwAEABwAAAAdAAAABwAAADsABAAcAAAALwAAAAcAAAA9AAQADgAAABsAAAAaAAAAPgADAB0AAAAYAAAAQQAFAB4AAAAfAAAAHQAAABsAAAA9AAQAEAAAACAAAAAfAAAAUQAFAAYAAAAjAAAAIAAAAAAAAABRAAUABgAAACQAAAAgAAAAAQAAAFAABwAHAAAAJQAAACMAAAAkAAAAIQAAACIAAABBAAUAJgAAACcAAAANAAAADwAAAD4AAwAnAAAAJQAAAD0ABAAOAAAALgAAABoAAAA+AAMALwAAAC0AAABBAAUAHgAAADAAAAAvAAAALgAAAD0ABAAQAAAAMQAAADAAAAA+AAMAKQAAADEAAAD9AAEAOAABAA==";

        private const string FullscreenBarycentricFragmentSpirv =
            "AwIjBwAAAQALAAgAEgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ABwAEAAAABAAAAG1haW4AAAAACQAAAAwAAAAQAAMABAAAAAcAAAADAAMAAgAAAMIBAAAFAAQABAAAAG1haW4AAAAABQAFAAkAAABvdXRDb2xvcgAAAAAFAAUADAAAAGJhcnljZW50cmljAEcABAAJAAAAHgAAAAAAAABHAAQADAAAAB4AAAAAAAAAEwACAAIAAAAhAAMAAwAAAAIAAAAWAAMABgAAACAAAAAXAAQABwAAAAYAAAAEAAAAIAAEAAgAAAADAAAABwAAADsABAAIAAAACQAAAAMAAAAXAAQACgAAAAYAAAACAAAAIAAEAAsAAAABAAAACgAAADsABAALAAAADAAAAAEAAAArAAQABgAAAA4AAAAAAAAANgAFAAIAAAAEAAAAAAAAAAMAAAD4AAIABQAAAD0ABAAKAAAADQAAAAwAAABRAAUABgAAAA8AAAANAAAAAAAAAFEABQAGAAAAEAAAAA0AAAABAAAAUAAHAAcAAAARAAAADwAAABAAAAAOAAAADgAAAD4AAwAJAAAAEQAAAP0AAQA4AAEA";

        private readonly IWindow _window;
        private const int MaxInFlightGuestSubmissions = 8;
        private static readonly bool _flushEveryGuestDraw =
            string.Equals(
                Environment.GetEnvironmentVariable(
                    "SHARPEMU_FLUSH_EVERY_GUEST_DRAW"),
                "1",
                StringComparison.Ordinal);
        private Vk _vk = null!;
        private KhrSurface _surfaceApi = null!;
        private KhrSwapchain _swapchainApi = null!;
        private delegate* unmanaged<Device, DebugUtilsObjectNameInfoEXT*, Result> _setDebugUtilsObjectName;
        private delegate* unmanaged<CommandBuffer, DebugUtilsLabelEXT*, void> _cmdBeginDebugUtilsLabel;
        private delegate* unmanaged<CommandBuffer, void> _cmdEndDebugUtilsLabel;
        private Instance _instance;
        private SurfaceKHR _surface;
        private DebugUtilsMessengerEXT _debugMessenger;
        private ExtDebugUtils? _debugUtils;
        private PhysicalDevice _physicalDevice;
        private uint _maxComputeWorkGroupCountX;
        private uint _maxComputeWorkGroupCountY;
        private uint _maxComputeWorkGroupCountZ;
        private uint _maxComputeWorkGroupSizeX;
        private uint _maxComputeWorkGroupSizeY;
        private uint _maxComputeWorkGroupSizeZ;
        private uint _maxComputeWorkGroupInvocations;
        private ulong _minStorageBufferOffsetAlignment = 1;
        private Device _device;
        private PipelineCache _pipelineCache;
        private string? _pipelineCachePath;
        private bool _pipelineCacheDirty;
        private long _lastPipelineCacheSaveTick;
        private Queue _queue;
        private uint _queueFamilyIndex;
        private SwapchainKHR _swapchain;
        private Image[] _swapchainImages = [];
        private ImageView[] _swapchainImageViews = [];
        private Framebuffer[] _framebuffers = [];
        private bool[] _imageInitialized = [];
        private Format _swapchainFormat;
        private Extent2D _extent;
        private RenderPass _renderPass;
        private PipelineLayout _pipelineLayout;
        private Pipeline _barycentricPipeline;
        private CommandPool _commandPool;
        private CommandBuffer _commandBuffer;
        private CommandBuffer _presentationCommandBuffer;
        // Presentation runs with multiple frames in flight: each frame slot
        // owns a command buffer, an acquire semaphore and a fence, so the CPU
        // can record frame N+1 while the GPU still executes frame N. The
        // per-present vkQueueWaitIdle this replaces made CPU and GPU costs
        // strictly additive. Render-finished semaphores are per swapchain
        // image because the presentation engine may still wait on them after
        // the frame fence has signaled.
        private const int MaxFramesInFlight = 2;
        private CommandBuffer[] _frameCommandBuffers = [];
        private VkSemaphore[] _frameImageAvailable = [];
        private VkSemaphore[] _renderFinishedPerImage = [];
        private Fence[] _frameFences = [];
        private bool[] _frameFencePending = [];
        private ulong[] _frameTimelines = [];
        private TranslatedDrawResources?[] _frameTranslatedResources = [];
        private int _currentFrameSlot;
        // Monotonic submission/completion counters across every queue submit
        // (guest batches, compute chunks and presents). Fences on a single
        // queue signal in submission order, so "timeline <= completed" means
        // the GPU is done with everything submitted up to that point; this
        // lets evicted resources be destroyed without a queue drain.
        private ulong _submitTimeline;
        private ulong _completedTimeline;
        private readonly Queue<(TextureResource Texture, ulong RetireTimeline)>
            _deferredTextureDestroys = new();
        private readonly Queue<(TranslatedDrawResources Resources, ulong RetireTimeline)>
            _deferredResourceDestroys = new();
        private readonly Stack<Fence> _recycledGuestFences = new();
        private readonly Stack<CommandBuffer> _recycledGuestCommandBuffers = new();
        private readonly List<(VkBuffer Buffer, DeviceMemory Memory)> _batchRetireBuffers = new();
        private const int MaxRecycledGuestFences = 32;
        private const int MaxRecycledGuestCommandBuffers = 32;
        private VkBuffer _stagingBuffer;
        private DeviceMemory _stagingMemory;
        private ulong _stagingSize;
        // Perf overlay: CPU-rasterized panel copied through per-slot staging
        // buffers into one image, then blitted onto the swapchain.
        private Image _overlayImage;
        private DeviceMemory _overlayImageMemory;
        private bool _overlayImageInitialized;
        private VkBuffer[] _overlayStagingBuffers = [];
        private DeviceMemory[] _overlayStagingMemory = [];
        private nint[] _overlayStagingMapped = [];
        private long _presentedSequence;
        private long _presentNotTakenLoggedSequence = long.MinValue;
        private bool _vulkanReady;
        private bool _firstFramePresented;
        private bool _firstGuestDrawPresented;
        private bool _splashPresented;
        private bool _swapchainRecreateDeferred;
        private bool _tracedPresentedSwapchain;
        private bool _swapchainReadbackPending;
        private static int _guestImageDumpSequence;
        private readonly System.Collections.Concurrent.ConcurrentQueue<GuestImageResource> _pendingAliasImageDumps = new();
        private bool _deviceLost;
        private bool _deviceLostLogged;
        private int _directPresentationCount;
        private readonly Dictionary<ulong, GuestImageResource> _guestImages = new();
        private readonly record struct GuestDepthKey(
            ulong Address,
            ulong ReadAddress,
            uint Width,
            uint Height,
            uint GuestFormat,
            uint SwizzleMode);

        private readonly Dictionary<GuestDepthKey, GuestDepthResource> _guestDepthImages = new();
        private readonly Dictionary<GuestDepthKey, ulong> _depthOnlyColorAddresses = new();
        private ulong _nextDepthOnlyColorAddress = 0xFFFF_FF00_0000_0000UL;
        private readonly HashSet<(ulong Address, uint Width, uint Height, Format Format)> _tracedTextureCacheHits = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height, uint DstSelect)> _tracedDepthTextureAliases = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height, Format Format)> _tracedTextureUploads = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height, uint Format)> _dumpedTextures = new();
        private readonly HashSet<(ulong Address, int Size)> _tracedGlobalBuffers = new();
        private readonly HashSet<(ulong Address, ulong Size)> _tracedGlobalWritebacks = new();
        private readonly HashSet<(ulong Shader, uint X, uint Y, uint Z, string Reason)>
            _rejectedComputeDispatches = new();
        private int _tracedSmallGlobalWritebackEvents;
        private int _tracedLargeGlobalWritebackEvents;
        private readonly HashSet<ulong> _tracedGuestImageContents = new();
        private readonly Dictionary<ulong, int> _tracedGuestWriteCounts = new();
        private static readonly long _traceCopyExecutionPeriod = long.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_COPY_EXEC_EVERY"),
            out var parsedCopyExecutionPeriod) && parsedCopyExecutionPeriod > 0
            ? parsedCopyExecutionPeriod
            : 0;
        private long _traceCopyExecutionCount;
        private long _traceExposureExecutionCount;
        private readonly Dictionary<ulong, Queue<string>> _recentGuestDrawsByTarget = new();
        private readonly Dictionary<ulong, HashSet<ulong>> _recentGuestDrawSourcesByTarget = new();
        private readonly HashSet<ulong> _guestDisplayBufferAddresses = [];
        private ulong _latestFullscreenSceneCandidateAddress;
        private bool _loggedFullscreenSceneRedirect;
        private int _tracedVertexBufferCount;
        // Compute translation can produce an equivalent new byte array on a
        // later submit. Reference identity turns that into an expensive new
        // MoltenVK pipeline compilation every frame, so key the cache by the
        // program content and descriptor-layout shape instead.
        private readonly Dictionary<ComputePipelineKey, Pipeline> _computePipelines = new();
        private readonly Dictionary<GraphicsPipelineKey, Pipeline> _graphicsPipelines = new();
        private readonly Dictionary<VulkanGuestSampler, Sampler> _samplers = new();
        private readonly Dictionary<byte[], string> _shaderDigests =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<DescriptorLayoutKey, DescriptorLayoutBundle>
            _descriptorLayouts = new();
        private readonly Dictionary<HostBufferPoolKey, Stack<HostBufferAllocation>>
            _hostBufferPool = new();
        private readonly Dictionary<ulong, HostBufferAllocation> _hostBufferAllocations = new();
        private readonly List<GuestBufferAllocation> _guestBufferAllocations = [];
        private readonly Queue<PendingGuestSubmission> _pendingGuestSubmissions = new();
        private readonly Stack<DescriptorPool> _recycledDescriptorPools = new();

        private readonly record struct GraphicsPipelineKey(
            string VertexShader,
            string FragmentShader,
            ulong RenderPass,
            PrimitiveTopology Topology,
            VulkanGuestBlendState Blend,
            string ResourceLayout,
            string VertexLayout,
            VulkanGuestRasterState Raster,
            VulkanGuestDepthState Depth);

        private readonly record struct HostBufferPoolKey(
            BufferUsageFlags Usage,
            ulong Capacity);

        private readonly record struct DescriptorLayoutKey(
            ShaderStageFlags Stages,
            string Resources);

        private readonly record struct ComputePipelineKey(
            string ShaderDigest,
            string Resources);

        private sealed record DescriptorLayoutBundle(
            DescriptorSetLayout DescriptorSetLayout,
            PipelineLayout PipelineLayout);

        private sealed record HostBufferAllocation(
            VkBuffer Buffer,
            DeviceMemory Memory,
            HostBufferPoolKey Key,
            nint Mapped);

        private readonly record struct DirtyGuestBufferRange(ulong Offset, ulong Length);

        private sealed class GuestBufferAllocation
        {
            public ulong BaseAddress;
            public ulong Size;
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public nint Mapped;
            public byte[] Shadow = [];
            public ulong LastUseTimeline;
            public List<DirtyGuestBufferRange> DirtyRanges { get; } = [];
        }

        private sealed class TranslatedDrawResources
        {
            public string DebugName = "SharpEmu translated";
            public PipelineLayout PipelineLayout;
            public Pipeline Pipeline;
            public bool PipelineCached;
            public bool DescriptorLayoutCached;
            public DescriptorSetLayout DescriptorSetLayout;
            public DescriptorPool DescriptorPool;
            public DescriptorSet DescriptorSet;
            public TextureResource[] Textures = [];
            public GlobalBufferResource[] GlobalMemoryBuffers = [];
            public VertexBufferResource[] VertexBuffers = [];
            public VkBuffer IndexBuffer;
            public DeviceMemory IndexMemory;
            public bool Index32Bit;
            public uint VertexCount = 3;
            public uint InstanceCount = 1;
            public PrimitiveTopology Topology = PrimitiveTopology.TriangleList;
            public VulkanGuestBlendState Blend = VulkanGuestBlendState.Default;
            // Vulkan format of this draw's color target. Needed to suppress
            // blending on formats Metal cannot blend (integer / 32-bit float),
            // which otherwise makes vkCreateGraphicsPipelines fail and can
            // trip a Metal validation assertion.
            public Format TargetFormat = Format.Undefined;
            public VulkanGuestRect? Scissor;
            public VulkanGuestViewport? Viewport;
            public VulkanGuestRasterState Raster = VulkanGuestRasterState.Default;
            public VulkanGuestDepthState Depth = VulkanGuestDepthState.Default;
            public bool HasDepthAttachment;
            // Layout keys are needed twice per draw (pipeline lookup and
            // descriptor-layout lookup); cache the built strings.
            public string? ResourceLayoutKey;
            public string? VertexLayoutKey;
        }

        private sealed class TextureResource
        {
            public ulong Address;
            public VkBuffer StagingBuffer;
            public DeviceMemory StagingMemory;
            public Image Image;
            public DeviceMemory ImageMemory;
            public ImageView View;
            public uint Width;
            public uint Height;
            public uint RowLength;
            public uint DstSelect;
            public bool NeedsUpload;
            public bool OwnsStorage;
            public bool IsStorage;
            public bool Cached;
            public VulkanGuestSampler SamplerState;
            public Sampler Sampler;
            public GuestImageResource? GuestImage;
            public GuestDepthResource? GuestDepth;
            // A sampled render-target alias cannot remain bound to the same
            // image while that image is a color attachment. The per-draw
            // snapshot uses this source to copy the target's pre-draw contents
            // before the render pass begins. The snapshot itself is owned by
            // this TextureResource and retires with the draw fence.
            public GuestImageResource? FeedbackSource;
            public GuestDepthResource? DepthFeedbackSource;
        }

        private sealed class GlobalBufferResource
        {
            public ulong BaseAddress;
            public bool Writable;
            public bool WriteBackToGuest;
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public nint Mapped;
            // DescriptorOffset/Size include the shader-visible byte bias.
            public ulong Offset;
            public ulong Size;
            // GuestOffset/Size identify only the original guest resource and
            // are used for dirty writeback; descriptor padding must not be
            // published over unrelated guest bytes.
            public ulong GuestOffset;
            public ulong GuestSize;
            public GuestBufferAllocation? Allocation;
        }

        private sealed class VertexBufferResource
        {
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public ulong Size;
            public uint Location;
            public uint ComponentCount;
            public uint DataFormat;
            public uint NumberFormat;
            public uint Stride;
            public uint OffsetBytes;
        }

        private const Format DepthFormat = Format.D32Sfloat;

        private sealed class GuestDepthResource
        {
            public GuestDepthKey Key;
            public ulong Address;
            public ulong ReadAddress;
            public ulong WriteAddress;
            public uint Width;
            public uint Height;
            public uint GuestFormat;
            public uint SwizzleMode;
            public Image Image;
            public DeviceMemory Memory;
            public ImageView View;
            public Dictionary<uint, ImageView> SampleViews { get; } = new();
            public bool Initialized;
            public ImageLayout Layout = ImageLayout.Undefined;
            public float ClearDepth = 1f;
        }

        private sealed class DepthFramebufferResource
        {
            public required GuestDepthResource Depth;
            public RenderPass LoadRenderPass;
            public RenderPass ColorClearRenderPass;
            public RenderPass DepthClearRenderPass;
            public RenderPass BothClearRenderPass;
            public Framebuffer Framebuffer;
        }

        private sealed class GuestImageResource
        {
            public ulong Address;
            public uint Width;
            public uint Height;
            public uint MipLevels;
            public uint GuestFormat;
            public Format Format;
            public Image Image;
            public DeviceMemory Memory;
            public ImageView View;
            public ImageView[] MipViews = [];
            public Dictionary<(Format Format, uint MipLevel, uint LevelCount, uint DstSelect), ImageView> FormatViews { get; } = new();
            public RenderPass RenderPass;
            public RenderPass InitialRenderPass;
            public Framebuffer Framebuffer;
            public Dictionary<GuestDepthKey, DepthFramebufferResource> DepthFramebuffers { get; } = new();
            public bool Initialized;
            public bool InitialUploadPending;
        }

        private sealed record PendingGuestSubmission(
            Fence Fence,
            CommandBuffer CommandBuffer,
            IReadOnlyList<TranslatedDrawResources> Resources,
            IReadOnlyList<GuestImageResource> TraceImages,
            IReadOnlyList<(VkBuffer Buffer, DeviceMemory Memory)> RetireBuffers,
            ulong Timeline,
            string DebugName);

        public Presenter(uint width, uint height)
        {
            var options = WindowOptions.DefaultVulkan;
            options.Size = new Vector2D<int>((int)DefaultWindowWidth, (int)DefaultWindowHeight);
            options.Title = VideoOutExports.GetWindowTitle();
            options.WindowBorder = WindowBorder.Fixed;
            options.VSync = true;
            options.FramesPerSecond = 60;
            options.UpdatesPerSecond = 60;
            _window = Window.Create(options);
            _window.Load += () =>
            {
                // The emulator was launched explicitly by the user, so make
                // the newly-created game window visible in front immediately
                // instead of leaving it behind the terminal on macOS.
                _window.Focus();
                Initialize();
            };
            _window.Render += Render;
            _window.Closing += () =>
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan VideoOut window closing; " +
                    $"requested={Volatile.Read(ref _presenterCloseRequested)} " +
                    $"deviceLost={_deviceLost}");
                DisposeVulkan();
            };
        }

        public void Run() => _window.Run();

        public void Dispose()
        {
            DisposeVulkan();
            try
            {
                _window.Dispose();
            }
            catch (InvalidOperationException exception)
                when (exception.Message.Contains("render loop", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan VideoOut window dispose skipped during render loop: {exception.Message}");
            }
        }

        private void Initialize()
        {
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    Pad.HostWindowInput.Attach(_window.CreateInput());
                    Console.Error.WriteLine("[LOADER][INFO] Window keyboard input attached for pad emulation.");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[LOADER][WARN] Window keyboard input unavailable: {exception.Message}");
                }
            }

            WaitForRenderDocAttachIfRequested();
            _vk = Vk.GetApi();
            CreateInstance();
            CreateSurface();
            SelectPhysicalDevice();
            CreateDevice();
            CreatePipelineCache();
            CreateSwapchain();
            CreateCommandResources();
            CreateGuestDrawResources();
            _vulkanReady = true;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut ready: {_extent.Width}x{_extent.Height}, format={_swapchainFormat}");
        }

        private static void WaitForRenderDocAttachIfRequested()
        {
            var value = Environment.GetEnvironmentVariable("SHARPEMU_RENDERDOC_WAIT");
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (string.Equals(value, "enter", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Waiting for RenderDoc attach before Vulkan init. pid={Environment.ProcessId}. Press Enter to continue.");
                _ = Console.ReadLine();
                return;
            }

            var seconds = 15;
            if (int.TryParse(value, out var parsedSeconds))
            {
                seconds = Math.Clamp(parsedSeconds, 1, 300);
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] Waiting {seconds}s for RenderDoc attach before Vulkan init. pid={Environment.ProcessId}");
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }

        private bool IsInstanceExtensionAvailable(string extensionName)
        {
            uint extensionCount = 0;
            if (_vk.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, null) != Result.Success ||
                extensionCount == 0)
            {
                return false;
            }

            var properties = new ExtensionProperties[extensionCount];
            fixed (ExtensionProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateInstanceExtensionProperties(
                        (byte*)null,
                        &extensionCount,
                        propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(extensionName);
                for (var index = 0; index < extensionCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].ExtensionName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool Utf8NullTerminatedEquals(byte* actual, ReadOnlySpan<byte> expected)
        {
            for (var index = 0; index < expected.Length; index++)
            {
                if (actual[index] != expected[index])
                {
                    return false;
                }
            }

            return actual[expected.Length] == 0;
        }

        private void LoadDebugUtilsCommands()
        {
            var setObjectName = _vk.GetDeviceProcAddr(_device, "vkSetDebugUtilsObjectNameEXT");
            var beginLabel = _vk.GetDeviceProcAddr(_device, "vkCmdBeginDebugUtilsLabelEXT");
            var endLabel = _vk.GetDeviceProcAddr(_device, "vkCmdEndDebugUtilsLabelEXT");
            _setDebugUtilsObjectName =
                (delegate* unmanaged<Device, DebugUtilsObjectNameInfoEXT*, Result>)
                setObjectName.Handle;
            _cmdBeginDebugUtilsLabel =
                (delegate* unmanaged<CommandBuffer, DebugUtilsLabelEXT*, void>)
                beginLabel.Handle;
            _cmdEndDebugUtilsLabel =
                (delegate* unmanaged<CommandBuffer, void>)
                endLabel.Handle;

            if (_setDebugUtilsObjectName is not null)
            {
                Console.Error.WriteLine("[LOADER][INFO] Vulkan debug labels enabled.");
            }
        }

        private void SetDebugName(ObjectType objectType, ulong objectHandle, string name)
        {
            if (_setDebugUtilsObjectName is null ||
                _device.Handle == 0 ||
                objectHandle == 0)
            {
                return;
            }

            var bytes = NullTerminatedUtf8(name);
            fixed (byte* namePointer = bytes)
            {
                var info = new DebugUtilsObjectNameInfoEXT
                {
                    SType = StructureType.DebugUtilsObjectNameInfoExt,
                    ObjectType = objectType,
                    ObjectHandle = objectHandle,
                    PObjectName = namePointer,
                };
                _ = _setDebugUtilsObjectName(_device, &info);
            }
        }

        private void BeginDebugLabel(CommandBuffer commandBuffer, string name)
        {
            if (_cmdBeginDebugUtilsLabel is null ||
                commandBuffer.Handle == 0)
            {
                return;
            }

            var bytes = NullTerminatedUtf8(name);
            fixed (byte* namePointer = bytes)
            {
                var label = new DebugUtilsLabelEXT
                {
                    SType = StructureType.DebugUtilsLabelExt,
                    PLabelName = namePointer,
                };
                label.Color[0] = 0.20f;
                label.Color[1] = 0.60f;
                label.Color[2] = 1.00f;
                label.Color[3] = 1.00f;
                _cmdBeginDebugUtilsLabel(commandBuffer, &label);
            }
        }

        private void EndDebugLabel(CommandBuffer commandBuffer)
        {
            if (_cmdEndDebugUtilsLabel is not null &&
                commandBuffer.Handle != 0)
            {
                _cmdEndDebugUtilsLabel(commandBuffer);
            }
        }

        private static byte[] NullTerminatedUtf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Array.Resize(ref bytes, bytes.Length + 1);
            return bytes;
        }

        private static string BuildComputeDebugName(VulkanComputeGuestDispatch dispatch)
        {
            var storage = dispatch.Textures.FirstOrDefault(texture => texture.IsStorage && texture.Address != 0);
            return storage is null
                ? $"SharpEmu compute cs=0x{dispatch.ShaderAddress:X16} " +
                  $"{dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ}"
                : $"SharpEmu compute cs=0x{dispatch.ShaderAddress:X16} " +
                  $"storage=0x{storage.Address:X16} " +
                  $"{storage.Width}x{storage.Height} fmt{storage.Format} " +
                  $"{dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ}";
        }

        private static string GuestImageDebugName(VulkanGuestRenderTarget target, Format format) =>
            $"SharpEmu guest 0x{target.Address:X16} {target.Width}x{target.Height} " +
            $"fmt{target.Format}/{format}";

        private static string TextureDebugName(VulkanGuestDrawTexture texture, Format format) =>
            $"SharpEmu texture 0x{texture.Address:X16} {texture.Width}x{texture.Height} " +
            $"fmt{texture.Format}/{format}";

        private void CreateInstance()
        {
            var applicationName = (byte*)SilkMarshal.StringToPtr("SharpEmu");
            var enableValidation = Environment.GetEnvironmentVariable("SHARPEMU_VK_VALIDATION") == "1";
            byte* validationLayerName = null;
            
            try
            {
                var applicationInfo = new ApplicationInfo
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = applicationName,
                    ApplicationVersion = Vk.MakeVersion(0, 0, 1),
                    PEngineName = applicationName,
                    EngineVersion = Vk.MakeVersion(0, 0, 1),
                    ApiVersion = Vk.Version12,
                };

                var extensions = _window.VkSurface!.GetRequiredExtensions(out var extensionCount);
                byte* debugUtilsExtension = null;
                byte* portabilityExtension = null;
                var instanceCreateFlags = InstanceCreateFlags.None;
                var enabledExtensionCount = (int)extensionCount;
                var enabledExtensions = stackalloc byte*[(int)extensionCount + 2];
                for (var index = 0; index < (int)extensionCount; index++)
                {
                    enabledExtensions[index] = extensions[index];
                }

                if (IsInstanceExtensionAvailable(DebugUtilsExtensionName))
                {
                    debugUtilsExtension = (byte*)SilkMarshal.StringToPtr(DebugUtilsExtensionName);
                    enabledExtensions[enabledExtensionCount++] = debugUtilsExtension;
                }

                if (IsInstanceExtensionAvailable(PortabilityEnumerationExtensionName))
                {
                    // MoltenVK is a portability (non-conformant) implementation;
                    // without this flag + extension the loader hides it.
                    portabilityExtension = (byte*)SilkMarshal.StringToPtr(PortabilityEnumerationExtensionName);
                    enabledExtensions[enabledExtensionCount++] = portabilityExtension;
                    instanceCreateFlags |= InstanceCreateFlags.EnumeratePortabilityBitKhr;
                }

                if (enableValidation && IsInstanceLayerAvailable("VK_LAYER_KHRONOS_validation"))
                {
                    validationLayerName = (byte*)SilkMarshal.StringToPtr("VK_LAYER_KHRONOS_validation");
                }
                else if (enableValidation)
                {
                    Console.Error.WriteLine("[LOADER][WARN] SHARPEMU_VK_VALIDATION=1 but VK_LAYER_KHRONOS_validation not found (Vulkan SDK installed?).");
                }

                var layers = stackalloc byte*[1];
                if (validationLayerName is not null)
                {
                    layers[0] = validationLayerName;
                }

                var createInfo = new InstanceCreateInfo
                {
                    SType = StructureType.InstanceCreateInfo,
                    Flags = instanceCreateFlags,
                    PApplicationInfo = &applicationInfo,
                    EnabledExtensionCount = (uint)enabledExtensionCount,
                    PpEnabledExtensionNames = enabledExtensions,
                    EnabledLayerCount = validationLayerName is not null ? 1u : 0u,
                    PpEnabledLayerNames = validationLayerName is not null ? layers : null,
                };

                try
                {
                    Check(_vk.CreateInstance(&createInfo, null, out _instance), "vkCreateInstance");
                    if (!_vk.TryGetInstanceExtension(_instance, out _surfaceApi))
                    {
                        throw new InvalidOperationException("VK_KHR_surface is unavailable.");
                    }
                    
                    if (validationLayerName is not null && _vk.TryGetInstanceExtension(_instance, out ExtDebugUtils debugUtils))
                    {
                        _debugUtils = debugUtils;
                        RegisterDebugMessenger(debugUtils);
                        Console.Error.WriteLine("[LOADER][INFO] Vulkan Validation Layers active (SHARPEMU_VK_VALIDATION=1).");
                    }
                }
                finally
                {
                    if (debugUtilsExtension is not null)
                    {
                        SilkMarshal.Free((nint)debugUtilsExtension);
                    }
                    if (portabilityExtension is not null)
                    {
                        SilkMarshal.Free((nint)portabilityExtension);
                    }
                }
            }
            finally
            {
                SilkMarshal.Free((nint)applicationName);
                if (validationLayerName is not null)
                {
                    SilkMarshal.Free((nint)validationLayerName);
                }
            }
        }

        private bool IsDeviceExtensionAvailable(string extensionName)
        {
            uint extensionCount = 0;
            if (_vk.EnumerateDeviceExtensionProperties(_physicalDevice, (byte*)null, &extensionCount, null) != Result.Success ||
                extensionCount == 0)
            {
                return false;
            }

            var properties = new ExtensionProperties[extensionCount];
            fixed (ExtensionProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateDeviceExtensionProperties(
                        _physicalDevice,
                        (byte*)null,
                        &extensionCount,
                        propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(extensionName);
                for (var index = 0; index < extensionCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].ExtensionName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsInstanceLayerAvailable(string layerName)
        {
            uint layerCount = 0;
            if (_vk.EnumerateInstanceLayerProperties(&layerCount, null) != Result.Success || layerCount == 0)
            {
                return false;
            }

            var properties = new LayerProperties[layerCount];
            fixed (LayerProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateInstanceLayerProperties(&layerCount, propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(layerName);
                for (var index = 0; index < layerCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].LayerName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        private void RegisterDebugMessenger(ExtDebugUtils debugUtils)
        {
            var messengerInfo = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt
                                  | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                              | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt
                              | DebugUtilsMessageTypeFlagsEXT.GeneralBitExt,
                PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback),
            };

            Check(debugUtils.CreateDebugUtilsMessenger(_instance, &messengerInfo, null, out _debugMessenger),
                "vkCreateDebugUtilsMessengerEXT");
        }
        
        private static unsafe uint DebugCallback(
            DebugUtilsMessageSeverityFlagsEXT severity,
            DebugUtilsMessageTypeFlagsEXT type,
            DebugUtilsMessengerCallbackDataEXT* callbackData,
            void* userData)
        {
            var message = SilkMarshal.PtrToString((nint)callbackData->PMessage);
            var prefix = severity switch
            {
                DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt => "[VULKAN][ERROR]",
                DebugUtilsMessageSeverityFlagsEXT.WarningBitExt => "[VULKAN][WARN]",
                _ => "[VULKAN][INFO]",
            };
            Console.Error.WriteLine($"{prefix} {message}");
            return Vk.False;
        }
        private void CreateSurface()
        {
            var instanceHandle = new VkHandle(_instance.Handle);
            var surfaceHandle = _window.VkSurface!.Create<AllocationCallbacks>(instanceHandle, null);
            _surface = new SurfaceKHR(surfaceHandle.Handle);
        }

        private void SelectPhysicalDevice()
        {
            uint deviceCount = 0;
            Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, null), "vkEnumeratePhysicalDevices");
            if (deviceCount == 0)
            {
                throw new InvalidOperationException("No Vulkan physical device was found.");
            }

            var devices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* devicePointer = devices)
            {
                Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, devicePointer), "vkEnumeratePhysicalDevices");
            }

            foreach (var device in devices)
            {
                uint queueCount = 0;
                _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, null);
                var queues = new QueueFamilyProperties[queueCount];
                fixed (QueueFamilyProperties* queuePointer = queues)
                {
                    _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, queuePointer);
                }

                for (uint index = 0; index < queueCount; index++)
                {
                    var supportsGraphics = (queues[index].QueueFlags & QueueFlags.GraphicsBit) != 0;
                    _surfaceApi.GetPhysicalDeviceSurfaceSupport(device, index, _surface, out var supportsPresent);
                    if (!supportsGraphics || !supportsPresent)
                    {
                        continue;
                    }

                    _physicalDevice = device;
                    _queueFamilyIndex = index;
                    LoadComputeDeviceLimits();
                    return;
                }
            }

            throw new InvalidOperationException("No Vulkan graphics/present queue was found.");
        }

        private void LoadComputeDeviceLimits()
        {
            _vk.GetPhysicalDeviceProperties(_physicalDevice, out var properties);
            _maxComputeWorkGroupCountX = properties.Limits.MaxComputeWorkGroupCount[0];
            _maxComputeWorkGroupCountY = properties.Limits.MaxComputeWorkGroupCount[1];
            _maxComputeWorkGroupCountZ = properties.Limits.MaxComputeWorkGroupCount[2];
            _maxComputeWorkGroupSizeX = properties.Limits.MaxComputeWorkGroupSize[0];
            _maxComputeWorkGroupSizeY = properties.Limits.MaxComputeWorkGroupSize[1];
            _maxComputeWorkGroupSizeZ = properties.Limits.MaxComputeWorkGroupSize[2];
            _maxComputeWorkGroupInvocations = properties.Limits.MaxComputeWorkGroupInvocations;
            _minStorageBufferOffsetAlignment = Math.Max(
                properties.Limits.MinStorageBufferOffsetAlignment,
                1UL);
            if (GuestStorageBufferOffsetAlignment %
                _minStorageBufferOffsetAlignment != 0)
            {
                throw new InvalidOperationException(
                    $"Vulkan storage-buffer alignment " +
                    $"{_minStorageBufferOffsetAlignment} is not compatible with " +
                    $"the portable alias alignment " +
                    $"{GuestStorageBufferOffsetAlignment}");
            }
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan compute limits groups=" +
                $"{_maxComputeWorkGroupCountX}x{_maxComputeWorkGroupCountY}x{_maxComputeWorkGroupCountZ} " +
                $"local={_maxComputeWorkGroupSizeX}x{_maxComputeWorkGroupSizeY}x" +
                $"{_maxComputeWorkGroupSizeZ} invocations={_maxComputeWorkGroupInvocations} " +
                $"storage_alignment={_minStorageBufferOffsetAlignment}");
        }

        private void CreateDevice()
        {
            var priority = 1.0f;
            var queueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = _queueFamilyIndex,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
            _vk.GetPhysicalDeviceFeatures(_physicalDevice, out var supportedFeatures);
            var enabledFeatures = new PhysicalDeviceFeatures
            {
                VertexPipelineStoresAndAtomics = supportedFeatures.VertexPipelineStoresAndAtomics,
                FragmentStoresAndAtomics = supportedFeatures.FragmentStoresAndAtomics,
                ShaderInt64 = supportedFeatures.ShaderInt64,
                ShaderImageGatherExtended = supportedFeatures.ShaderImageGatherExtended,
                ShaderStorageImageExtendedFormats = supportedFeatures.ShaderStorageImageExtendedFormats,
                ShaderStorageImageReadWithoutFormat = supportedFeatures.ShaderStorageImageReadWithoutFormat,
                ShaderStorageImageWriteWithoutFormat = supportedFeatures.ShaderStorageImageWriteWithoutFormat,
                RobustBufferAccess = supportedFeatures.RobustBufferAccess,
            };

            if (!supportedFeatures.RobustBufferAccess)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support robustBufferAccess " +
                    "translated shaders performing out-of-bounds buffer access may cause device loss.");
            }

            if (!supportedFeatures.ShaderInt64)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderInt64 " +
                    "translated shaders using 64-bit integers will fail.");
            }

            if (!supportedFeatures.VertexPipelineStoresAndAtomics || !supportedFeatures.FragmentStoresAndAtomics)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support vertexPipelineStoresAndAtomics/fragmentStoresAndAtomics " +
                    "translated shaders using storage buffers in vertex/fragment stages may fail.");
            }

            if (!supportedFeatures.ShaderImageGatherExtended)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderImageGatherExtended " +
                    "translated shaders using image gather with offsets/LOD/bias will fail.");
            }

            if (!supportedFeatures.ShaderStorageImageReadWithoutFormat ||
                !supportedFeatures.ShaderStorageImageWriteWithoutFormat)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderStorageImage(Read|Write)WithoutFormat " +
                    "translated shaders using unformatted storage image load/store will fail.");
            }

            var maintenance8Features = new PhysicalDeviceMaintenance8FeaturesKHR
            {
                SType = StructureType.PhysicalDeviceMaintenance8FeaturesKhr,
            };
            var robustness2Features = new PhysicalDeviceRobustness2FeaturesEXT
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
                PNext = &maintenance8Features,
            };
            var featuresQuery = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &robustness2Features,
            };
            _vk.GetPhysicalDeviceFeatures2(_physicalDevice, &featuresQuery);
            var supportsMaintenance8 = maintenance8Features.Maintenance8;
            var supportsRobustBufferAccess2 = robustness2Features.RobustBufferAccess2;
            var supportsRobustImageAccess2 = robustness2Features.RobustImageAccess2;
            var supportsNullDescriptor = robustness2Features.NullDescriptor;
            var supportsRobustness2 = supportsRobustImageAccess2 || supportsNullDescriptor;
            if (!supportsMaintenance8)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support VK_KHR_maintenance8 " +
                    "translated shaders using a dynamic texel offset on non-gather image samples will fail.");
            }

            if (!supportsRobustImageAccess2)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support VK_EXT_robustness2 robustImageAccess2 " +
                    "translated shaders performing out-of-bounds image access may cause device loss.");
            }

            var swapchainExtension = (byte*)SilkMarshal.StringToPtr("VK_KHR_swapchain");
            var maintenance8Extension = (byte*)SilkMarshal.StringToPtr("VK_KHR_maintenance8");
            var robustness2Extension = (byte*)SilkMarshal.StringToPtr("VK_EXT_robustness2");
            var portabilitySubsetExtension = (byte*)SilkMarshal.StringToPtr(PortabilitySubsetExtensionName);
            try
            {
                var extensions = stackalloc byte*[4];
                var extensionCount = 0u;
                extensions[extensionCount++] = swapchainExtension;
                if (supportsMaintenance8)
                {
                    extensions[extensionCount++] = maintenance8Extension;
                }

                if (supportsRobustness2)
                {
                    extensions[extensionCount++] = robustness2Extension;
                }

                if (IsDeviceExtensionAvailable(PortabilitySubsetExtensionName))
                {
                    // The spec requires enabling this when the (MoltenVK)
                    // device advertises it.
                    extensions[extensionCount++] = portabilitySubsetExtension;
                }

                maintenance8Features.Maintenance8 = supportsMaintenance8;
                maintenance8Features.PNext = null;
                robustness2Features.RobustBufferAccess2 =
                    supportsRobustBufferAccess2 && supportedFeatures.RobustBufferAccess;
                robustness2Features.RobustImageAccess2 = supportsRobustImageAccess2;
                robustness2Features.NullDescriptor = supportsNullDescriptor;
                robustness2Features.PNext = supportsMaintenance8 ? &maintenance8Features : null;
                var features2 = new PhysicalDeviceFeatures2
                {
                    SType = StructureType.PhysicalDeviceFeatures2,
                    PNext = supportsRobustness2
                        ? &robustness2Features
                        : (supportsMaintenance8 ? &maintenance8Features : null),
                    Features = enabledFeatures,
                };
                var createInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    PNext = &features2,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    EnabledExtensionCount = extensionCount,
                    PpEnabledExtensionNames = extensions,
                };

                Check(_vk.CreateDevice(_physicalDevice, &createInfo, null, out _device), "vkCreateDevice");
            }
            finally
            {
                SilkMarshal.Free((nint)swapchainExtension);
                SilkMarshal.Free((nint)maintenance8Extension);
                SilkMarshal.Free((nint)robustness2Extension);
                SilkMarshal.Free((nint)portabilitySubsetExtension);
            }

            _vk.GetDeviceQueue(_device, _queueFamilyIndex, 0, out _queue);
            LoadDebugUtilsCommands();
            if (!_vk.TryGetDeviceExtension(_instance, _device, out _swapchainApi))
            {
                throw new InvalidOperationException("VK_KHR_swapchain is unavailable.");
            }
        }

        private void CreatePipelineCache()
        {
            var cacheMode = Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE");
            // Vulkan cache blobs carry the implementation's compatibility
            // header and are rejected/rebuilt below when the device or driver
            // changes. MoltenVK compilation of a large translated shader can
            // take ten seconds, so discarding a valid cache at every launch is
            // much more harmful than using Vulkan's normal persistence path.
            // Keep an explicit opt-out for diagnostics and read-only systems.
            var persistentCacheEnabled =
                !string.Equals(cacheMode, "0", StringComparison.Ordinal);
            _pipelineCachePath = persistentCacheEnabled ? GetPipelineCachePath() : null;
            byte[] initialData = [];
            try
            {
                if (_pipelineCachePath is not null && File.Exists(_pipelineCachePath))
                {
                    initialData = File.ReadAllBytes(_pipelineCachePath);
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache read failed: {exception.Message}");
            }

            var result = TryCreatePipelineCache(initialData, out _pipelineCache);
            if (result != Result.Success && initialData.Length != 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache rejected ({result}); rebuilding it.");
                result = TryCreatePipelineCache([], out _pipelineCache);
            }

            if (result != Result.Success)
            {
                _pipelineCache = default;
                _pipelineCachePath = null;
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache unavailable: {result}");
                return;
            }

            SetDebugName(
                ObjectType.PipelineCache,
                _pipelineCache.Handle,
                _pipelineCachePath is null
                    ? "SharpEmu in-memory pipeline cache"
                    : "SharpEmu persistent pipeline cache");
            _lastPipelineCacheSaveTick = Environment.TickCount64;
            if (_pipelineCachePath is null)
            {
                Console.Error.WriteLine(
                    "[LOADER][INFO] Vulkan pipeline cache ready: memory-only " +
                    "(persistence disabled with SHARPEMU_VK_PIPELINE_CACHE=0).");
            }
            else
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan pipeline cache ready: path={_pipelineCachePath} initial={initialData.Length} bytes");
            }
        }

        private Result TryCreatePipelineCache(byte[] initialData, out PipelineCache pipelineCache)
        {
            fixed (byte* initialDataPointer = initialData)
            {
                var createInfo = new PipelineCacheCreateInfo
                {
                    SType = StructureType.PipelineCacheCreateInfo,
                    InitialDataSize = (nuint)initialData.Length,
                    PInitialData = initialData.Length == 0 ? null : initialDataPointer,
                };
                return _vk.CreatePipelineCache(
                    _device,
                    &createInfo,
                    null,
                    out pipelineCache);
            }
        }

        private static string GetPipelineCachePath()
        {
            var configured = Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(configured));
            }

            var root = OperatingSystem.IsMacOS()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library",
                    "Caches")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "SharpEmu", "vulkan-pipeline-cache.bin");
        }

        private void MarkPipelineCacheDirty(long creationStartTimestamp = 0)
        {
            if (_pipelineCache.Handle == 0)
            {
                return;
            }

            _pipelineCacheDirty = true;
            if (creationStartTimestamp != 0 &&
                Stopwatch.GetTimestamp() - creationStartTimestamp >=
                    Stopwatch.Frequency)
            {
                // Persist exceptionally expensive compilations immediately.
                // A crash or short diagnostic run must not throw away a
                // pipeline that cost seconds to build; ordinary pipelines keep
                // using the coalesced 30-second save cadence below.
                SavePipelineCache(force: true);
                return;
            }

            if (Environment.TickCount64 - _lastPipelineCacheSaveTick >= 30_000)
            {
                SavePipelineCache(force: false);
            }
        }

        private void SavePipelineCache(bool force)
        {
            if (_pipelineCache.Handle == 0 || string.IsNullOrWhiteSpace(_pipelineCachePath))
            {
                return;
            }

            if (!force && !_pipelineCacheDirty)
            {
                return;
            }

            try
            {
                nuint size = 0;
                var result = _vk.GetPipelineCacheData(
                    _device,
                    _pipelineCache,
                    &size,
                    null);
                if (result != Result.Success || size == 0 || size > 256u * 1024u * 1024u)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan pipeline cache query failed: result={result} size={size}");
                    return;
                }

                var data = new byte[checked((int)size)];
                fixed (byte* dataPointer = data)
                {
                    result = _vk.GetPipelineCacheData(
                        _device,
                        _pipelineCache,
                        &size,
                        dataPointer);
                }

                if (result != Result.Success)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan pipeline cache export failed: {result}");
                    return;
                }

                if (size != (nuint)data.Length)
                {
                    Array.Resize(ref data, checked((int)size));
                }

                var directory = Path.GetDirectoryName(_pipelineCachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temporaryPath = _pipelineCachePath + $".{Environment.ProcessId}.tmp";
                File.WriteAllBytes(temporaryPath, data);
                File.Move(temporaryPath, _pipelineCachePath, overwrite: true);
                _pipelineCacheDirty = false;
                _lastPipelineCacheSaveTick = Environment.TickCount64;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan pipeline cache saved: path={_pipelineCachePath} bytes={data.Length}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache save failed: {exception.Message}");
            }
        }

        private void CreateSwapchain()
        {
            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var capabilities),
                "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

            uint formatCount = 0;
            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, null),
                "vkGetPhysicalDeviceSurfaceFormatsKHR");
            var formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatPointer = formats)
            {
                Check(
                    _surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, formatPointer),
                    "vkGetPhysicalDeviceSurfaceFormatsKHR");
            }

            var surfaceFormat = ChooseSurfaceFormat(formats);
            _swapchainFormat = surfaceFormat.Format;
            _extent = ChooseExtent(capabilities);
            var presentMode = ChoosePresentMode();
            var imageCount = capabilities.MinImageCount + 1;
            if (capabilities.MaxImageCount != 0)
            {
                imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
            }

            var compositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha);
            var createInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = _extent,
                ImageArrayLayers = 1,
                ImageUsage =
                    ImageUsageFlags.TransferDstBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = SharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = compositeAlpha,
                PresentMode = presentMode,
                Clipped = true,
            };

            Check(_swapchainApi.CreateSwapchain(_device, &createInfo, null, out _swapchain), "vkCreateSwapchainKHR");

            uint swapchainImageCount = 0;
            Check(
                _swapchainApi.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, null),
                "vkGetSwapchainImagesKHR");
            _swapchainImages = new Image[swapchainImageCount];
            fixed (Image* imagePointer = _swapchainImages)
            {
                Check(
                    _swapchainApi.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, imagePointer),
                    "vkGetSwapchainImagesKHR");
            }

            _imageInitialized = new bool[swapchainImageCount];
        }

        private PresentModeKHR ChoosePresentMode()
        {
            // MAILBOX never blocks vkQueuePresentKHR on vblank, so a slow
            // frame does not quantize the frame rate down to 30/20 fps the
            // way FIFO does; the guest side is already paced by PaceFlip.
            // FIFO is the only mode guaranteed by the spec and remains the
            // fallback (MoltenVK typically exposes FIFO + IMMEDIATE only).
            uint modeCount = 0;
            if (_surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                    _physicalDevice,
                    _surface,
                    &modeCount,
                    null) != Result.Success ||
                modeCount == 0)
            {
                return PresentModeKHR.FifoKhr;
            }

            var modes = stackalloc PresentModeKHR[(int)modeCount];
            if (_surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                    _physicalDevice,
                    _surface,
                    &modeCount,
                    modes) != Result.Success)
            {
                return PresentModeKHR.FifoKhr;
            }

            for (var index = 0u; index < modeCount; index++)
            {
                if (modes[index] == PresentModeKHR.MailboxKhr)
                {
                    return PresentModeKHR.MailboxKhr;
                }
            }

            return PresentModeKHR.FifoKhr;
        }

        private void CreateCommandResources()
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = _queueFamilyIndex,
            };
            Check(_vk.CreateCommandPool(_device, &poolInfo, null, out _commandPool), "vkCreateCommandPool");

            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = MaxFramesInFlight,
            };
            _frameCommandBuffers = new CommandBuffer[MaxFramesInFlight];
            fixed (CommandBuffer* frameCommandBuffers = _frameCommandBuffers)
            {
                Check(
                    _vk.AllocateCommandBuffers(_device, &allocateInfo, frameCommandBuffers),
                    "vkAllocateCommandBuffers");
            }

            var semaphoreInfo = new SemaphoreCreateInfo
            {
                SType = StructureType.SemaphoreCreateInfo,
            };
            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
            };
            _frameImageAvailable = new VkSemaphore[MaxFramesInFlight];
            _frameFences = new Fence[MaxFramesInFlight];
            _frameFencePending = new bool[MaxFramesInFlight];
            _frameTimelines = new ulong[MaxFramesInFlight];
            _frameTranslatedResources = new TranslatedDrawResources?[MaxFramesInFlight];
            for (var slot = 0; slot < MaxFramesInFlight; slot++)
            {
                Check(
                    _vk.CreateSemaphore(_device, &semaphoreInfo, null, out _frameImageAvailable[slot]),
                    "vkCreateSemaphore");
                Check(
                    _vk.CreateFence(_device, &fenceInfo, null, out _frameFences[slot]),
                    "vkCreateFence(frame)");
            }

            _renderFinishedPerImage = new VkSemaphore[_swapchainImages.Length];
            for (var image = 0; image < _renderFinishedPerImage.Length; image++)
            {
                Check(
                    _vk.CreateSemaphore(_device, &semaphoreInfo, null, out _renderFinishedPerImage[image]),
                    "vkCreateSemaphore");
            }

            _currentFrameSlot = 0;
            _commandBuffer = _frameCommandBuffers[0];
            _presentationCommandBuffer = _commandBuffer;

            CreateStagingBuffer((ulong)_extent.Width * _extent.Height * 4);
            CreateOverlayResources();
        }

        private void CreateOverlayResources()
        {
            const ulong overlayBytes = PerfOverlay.PanelWidth * PerfOverlay.PanelHeight * 4;
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = Format.B8G8R8A8Unorm,
                Extent = new Extent3D(PerfOverlay.PanelWidth, PerfOverlay.PanelHeight, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out _overlayImage), "vkCreateImage(overlay)");
            _vk.GetImageMemoryRequirements(_device, _overlayImage, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(
                _vk.AllocateMemory(_device, &memoryInfo, null, out _overlayImageMemory),
                "vkAllocateMemory(overlay)");
            Check(
                _vk.BindImageMemory(_device, _overlayImage, _overlayImageMemory, 0),
                "vkBindImageMemory(overlay)");
            _overlayImageInitialized = false;

            _overlayStagingBuffers = new VkBuffer[MaxFramesInFlight];
            _overlayStagingMemory = new DeviceMemory[MaxFramesInFlight];
            _overlayStagingMapped = new nint[MaxFramesInFlight];
            for (var slot = 0; slot < MaxFramesInFlight; slot++)
            {
                _overlayStagingBuffers[slot] = CreateBuffer(
                    overlayBytes,
                    BufferUsageFlags.TransferSrcBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out _overlayStagingMemory[slot]);
                void* mapped;
                Check(
                    _vk.MapMemory(_device, _overlayStagingMemory[slot], 0, overlayBytes, 0, &mapped),
                    "vkMapMemory(overlay staging)");
                _overlayStagingMapped[slot] = (nint)mapped;
            }
        }

        private void RecordOverlayBlit(uint imageIndex, int frameSlot)
        {
            if (_overlayImage.Handle == 0 || _overlayStagingMapped.Length <= frameSlot)
            {
                return;
            }

            int pendingWork;
            lock (_gate)
            {
                pendingWork = _pendingGuestWork.Count;
            }

            var pixels = new Span<byte>(
                (void*)_overlayStagingMapped[frameSlot],
                PerfOverlay.PanelWidth * PerfOverlay.PanelHeight * 4);
            PerfOverlay.Fill(pixels, pendingWork, _pendingGuestSubmissions.Count);

            var toTransferDst = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _overlayImageInitialized ? AccessFlags.TransferReadBit : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = _overlayImageInitialized
                    ? ImageLayout.TransferSrcOptimal
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _overlayImage,
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, &toTransferDst);

            var copyRegion = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(PerfOverlay.PanelWidth, PerfOverlay.PanelHeight, 1),
            };
            _vk.CmdCopyBufferToImage(
                _commandBuffer,
                _overlayStagingBuffers[frameSlot],
                _overlayImage,
                ImageLayout.TransferDstOptimal,
                1,
                &copyRegion);

            var toTransferSrc = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _overlayImage,
                SubresourceRange = ColorSubresourceRange(),
            };
            var swapchainToDst = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.PresentSrcKhr,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            var preBlitBarriers = stackalloc ImageMemoryBarrier[2] { toTransferSrc, swapchainToDst };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit | PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 2, preBlitBarriers);

            const int margin = 12;
            var panelWidth = (int)Math.Min(PerfOverlay.PanelWidth, _extent.Width - margin);
            var panelHeight = (int)Math.Min(PerfOverlay.PanelHeight, _extent.Height - margin);
            // Source and destination are both B8G8R8A8 and the panel is not
            // scaled. MoltenVK has corrupted pixels outside the blit region
            // for this transfer-on-swapchain path (horizontal red/yellow
            // scanlines across the entire window). An exact image copy has
            // the required semantics and avoids the driver's blit conversion
            // path altogether.
            var copy = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                SrcOffset = new Offset3D(0, 0, 0),
                DstOffset = new Offset3D(margin, margin, 0),
                Extent = new Extent3D((uint)panelWidth, (uint)panelHeight, 1),
            };
            _vk.CmdCopyImage(
                _commandBuffer,
                _overlayImage,
                ImageLayout.TransferSrcOptimal,
                _swapchainImages[imageIndex],
                ImageLayout.TransferDstOptimal,
                1,
                &copy);

            var swapchainToPresent = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = 0,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.BottomOfPipeBit,
                0, 0, null, 0, null, 1, &swapchainToPresent);
            _overlayImageInitialized = true;
        }

        private CommandBuffer AllocateGuestCommandBuffer()
        {
            // The pool has ResetCommandBufferBit, so vkBeginCommandBuffer
            // implicitly resets recycled buffers.
            if (_recycledGuestCommandBuffers.TryPop(out var recycled))
            {
                return recycled;
            }

            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer commandBuffer;
            Check(
                _vk.AllocateCommandBuffers(
                    _device,
                    &allocateInfo,
                    out commandBuffer),
                "vkAllocateCommandBuffers(guest)");
            return commandBuffer;
        }

        private Fence AcquireGuestFence()
        {
            // Recycled fences were reset when they were collected.
            if (_recycledGuestFences.TryPop(out var recycled))
            {
                return recycled;
            }

            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
            };
            Fence fence;
            Check(
                _vk.CreateFence(_device, &fenceInfo, null, out fence),
                "vkCreateFence(guest)");
            return fence;
        }

        private void ReleaseGuestCommandBuffer(CommandBuffer commandBuffer)
        {
            if (_recycledGuestCommandBuffers.Count < MaxRecycledGuestCommandBuffers)
            {
                _recycledGuestCommandBuffers.Push(commandBuffer);
                return;
            }

            _vk.FreeCommandBuffers(_device, _commandPool, 1, &commandBuffer);
        }

        private void ReleaseGuestFence(Fence fence, bool needsReset)
        {
            if (_recycledGuestFences.Count < MaxRecycledGuestFences)
            {
                if (needsReset)
                {
                    Check(_vk.ResetFences(_device, 1, &fence), "vkResetFences(guest)");
                }

                _recycledGuestFences.Push(fence);
                return;
            }

            _vk.DestroyFence(_device, fence, null);
        }

        // Translated draws are recorded into a shared command buffer and
        // submitted once per drained work batch: on MoltenVK every
        // vkQueueSubmit is a Metal command-buffer commit (~0.8ms), which used
        // to be paid per draw and dominated the frame time.
        private CommandBuffer _batchCommandBuffer;
        private bool _batchOpen;
        private int _batchDrawCount;
        private readonly List<TranslatedDrawResources> _batchResources = new();
        private readonly List<GuestImageResource> _batchTraceImages = new();
        private bool _tracedScanoutSources;
        private bool _tracedFirst4kUfloatSources;
        private int _seen4kUfloatSources;
        private bool _tracedFirst4kVertexInputs;
        private int _tracedSelectedVertexInputs;

        // Consecutive draws into the same target stay inside one render pass:
        // on MoltenVK every render pass is a Metal render encoder, and one
        // encoder per draw was the dominant per-draw fixed cost after submit
        // batching. The pass closes when the target changes, when a draw
        // needs transfer/storage work outside a pass, or when the batch
        // flushes.
        private GuestImageResource? _openPassTarget;
        private GuestDepthResource? _openPassDepth;

        private void CloseOpenTranslatedRenderPass()
        {
            if (_openPassTarget is not { } target)
            {
                return;
            }

            _openPassTarget = null;
            _openPassDepth = null;
            _vk.CmdEndRenderPass(_batchCommandBuffer);
            var toShaderRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.ColorAttachmentOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _batchCommandBuffer,
                PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.FragmentShaderBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toShaderRead);
        }

        private CommandBuffer BeginBatchedGuestCommands()
        {
            if (_batchOpen)
            {
                return _batchCommandBuffer;
            }

            _batchCommandBuffer = AllocateGuestCommandBuffer();
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(
                _vk.BeginCommandBuffer(_batchCommandBuffer, &beginInfo),
                "vkBeginCommandBuffer(batch)");
            _batchOpen = true;
            _batchDrawCount = 0;
            return _batchCommandBuffer;
        }

        private void FlushBatchedGuestCommands()
        {
            if (!_batchOpen)
            {
                return;
            }

            CloseOpenTranslatedRenderPass();
            _batchOpen = false;
            try
            {
                Check(_vk.EndCommandBuffer(_batchCommandBuffer), "vkEndCommandBuffer(batch)");
                SubmitGuestCommandBuffer(
                    _batchCommandBuffer,
                    _batchResources.ToArray(),
                    _batchTraceImages.ToArray(),
                    _batchRetireBuffers.Count > 0 ? _batchRetireBuffers.ToArray() : []);
            }
            catch
            {
                // The batch never reached the queue: release everything it
                // owned here so the stale lists cannot ride into the next
                // batch's submission.
                foreach (var resources in _batchResources)
                {
                    DestroyTranslatedDrawResources(resources);
                }

                foreach (var (buffer, memory) in _batchRetireBuffers)
                {
                    _vk.DestroyBuffer(_device, buffer, null);
                    _vk.FreeMemory(_device, memory, null);
                }

                ReleaseGuestCommandBuffer(_batchCommandBuffer);
                throw;
            }
            finally
            {
                _batchResources.Clear();
                _batchTraceImages.Clear();
                _batchRetireBuffers.Clear();
                _batchCommandBuffer = default;
            }
        }

        private void SubmitGuestCommandBuffer(
            CommandBuffer commandBuffer,
            IReadOnlyList<TranslatedDrawResources> resources,
            IReadOnlyList<GuestImageResource> traceImages,
            IReadOnlyList<(VkBuffer Buffer, DeviceMemory Memory)>? retireBuffers = null,
            IReadOnlyList<TranslatedDrawResources>? referencedResources = null)
        {
            var fence = AcquireGuestFence();
            try
            {
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, fence),
                    "vkQueueSubmit(guest)");
            }
            catch
            {
                ReleaseGuestFence(fence, needsReset: false);
                throw;
            }

            _submitTimeline++;
            foreach (var referenced in referencedResources ?? resources)
            {
                foreach (var globalBuffer in referenced.GlobalMemoryBuffers)
                {
                    if (globalBuffer.Allocation is not { } allocation)
                    {
                        continue;
                    }

                    allocation.LastUseTimeline = Math.Max(
                        allocation.LastUseTimeline,
                        _submitTimeline);
                    if (globalBuffer.Writable && globalBuffer.WriteBackToGuest)
                    {
                        MarkGuestBufferDirty(
                            allocation,
                            globalBuffer.GuestOffset,
                            globalBuffer.GuestSize);
                    }
                }
            }

            _pendingGuestSubmissions.Enqueue(
                new PendingGuestSubmission(
                    fence,
                    commandBuffer,
                    resources,
                    traceImages,
                    retireBuffers ?? [],
                    _submitTimeline,
                    resources.Count > 0 ? resources[0].DebugName : "batch"));
        }

        private void EnsureGuestSubmissionCapacity()
        {
            CollectCompletedGuestSubmissions(waitForOldest: false);
            if (_pendingGuestSubmissions.Count >= MaxInFlightGuestSubmissions)
            {
                // Bounded wait so the macOS main thread returns to its event
                // pump promptly under a slow-compute backlog; if the oldest
                // isn't done yet we proceed (soft cap, dynamic pools).
                CollectCompletedGuestSubmissions(
                    waitForOldest: true,
                    maxWaitNs: _submissionCapacityWaitNs == 0
                        ? _guestFenceWaitTimeoutNs
                        : _submissionCapacityWaitNs);
            }
        }

        private void CollectCompletedGuestSubmissions(bool waitForOldest, ulong maxWaitNs = 0)
        {
            if (waitForOldest && _pendingGuestSubmissions.TryPeek(out var oldest))
            {
                var fence = oldest.Fence;
                // maxWaitNs==0 => the full "is this submission hung" timeout,
                // which also emits the one-shot hang warning below. A shorter
                // capacity-probe wait (maxWaitNs>0) must NOT report a hang: the
                // submission is still tracked and will be collected once the GPU
                // finishes it on a later frame.
                var isProbeWait = maxWaitNs != 0 && maxWaitNs < _guestFenceWaitTimeoutNs;
                var waitNs = maxWaitNs != 0 ? maxWaitNs : _guestFenceWaitTimeoutNs;
                var result = _vk.WaitForFences(
                    _device,
                    1,
                    &fence,
                    true,
                    waitNs);
                if (result == Result.Timeout)
                {
                    // A GPU submission whose fence never signals (typically a
                    // mistranslated compute shader that hangs the Metal queue)
                    // would otherwise block the render thread forever, starving
                    // the swapchain present (black screen). Log the culprit and
                    // continue so at least the last good frame can be shown.
                    if (!isProbeWait && _tracedFenceTimeouts.Add(oldest.DebugName))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] vk.fence_wait_timeout submission='{oldest.DebugName}' " +
                            $"— GPU work not completing after {_guestFenceWaitTimeoutNs / 1_000_000}ms; " +
                            "render thread continuing (present not blocked).");
                    }

                    return;
                }

                Check(result, $"vkWaitForFences(guest: {oldest.DebugName})");
            }

            while (_pendingGuestSubmissions.TryPeek(out var submission))
            {
                var status = _vk.GetFenceStatus(_device, submission.Fence);
                if (status == Result.NotReady)
                {
                    break;
                }

                Check(status, $"vkGetFenceStatus(guest: {submission.DebugName})");
                _pendingGuestSubmissions.Dequeue();

                foreach (var image in submission.TraceImages)
                {
                    TraceGuestImageContents(image);
                }

                foreach (var resources in submission.Resources)
                {
                    DestroyTranslatedDrawResources(resources);
                }

                foreach (var (buffer, memory) in submission.RetireBuffers)
                {
                    _vk.DestroyBuffer(_device, buffer, null);
                    _vk.FreeMemory(_device, memory, null);
                }

                ReleaseGuestCommandBuffer(submission.CommandBuffer);
                ReleaseGuestFence(submission.Fence, needsReset: true);
                if (submission.Timeline > _completedTimeline)
                {
                    _completedTimeline = submission.Timeline;
                }
            }

            ProcessDeferredTextureDestroys();
        }

        private void WaitForAllGuestSubmissionsForCpuVisibility()
        {
            FlushBatchedGuestCommands();
            while (_pendingGuestSubmissions.TryPeek(out var oldest))
            {
                var fence = oldest.Fence;
                Check(
                    _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue),
                    $"vkWaitForFences(cpu visibility: {oldest.DebugName})");
                CollectCompletedGuestSubmissions(waitForOldest: false);
            }
        }

        private void ExecuteOrderedGuestAction(VulkanOrderedGuestAction work)
        {
            WaitForAllGuestSubmissionsForCpuVisibility();
            WriteBackAllDirtyGuestBuffers();
            work.Action();
            TraceVulkanShader($"vk.ordered_action name='{work.DebugName}'");
        }

        private static byte[]? TryReadGuestTexturePixels(VulkanGuestDrawTexture texture)
        {
            var memory = _guestMemory;
            if (memory is null || texture.Address == 0)
            {
                return null;
            }

            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var rowLength = texture.TileMode == 0
                ? Math.Max(texture.Pitch, width)
                : width;
            var byteCount = GetTextureByteCount(texture.Format, rowLength, height);
            if (byteCount == 0 || byteCount > int.MaxValue)
            {
                return null;
            }

            var pixels = new byte[(int)byteCount];
            return memory.TryRead(texture.Address, pixels) ? pixels : null;
        }

        /// <summary>
        /// Returns a skipped draw's pooled data arrays: draws dropped before
        /// resource creation would otherwise strand their rented buffers.
        /// </summary>
        private static void ReturnPooledGuestData(VulkanTranslatedGuestDraw draw)
        {
            foreach (var buffer in draw.GlobalMemoryBuffers)
            {
                if (buffer.Pooled)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer.Data);
                }
            }

            foreach (var buffer in draw.VertexBuffers)
            {
                if (buffer.Pooled)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer.Data);
                }
            }

            if (draw.IndexBuffer is { Pooled: true } indexBuffer)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(indexBuffer.Data);
            }
        }

        private void ProcessDeferredTextureDestroys()
        {
            while (_deferredTextureDestroys.TryPeek(out var entry) &&
                   entry.RetireTimeline <= _completedTimeline)
            {
                _deferredTextureDestroys.Dequeue();
                DestroyCachedTextureResource(entry.Texture);
            }

            while (_deferredResourceDestroys.TryPeek(out var resourceEntry) &&
                   resourceEntry.RetireTimeline <= _completedTimeline)
            {
                _deferredResourceDestroys.Dequeue();
                DestroyTranslatedDrawResources(resourceEntry.Resources);
            }
        }

        private void WaitFrameSlot(int slot) => TryWaitFrameSlot(slot, ulong.MaxValue);

        // Returns false when the slot's fence is still unsignaled after
        // timeoutNs (the GPU is behind, e.g. a slow-compute backlog). Callers
        // on the macOS main thread must NOT wait forever here or the Cocoa
        // event pump stalls and the window goes "Not Responding" (F1 overlay /
        // close stop working). A bounded wait lets Render() skip the frame and
        // return to the pump; the fence still signals later and the frame is
        // retried.
        private bool TryWaitFrameSlot(int slot, ulong timeoutNs)
        {
            if (_frameFencePending.Length <= slot || !_frameFencePending[slot])
            {
                return true;
            }

            var fence = _frameFences[slot];
            var waitResult = _vk.WaitForFences(_device, 1, &fence, true, timeoutNs);
            if (waitResult == Result.Timeout)
            {
                return false;
            }

            Check(waitResult, "vkWaitForFences(frame)");
            Check(_vk.ResetFences(_device, 1, &fence), "vkResetFences(frame)");
            _frameFencePending[slot] = false;
            if (_frameTimelines[slot] > _completedTimeline)
            {
                _completedTimeline = _frameTimelines[slot];
            }

            if (_frameTranslatedResources[slot] is { } translated)
            {
                _frameTranslatedResources[slot] = null;
                DestroyTranslatedDrawResources(translated);
            }

            ProcessDeferredTextureDestroys();
            return true;
        }

        private void WaitAllFrameSlots()
        {
            for (var slot = 0; slot < _frameFencePending.Length; slot++)
            {
                WaitFrameSlot(slot);
            }
        }

        // Used on teardown paths that already drained the queue (device
        // wait-idle): releases per-slot state without touching fences that
        // were never submitted.
        private void DrainFrameSlots()
        {
            WaitAllFrameSlots();
            _completedTimeline = _submitTimeline;
            ProcessDeferredTextureDestroys();
        }

        private IReadOnlyList<GuestImageResource> GetTraceImages(
            TranslatedDrawResources resources,
            GuestImageResource? renderTarget = null)
        {
            var images = new HashSet<GuestImageResource>();
            if (renderTarget is not null &&
                ShouldTraceGuestImageContents(renderTarget))
            {
                images.Add(renderTarget);
            }

            foreach (var texture in resources.Textures)
            {
                if (texture.IsStorage &&
                    texture.GuestImage is { } image &&
                    ShouldTraceGuestImageContents(image))
                {
                    images.Add(image);
                }
            }

            return images.ToArray();
        }

        private void CreateGuestDrawResources()
        {
            var colorAttachment = new AttachmentDescription
            {
                Format = _swapchainFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.PresentSrcKhr,
            };
            var colorReference = new AttachmentReference
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorReference,
            };
            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
            };
            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorAttachment,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };
            Check(_vk.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass), "vkCreateRenderPass");

            _swapchainImageViews = new ImageView[_swapchainImages.Length];
            _framebuffers = new Framebuffer[_swapchainImages.Length];
            for (var index = 0; index < _swapchainImages.Length; index++)
            {
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = _swapchainImages[index],
                    ViewType = ImageViewType.Type2D,
                    Format = _swapchainFormat,
                    Components = new ComponentMapping(
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity),
                    SubresourceRange = ColorSubresourceRange(),
                };
                Check(
                    _vk.CreateImageView(_device, &viewInfo, null, out _swapchainImageViews[index]),
                    "vkCreateImageView");

                var imageView = _swapchainImageViews[index];
                var framebufferInfo = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _renderPass,
                    AttachmentCount = 1,
                    PAttachments = &imageView,
                    Width = _extent.Width,
                    Height = _extent.Height,
                    Layers = 1,
                };
                Check(
                    _vk.CreateFramebuffer(_device, &framebufferInfo, null, out _framebuffers[index]),
                    "vkCreateFramebuffer");
            }

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
            };
            Check(
                _vk.CreatePipelineLayout(_device, &layoutInfo, null, out _pipelineLayout),
                "vkCreatePipelineLayout");
            CreateBarycentricPipeline();
        }

        private void CreateBarycentricPipeline()
        {
            var vertexBytes = Convert.FromBase64String(FullscreenBarycentricVertexSpirv);
            var fragmentBytes = Convert.FromBase64String(FullscreenBarycentricFragmentSpirv);
            var vertexModule = CreateShaderModule(vertexBytes);
            var fragmentModule = CreateShaderModule(fragmentBytes);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                shaderStages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = entryPoint,
                };

                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                };
                var viewport = new Viewport(0, 0, _extent.Width, _extent.Height, 0, 1);
                var scissor = new Rect2D(new Offset2D(0, 0), _extent);
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor,
                };
                var rasterization = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1,
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };
                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask =
                        ColorComponentFlags.RBit |
                        ColorComponentFlags.GBit |
                        ColorComponentFlags.BBit |
                        ColorComponentFlags.ABit,
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment,
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = shaderStages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PColorBlendState = &colorBlend,
                    Layout = _pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                };
                Check(
                    _vk.CreateGraphicsPipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out _barycentricPipeline),
                    "vkCreateGraphicsPipelines");
                MarkPipelineCacheDirty();
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private ShaderModule CreateShaderModule(byte[] code)
        {
            fixed (byte* codePointer = code)
            {
                var createInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode = (uint*)codePointer,
                };
                Check(
                    _vk.CreateShaderModule(_device, &createInfo, null, out var module),
                    "vkCreateShaderModule");
                return module;
            }
        }

        private TranslatedDrawResources CreateTranslatedDrawResources(
            VulkanTranslatedGuestDraw draw,
            RenderPass renderPass,
            Extent2D extent,
            Format targetFormat,
            GuestImageResource? feedbackTarget = null,
            bool hasDepthAttachment = false,
            GuestDepthResource? feedbackDepth = null)
        {
            var vertexSpirv = draw.VertexSpirv;
            if (vertexSpirv.Length == 0 &&
                !TryCompileFullscreenVertexShader(
                    draw.AttributeCount,
                    out vertexSpirv,
                    out var vertexError))
            {
                throw new InvalidOperationException($"translated vertex shader failed: {vertexError}");
            }

            var resources = new TranslatedDrawResources
            {
                DebugName = "SharpEmu draw",
                Textures = new TextureResource[draw.Textures.Count],
                GlobalMemoryBuffers =
                    new GlobalBufferResource[draw.GlobalMemoryBuffers.Count],
                VertexBuffers = new VertexBufferResource[draw.VertexBuffers.Count],
                VertexCount = GetDrawVertexCount(draw.PrimitiveType, draw.VertexCount, draw.IndexBuffer),
                InstanceCount = Math.Max(draw.InstanceCount, 1),
                Topology = GetPrimitiveTopology(draw.PrimitiveType),
                Blend = draw.RenderState.Blend,
                Scissor = draw.RenderState.Scissor,
                Viewport = draw.RenderState.Viewport,
                Raster = draw.RenderState.Raster,
                Depth = draw.RenderState.Depth,
                HasDepthAttachment = hasDepthAttachment,
                TargetFormat = targetFormat,
            };

            try
            {
                foreach (var texture in draw.Textures)
                {
                    // Skip address-0 storage bindings here: the real resolution
                    // path uses a scratch image for those, but this warm-up pass
                    // called ResolveStorageGuestImage directly, which throws on
                    // address 0 and dropped the whole draw (Demon's Souls G-buffer
                    // normals/IDs passes -> lighting had no input -> black).
                    if (texture.IsStorage && texture.Address != 0)
                    {
                        _ = ResolveStorageGuestImage(texture);
                    }
                }

                for (var index = 0; index < draw.Textures.Count; index++)
                {
                    var texture = draw.Textures[index];
                    var resolved = ResolveTextureResource(texture);
                    // ResolveTextureResource may deliberately decline an
                    // address alias when the descriptor is incompatible with
                    // the render-target image. Only snapshot an alias which
                    // actually resolved to the target; a separately uploaded
                    // texture has no Vulkan attachment feedback hazard.
                    resources.Textures[index] =
                        feedbackDepth is not null &&
                        !texture.IsStorage &&
                        ReferenceEquals(resolved.GuestDepth, feedbackDepth)
                            ? CreateDepthFeedbackSnapshot(texture, feedbackDepth)
                            :
                        feedbackTarget is not null &&
                        !texture.IsStorage &&
                        ReferenceEquals(resolved.GuestImage, feedbackTarget)
                            ? CreateRenderTargetFeedbackSnapshot(texture, feedbackTarget)
                            : resolved;
                }

                PrepareGuestBufferAllocations(draw.GlobalMemoryBuffers);
                for (var index = 0; index < draw.GlobalMemoryBuffers.Count; index++)
                {
                    resources.GlobalMemoryBuffers[index] =
                        CreateGlobalBufferResource(draw.GlobalMemoryBuffers[index]);
                }

                for (var index = 0; index < draw.VertexBuffers.Count; index++)
                {
                    resources.VertexBuffers[index] =
                        CreateVertexBufferResource(draw.VertexBuffers[index]);
                }

                if (draw.IndexBuffer is { Length: > 0 } indexBuffer)
                {
                    resources.IndexBuffer = CreateHostBuffer(
                        indexBuffer.Data.AsSpan(0, indexBuffer.Length),
                        BufferUsageFlags.IndexBufferBit,
                        out resources.IndexMemory);
                    resources.Index32Bit = indexBuffer.Is32Bit;
                    if (indexBuffer.Pooled)
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(indexBuffer.Data);
                    }
                }

                CreateTranslatedDescriptorResources(
                    resources,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit);
                CreateTranslatedPipeline(resources, vertexSpirv, draw.PixelSpirv, renderPass, extent);
                return resources;
            }
            catch
            {
                DestroyTranslatedDrawResources(resources);
                throw;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TranslatedDrawResources CreateComputeDispatchResources(
            VulkanComputeGuestDispatch dispatch)
        {
            var traceResources = dispatch.Textures.Count >= 8;
            if (traceResources)
            {
                TraceVulkanShader(
                    $"vk.compute_resources begin groups={dispatch.GroupCountX}x" +
                    $"{dispatch.GroupCountY}x{dispatch.GroupCountZ} textures={dispatch.Textures.Count}");
            }

            var resources = new TranslatedDrawResources
            {
                DebugName = BuildComputeDebugName(dispatch),
                Textures = new TextureResource[dispatch.Textures.Count],
                GlobalMemoryBuffers =
                    new GlobalBufferResource[dispatch.GlobalMemoryBuffers.Count],
            };

            try
            {
                for (var index = 0; index < dispatch.Textures.Count; index++)
                {
                    var texture = dispatch.Textures[index];
                    if (texture.IsStorage)
                    {
                        if (traceResources)
                        {
                            TraceVulkanShader(
                                $"vk.compute_resources storage[{index}] begin " +
                                $"addr=0x{texture.Address:X16} fmt={texture.Format} " +
                                $"size={texture.Width}x{texture.Height} " +
                                $"view_mips={texture.BaseMipLevel}+{texture.MipLevels} " +
                                $"resource_mips={texture.ResourceMipLevels} " +
                                $"relative_level={texture.MipLevel}");
                        }

                        _ = ResolveStorageImageResource(texture);
                        if (traceResources)
                        {
                            TraceVulkanShader($"vk.compute_resources storage[{index}] ready");
                        }
                    }
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources resolve begin");
                }

                for (var index = 0; index < dispatch.Textures.Count; index++)
                {
                    resources.Textures[index] =
                        ResolveTextureResource(dispatch.Textures[index]);
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources resolve ready");
                }

                PrepareGuestBufferAllocations(dispatch.GlobalMemoryBuffers);
                for (var index = 0; index < dispatch.GlobalMemoryBuffers.Count; index++)
                {
                    resources.GlobalMemoryBuffers[index] =
                        CreateGlobalBufferResource(dispatch.GlobalMemoryBuffers[index]);
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources descriptors begin");
                }

                CreateTranslatedDescriptorResources(resources, ShaderStageFlags.ComputeBit);
                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources descriptors ready");
                }

                if (traceResources)
                {
                    TraceVulkanShader(
                        $"vk.compute_resources pipeline begin " +
                        $"cs=0x{dispatch.ShaderAddress:X16} " +
                        $"spirv={dispatch.ComputeSpirv.Length} " +
                        $"textures={resources.Textures.Length} " +
                        $"globals={resources.GlobalMemoryBuffers.Length}");
                }

                CreateComputePipeline(resources, dispatch.ComputeSpirv);
                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources pipeline ready");
                }

                return resources;
            }
            catch
            {
                DestroyTranslatedDrawResources(resources);
                throw;
            }
        }

        private static bool TryCompileFullscreenVertexShader(
            uint attributeCount,
            out byte[] spirv,
            out string error)
        {
            spirv = [];
            error = string.Empty;
            if (attributeCount > 32)
            {
                error = $"too many interpolated attributes: {attributeCount}";
                return false;
            }

            spirv = SpirvFixedShaders.CreateFullscreenVertex(attributeCount);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateTranslatedDescriptorResources(
            TranslatedDrawResources resources,
            ShaderStageFlags stageFlags)
        {
            var textureCount = resources.Textures.Length;
            var sampledImageCount = resources.Textures.Count(texture => !texture.IsStorage);
            var storageImageCount = textureCount - sampledImageCount;
            var globalBufferCount = resources.GlobalMemoryBuffers.Length;
            var bindingCount = textureCount + (globalBufferCount == 0 ? 0 : 1);
            var layout = GetOrCreateDescriptorLayout(resources, stageFlags, bindingCount);
            resources.DescriptorSetLayout = layout.DescriptorSetLayout;
            resources.PipelineLayout = layout.PipelineLayout;
            resources.DescriptorLayoutCached = true;
            if (bindingCount == 0)
            {
                return;
            }

            var setLayout = layout.DescriptorSetLayout;

            var poolSizes = new DescriptorPoolSize[
                (sampledImageCount == 0 ? 0 : 1) +
                (storageImageCount == 0 ? 0 : 1) +
                (globalBufferCount == 0 ? 0 : 1)];
            var poolSizeIndex = 0;
            if (sampledImageCount != 0)
            {
                poolSizes[poolSizeIndex++] = new DescriptorPoolSize
                {
                    Type = DescriptorType.CombinedImageSampler,
                    DescriptorCount = (uint)sampledImageCount,
                };
            }

            if (storageImageCount != 0)
            {
                poolSizes[poolSizeIndex++] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageImage,
                    DescriptorCount = (uint)storageImageCount,
                };
            }

            if (globalBufferCount != 0)
            {
                poolSizes[poolSizeIndex] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageBuffer,
                    DescriptorCount = (uint)globalBufferCount,
                };
            }

            if (_recycledDescriptorPools.TryPop(out var recycledPool))
            {
                Check(
                    _vk.ResetDescriptorPool(_device, recycledPool, 0),
                    "vkResetDescriptorPool");
                resources.DescriptorPool = recycledPool;
            }
            else
            {
                // Generously sized so any draw's set fits, making the pool
                // recyclable regardless of the draw's binding mix. AAA titles
                // (e.g. Demon's Souls) bind well over 32 textures in a single
                // descriptor set, so the sampled-image budget in particular
                // must be large enough to avoid a per-draw dynamic fallback.
                var genericPoolSizes = stackalloc DescriptorPoolSize[3];
                genericPoolSizes[0] = new DescriptorPoolSize
                {
                    Type = DescriptorType.CombinedImageSampler,
                    DescriptorCount = 256,
                };
                genericPoolSizes[1] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageImage,
                    DescriptorCount = 64,
                };
                genericPoolSizes[2] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageBuffer,
                    DescriptorCount = 64,
                };
                var poolInfo = new DescriptorPoolCreateInfo
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    MaxSets = 1,
                    PoolSizeCount = 3,
                    PPoolSizes = genericPoolSizes,
                };
                DescriptorPool descriptorPool;
                Check(
                    _vk.CreateDescriptorPool(
                        _device,
                        &poolInfo,
                        null,
                        out descriptorPool),
                    "vkCreateDescriptorPool");
                resources.DescriptorPool = descriptorPool;
            }

            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = resources.DescriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            DescriptorSet descriptorSet;
            Check(
                _vk.AllocateDescriptorSets(_device, &allocateInfo, out descriptorSet),
                "vkAllocateDescriptorSets");
            resources.DescriptorSet = descriptorSet;

            var imageInfos = new DescriptorImageInfo[textureCount];
            var bufferInfos = new DescriptorBufferInfo[globalBufferCount];
            var writes = new WriteDescriptorSet[bindingCount];
            fixed (DescriptorImageInfo* imageInfoPointer = imageInfos)
            fixed (DescriptorBufferInfo* bufferInfoPointer = bufferInfos)
            fixed (WriteDescriptorSet* writePointer = writes)
            {
                var writeIndex = 0;
                if (globalBufferCount != 0)
                {
                    for (var index = 0; index < globalBufferCount; index++)
                    {
                        bufferInfoPointer[index] = new DescriptorBufferInfo
                        {
                            Buffer = resources.GlobalMemoryBuffers[index].Buffer,
                            Offset = resources.GlobalMemoryBuffers[index].Offset,
                            Range = resources.GlobalMemoryBuffers[index].Size,
                        };
                    }

                    writePointer[writeIndex++] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = resources.DescriptorSet,
                        DstBinding = 0,
                        DescriptorCount = (uint)globalBufferCount,
                        DescriptorType = DescriptorType.StorageBuffer,
                        PBufferInfo = bufferInfoPointer,
                    };
                }

                for (var index = 0; index < textureCount; index++)
                {
                    var isStorage = resources.Textures[index].IsStorage;
                    if (!isStorage &&
                        resources.Textures[index].Sampler.Handle == 0)
                    {
                        resources.Textures[index].Sampler =
                            CreateSampler(resources.Textures[index].SamplerState);
                    }

                    imageInfoPointer[index] = new DescriptorImageInfo
                    {
                        Sampler = isStorage ? default : resources.Textures[index].Sampler,
                        ImageView = resources.Textures[index].View,
                        ImageLayout = isStorage ||
                            resources.Textures[index].GuestImage is { } guestImage &&
                            resources.Textures.Any(
                                texture =>
                                    texture.IsStorage &&
                                    texture.GuestImage == guestImage)
                                ? ImageLayout.General
                                : ImageLayout.ShaderReadOnlyOptimal,
                    };
                    writePointer[writeIndex++] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = resources.DescriptorSet,
                        DstBinding = (uint)(index + 1),
                        DescriptorCount = 1,
                        DescriptorType = isStorage
                            ? DescriptorType.StorageImage
                            : DescriptorType.CombinedImageSampler,
                        PImageInfo = &imageInfoPointer[index],
                    };
                }

                _vk.UpdateDescriptorSets(
                    _device,
                    (uint)bindingCount,
                    writePointer,
                    0,
                    null);
            }
        }

        private void CreateTranslatedPipeline(
            TranslatedDrawResources resources,
            byte[] vertexSpirv,
            byte[] fragmentSpirv,
            RenderPass renderPass,
            Extent2D extent)
        {
            var pipelineKey = new GraphicsPipelineKey(
                GetShaderDigest(vertexSpirv),
                GetShaderDigest(fragmentSpirv),
                renderPass.Handle,
                resources.Topology,
                resources.Blend,
                GetResourceLayoutKey(resources),
                GetVertexLayoutKey(resources),
                resources.Raster,
                resources.HasDepthAttachment ? resources.Depth : VulkanGuestDepthState.Default);
            if (_graphicsPipelines.TryGetValue(pipelineKey, out var cachedPipeline))
            {
                resources.Pipeline = cachedPipeline;
                resources.PipelineCached = true;
                return;
            }

            var vertexModule = CreateShaderModule(vertexSpirv);
            var fragmentModule = CreateShaderModule(fragmentSpirv);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                shaderStages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = entryPoint,
                };

                var vertexBindingDescriptions =
                    new VertexInputBindingDescription[resources.VertexBuffers.Length];
                var vertexAttributeDescriptions =
                    new VertexInputAttributeDescription[resources.VertexBuffers.Length];
                for (var index = 0; index < resources.VertexBuffers.Length; index++)
                {
                    var vertexBuffer = resources.VertexBuffers[index];
                    vertexBindingDescriptions[index] = new VertexInputBindingDescription
                    {
                        Binding = (uint)index,
                        Stride = vertexBuffer.Stride == 0
                            ? Math.Max(vertexBuffer.ComponentCount, 1) * sizeof(float)
                            : vertexBuffer.Stride,
                        InputRate = VertexInputRate.Vertex,
                    };
                    vertexAttributeDescriptions[index] = new VertexInputAttributeDescription
                    {
                        Location = vertexBuffer.Location,
                        Binding = (uint)index,
                        Format = ToVkVertexFormat(
                            vertexBuffer.DataFormat,
                            vertexBuffer.NumberFormat,
                            vertexBuffer.ComponentCount),
                        Offset = 0,
                    };
                }

                fixed (VertexInputBindingDescription* vertexBindingPointerBase = vertexBindingDescriptions)
                fixed (VertexInputAttributeDescription* vertexAttributePointerBase = vertexAttributeDescriptions)
                {
                    var vertexInput = new PipelineVertexInputStateCreateInfo
                    {
                        SType = StructureType.PipelineVertexInputStateCreateInfo,
                        VertexBindingDescriptionCount = (uint)vertexBindingDescriptions.Length,
                        PVertexBindingDescriptions = vertexBindingDescriptions.Length == 0
                            ? null
                            : vertexBindingPointerBase,
                        VertexAttributeDescriptionCount = (uint)vertexAttributeDescriptions.Length,
                        PVertexAttributeDescriptions = vertexAttributeDescriptions.Length == 0
                            ? null
                            : vertexAttributePointerBase,
                    };
                    var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                    {
                        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                        Topology = resources.Topology,
                        // Metal always applies primitive restart to strip/fan
                        // topologies with the max index as the cut value, so
                        // match that here. Enabling it for lists is what makes
                        // MoltenVK warn ("Metal does not support disabling
                        // primitive restart"); lists never carry a restart
                        // index, so leaving it off for them is both correct
                        // and warning-free.
                        PrimitiveRestartEnable = RequiresPrimitiveRestart(resources.Topology),
                    };
                    var viewport = new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
                    var scissor = new Rect2D(new Offset2D(0, 0), extent);
                    var viewportState = new PipelineViewportStateCreateInfo
                    {
                        SType = StructureType.PipelineViewportStateCreateInfo,
                        ViewportCount = 1,
                        PViewports = &viewport,
                        ScissorCount = 1,
                        PScissors = &scissor,
                    };
                    var raster = resources.Raster;
                    var cullMode = CullModeFlags.None;
                    if (raster.CullFront)
                    {
                        cullMode |= CullModeFlags.FrontBit;
                    }

                    if (raster.CullBack)
                    {
                        cullMode |= CullModeFlags.BackBit;
                    }

                    var rasterization = new PipelineRasterizationStateCreateInfo
                    {
                        SType = StructureType.PipelineRasterizationStateCreateInfo,
                        // Wireframe (PolygonMode.Line) needs the fillModeNonSolid
                        // device feature and is effectively unused by shipping
                        // titles, so fall back to a solid fill.
                        PolygonMode = PolygonMode.Fill,
                        CullMode = cullMode,
                        FrontFace = raster.FrontFaceClockwise
                            ? FrontFace.Clockwise
                            : FrontFace.CounterClockwise,
                        LineWidth = 1,
                    };
                    var multisample = new PipelineMultisampleStateCreateInfo
                    {
                        SType = StructureType.PipelineMultisampleStateCreateInfo,
                        RasterizationSamples = SampleCountFlags.Count1Bit,
                    };
                    var colorBlendAttachment = new PipelineColorBlendAttachmentState
                    {
                        BlendEnable = resources.Blend.Enable &&
                            IsBlendableFormat(resources.TargetFormat),
                        SrcColorBlendFactor = ToVkBlendFactor(resources.Blend.ColorSrcFactor),
                        DstColorBlendFactor = ToVkBlendFactor(resources.Blend.ColorDstFactor),
                        ColorBlendOp = ToVkBlendOp(resources.Blend.ColorFunc),
                        SrcAlphaBlendFactor = resources.Blend.SeparateAlphaBlend
                            ? ToVkBlendFactor(resources.Blend.AlphaSrcFactor)
                            : ToVkBlendFactor(resources.Blend.ColorSrcFactor),
                        DstAlphaBlendFactor = resources.Blend.SeparateAlphaBlend
                            ? ToVkBlendFactor(resources.Blend.AlphaDstFactor)
                            : ToVkBlendFactor(resources.Blend.ColorDstFactor),
                        AlphaBlendOp = resources.Blend.SeparateAlphaBlend
                            ? ToVkBlendOp(resources.Blend.AlphaFunc)
                            : ToVkBlendOp(resources.Blend.ColorFunc),
                        ColorWriteMask =
                            ToVkColorWriteMask(resources.Blend.WriteMask),
                    };
                    var colorBlend = new PipelineColorBlendStateCreateInfo
                    {
                        SType = StructureType.PipelineColorBlendStateCreateInfo,
                        AttachmentCount = 1,
                        PAttachments = &colorBlendAttachment,
                    };
                    var dynamicStateValues = stackalloc DynamicState[2];
                    dynamicStateValues[0] = DynamicState.Viewport;
                    dynamicStateValues[1] = DynamicState.Scissor;
                    var dynamicState = new PipelineDynamicStateCreateInfo
                    {
                        SType = StructureType.PipelineDynamicStateCreateInfo,
                        DynamicStateCount = 2,
                        PDynamicStates = dynamicStateValues,
                    };
                    var depth = resources.Depth;
                    var depthStencil = new PipelineDepthStencilStateCreateInfo
                    {
                        SType = StructureType.PipelineDepthStencilStateCreateInfo,
                        DepthTestEnable = depth.TestEnable,
                        DepthWriteEnable = depth.WriteEnable,
                        DepthCompareOp = ToVkCompareOp(depth.CompareOp),
                        DepthBoundsTestEnable = false,
                        StencilTestEnable = false,
                    };
                    var pipelineInfo = new GraphicsPipelineCreateInfo
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        StageCount = 2,
                        PStages = shaderStages,
                        PVertexInputState = &vertexInput,
                        PInputAssemblyState = &inputAssembly,
                        PViewportState = &viewportState,
                        PRasterizationState = &rasterization,
                        PMultisampleState = &multisample,
                        PColorBlendState = &colorBlend,
                        PDepthStencilState = resources.HasDepthAttachment ? &depthStencil : null,
                        PDynamicState = &dynamicState,
                        Layout = resources.PipelineLayout,
                        RenderPass = renderPass,
                        Subpass = 0,
                    };
                    Pipeline pipeline;
                    var pipelineCreationStart = Stopwatch.GetTimestamp();
                    Check(
                        _vk.CreateGraphicsPipelines(
                            _device,
                            _pipelineCache,
                            1,
                            &pipelineInfo,
                        null,
                        out pipeline),
                    "vkCreateGraphicsPipelines(translated)");
                    MarkPipelineCacheDirty(pipelineCreationStart);
                    resources.Pipeline = pipeline;
                    resources.PipelineCached = true;
                    _graphicsPipelines.Add(pipelineKey, pipeline);
                    Interlocked.Increment(ref _perfPipelineCreations);
                    SetDebugName(
                        ObjectType.Pipeline,
                        pipeline.Handle,
                        $"SharpEmu graphics ps={fragmentSpirv.Length}b attrs={resources.Textures.Length}");
                }
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private DescriptorLayoutBundle GetOrCreateDescriptorLayout(
            TranslatedDrawResources resources,
            ShaderStageFlags stageFlags,
            int bindingCount)
        {
            var key = new DescriptorLayoutKey(stageFlags, GetResourceLayoutKey(resources));
            if (_descriptorLayouts.TryGetValue(key, out var cached))
            {
                return cached;
            }

            DescriptorSetLayout descriptorSetLayout = default;
            if (bindingCount != 0)
            {
                var bindings = new DescriptorSetLayoutBinding[bindingCount];
                var bindingOffset = 0;
                if (resources.GlobalMemoryBuffers.Length != 0)
                {
                    bindings[bindingOffset++] = new DescriptorSetLayoutBinding
                    {
                        Binding = 0,
                        DescriptorType = DescriptorType.StorageBuffer,
                        DescriptorCount = (uint)resources.GlobalMemoryBuffers.Length,
                        StageFlags = stageFlags,
                    };
                }

                for (var index = 0; index < resources.Textures.Length; index++)
                {
                    bindings[bindingOffset + index] = new DescriptorSetLayoutBinding
                    {
                        Binding = (uint)(index + 1),
                        DescriptorType = resources.Textures[index].IsStorage
                            ? DescriptorType.StorageImage
                            : DescriptorType.CombinedImageSampler,
                        DescriptorCount = 1,
                        StageFlags = stageFlags,
                    };
                }

                fixed (DescriptorSetLayoutBinding* bindingPointer = bindings)
                {
                    var descriptorInfo = new DescriptorSetLayoutCreateInfo
                    {
                        SType = StructureType.DescriptorSetLayoutCreateInfo,
                        BindingCount = (uint)bindings.Length,
                        PBindings = bindingPointer,
                    };
                    Check(
                        _vk.CreateDescriptorSetLayout(
                            _device,
                            &descriptorInfo,
                            null,
                            out descriptorSetLayout),
                        "vkCreateDescriptorSetLayout");
                }
            }

            var pipelineInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
            };
            if (descriptorSetLayout.Handle != 0)
            {
                pipelineInfo.SetLayoutCount = 1;
                pipelineInfo.PSetLayouts = &descriptorSetLayout;
            }

            var computePushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = 3 * sizeof(uint),
            };
            if ((stageFlags & ShaderStageFlags.ComputeBit) != 0)
            {
                pipelineInfo.PushConstantRangeCount = 1;
                pipelineInfo.PPushConstantRanges = &computePushConstantRange;
            }

            PipelineLayout pipelineLayout;
            Check(
                _vk.CreatePipelineLayout(
                    _device,
                    &pipelineInfo,
                    null,
                    out pipelineLayout),
                "vkCreatePipelineLayout");
            var created = new DescriptorLayoutBundle(descriptorSetLayout, pipelineLayout);
            _descriptorLayouts.Add(key, created);
            return created;
        }

        private string GetShaderDigest(byte[] spirv)
        {
            if (_shaderDigests.TryGetValue(spirv, out var digest))
            {
                return digest;
            }

            digest = Convert.ToHexString(SHA256.HashData(spirv));
            _shaderDigests.Add(spirv, digest);
            return digest;
        }

        private static string GetResourceLayoutKey(TranslatedDrawResources resources) =>
            resources.ResourceLayoutKey ??= BuildResourceLayoutKey(resources);

        private static string GetVertexLayoutKey(TranslatedDrawResources resources) =>
            resources.VertexLayoutKey ??= BuildVertexLayoutKey(resources);

        private static string BuildResourceLayoutKey(TranslatedDrawResources resources)
        {
            var key = new StringBuilder();
            key.Append(resources.GlobalMemoryBuffers.Length).Append(':');
            foreach (var texture in resources.Textures)
            {
                key.Append(texture.IsStorage ? 'S' : 'T');
            }

            return key.ToString();
        }

        private static string BuildVertexLayoutKey(TranslatedDrawResources resources)
        {
            var key = new StringBuilder();
            foreach (var buffer in resources.VertexBuffers)
            {
                key.Append(buffer.Location).Append(',')
                    .Append(buffer.ComponentCount).Append(',')
                    .Append(buffer.DataFormat).Append(',')
                    .Append(buffer.NumberFormat).Append(',')
                    .Append(buffer.Stride == 0
                        ? Math.Max(buffer.ComponentCount, 1) * sizeof(float)
                        : buffer.Stride)
                    .Append(';');
            }

            return key.ToString();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateComputePipeline(
            TranslatedDrawResources resources,
            byte[] computeSpirv)
        {
            var pipelineKey = new ComputePipelineKey(
                GetShaderDigest(computeSpirv),
                GetResourceLayoutKey(resources));
            if (_computePipelines.TryGetValue(pipelineKey, out var cachedPipeline))
            {
                resources.Pipeline = cachedPipeline;
                resources.PipelineCached = true;
                return;
            }

            var computeModule = CreateShaderModule(computeSpirv);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = computeModule,
                    PName = entryPoint,
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Flags = PipelineCreateFlags.CreateDispatchBaseBit,
                    Stage = stage,
                    Layout = resources.PipelineLayout,
                };
                Pipeline pipeline;
                var pipelineCreationStart = Stopwatch.GetTimestamp();
                Check(
                    _vk.CreateComputePipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out pipeline),
                    "vkCreateComputePipelines(translated)");
                MarkPipelineCacheDirty(pipelineCreationStart);
                resources.Pipeline = pipeline;
                resources.PipelineCached = true;
                SetDebugName(
                    ObjectType.Pipeline,
                    pipeline.Handle,
                    $"SharpEmu compute cs={computeSpirv.Length}b");
                _computePipelines.Add(pipelineKey, pipeline);
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, computeModule, null);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TextureResource ResolveTextureResource(VulkanGuestDrawTexture texture)
        {
            if (texture.IsStorage)
            {
                return ResolveStorageImageResource(texture);
            }

            if (texture.Address != 0 &&
                TryResolveGuestDepthTexture(texture, out var depthTexture))
            {
                return depthTexture;
            }

            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);
            if (texture.Address != 0 &&
                _guestImages.TryGetValue(texture.Address, out var guestImage) &&
                IsCompatibleGuestImageAlias(texture, guestImage) &&
                IsCompatibleViewFormat(guestImage.Format, vkFormat) &&
                TryGetOrCreateGuestImageView(
                    guestImage,
                    vkFormat,
                    mipLevel: texture.BaseMipLevel,
                    levelCount: texture.MipLevels,
                    dstSelect: texture.DstSelect,
                    out var view))
            {
                if (ShouldTraceVulkanResources() &&
                    _tracedTextureCacheHits.Add(
                        (texture.Address, texture.Width, texture.Height, vkFormat)))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.texture_cache_hit addr=0x{texture.Address:X16} " +
                        $"size={texture.Width}x{texture.Height} " +
                        $"image_format={guestImage.Format} view_format={vkFormat}");
                }

                if (guestImage.Width != texture.Width ||
                    guestImage.Height != texture.Height)
                {
                    TraceVulkanShader(
                        $"vk.texture_cache_alias addr=0x{texture.Address:X16} " +
                        $"texture={texture.Width}x{texture.Height} " +
                        $"image={guestImage.Width}x{guestImage.Height} " +
                        $"tile={texture.TileMode} format={vkFormat}");
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES"),
                        "alias",
                        StringComparison.OrdinalIgnoreCase) &&
                    _tracedGuestImageContents.Add(guestImage.Address))
                {
                    // Deferred: reading back here would clobber the command
                    // buffer mid-recording; drained after the next present.
                    _pendingAliasImageDumps.Enqueue(guestImage);
                }

                return new TextureResource
                {
                    Address = texture.Address,
                    Image = guestImage.Image,
                    View = view,
                    Width = guestImage.Width,
                    Height = guestImage.Height,
                    RowLength = guestImage.Width,
                    DstSelect = texture.DstSelect,
                    SamplerState = texture.Sampler,
                    GuestImage = guestImage,
                };
            }

            // Deferred/compute pipelines (Silent Hill's title screen) render the
            // scene into offscreen render targets, then sample those targets in a
            // composite/lighting pass. When the sampled descriptor's size or exact
            // format does not match the cached image (a common alias miss), the
            // old code fell back to reading GUEST MEMORY - which the GPU never
            // wrote, so the composite sampled zeros and presented a black frame.
            // A live GPU-produced image (a real render target, RenderPass set, or
            // already initialized) is authoritative: prefer sampling it through a
            // format-compatible view over zero guest memory, using the image's own
            // dimensions. Guarded to genuine GPU-written images so an unrelated
            // resource that merely collided on the address is not aliased.
            if (texture.Address != 0 &&
                !texture.IsStorage &&
                _guestImages.TryGetValue(texture.Address, out var liveImage) &&
                (liveImage.RenderPass.Handle != 0 || liveImage.Initialized) &&
                IsCompatibleViewFormat(liveImage.Format, vkFormat) &&
                TryGetOrCreateGuestImageView(
                    liveImage,
                    vkFormat,
                    mipLevel: Math.Min(texture.BaseMipLevel, liveImage.MipLevels - 1),
                    levelCount: 1,
                    dstSelect: texture.DstSelect,
                    out var liveView))
            {
                if (ShouldTraceVulkanResources() &&
                    _tracedTextureCacheHits.Add(
                        (texture.Address, texture.Width, texture.Height, vkFormat)))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.live_image_alias addr=0x{texture.Address:X16} " +
                        $"tex={texture.Width}x{texture.Height} img={liveImage.Width}x{liveImage.Height} " +
                        $"rt={liveImage.RenderPass.Handle != 0} init={liveImage.Initialized} vk={vkFormat}");
                }

                return new TextureResource
                {
                    Address = texture.Address,
                    Image = liveImage.Image,
                    View = liveView,
                    Width = liveImage.Width,
                    Height = liveImage.Height,
                    RowLength = liveImage.Width,
                    DstSelect = texture.DstSelect,
                    SamplerState = texture.Sampler,
                    GuestImage = liveImage,
                };
            }

            if (ShouldTraceVulkanResources() && texture.Address != 0)
            {
                if (_guestImages.TryGetValue(texture.Address, out var missImage))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.alias_miss addr=0x{texture.Address:X16} " +
                        $"reason={(IsCompatibleGuestImageAlias(texture, missImage) ? "format" : "size")} " +
                        $"tex={texture.Width}x{texture.Height}/f{texture.Format}/n{texture.NumberType}/vk{vkFormat} " +
                        $"img={missImage.Width}x{missImage.Height}/imgfmt{missImage.Format} " +
                        $"init={missImage.Initialized}");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.alias_miss addr=0x{texture.Address:X16} " +
                        $"reason=absent tex={texture.Width}x{texture.Height}/f{texture.Format}/n{texture.NumberType}");
                }
            }

            return GetOrCreateCachedTextureResource(texture);
        }

        private bool TryResolveGuestDepthTexture(
            VulkanGuestDrawTexture texture,
            out TextureResource resource)
        {
            foreach (var depth in _guestDepthImages.Values)
            {
                if (texture.Address != depth.Address &&
                    texture.Address != depth.ReadAddress &&
                    texture.Address != depth.WriteAddress)
                {
                    continue;
                }

                if (texture.Width > depth.Width || texture.Height > depth.Height)
                {
                    continue;
                }

                if (!depth.SampleViews.TryGetValue(texture.DstSelect, out var view))
                {
                    var viewInfo = new ImageViewCreateInfo
                    {
                        SType = StructureType.ImageViewCreateInfo,
                        Image = depth.Image,
                        ViewType = ImageViewType.Type2D,
                        Format = DepthFormat,
                        Components = ToVkComponentMapping(texture.DstSelect),
                        SubresourceRange = new ImageSubresourceRange(
                            ImageAspectFlags.DepthBit,
                            0,
                            1,
                            0,
                            1),
                    };
                    Check(
                        _vk.CreateImageView(_device, &viewInfo, null, out view),
                        "vkCreateImageView(depth sample)");
                    SetDebugName(
                        ObjectType.ImageView,
                        view.Handle,
                        $"SharpEmu guest depth sample 0x{depth.Address:X16} " +
                        $"dst=0x{texture.DstSelect:X3}");
                    depth.SampleViews.Add(texture.DstSelect, view);
                }

                if (_tracedDepthTextureAliases.Add(
                        (texture.Address, texture.Width, texture.Height, texture.DstSelect)))
                {
                    TraceVulkanShader(
                        $"vk.depth_texture_alias addr=0x{texture.Address:X16} " +
                        $"depth=0x{depth.Address:X16} " +
                        $"texture={texture.Width}x{texture.Height} " +
                        $"surface={depth.Width}x{depth.Height} dst=0x{texture.DstSelect:X3}");
                }
                resource = new TextureResource
                {
                    Address = texture.Address,
                    Image = depth.Image,
                    View = view,
                    Width = depth.Width,
                    Height = depth.Height,
                    RowLength = depth.Width,
                    DstSelect = texture.DstSelect,
                    SamplerState = texture.Sampler,
                    GuestDepth = depth,
                };
                return true;
            }

            resource = null!;
            return false;
        }

        private readonly Dictionary<TextureContentIdentity, TextureResource> _textureCache = new();

        /// <summary>
        /// Guest textures are static assets in the common case, but every draw
        /// used to restage and reupload them (a fresh image, device memory and
        /// staging buffer per draw). Cache the uploaded resource per descriptor
        /// identity and invalidate through the CPU write tracker, so animated
        /// or streamed texture memory still refreshes.
        /// </summary>
        private TextureResource GetOrCreateCachedTextureResource(VulkanGuestDrawTexture texture)
        {
            if (texture.Address == 0)
            {
                return CreateTextureResource(texture);
            }

            var key = new TextureContentIdentity(
                texture.Address,
                texture.Width,
                texture.Height,
                texture.Format,
                texture.NumberType,
                texture.DstSelect,
                texture.TileMode,
                texture.Pitch,
                texture.Sampler);
            if (_textureCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // Empty pixels mean the submit thread skipped the guest-memory
            // copy because this identity was marked cached; a miss here is
            // an invalidation race (eviction, cache clear). Self-heal by
            // reading the texels directly rather than rendering a fallback.
            if (texture.RgbaPixels.Length == 0)
            {
                var refreshed = TryReadGuestTexturePixels(texture);
                if (refreshed is null)
                {
                    return CreateTextureResource(texture);
                }

                texture = texture with { RgbaPixels = refreshed };
            }

            var resource = CreateTextureResource(texture);
            if (resource.OwnsStorage)
            {
                resource.Cached = true;
                _textureCache[key] = resource;
                MarkTextureContentCached(key);
                SharpEmu.HLE.GuestImageWriteTracker.Track(
                    texture.Address,
                    (ulong)texture.RgbaPixels.Length);
            }

            return resource;
        }

        private void EvictDirtyCachedTextures()
        {
            if (_textureCache.Count == 0)
            {
                return;
            }

            List<TextureContentIdentity>? evicted = null;
            foreach (var entry in _textureCache)
            {
                if (SharpEmu.HLE.GuestImageWriteTracker.ConsumeDirty(entry.Key.Address))
                {
                    (evicted ??= []).Add(entry.Key);
                }
            }

            if (evicted is null && _textureCache.Count <= 2048)
            {
                return;
            }

            // Destruction is deferred until every submission that may still
            // reference the texture has completed (fences signal in queue
            // order), so eviction never has to drain the GPU. An open batch
            // is flushed first so the retire timeline exactly covers every
            // recorded reference (nothing may guess which submission lands
            // next on the shared queue).
            if (_batchOpen)
            {
                FlushBatchedGuestCommands();
            }

            var retireTimeline = _submitTimeline;
            if (_textureCache.Count > 2048)
            {
                foreach (var entry in _textureCache)
                {
                    _deferredTextureDestroys.Enqueue((entry.Value, retireTimeline));
                }

                _textureCache.Clear();
                ClearCachedTextureIdentities();
                return;
            }

            foreach (var key in evicted!)
            {
                if (_textureCache.Remove(key, out var resource))
                {
                    UnmarkTextureContentCached(key);
                    _deferredTextureDestroys.Enqueue((resource, retireTimeline));
                    SharpEmu.HLE.GuestImageWriteTracker.Rearm(key.Address);
                }
            }
        }

        private void DestroyCachedTextureResource(TextureResource texture)
        {
            texture.Cached = false;
            if (texture.View.Handle != 0)
            {
                _vk.DestroyImageView(_device, texture.View, null);
            }

            if (texture.Image.Handle != 0 && texture.GuestImage is null)
            {
                _vk.DestroyImage(_device, texture.Image, null);
                if (texture.ImageMemory.Handle != 0)
                {
                    _vk.FreeMemory(_device, texture.ImageMemory, null);
                }
            }

            if (texture.StagingBuffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, texture.StagingBuffer, null);
            }

            if (texture.StagingMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, texture.StagingMemory, null);
            }
        }

        private static bool IsCompatibleGuestImageAlias(
            VulkanGuestDrawTexture texture,
            GuestImageResource guestImage)
        {
            if (guestImage.Width == texture.Width &&
                guestImage.Height == texture.Height)
            {
                return true;
            }

            if (texture.TileMode == 0 ||
                texture.Width == 0 ||
                texture.Height == 0)
            {
                return false;
            }

            return texture.Width <= guestImage.Width &&
                texture.Height <= guestImage.Height;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TextureResource ResolveStorageImageResource(VulkanGuestDrawTexture texture)
        {
            if (texture.Address == 0)
            {
                return CreateStorageScratchResource(texture);
            }

            var guestImage = ResolveStorageGuestImage(texture);
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);
            var selectedMipLevel = GetStorageMipLevel(texture);
            var view = GetOrCreateGuestImageView(
                guestImage,
                vkFormat,
                selectedMipLevel,
                levelCount: 1);
            var resource = new TextureResource
            {
                Address = texture.Address,
                Image = guestImage.Image,
                View = view,
                Width = guestImage.Width,
                Height = guestImage.Height,
                RowLength = guestImage.Width,
                DstSelect = texture.DstSelect,
                IsStorage = true,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
            };

            if (!guestImage.Initialized &&
                !guestImage.InitialUploadPending &&
                texture.MipLevel == 0)
            {
                var expectedSize = GetTextureByteCount(
                    texture.Format,
                    texture.Width,
                    texture.Height);
                if ((ulong)texture.RgbaPixels.Length == expectedSize &&
                    texture.RgbaPixels.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                {
                    var uploadPixels = texture.Format == 13
                        ? ExpandRgb32Pixels(texture.RgbaPixels)
                        : texture.RgbaPixels;
                    var uploadSize = (ulong)uploadPixels.Length;
                    resource.StagingBuffer = CreateBuffer(
                        uploadSize,
                        BufferUsageFlags.TransferSrcBit,
                        MemoryPropertyFlags.HostVisibleBit |
                        MemoryPropertyFlags.HostCoherentBit,
                        out resource.StagingMemory);

                    void* mapped;
                    Check(
                        _vk.MapMemory(
                            _device,
                            resource.StagingMemory,
                            0,
                            uploadSize,
                            0,
                            &mapped),
                        "vkMapMemory(storage texture)");
                    fixed (byte* source = uploadPixels)
                    {
                        System.Buffer.MemoryCopy(
                            source,
                            mapped,
                            uploadPixels.Length,
                            uploadPixels.Length);
                    }

                    _vk.UnmapMemory(_device, resource.StagingMemory);
                    resource.NeedsUpload = true;
                    guestImage.InitialUploadPending = true;
                    TraceVulkanShader(
                        $"vk.storage_upload addr=0x{texture.Address:X16} " +
                        $"size={texture.Width}x{texture.Height} " +
                        $"logical_bytes={expectedSize} upload_bytes={uploadSize}");
                }
            }

            return resource;
        }

        private TextureResource CreateStorageScratchResource(VulkanGuestDrawTexture texture)
        {
            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = vkFormat,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage =
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.StorageBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(
                _vk.CreateImage(_device, &imageInfo, null, out var image),
                "vkCreateImage(storage scratch)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(
                _vk.AllocateMemory(_device, &allocationInfo, null, out var memory),
                "vkAllocateMemory(storage scratch)");
            Check(
                _vk.BindImageMemory(_device, image, memory, 0),
                "vkBindImageMemory(storage scratch)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = vkFormat,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = ColorSubresourceRange(),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var view),
                "vkCreateImageView(storage scratch)");
            SetDebugName(ObjectType.Image, image.Handle, $"SharpEmu scratch storage {width}x{height} {vkFormat}");
            SetDebugName(ObjectType.ImageView, view.Handle, $"SharpEmu scratch storage {width}x{height} {vkFormat} view");

            var guestImage = new GuestImageResource
            {
                Address = 0,
                Width = width,
                Height = height,
                MipLevels = 1,
                GuestFormat = GetGuestTextureFormat(texture.Format, texture.NumberType),
                Format = vkFormat,
                Image = image,
                Memory = memory,
                View = view,
            };

            return new TextureResource
            {
                Address = 0,
                Image = image,
                ImageMemory = memory,
                View = view,
                Width = width,
                Height = height,
                RowLength = width,
                DstSelect = texture.DstSelect,
                OwnsStorage = true,
                IsStorage = true,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
            };
        }

        private GuestImageResource ResolveStorageGuestImage(VulkanGuestDrawTexture texture)
        {
            if (texture.Address == 0)
            {
                throw new InvalidOperationException("Storage image has no guest address.");
            }

            var format = GetTextureFormat(texture.Format, texture.NumberType);
            var guestImage = GetOrCreateGuestImage(
                new VulkanGuestRenderTarget(
                    texture.Address,
                    texture.Width,
                    texture.Height,
                    texture.Format,
                    texture.NumberType,
                    texture.ResourceMipLevels),
                format);
            var selectedMipLevel = GetStorageMipLevel(texture);
            if (selectedMipLevel >= guestImage.MipLevels)
            {
                throw new InvalidOperationException(
                    $"Storage mip {selectedMipLevel} (base {texture.BaseMipLevel} + relative " +
                    $"{texture.MipLevel}) exceeds image mip count {guestImage.MipLevels}.");
            }

            return guestImage;
        }

        private static uint GetStorageMipLevel(VulkanGuestDrawTexture texture)
        {
            // IMAGE_STORE targets BASE_LEVEL and IMAGE_STORE_MIP's operand is
            // expressed in resource-view space, so Vulkan's absolute image
            // subresource is descriptor base plus the instruction-relative
            // mip. Sampled views achieve the same mapping through their view
            // base in ResolveTextureResource.
            var selectedMipLevel = (ulong)texture.BaseMipLevel + texture.MipLevel;
            if (selectedMipLevel > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Storage mip overflow (base {texture.BaseMipLevel} + relative {texture.MipLevel}).");
            }

            return (uint)selectedMipLevel;
        }

        private TextureResource CreateTextureResource(VulkanGuestDrawTexture texture)
        {
            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var rowLength = texture.TileMode == 0
                ? Math.Max(texture.Pitch, width)
                : width;
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);

            var expectedSize = GetTextureByteCount(texture.Format, rowLength, height);
            if (_tracedTextureUploads.Add((texture.Address, width, height, vkFormat)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.texture addr=0x{texture.Address:X16} " +
                    $"fmt={texture.Format} num={texture.NumberType} vk={vkFormat} " +
                    $"size={width}x{height} row={rowLength} tile={texture.TileMode} " +
                    $"dst=0x{texture.DstSelect:X3} " +
                    $"bytes={texture.RgbaPixels.Length} expected={expectedSize}");
            }
            var pixels = texture.RgbaPixels.Length == (int)expectedSize
                ? texture.RgbaPixels
                : CreateFallbackTexturePixels(texture.Format, rowLength, height, expectedSize);
            DumpTextureUpload(texture, pixels, rowLength, width, height);
            var uploadPixels = texture.Format == 13
                ? ExpandRgb32Pixels(pixels)
                : pixels;
            var uploadSize = (ulong)uploadPixels.Length;

            var stagingBuffer = CreateBuffer(
                uploadSize,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out var stagingMemory);
            void* mapped;
            Check(_vk.MapMemory(_device, stagingMemory, 0, uploadSize, 0, &mapped), "vkMapMemory(texture)");
            fixed (byte* source = uploadPixels)
            {
                System.Buffer.MemoryCopy(
                    source,
                    mapped,
                    uploadPixels.Length,
                    uploadPixels.Length);
            }
            _vk.UnmapMemory(_device, stagingMemory);

            var supportsAttachmentUsage = !IsBlockCompressedFormat(vkFormat);
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = supportsAttachmentUsage
                    ? ImageCreateFlags.CreateMutableFormatBit | ImageCreateFlags.CreateExtendedUsageBit
                    : 0,
                ImageType = ImageType.Type2D,
                Format = vkFormat,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = supportsAttachmentUsage
                    ? ImageUsageFlags.TransferDstBit |
                      ImageUsageFlags.SampledBit |
                      ImageUsageFlags.ColorAttachmentBit |
                      ImageUsageFlags.StorageBit |
                      ImageUsageFlags.TransferSrcBit
                    : ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(texture)");
            _vk.GetImageMemoryRequirements(_device, image, out var imageRequirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = imageRequirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    imageRequirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(_vk.AllocateMemory(_device, &memoryInfo, null, out var imageMemory), "vkAllocateMemory(texture)");
            Check(_vk.BindImageMemory(_device, image, imageMemory, 0), "vkBindImageMemory(texture)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = vkFormat,
                Components = ToVkComponentMapping(texture.DstSelect),
                SubresourceRange = ColorSubresourceRange(),
            };
            Check(_vk.CreateImageView(_device, &viewInfo, null, out var view), "vkCreateImageView(texture)");
            var debugName = TextureDebugName(texture, vkFormat);
            SetDebugName(ObjectType.Buffer, stagingBuffer.Handle, $"{debugName} staging");
            SetDebugName(ObjectType.Image, image.Handle, $"{debugName} image");
            SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} view");
            var resource = new TextureResource
            {
                Address = texture.Address,
                StagingBuffer = stagingBuffer,
                StagingMemory = stagingMemory,
                Image = image,
                ImageMemory = imageMemory,
                View = view,
                Width = width,
                Height = height,
                RowLength = rowLength,
                DstSelect = texture.DstSelect,
                NeedsUpload = true,
                OwnsStorage = true,
                SamplerState = texture.Sampler,
            };

            if (texture.Address != 0 &&
                !_guestImages.ContainsKey(texture.Address))
            {
                var guestImage = new GuestImageResource
                {
                    Address = texture.Address,
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    GuestFormat = GetGuestTextureFormat(texture.Format, texture.NumberType),
                    Format = vkFormat,
                    Image = image,
                    Memory = imageMemory,
                    View = view,
                    InitialUploadPending = true,
                };
                _guestImages.Add(texture.Address, guestImage);
                lock (_gate)
                {
                    _guestImageExtents[texture.Address] = (
                        width,
                        height,
                        GetTextureByteCount(texture.Format, width, height));
                }

                if (_traceGuestImageEvents)
                {
                    Console.Error.WriteLine(
                        $"[GIMG] created-as-texture addr=0x{texture.Address:X} " +
                        $"{width}x{height} fmt={vkFormat}");
                }

                resource.OwnsStorage = false;
                resource.GuestImage = guestImage;
                lock (_gate)
                {
                    var guestFormat = VulkanVideoPresenter.GetGuestTextureFormat(
                        texture.Format,
                        texture.NumberType);
                    if (guestFormat != 0)
                    {
                        _availableGuestImages[texture.Address] = guestFormat;
                    }
                }
            }

            return resource;
        }

        /// <summary>
        /// Creates a draw-local sampled image containing the render target's
        /// value immediately before the draw. RDNA permits a pixel shader to
        /// read an attachment while producing its replacement value, whereas
        /// core Vulkan does not permit the same subresource to be both a
        /// sampled image and a color attachment. Keeping the two images
        /// distinct preserves the guest's read-before-write behavior without
        /// a queue idle or an undefined Vulkan feedback loop.
        /// </summary>
        private TextureResource CreateRenderTargetFeedbackSnapshot(
            VulkanGuestDrawTexture texture,
            GuestImageResource source)
        {
            var image = default(Image);
            var memory = default(DeviceMemory);
            var view = default(ImageView);
            try
            {
                var imageInfo = new ImageCreateInfo
                {
                    SType = StructureType.ImageCreateInfo,
                    Flags =
                        ImageCreateFlags.CreateMutableFormatBit |
                        ImageCreateFlags.CreateExtendedUsageBit,
                    ImageType = ImageType.Type2D,
                    Format = source.Format,
                    Extent = new Extent3D(source.Width, source.Height, 1),
                    MipLevels = source.MipLevels,
                    ArrayLayers = 1,
                    Samples = SampleCountFlags.Count1Bit,
                    Tiling = ImageTiling.Optimal,
                    Usage =
                        ImageUsageFlags.TransferDstBit |
                        ImageUsageFlags.SampledBit,
                    SharingMode = SharingMode.Exclusive,
                    InitialLayout = ImageLayout.Undefined,
                };
                Check(
                    _vk.CreateImage(_device, &imageInfo, null, out image),
                    "vkCreateImage(render-target feedback snapshot)");
                _vk.GetImageMemoryRequirements(_device, image, out var requirements);
                var memoryInfo = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = FindMemoryType(
                        requirements.MemoryTypeBits,
                        MemoryPropertyFlags.DeviceLocalBit),
                };
                Check(
                    _vk.AllocateMemory(_device, &memoryInfo, null, out memory),
                    "vkAllocateMemory(render-target feedback snapshot)");
                Check(
                    _vk.BindImageMemory(_device, image, memory, 0),
                    "vkBindImageMemory(render-target feedback snapshot)");

                var viewFormat = GetTextureFormat(texture.Format, texture.NumberType);
                if (!IsCompatibleViewFormat(source.Format, viewFormat))
                {
                    throw new InvalidOperationException(
                        $"Feedback view format {viewFormat} is incompatible with " +
                        $"render-target format {source.Format}.");
                }

                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = image,
                    ViewType = ImageViewType.Type2D,
                    Format = viewFormat,
                    Components = ToVkComponentMapping(texture.DstSelect),
                    SubresourceRange = ColorSubresourceRange(0, source.MipLevels),
                };
                Check(
                    _vk.CreateImageView(_device, &viewInfo, null, out view),
                    "vkCreateImageView(render-target feedback snapshot)");

                var debugName =
                    $"SharpEmu feedback 0x{source.Address:X16} " +
                    $"{source.Width}x{source.Height} {source.Format}->{viewFormat}";
                SetDebugName(ObjectType.Image, image.Handle, $"{debugName} image");
                SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} view");
                TraceVulkanShader(
                    $"vk.feedback_snapshot_create addr=0x{source.Address:X16} " +
                    $"size={source.Width}x{source.Height} mips={source.MipLevels} " +
                    $"image_format={source.Format} view_format={viewFormat} " +
                    $"dst=0x{texture.DstSelect:X3}");

                return new TextureResource
                {
                    Address = texture.Address,
                    Image = image,
                    ImageMemory = memory,
                    View = view,
                    Width = source.Width,
                    Height = source.Height,
                    RowLength = source.Width,
                    DstSelect = texture.DstSelect,
                    OwnsStorage = true,
                    SamplerState = texture.Sampler,
                    FeedbackSource = source,
                };
            }
            catch
            {
                if (view.Handle != 0)
                {
                    _vk.DestroyImageView(_device, view, null);
                }

                if (image.Handle != 0)
                {
                    _vk.DestroyImage(_device, image, null);
                }

                if (memory.Handle != 0)
                {
                    _vk.FreeMemory(_device, memory, null);
                }

                throw;
            }
        }

        private TextureResource CreateDepthFeedbackSnapshot(
            VulkanGuestDrawTexture texture,
            GuestDepthResource source)
        {
            var image = default(Image);
            var memory = default(DeviceMemory);
            var view = default(ImageView);
            try
            {
                var imageInfo = new ImageCreateInfo
                {
                    SType = StructureType.ImageCreateInfo,
                    ImageType = ImageType.Type2D,
                    Format = DepthFormat,
                    Extent = new Extent3D(source.Width, source.Height, 1),
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Samples = SampleCountFlags.Count1Bit,
                    Tiling = ImageTiling.Optimal,
                    Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                    SharingMode = SharingMode.Exclusive,
                    InitialLayout = ImageLayout.Undefined,
                };
                Check(
                    _vk.CreateImage(_device, &imageInfo, null, out image),
                    "vkCreateImage(depth feedback snapshot)");
                _vk.GetImageMemoryRequirements(_device, image, out var requirements);
                var memoryInfo = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = FindMemoryType(
                        requirements.MemoryTypeBits,
                        MemoryPropertyFlags.DeviceLocalBit),
                };
                Check(
                    _vk.AllocateMemory(_device, &memoryInfo, null, out memory),
                    "vkAllocateMemory(depth feedback snapshot)");
                Check(
                    _vk.BindImageMemory(_device, image, memory, 0),
                    "vkBindImageMemory(depth feedback snapshot)");
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = image,
                    ViewType = ImageViewType.Type2D,
                    Format = DepthFormat,
                    Components = ToVkComponentMapping(texture.DstSelect),
                    SubresourceRange = new ImageSubresourceRange(
                        ImageAspectFlags.DepthBit,
                        0,
                        1,
                        0,
                        1),
                };
                Check(
                    _vk.CreateImageView(_device, &viewInfo, null, out view),
                    "vkCreateImageView(depth feedback snapshot)");
                SetDebugName(
                    ObjectType.Image,
                    image.Handle,
                    $"SharpEmu depth feedback 0x{source.Address:X16} image");
                SetDebugName(
                    ObjectType.ImageView,
                    view.Handle,
                    $"SharpEmu depth feedback 0x{source.Address:X16} view");
                return new TextureResource
                {
                    Address = texture.Address,
                    Image = image,
                    ImageMemory = memory,
                    View = view,
                    Width = source.Width,
                    Height = source.Height,
                    RowLength = source.Width,
                    DstSelect = texture.DstSelect,
                    OwnsStorage = true,
                    SamplerState = texture.Sampler,
                    DepthFeedbackSource = source,
                };
            }
            catch
            {
                if (view.Handle != 0)
                {
                    _vk.DestroyImageView(_device, view, null);
                }
                if (image.Handle != 0)
                {
                    _vk.DestroyImage(_device, image, null);
                }
                if (memory.Handle != 0)
                {
                    _vk.FreeMemory(_device, memory, null);
                }
                throw;
            }
        }

        private void DumpTextureUpload(
            VulkanGuestDrawTexture texture,
            byte[] pixels,
            uint rowLength,
            uint width,
            uint height)
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("SHARPEMU_DUMP_TEXTURES"),
                    "1",
                    StringComparison.Ordinal) ||
                texture.IsFallback ||
                texture.IsStorage ||
                GetTextureBytesPerPixel(texture.Format) != 4 ||
                width == 0 ||
                height == 0 ||
                !_dumpedTextures.Add((texture.Address, width, height, texture.Format)))
            {
                return;
            }

            var rowBytes = checked((int)rowLength * 4);
            var visibleRowBytes = checked((int)width * 4);
            if (pixels.Length < checked(rowBytes * (int)height))
            {
                return;
            }

            var directory = Path.Combine(AppContext.BaseDirectory, "texture-dumps");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"tex-{texture.Address:X16}-{width}x{height}-fmt{texture.Format}-row{rowLength}.bmp");
            WriteRgbaBmp(path, pixels, rowBytes, visibleRowBytes, (int)width, (int)height);
        }

        private static void WriteRgbaBmp(
            string path,
            byte[] rgba,
            int sourceRowBytes,
            int visibleRowBytes,
            int width,
            int height)
        {
            const int fileHeaderSize = 14;
            const int infoHeaderSize = 40;
            const int bytesPerPixel = 4;
            var pixelBytes = checked(width * height * bytesPerPixel);
            var fileSize = fileHeaderSize + infoHeaderSize + pixelBytes;
            var output = new byte[fileSize];

            output[0] = (byte)'B';
            output[1] = (byte)'M';
            WriteUInt32(output, 2, (uint)fileSize);
            WriteUInt32(output, 10, fileHeaderSize + infoHeaderSize);
            WriteUInt32(output, 14, infoHeaderSize);
            WriteInt32(output, 18, width);
            WriteInt32(output, 22, -height);
            WriteUInt16(output, 26, 1);
            WriteUInt16(output, 28, 32);
            WriteUInt32(output, 34, (uint)pixelBytes);

            var destinationOffset = fileHeaderSize + infoHeaderSize;
            for (var y = 0; y < height; y++)
            {
                var sourceOffset = y * sourceRowBytes;
                for (var x = 0; x < visibleRowBytes; x += bytesPerPixel)
                {
                    var destination = destinationOffset + y * visibleRowBytes + x;
                    output[destination + 0] = rgba[sourceOffset + x + 2];
                    output[destination + 1] = rgba[sourceOffset + x + 1];
                    output[destination + 2] = rgba[sourceOffset + x + 0];
                    output[destination + 3] = rgba[sourceOffset + x + 3];
                }
            }

            File.WriteAllBytes(path, output);
        }

        private static void WriteUInt16(byte[] output, int offset, ushort value)
        {
            output[offset + 0] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] output, int offset, uint value)
        {
            output[offset + 0] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
            output[offset + 2] = (byte)(value >> 16);
            output[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt32(byte[] output, int offset, int value) =>
            WriteUInt32(output, offset, unchecked((uint)value));

        private Sampler CreateSampler(VulkanGuestSampler sampler)
        {
            if (_samplers.TryGetValue(sampler, out var cachedSampler))
            {
                return cachedSampler;
            }

            var minLod = DecodeSamplerMipFilter(sampler) == 0
                ? 0f
                : DecodeSamplerMinLod(sampler);
            var maxLod = DecodeSamplerMipFilter(sampler) == 0
                ? 0f
                : DecodeSamplerMaxLod(sampler);
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = ToVkFilter(DecodeSamplerMagFilter(sampler)),
                MinFilter = ToVkFilter(DecodeSamplerMinFilter(sampler)),
                MipmapMode = ToVkMipFilter(DecodeSamplerMipFilter(sampler)),
                AddressModeU = ToVkSamplerAddressMode(DecodeSamplerClampX(sampler)),
                AddressModeV = ToVkSamplerAddressMode(DecodeSamplerClampY(sampler)),
                AddressModeW = ToVkSamplerAddressMode(DecodeSamplerClampZ(sampler)),
                MipLodBias = DecodeSamplerLodBias(sampler),
                CompareEnable = DecodeSamplerDepthCompare(sampler) != 0,
                CompareOp = ToVkCompareOp(DecodeSamplerDepthCompare(sampler)),
                MinLod = minLod,
                MaxLod = Math.Max(minLod, maxLod),
                BorderColor = ToVkBorderColor(DecodeSamplerBorderColor(sampler)),
            };
            Sampler vkSampler;
            Check(
                _vk.CreateSampler(_device, &samplerInfo, null, out vkSampler),
                "vkCreateSampler(texture)");
            _samplers.Add(sampler, vkSampler);
            return vkSampler;
        }

        private static ComponentMapping ToVkComponentMapping(uint dstSelect)
        {
            return new ComponentMapping(
                ToVkComponentSwizzle(dstSelect & 0x7),
                ToVkComponentSwizzle((dstSelect >> 3) & 0x7),
                ToVkComponentSwizzle((dstSelect >> 6) & 0x7),
                ToVkComponentSwizzle((dstSelect >> 9) & 0x7));
        }

        private static ComponentSwizzle ToVkComponentSwizzle(uint selector) =>
            selector switch
            {
                0 => ComponentSwizzle.Zero,
                1 => ComponentSwizzle.One,
                4 => ComponentSwizzle.R,
                5 => ComponentSwizzle.G,
                6 => ComponentSwizzle.B,
                7 => ComponentSwizzle.A,
                _ => ComponentSwizzle.Identity,
            };

        private static byte[] ExpandRgb32Pixels(byte[] pixels)
        {
            var texelCount = pixels.Length / 12;
            var expanded = new byte[checked(texelCount * 16)];
            for (var texel = 0; texel < texelCount; texel++)
            {
                System.Buffer.BlockCopy(pixels, texel * 12, expanded, texel * 16, 12);
                expanded[texel * 16 + 14] = 0x80;
                expanded[texel * 16 + 15] = 0x3F;
            }

            return expanded;
        }

        // Constant-buffer-sized read-only bindings bind their parse-time
        // snapshot rather than live guest memory. Unity recycles its transient
        // constant ring long before a translated draw reaches the GPU, so a
        // live alias hands later draws' constants to earlier draws (fullscreen
        // blits then rasterize with the wrong ortho matrix), and refreshing
        // the shared alias shadow stalls the whole guest queue on every reuse.
        private const int SnapshotGlobalBufferLimit = 64 * 1024;

        private GlobalBufferResource CreateGlobalBufferResource(
            VulkanGuestMemoryBuffer guestBuffer)
        {
            // Read-only resources are immutable snapshots captured for this
            // draw. Binding them through the shared guest-VA allocation both
            // loses snapshot consistency and forces a full GPU drain when the
            // guest updates per-draw constants. Writable resources still use
            // the shared allocation so shader stores reach the guest address.
            if (guestBuffer.BaseAddress == 0 ||
                (UseTransientReadOnlyGuestBuffers && !guestBuffer.Writable))
            {
                return CreateTransientGlobalBufferResource(guestBuffer);
            }

            if (!guestBuffer.Writable &&
                guestBuffer.Length <= SnapshotGlobalBufferLimit &&
                !IntersectsGpuWrittenGuestRange(
                    guestBuffer.BaseAddress,
                    checked(guestBuffer.BaseAddress + (ulong)guestBuffer.Length)))
            {
                return CreateSnapshotGlobalBufferResource(guestBuffer);
            }

            var size = (ulong)Math.Max(guestBuffer.Length, sizeof(uint));
            var endAddress = checked(guestBuffer.BaseAddress + size);
            var allocation = _guestBufferAllocations.Single(candidate =>
                candidate.BaseAddress <= guestBuffer.BaseAddress &&
                candidate.BaseAddress + candidate.Size >= endAddress);
            var guestOffset = guestBuffer.BaseAddress - allocation.BaseAddress;
            var descriptorOffset = guestOffset &
                ~(GuestStorageBufferOffsetAlignment - 1);
            var byteBias = guestOffset - descriptorOffset;
            if (descriptorOffset % _minStorageBufferOffsetAlignment != 0)
            {
                throw new InvalidOperationException(
                    $"guest buffer alias offset 0x{descriptorOffset:X} is not aligned to Vulkan's " +
                    $"minStorageBufferOffsetAlignment={_minStorageBufferOffsetAlignment}");
            }

            var expectedBias = guestBuffer.BaseAddress &
                (GuestStorageBufferOffsetAlignment - 1);
            if (byteBias != expectedBias)
            {
                throw new InvalidOperationException(
                    $"guest buffer allocation base 0x{allocation.BaseAddress:X16} " +
                    $"does not satisfy alias alignment " +
                    $"{GuestStorageBufferOffsetAlignment}");
            }

            var source = guestBuffer.Data.AsSpan(0, guestBuffer.Length);
            var shadow = allocation.Shadow.AsSpan(checked((int)guestOffset), guestBuffer.Length);
            if (!source.SequenceEqual(shadow))
            {
                // HOST_COHERENT does not permit racing a mapped CPU write with
                // an in-flight shader access. Retire prior users, publish their
                // dirty ranges to guest memory, then upload the current guest
                // bytes (which may be newer than the parser's captured array).
                WaitForAllGuestSubmissionsForCpuVisibility();
                WriteBackAllDirtyGuestBuffers();
                var live = new byte[guestBuffer.Length];
                if (_guestMemory?.TryRead(guestBuffer.BaseAddress, live) == true)
                {
                    source = live;
                }

                source.CopyTo(new Span<byte>(
                    (void*)(allocation.Mapped + checked((nint)guestOffset)),
                    source.Length));
                source.CopyTo(shadow);
            }

            if (ShouldTraceVulkanResources() &&
                _tracedGlobalBuffers.Add((guestBuffer.BaseAddress, guestBuffer.Length)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.global_buffer base=0x{guestBuffer.BaseAddress:X16} " +
                    $"bytes={guestBuffer.Length}");
            }
            if (_setDebugUtilsObjectName is not null)
            {
                SetDebugName(
                    ObjectType.Buffer,
                    allocation.Buffer.Handle,
                    $"SharpEmu global 0x{guestBuffer.BaseAddress:X16} {guestBuffer.Length}b");
            }

            if (guestBuffer.Pooled)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(guestBuffer.Data);
            }

            return new GlobalBufferResource
            {
                BaseAddress = guestBuffer.BaseAddress,
                Writable = guestBuffer.Writable,
                WriteBackToGuest = guestBuffer.WriteBackToGuest,
                Buffer = allocation.Buffer,
                Memory = allocation.Memory,
                Mapped = allocation.Mapped + checked((nint)guestOffset),
                Offset = descriptorOffset,
                Size = checked((size + byteBias + 3) & ~3UL),
                GuestOffset = guestOffset,
                GuestSize = size,
                Allocation = allocation,
            };
        }

        private GlobalBufferResource CreateSnapshotGlobalBufferResource(
            VulkanGuestMemoryBuffer guestBuffer)
        {
            // The translated shader adds the address's sub-alignment bias to
            // every access (packed at parse time), so the snapshot must sit
            // at that same bias inside its host buffer.
            var byteBias = checked((int)(
                guestBuffer.BaseAddress &
                (GuestStorageBufferOffsetAlignment - 1)));
            var length = checked(byteBias + Math.Max(guestBuffer.Length, sizeof(uint)));
            var staging = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
            staging.AsSpan(0, byteBias).Clear();
            guestBuffer.Data.AsSpan(0, guestBuffer.Length)
                .CopyTo(staging.AsSpan(byteBias));
            if (staging.AsSpan(byteBias, guestBuffer.Length)
                    .IndexOfAnyExcept((byte)0) < 0)
            {
                // An all-zero parse-time snapshot means the guest had not yet
                // written this constant buffer when the command list was
                // parsed (queues are parsed ahead of the CPU's late writes).
                // By execution time — now — the live contents are the values
                // a real GPU would fetch. A recycled transient ring entry is
                // never all-zero at parse, so this cannot reintroduce the
                // stale-ring corruption.
                _guestMemory?.TryRead(
                    guestBuffer.BaseAddress,
                    staging.AsSpan(byteBias, guestBuffer.Length));
            }

            var buffer = CreateHostBuffer(
                staging.AsSpan(0, length),
                BufferUsageFlags.StorageBufferBit,
                out var memory);
            System.Buffers.ArrayPool<byte>.Shared.Return(staging);
            var allocation = _hostBufferAllocations[buffer.Handle];
            if (guestBuffer.Pooled)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(guestBuffer.Data);
            }

            return new GlobalBufferResource
            {
                // Keep the guest address for diagnostics; Writable=false and
                // Allocation=null keep this resource out of the writeback and
                // dirty-tracking paths.
                BaseAddress = guestBuffer.BaseAddress,
                Writable = false,
                WriteBackToGuest = false,
                Buffer = buffer,
                Memory = memory,
                Mapped = allocation.Mapped,
                Offset = 0,
                Size = checked(((ulong)length + 3) & ~3UL),
                GuestOffset = 0,
                GuestSize = (ulong)length,
            };
        }

        private GlobalBufferResource CreateTransientGlobalBufferResource(
            VulkanGuestMemoryBuffer guestBuffer)
        {
            var buffer = CreateHostBuffer(
                guestBuffer.Data.AsSpan(0, guestBuffer.Length),
                BufferUsageFlags.StorageBufferBit,
                out var memory);
            var allocation = _hostBufferAllocations[buffer.Handle];
            if (guestBuffer.Pooled)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(guestBuffer.Data);
            }

            return new GlobalBufferResource
            {
                BaseAddress = guestBuffer.BaseAddress,
                Writable = false,
                WriteBackToGuest = false,
                Buffer = buffer,
                Memory = memory,
                Mapped = allocation.Mapped,
                Offset = 0,
                Size = (ulong)Math.Max(guestBuffer.Length, sizeof(uint)),
                GuestOffset = 0,
                GuestSize = (ulong)Math.Max(guestBuffer.Length, sizeof(uint)),
            };
        }

        private void PrepareGuestBufferAllocations(
            IReadOnlyList<VulkanGuestMemoryBuffer> buffers)
        {
            if (buffers.Count == 0)
            {
                return;
            }

            var ranges = new List<(ulong Start, ulong End)>(buffers.Count);
            foreach (var buffer in buffers)
            {
                if (buffer.BaseAddress == 0 ||
                    (UseTransientReadOnlyGuestBuffers && !buffer.Writable) ||
                    (!buffer.Writable &&
                     buffer.Length <= SnapshotGlobalBufferLimit &&
                     !IntersectsGpuWrittenGuestRange(
                         buffer.BaseAddress,
                         checked(buffer.BaseAddress + (ulong)buffer.Length))))
                {
                    // Snapshot-bound buffers never alias guest memory.
                    continue;
                }

                var size = (ulong)Math.Max(buffer.Length, sizeof(uint));
                var alignedStart = buffer.BaseAddress &
                    ~(GuestStorageBufferOffsetAlignment - 1);
                var paddedEnd = checked(buffer.BaseAddress + size + 3) & ~3UL;
                ranges.Add((alignedStart, paddedEnd));
            }

            if (ranges.Count == 0)
            {
                return;
            }

            ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            var merged = new List<(ulong Start, ulong End)>(ranges.Count);
            foreach (var range in ranges)
            {
                if (merged.Count == 0 || range.Start > merged[^1].End)
                {
                    merged.Add(range);
                    continue;
                }

                var previous = merged[^1];
                merged[^1] = (previous.Start, Math.Max(previous.End, range.End));
            }

            foreach (var range in merged)
            {
                EnsureGuestBufferAllocation(range.Start, range.End);
            }
        }

        private void EnsureGuestBufferAllocation(ulong requestedStart, ulong requestedEnd)
        {
            var start = requestedStart;
            var end = requestedEnd;
            List<GuestBufferAllocation> overlaps;
            do
            {
                overlaps = _guestBufferAllocations
                    .Where(allocation =>
                        allocation.BaseAddress < end &&
                        start < allocation.BaseAddress + allocation.Size)
                    .ToList();
                var expandedStart = overlaps.Aggregate(
                    start,
                    static (value, allocation) => Math.Min(value, allocation.BaseAddress));
                var expandedEnd = overlaps.Aggregate(
                    end,
                    static (value, allocation) =>
                        Math.Max(value, allocation.BaseAddress + allocation.Size));
                if (expandedStart == start && expandedEnd == end)
                {
                    break;
                }

                start = expandedStart;
                end = expandedEnd;
            }
            while (true);

            if (overlaps.Count == 1 &&
                overlaps[0].BaseAddress <= requestedStart &&
                overlaps[0].BaseAddress + overlaps[0].Size >= requestedEnd)
            {
                return;
            }

            if (overlaps.Count > 0)
            {
                // Growing/merging an aliased allocation is rare. Synchronize
                // only this structural transition so no in-flight descriptor
                // can observe storage being replaced underneath it.
                WaitForAllGuestSubmissionsForCpuVisibility();
                WriteBackAllDirtyGuestBuffers();
            }

            var replacement = CreateGuestBufferAllocation(start, end);
            foreach (var overlap in overlaps)
            {
                _guestBufferAllocations.Remove(overlap);
                DestroyGuestBufferAllocation(overlap);
            }

            _guestBufferAllocations.Add(replacement);
            _guestBufferAllocations.Sort(static (left, right) =>
                left.BaseAddress.CompareTo(right.BaseAddress));
            TraceVulkanShader(
                $"vk.guest_buffer_allocation base=0x{start:X16} bytes={replacement.Size} " +
                $"merged={overlaps.Count}");
        }

        private GuestBufferAllocation CreateGuestBufferAllocation(ulong start, ulong end)
        {
            var size = checked(end - start);
            if (size == 0 || size > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"guest buffer allocation is outside the supported host span: " +
                    $"base=0x{start:X16} bytes={size}");
            }

            var buffer = CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out var memory);
            void* mapped;
            Check(_vk.MapMemory(_device, memory, 0, size, 0, &mapped), "vkMapMemory(guest buffer)");
            var shadow = new byte[checked((int)size)];
            _ = _guestMemory?.TryRead(start, shadow);
            shadow.CopyTo(new Span<byte>(mapped, shadow.Length));
            SetDebugName(
                ObjectType.Buffer,
                buffer.Handle,
                $"SharpEmu guest VA 0x{start:X16}-0x{end:X16}");
            return new GuestBufferAllocation
            {
                BaseAddress = start,
                Size = size,
                Buffer = buffer,
                Memory = memory,
                Mapped = (nint)mapped,
                Shadow = shadow,
            };
        }

        private void DestroyGuestBufferAllocation(GuestBufferAllocation allocation)
        {
            if (allocation.Mapped != 0)
            {
                _vk.UnmapMemory(_device, allocation.Memory);
            }

            _vk.DestroyBuffer(_device, allocation.Buffer, null);
            _vk.FreeMemory(_device, allocation.Memory, null);
        }

        private long _deferredVertexResolveCount;
        private long _deferredVertexFailCount;

        // Vertex buffers whose V# read garbage at parse carry the guest
        // address the descriptor was loaded from (per-frame Slate/UMG vertex
        // rings are written after the command list is parsed). By execution
        // time the table holds the real V#: re-read it and snapshot the live
        // vertex data in place of the empty parse-time placeholder.
        private VulkanGuestVertexBuffer RefreshDeferredVertexBuffer(
            VulkanGuestVertexBuffer guestBuffer)
        {
            if (guestBuffer.DeferredDescriptorAddress == 0 || _guestMemory is null)
            {
                return guestBuffer;
            }

            var descriptorBytes = new byte[16];
            if (!_guestMemory.TryRead(
                    guestBuffer.DeferredDescriptorAddress,
                    descriptorBytes))
            {
                if (Interlocked.Increment(ref _deferredVertexFailCount) <= 32)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.deferred_vertex_unreadable " +
                        $"table=0x{guestBuffer.DeferredDescriptorAddress:X16}");
                }

                return guestBuffer;
            }

            var descriptorWords = new uint[4];
            for (var word = 0; word < 4; word++)
            {
                descriptorWords[word] = System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt32LittleEndian(descriptorBytes.AsSpan(word * 4, 4));
            }

            if (!Gen5ShaderScalarEvaluator.TryDecodeDeferredVertexDescriptor(
                    descriptorWords,
                    out var baseAddress,
                    out var stride,
                    out var sizeBytes,
                    out var dataFormat,
                    out var numberFormat))
            {
                if (Interlocked.Increment(ref _deferredVertexFailCount) <= 32)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.deferred_vertex_unresolved " +
                        $"table=0x{guestBuffer.DeferredDescriptorAddress:X16} " +
                        $"words=[{string.Join(',', descriptorWords.Select(word => $"{word:X8}"))}]");
                }

                return guestBuffer;
            }

            var elementBytes =
                (ulong)Math.Max(guestBuffer.ComponentCount, 1u) * sizeof(uint);
            var recordSpan = Math.Max(stride, guestBuffer.OffsetBytes + elementBytes);
            var requiredBytes = guestBuffer.RequiredRecords > 0
                ? ((ulong)(guestBuffer.RequiredRecords - 1) * stride) + recordSpan
                : sizeBytes;
            var readBytes = (int)Math.Min(
                Math.Min(requiredBytes, sizeBytes),
                16UL * 1024 * 1024);
            var vertexData = new byte[Math.Max(readBytes, sizeof(uint))];
            if (readBytes > 0 &&
                !_guestMemory.TryRead(baseAddress, vertexData.AsSpan(0, readBytes)))
            {
                if (Interlocked.Increment(ref _deferredVertexFailCount) <= 32)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.deferred_vertex_data_unreadable " +
                        $"base=0x{baseAddress:X16} bytes={readBytes}");
                }

                return guestBuffer;
            }

            if (Interlocked.Increment(ref _deferredVertexResolveCount) <= 64)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.deferred_vertex_resolved " +
                    $"table=0x{guestBuffer.DeferredDescriptorAddress:X16} " +
                    $"base=0x{baseAddress:X16} stride={stride} bytes={readBytes} " +
                    $"fmt={dataFormat}/n{numberFormat}");
            }

            if (guestBuffer.Pooled)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(guestBuffer.Data);
            }

            return guestBuffer with
            {
                BaseAddress = baseAddress,
                Stride = stride,
                DataFormat = dataFormat,
                NumberFormat = numberFormat,
                Data = vertexData,
                Length = vertexData.Length,
                Pooled = false,
                DeferredDescriptorAddress = 0,
            };
        }

        private VertexBufferResource CreateVertexBufferResource(
            VulkanGuestVertexBuffer guestBuffer)
        {
            guestBuffer = RefreshDeferredVertexBuffer(guestBuffer);
            var buffer = CreateHostBuffer(
                guestBuffer.Data.AsSpan(0, guestBuffer.Length),
                BufferUsageFlags.VertexBufferBit,
                out var memory);
            var size = (ulong)Math.Max(guestBuffer.Length, sizeof(uint));
            if (_setDebugUtilsObjectName is not null)
            {
                SetDebugName(
                    ObjectType.Buffer,
                    buffer.Handle,
                    $"SharpEmu vertex loc{guestBuffer.Location} " +
                    $"0x{guestBuffer.BaseAddress:X16} {guestBuffer.Length}b");
            }
            if (_tracedVertexBufferCount++ < 64)
            {
                TraceVulkanShader(
                    $"vk.vertex_buffer loc={guestBuffer.Location} " +
                    $"base=0x{guestBuffer.BaseAddress:X16} stride={guestBuffer.Stride} " +
                    $"offset={guestBuffer.OffsetBytes} comps={guestBuffer.ComponentCount} " +
                    $"fmt={guestBuffer.DataFormat}/num={guestBuffer.NumberFormat} " +
                    $"bytes={guestBuffer.Length}");
            }

            if (guestBuffer.Pooled)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(guestBuffer.Data);
            }

            return new VertexBufferResource
            {
                Buffer = buffer,
                Memory = memory,
                Size = size,
                Location = guestBuffer.Location,
                ComponentCount = guestBuffer.ComponentCount,
                DataFormat = guestBuffer.DataFormat,
                NumberFormat = guestBuffer.NumberFormat,
                Stride = guestBuffer.Stride,
                OffsetBytes = guestBuffer.OffsetBytes,
            };
        }

        private VkBuffer CreateHostBuffer(
            ReadOnlySpan<byte> data,
            BufferUsageFlags usage,
            out DeviceMemory memory)
        {
            var size = (ulong)Math.Max(data.Length, sizeof(uint));
            var capacity = BitOperations.RoundUpToPowerOf2(size);
            var key = new HostBufferPoolKey(usage, capacity);
            if (!_hostBufferPool.TryGetValue(key, out var available))
            {
                available = new Stack<HostBufferAllocation>();
                _hostBufferPool.Add(key, available);
            }

            HostBufferAllocation allocation;
            if (available.TryPop(out var pooled))
            {
                allocation = pooled;
            }
            else
            {
                var buffer = CreateBuffer(
                    capacity,
                    usage,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out var allocatedMemory);
                // Persistently mapped: map/unmap per draw was a measurable
                // share of the per-draw fixed cost, and HOST_COHERENT memory
                // may legally stay mapped for its lifetime.
                void* persistentMapping;
                Check(
                    _vk.MapMemory(_device, allocatedMemory, 0, capacity, 0, &persistentMapping),
                    "vkMapMemory(host persistent)");
                allocation = new HostBufferAllocation(
                    buffer,
                    allocatedMemory,
                    key,
                    (nint)persistentMapping);
                _hostBufferAllocations.Add(buffer.Handle, allocation);
            }

            memory = allocation.Memory;
            fixed (byte* source = data)
            {
                System.Buffer.MemoryCopy(
                    source,
                    (void*)allocation.Mapped,
                    checked((long)allocation.Key.Capacity),
                    data.Length);
            }

            return allocation.Buffer;
        }

        private void RecycleHostBuffer(VkBuffer buffer, DeviceMemory memory)
        {
            if (buffer.Handle == 0)
            {
                return;
            }

            if (_hostBufferAllocations.TryGetValue(buffer.Handle, out var allocation) &&
                allocation.Memory.Handle == memory.Handle)
            {
                _hostBufferPool[allocation.Key].Push(allocation);
                return;
            }

            _vk.DestroyBuffer(_device, buffer, null);
            if (memory.Handle != 0)
            {
                _vk.FreeMemory(_device, memory, null);
            }
        }

        private static PrimitiveTopology GetPrimitiveTopology(uint primitiveType) =>
            primitiveType switch
            {
                1 => PrimitiveTopology.PointList,
                2 => PrimitiveTopology.LineList,
                3 => PrimitiveTopology.LineStrip,
                5 => PrimitiveTopology.TriangleFan,
                6 => PrimitiveTopology.TriangleStrip,
                GuestPrimitiveRectList => PrimitiveTopology.TriangleStrip,
                _ => PrimitiveTopology.TriangleList,
            };

        // Strip and fan topologies are the ones for which a restart index
        // splits primitives; list topologies never restart.
        private static bool RequiresPrimitiveRestart(PrimitiveTopology topology) =>
            topology is PrimitiveTopology.LineStrip
                or PrimitiveTopology.TriangleStrip
                or PrimitiveTopology.TriangleFan;

        private static Format ToVkVertexFormat(
            uint dataFormat,
            uint numberFormat,
            uint componentCount) =>
            (dataFormat, numberFormat) switch
            {
                (1, 0) => Format.R8Unorm,
                (1, 1) => Format.R8SNorm,
                (1, 4) => Format.R8Uint,
                (1, 5) => Format.R8Sint,
                (1, 9) => Format.R8Srgb,
                (2, 0) => Format.R16Unorm,
                (2, 1) => Format.R16SNorm,
                (2, 4) => Format.R16Uint,
                (2, 5) => Format.R16Sint,
                (2, 7) => Format.R16Sfloat,
                (3, 0) => Format.R8G8Unorm,
                (3, 1) => Format.R8G8SNorm,
                (3, 4) => Format.R8G8Uint,
                (3, 5) => Format.R8G8Sint,
                (3, 9) => Format.R8G8Srgb,
                (4, 4) => Format.R32Uint,
                (4, 5) => Format.R32Sint,
                (4, 7) => Format.R32Sfloat,
                (5, 0) => Format.R16G16Unorm,
                (5, 1) => Format.R16G16SNorm,
                (5, 2) => Format.R16G16Uscaled,
                (5, 3) => Format.R16G16Sscaled,
                (5, 4) => Format.R16G16Uint,
                (5, 5) => Format.R16G16Sint,
                (5, 7) => Format.R16G16Sfloat,
                (6, 7) => Format.B10G11R11UfloatPack32,
                (7, 7) => Format.B10G11R11UfloatPack32,
                (8, 0) => Format.A2B10G10R10UnormPack32,
                (8, 1) => Format.A2B10G10R10SNormPack32,
                (8, 2) => Format.A2B10G10R10UscaledPack32,
                (8, 3) => Format.A2B10G10R10SscaledPack32,
                (8, 4) => Format.A2B10G10R10UintPack32,
                (8, 5) => Format.A2B10G10R10SintPack32,
                // RDNA COLOR_2_10_10_10 stores component 0 (R) in bits
                // 0..9 and A in 30..31. Vulkan names that exact bit layout
                // A2B10G10R10_PACK32 (the packed name is MSB-to-LSB).
                (9, 0) => Format.A2B10G10R10UnormPack32,
                (9, 1) => Format.A2B10G10R10SNormPack32,
                (9, 2) => Format.A2B10G10R10UscaledPack32,
                (9, 3) => Format.A2B10G10R10SscaledPack32,
                (9, 4) => Format.A2B10G10R10UintPack32,
                (9, 5) => Format.A2B10G10R10SintPack32,
                (10, 0) => Format.R8G8B8A8Unorm,
                (10, 1) => Format.R8G8B8A8SNorm,
                (10, 2) => Format.R8G8B8A8Uscaled,
                (10, 3) => Format.R8G8B8A8Sscaled,
                (10, 4) => Format.R8G8B8A8Uint,
                (10, 5) => Format.R8G8B8A8Sint,
                (10, 9) => Format.R8G8B8A8Srgb,
                (11, 4) => Format.R32G32Uint,
                (11, 5) => Format.R32G32Sint,
                (11, 7) => Format.R32G32Sfloat,
                (12, 0) => Format.R16G16B16A16Unorm,
                (12, 1) => Format.R16G16B16A16SNorm,
                (12, 2) => Format.R16G16B16A16Uscaled,
                (12, 3) => Format.R16G16B16A16Sscaled,
                (12, 4) => Format.R16G16B16A16Uint,
                (12, 5) => Format.R16G16B16A16Sint,
                (12, 6) => Format.R16G16B16A16SNorm,
                (12, 7) => Format.R16G16B16A16Sfloat,
                (13, 4) => Format.R32G32B32Uint,
                (13, 5) => Format.R32G32B32Sint,
                (13, 7) => Format.R32G32B32Sfloat,
                (14, 4) => Format.R32G32B32A32Uint,
                (14, 5) => Format.R32G32B32A32Sint,
                (14, 7) => Format.R32G32B32A32Sfloat,
                (16, 0) => Format.B5G6R5UnormPack16,
                (17, 0) => Format.R5G5B5A1UnormPack16,
                (19, 0) => Format.R4G4B4A4UnormPack16,
                (34, 7) => Format.E5B9G9R9UfloatPack32,
                _ => ToVkFloatVertexFormat(componentCount),
            };

        private static Format ToVkFloatVertexFormat(uint componentCount) =>
            componentCount switch
            {
                1 => Format.R32Sfloat,
                2 => Format.R32G32Sfloat,
                3 => Format.R32G32B32Sfloat,
                4 => Format.R32G32B32A32Sfloat,
                _ => Format.R32Sfloat,
            };

        private static ulong GetVertexBindingOffset(VertexBufferResource vertexBuffer)
        {
            if (vertexBuffer.OffsetBytes < vertexBuffer.Size)
            {
                return vertexBuffer.OffsetBytes;
            }

            TraceVulkanShader(
                $"vk.vertex_offset_oob loc={vertexBuffer.Location} " +
                $"offset={vertexBuffer.OffsetBytes} size={vertexBuffer.Size}");
            return 0;
        }

        private static uint GetDrawVertexCount(
            uint primitiveType,
            uint vertexCount,
            VulkanGuestIndexBuffer? indexBuffer)
        {
            if (primitiveType == GuestPrimitiveRectList && indexBuffer is null)
            {
                return 4;
            }

            return vertexCount;
        }

        private static BlendFactor ToVkBlendFactor(uint factor) =>
            factor switch
            {
                0 => BlendFactor.Zero,
                1 => BlendFactor.One,
                2 => BlendFactor.SrcColor,
                3 => BlendFactor.OneMinusSrcColor,
                4 => BlendFactor.SrcAlpha,
                5 => BlendFactor.OneMinusSrcAlpha,
                6 => BlendFactor.DstAlpha,
                7 => BlendFactor.OneMinusDstAlpha,
                8 => BlendFactor.DstColor,
                9 => BlendFactor.OneMinusDstColor,
                10 => BlendFactor.SrcAlphaSaturate,
                13 => BlendFactor.ConstantColor,
                14 => BlendFactor.OneMinusConstantColor,
                15 => BlendFactor.Src1Color,
                16 => BlendFactor.OneMinusSrc1Color,
                17 => BlendFactor.Src1Alpha,
                18 => BlendFactor.OneMinusSrc1Alpha,
                19 => BlendFactor.ConstantAlpha,
                20 => BlendFactor.OneMinusConstantAlpha,
                _ => BlendFactor.One,
            };

        private static BlendOp ToVkBlendOp(uint function) =>
            function switch
            {
                0 => BlendOp.Add,
                1 => BlendOp.Subtract,
                2 => BlendOp.Min,
                3 => BlendOp.Max,
                4 => BlendOp.ReverseSubtract,
                _ => BlendOp.Add,
            };

        private static uint DecodeSamplerClampX(VulkanGuestSampler sampler) =>
            sampler.Word0 & 0x7u;

        private static uint DecodeSamplerClampY(VulkanGuestSampler sampler) =>
            (sampler.Word0 >> 3) & 0x7u;

        private static uint DecodeSamplerClampZ(VulkanGuestSampler sampler) =>
            (sampler.Word0 >> 6) & 0x7u;

        private static uint DecodeSamplerDepthCompare(VulkanGuestSampler sampler) =>
            (sampler.Word0 >> 12) & 0x7u;

        private static float DecodeSamplerMinLod(VulkanGuestSampler sampler) =>
            (sampler.Word1 & 0xFFFu) / 256.0f;

        private static float DecodeSamplerMaxLod(VulkanGuestSampler sampler) =>
            ((sampler.Word1 >> 12) & 0xFFFu) / 256.0f;

        private static float DecodeSamplerLodBias(VulkanGuestSampler sampler)
        {
            var raw = sampler.Word2 & 0x3FFFu;
            var signed = (short)((raw ^ 0x2000u) - 0x2000u);
            return signed / 256.0f;
        }

        private static uint DecodeSamplerMagFilter(VulkanGuestSampler sampler) =>
            (sampler.Word2 >> 20) & 0x3u;

        private static uint DecodeSamplerMinFilter(VulkanGuestSampler sampler) =>
            (sampler.Word2 >> 22) & 0x3u;

        private static uint DecodeSamplerMipFilter(VulkanGuestSampler sampler) =>
            (sampler.Word2 >> 26) & 0x3u;

        private static uint DecodeSamplerBorderColor(VulkanGuestSampler sampler) =>
            (sampler.Word3 >> 30) & 0x3u;

        private static SamplerAddressMode ToVkSamplerAddressMode(uint mode) =>
            mode switch
            {
                0 => SamplerAddressMode.Repeat,
                1 => SamplerAddressMode.MirroredRepeat,
                2 => SamplerAddressMode.ClampToEdge,
                3 or 5 or 7 => SamplerAddressMode.MirrorClampToEdge,
                4 or 6 => SamplerAddressMode.ClampToBorder,
                _ => SamplerAddressMode.ClampToEdge,
            };

        private static Filter ToVkFilter(uint filter) =>
            filter is 1 or 3 ? Filter.Linear : Filter.Nearest;

        private static SamplerMipmapMode ToVkMipFilter(uint filter) =>
            filter == 2 ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest;

        private static CompareOp ToVkCompareOp(uint compare) =>
            compare switch
            {
                1 => CompareOp.Less,
                2 => CompareOp.Equal,
                3 => CompareOp.LessOrEqual,
                4 => CompareOp.Greater,
                5 => CompareOp.NotEqual,
                6 => CompareOp.GreaterOrEqual,
                7 => CompareOp.Always,
                _ => CompareOp.Never,
            };

        private static BorderColor ToVkBorderColor(uint color) =>
            color switch
            {
                1 => BorderColor.FloatTransparentBlack,
                2 => BorderColor.FloatOpaqueWhite,
                _ => BorderColor.FloatOpaqueBlack,
            };

        private static ColorComponentFlags ToVkColorWriteMask(uint mask)
        {
            var flags = default(ColorComponentFlags);
            if ((mask & 1u) != 0)
            {
                flags |= ColorComponentFlags.RBit;
            }

            if ((mask & 2u) != 0)
            {
                flags |= ColorComponentFlags.GBit;
            }

            if ((mask & 4u) != 0)
            {
                flags |= ColorComponentFlags.BBit;
            }

            if ((mask & 8u) != 0)
            {
                flags |= ColorComponentFlags.ABit;
            }

            return flags;
        }

        private static VulkanGuestRect ClampScissor(VulkanGuestRect? scissor, Extent2D extent)
        {
            if (scissor is not { } rect)
            {
                return new VulkanGuestRect(0, 0, extent.Width, extent.Height);
            }

            var left = Math.Clamp(rect.X, 0, checked((int)extent.Width));
            var top = Math.Clamp(rect.Y, 0, checked((int)extent.Height));
            var right = Math.Clamp(
                rect.X + checked((int)rect.Width),
                left,
                checked((int)extent.Width));
            var bottom = Math.Clamp(
                rect.Y + checked((int)rect.Height),
                top,
                checked((int)extent.Height));
            return new VulkanGuestRect(
                left,
                top,
                checked((uint)(right - left)),
                checked((uint)(bottom - top)));
        }

        private static readonly float ViewportDebugEpsilon = float.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_VIEWPORT_EPSILON"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var viewportEpsilon)
            ? viewportEpsilon
            : 0f;

        private static Viewport ClampViewport(VulkanGuestViewport? viewport, Extent2D extent)
        {
            if (viewport is not { } rect)
            {
                return new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
            }

            // Do NOT trim the rectangle to the render target: Vulkan allows
            // viewports that extend beyond the framebuffer (rendering is
            // confined by the scissor), and trimming changes the guest's
            // scale and offset. That skews texel addressing on 1:1 draws -
            // source rows get skipped or duplicated - which shredded the
            // game's pre-composed tile surfaces. Only guard what the spec
            // requires: a positive width and hardware viewport bounds.
            const float bound = 32767f;
            var x = Math.Clamp(rect.X, -bound, bound);
            var y = Math.Clamp(rect.Y, -bound, bound);
            var width = Math.Clamp(rect.Width, 1e-3f, bound);
            var height = Math.Clamp(rect.Height, -bound, bound);
            if (height == 0f)
            {
                height = extent.Height;
            }

            var minDepth = Math.Clamp(rect.MinDepth, 0f, 1f);
            var maxDepth = Math.Clamp(rect.MaxDepth, minDepth, 1f);
            return new Viewport(x, y, width, height, minDepth, maxDepth);
        }

        private static byte[] CreateFallbackTexturePixels(uint format, uint width, uint height, ulong expectedSize)
        {
            // Unresolved textures degrade to ZERO: RDNA2 samples of a null T#
            // return 0, and shaders ship dead feature blocks that sample with
            // clobbered descriptor registers relying on exactly that (their
            // results are zero-weighted). A white fallback turns those benign
            // samples into screen-filling contributions.
            return new byte[checked((int)expectedSize)];
        }

        private static ulong GetTextureBytesPerPixel(uint format) =>
            format switch
            {
                1 => 1UL,
                2 => 2UL,
                3 => 2UL,
                4 => 4UL,
                5 => 4UL,
                6 => 4UL,
                7 => 4UL,
                9 => 4UL,
                10 => 4UL,
                11 => 8UL,
                12 => 8UL,
                13 => 12UL,
                14 => 16UL,
                _ => 4UL,
            };

        private static ulong GetTextureByteCount(uint format, uint width, uint height)
        {
            var blockBytes = format switch
            {
                // BC1 (169/170) and BC4 (175/176) are 8 bytes per 4x4 block;
                // BC2/BC3/BC5/BC6H/BC7 are 16 bytes per block.
                169 or 170 or 175 or 176 => 8UL,
                171 or 172 or 173 or 174 or
                177 or 178 or 179 or 180 or 181 or 182 => 16UL,
                _ => 0UL,
            };
            return blockBytes == 0
                ? checked((ulong)width * height * GetTextureBytesPerPixel(format))
                : checked(((ulong)width + 3) / 4 * (((ulong)height + 3) / 4) * blockBytes);
        }

        private static Format GetTextureFormat(uint format, uint numberType) =>
            (format, numberType) switch
            {
                (9, _) => Format.A2B10G10R10UnormPack32,
                (1, 0) => Format.R8Unorm,
                (1, 1) => Format.R8SNorm,
                (1, 2) => Format.R8Uscaled,
                (1, 3) => Format.R8Sscaled,
                (1, 4) => Format.R8Uint,
                (1, 5) => Format.R8Sint,
                (2, 7) => Format.R16Sfloat,
                (2, 0) => Format.R16Unorm,
                (2, 1) => Format.R16SNorm,
                (2, 2) => Format.R16Uscaled,
                (2, 3) => Format.R16Sscaled,
                (2, 4) => Format.R16Uint,
                (2, 5) => Format.R16Sint,
                (3, 0) => Format.R8G8Unorm,
                (3, 1) => Format.R8G8SNorm,
                (3, 2) => Format.R8G8Uscaled,
                (3, 3) => Format.R8G8Sscaled,
                (3, 4) => Format.R8G8Uint,
                (3, 5) => Format.R8G8Sint,
                (4, 4) => Format.R32Uint,
                (4, 5) => Format.R32Sint,
                (4, 7) => Format.R32Sfloat,
                (5, 0) => Format.R16G16Unorm,
                (5, 4) => Format.R16G16Uint,
                (5, 5) => Format.R16G16Sint,
                (5, 7) => Format.R16G16Sfloat,
                (6, 7) => Format.B10G11R11UfloatPack32,
                (7, 7) => Format.B10G11R11UfloatPack32,
                (8, 0) => Format.A2B10G10R10UnormPack32,
                (8, 1) => Format.A2B10G10R10SNormPack32,
                (8, 2) => Format.A2B10G10R10UscaledPack32,
                (8, 3) => Format.A2B10G10R10SscaledPack32,
                (8, 4) => Format.A2B10G10R10UintPack32,
                (8, 5) => Format.A2B10G10R10SintPack32,
                (10, 0) => Format.R8G8B8A8Unorm,
                (10, 4) => Format.R8G8B8A8Uint,
                (10, 5) => Format.R8G8B8A8Sint,
                (10, 9) => Format.R8G8B8A8Srgb,
                (1, 9) => Format.R8Srgb,
                (3, 9) => Format.R8G8Srgb,
                (11, 4) => Format.R32G32Uint,
                (11, 5) => Format.R32G32Sint,
                (11, 7) => Format.R32G32Sfloat,
                (12, 0) => Format.R16G16B16A16Unorm,
                (12, 4) => Format.R16G16B16A16Uint,
                (12, 5) => Format.R16G16B16A16Sint,
                (12, 7) => Format.R16G16B16A16Sfloat,
                (13, 4) => Format.R32G32B32A32Uint,
                (13, 5) => Format.R32G32B32A32Sint,
                (13, _) => Format.R32G32B32A32Sfloat,
                (14, 4) => Format.R32G32B32A32Uint,
                (14, 5) => Format.R32G32B32A32Sint,
                (14, 7) => Format.R32G32B32A32Sfloat,
                (16, 0) => Format.B5G6R5UnormPack16,
                (17, 0) => Format.R5G5B5A1UnormPack16,
                (19, 0) => Format.R4G4B4A4UnormPack16,
                (34, 7) => Format.E5B9G9R9UfloatPack32,
                (169, _) => Format.BC1RgbaUnormBlock,
                (170, _) => Format.BC1RgbaSrgbBlock,
                (171, _) => Format.BC2UnormBlock,
                (172, _) => Format.BC2SrgbBlock,
                (173, _) => Format.BC3UnormBlock,
                (174, _) => Format.BC3SrgbBlock,
                (175, 1) => Format.BC4SNormBlock,
                (175, _) => Format.BC4UnormBlock,
                (176, _) => Format.BC4SNormBlock,
                (177, 1) => Format.BC5SNormBlock,
                (177, _) => Format.BC5UnormBlock,
                (178, _) => Format.BC5SNormBlock,
                (179, _) => Format.BC6HUfloatBlock,
                (180, _) => Format.BC6HSfloatBlock,
                (181, _) => Format.BC7UnormBlock,
                (182, _) => Format.BC7SrgbBlock,
                _ => Format.R8G8B8A8Unorm,
            };

        private static Format GetRenderTargetFormat(uint format, uint numberType) =>
            (format, numberType) switch
            {
                (4, 4) => Format.R32Uint,
                (4, 5) => Format.R32Sint,
                (4, 7) => Format.R32Sfloat,
                (5, 4) => Format.R16G16Uint,
                (5, 5) => Format.R16G16Sint,
                (5, 7) => Format.R16G16Sfloat,
                (6, 7) => Format.B10G11R11UfloatPack32,
                (7, 7) => Format.B10G11R11UfloatPack32,
                (9, _) => Format.A2B10G10R10UnormPack32,
                (10, 9) => Format.R8G8B8A8Srgb,
                (10, 4) => Format.R8G8B8A8Uint,
                (10, 5) => Format.R8G8B8A8Sint,
                (10, _) => Format.R8G8B8A8Unorm,
                (11, 7) => Format.R32G32Sfloat,
                (12, 4) => Format.R16G16B16A16Uint,
                (12, 5) => Format.R16G16B16A16Sint,
                (12, 7) => Format.R16G16B16A16Sfloat,
                (13, 7) => Format.R32G32B32A32Sfloat,
                (14, 7) => Format.R32G32B32A32Sfloat,
                (_, 0) => GetTextureFormat(format, numberType),
                (_, 9) => GetTextureFormat(format, numberType),
                _ => Format.Undefined,
            };

        private static bool IsBlockCompressedFormat(Format format) =>
            format is Format.BC1RgbaUnormBlock or
                Format.BC1RgbaSrgbBlock or
                Format.BC2UnormBlock or
                Format.BC2SrgbBlock or
                Format.BC3UnormBlock or
                Format.BC3SrgbBlock or
                Format.BC4UnormBlock or
                Format.BC4SNormBlock or
                Format.BC5UnormBlock or
                Format.BC5SNormBlock or
                Format.BC6HUfloatBlock or
                Format.BC6HSfloatBlock or
                Format.BC7UnormBlock or
                Format.BC7SrgbBlock;

        private VkBuffer CreateBuffer(
            ulong size,
            BufferUsageFlags usage,
            MemoryPropertyFlags memoryFlags,
            out DeviceMemory memory)
        {
            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };
            Check(_vk.CreateBuffer(_device, &bufferInfo, null, out var buffer), "vkCreateBuffer");

            _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, memoryFlags),
            };
            Check(_vk.AllocateMemory(_device, &memoryInfo, null, out memory), "vkAllocateMemory");
            Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
            return buffer;
        }

        private void CreateStagingBuffer(ulong size)
        {
            _stagingBuffer = CreateBuffer(
                size,
                BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _stagingMemory);
            _stagingSize = size;
        }

        private uint FindMemoryType(uint typeBits, MemoryPropertyFlags requiredFlags)
        {
            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var properties);
            var memoryTypes = &properties.MemoryTypes.Element0;
            for (uint index = 0; index < properties.MemoryTypeCount; index++)
            {
                if ((typeBits & (1u << (int)index)) != 0 &&
                    (memoryTypes[index].PropertyFlags & requiredFlags) == requiredFlags)
                {
                    return index;
                }
            }

            throw new InvalidOperationException("No compatible Vulkan host-visible memory type was found.");
        }

        private const uint MaxComputeZSlicesPerSubmission = 8;
        // An indirect guest dispatch above this size is not credible frame work
        // (at the minimum 64-thread group used by the captured title this is
        // already over one billion invocations).  Treat it as poisoned
        // indirect-command data and quarantine it instead of feeding a host
        // API a multi-billion-workgroup command.  This is validation, not a
        // clamp: the raw dimensions remain visible in the trace so the
        // producer can be fixed without changing the guest value.
        private const ulong MaxCredibleGuestWorkgroupsPerDispatch = 16UL * 1024 * 1024;

        private void ExecuteComputeDispatch(VulkanComputeGuestDispatch work)
        {
            var perfStart = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _perfDrawCount);
            PerfOverlay.RecordDraw();
            try
            {
                ExecuteComputeDispatchCore(work);
                DebugReadbackAfterComputeStore(work);
            }
            finally
            {
                Interlocked.Add(
                    ref _perfDrawTicks,
                    Stopwatch.GetTimestamp() - perfStart);
            }
        }

        // SHARPEMU_READBACK_IMAGE=<hex guest address>|lut: after every compute
        // dispatch that binds a matching storage image ("lut" matches any 1x1
        // storage image), drain the GPU and log the live image contents.
        // Diagnostic-only; pins down where a compute-written image loses its
        // data (store never landing vs a later upload/recreate clobbering it).
        private static readonly string? _debugReadbackImageSpec =
            Environment.GetEnvironmentVariable("SHARPEMU_READBACK_IMAGE");
        private static int _debugReadbackCount;

        private void DebugReadbackAfterComputeStore(VulkanComputeGuestDispatch work)
        {
            if (_debugReadbackImageSpec is not { Length: > 0 } spec ||
                _deviceLost ||
                _debugReadbackCount > 400)
            {
                return;
            }

            var matchAnyLut = spec.Equals("lut", StringComparison.OrdinalIgnoreCase);
            // "lut8": only 1x1 guest-format-10 (RGBA8) images — the UE5
            // exposure copy targets — so early high-frequency R32F LUT
            // copies don't exhaust the readback budget first.
            var matchRgba8Lut = spec.Equals("lut8", StringComparison.OrdinalIgnoreCase);
            var address = 0UL;
            if (!matchAnyLut &&
                !matchRgba8Lut &&
                !ulong.TryParse(
                    spec.Replace("0x", string.Empty),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out address))
            {
                return;
            }

            List<GuestImageResource>? images = null;
            foreach (var texture in work.Textures)
            {
                if (texture.IsStorage &&
                    texture.Address != 0 &&
                    (matchAnyLut
                        ? texture.Width == 1 && texture.Height == 1
                        : matchRgba8Lut
                            ? texture.Width == 1 && texture.Height == 1 &&
                              texture.Format == 10
                            : texture.Address == address) &&
                    _guestImages.TryGetValue(texture.Address, out var image))
                {
                    images ??= [];
                    if (!images.Contains(image))
                    {
                        images.Add(image);
                    }
                }
            }

            if (images is null)
            {
                return;
            }

            _debugReadbackCount++;
            _commandBuffer = _presentationCommandBuffer;
            FlushBatchedGuestCommands();
            Check(
                _vk.QueueWaitIdle(_queue),
                "vkQueueWaitIdle(debug compute readback)");
            foreach (var image in images)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] dbg.post_dispatch_readback cs=0x{work.ShaderAddress:X16} " +
                    $"addr=0x{image.Address:X16} init={image.Initialized} " +
                    $"pendingInit={image.InitialUploadPending}");
                TraceGuestImageContents(image);
            }
        }

        private void ExecuteComputeDispatchCore(VulkanComputeGuestDispatch work)
        {
            FlushBatchedGuestCommands();
            if (_deviceLost)
            {
                return;
            }

            if (_skipAllCompute ||
                AddressListContains("SHARPEMU_SKIP_COMPUTE_CS", work.ShaderAddress) ||
                (_skipTallComputeZ > 0 && work.GroupCountZ >= _skipTallComputeZ))
            {
                TraceVulkanShader(
                    $"vk.compute_skip cs=0x{work.ShaderAddress:X16} " +
                    $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                    $"textures={work.Textures.Count}");
                return;
            }

            if (!TryValidateComputeDispatch(work, out var validationError))
            {
                LogRejectedComputeDispatch(work, validationError);
                return;
            }

            TranslatedDrawResources? resources = null;
            CommandBuffer commandBuffer = default;
            var submitted = false;
            var chunksSubmitted = 0;
            try
            {
                EnsureGuestSubmissionCapacity();
                resources = CreateComputeDispatchResources(work);

                var batchCount = Math.Max(
                    1u,
                    (uint)Math.Ceiling(work.GroupCountZ / (double)MaxComputeZSlicesPerSubmission));
                var threadLimits = stackalloc uint[3]
                {
                    work.ThreadCountX,
                    work.ThreadCountY,
                    work.ThreadCountZ,
                };

                for (var batchIndex = 0u; batchIndex < batchCount; batchIndex++)
                {
                    var zStart = batchIndex * MaxComputeZSlicesPerSubmission;
                    var zCount = Math.Min(MaxComputeZSlicesPerSubmission, work.GroupCountZ - zStart);
                    var isFirstBatch = batchIndex == 0;
                    var isLastBatch = batchIndex == batchCount - 1;

                    if (!isFirstBatch)
                    {
                        // Each chunk is its own queue submission; without
                        // this the in-flight submission cap only applies to
                        // the first chunk of a tall dispatch.
                        EnsureGuestSubmissionCapacity();
                    }

                    commandBuffer = AllocateGuestCommandBuffer();
                    _commandBuffer = commandBuffer;
                    var beginInfo = new CommandBufferBeginInfo
                    {
                        SType = StructureType.CommandBufferBeginInfo,
                        Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                    };
                    Check(
                        _vk.BeginCommandBuffer(_commandBuffer, &beginInfo),
                        "vkBeginCommandBuffer(compute)");

                    BeginDebugLabel(_commandBuffer, resources.DebugName);
                    if (isFirstBatch)
                    {
                        RecordGlobalBufferVisibilityBarrier(
                            _commandBuffer,
                            resources,
                            PipelineStageFlags.ComputeShaderBit);
                        RecordTextureUploads(resources, PipelineStageFlags.ComputeShaderBit);
                        RecordStorageImagesForWrite(resources, PipelineStageFlags.ComputeShaderBit);
                    }
                    else
                    {
                        // Chunks are submitted without CPU waits; this
                        // barrier orders them against the previous chunk's
                        // shader writes on the same queue.
                        var chunkBarrier = new MemoryBarrier
                        {
                            SType = StructureType.MemoryBarrier,
                            SrcAccessMask = AccessFlags.ShaderWriteBit,
                            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                        };
                        _vk.CmdPipelineBarrier(
                            _commandBuffer,
                            PipelineStageFlags.ComputeShaderBit,
                            PipelineStageFlags.ComputeShaderBit,
                            0,
                            1,
                            &chunkBarrier,
                            0,
                            null,
                            0,
                            null);
                    }

                    _vk.CmdBindPipeline(
                        _commandBuffer,
                        PipelineBindPoint.Compute,
                        resources.Pipeline);
                    if (resources.DescriptorSet.Handle != 0)
                    {
                        var descriptorSet = resources.DescriptorSet;
                        _vk.CmdBindDescriptorSets(
                            _commandBuffer,
                            PipelineBindPoint.Compute,
                            resources.PipelineLayout,
                            0,
                            1,
                            &descriptorSet,
                            0,
                            null);
                    }

                    _vk.CmdPushConstants(
                        _commandBuffer,
                        resources.PipelineLayout,
                        ShaderStageFlags.ComputeBit,
                        0,
                        3 * sizeof(uint),
                        threadLimits);

                    RecordChunkedComputeDispatch(_commandBuffer, work, zStart, zCount);

                    if (isLastBatch)
                    {
                        RecordStorageImagesForRead(resources, PipelineStageFlags.ComputeShaderBit);
                    }

                    EndDebugLabel(_commandBuffer);
                    Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer(compute)");

                    TraceVulkanShader(
                        $"vk.compute_submit cs=0x{work.ShaderAddress:X16} " +
                        $"batch={batchIndex}/{batchCount} z={zStart}..{zStart + zCount}");
                    if (isLastBatch)
                    {
                        SubmitGuestCommandBuffer(
                            commandBuffer,
                            [resources],
                            GetTraceImages(resources));
                        submitted = true;
                    }
                    else
                    {
                        SubmitGuestCommandBuffer(
                            commandBuffer,
                            [],
                            [],
                            referencedResources: [resources]);
                        chunksSubmitted++;
                        commandBuffer = default;
                    }
                }

                MarkSampledImagesInitialized(resources);
                MarkStorageImagesInitialized(resources, traceContents: false);
                if (work.WritesGlobalMemory)
                {
                    // The CPU submit thread may immediately consume an indirect
                    // argument written by this dispatch. Wait for the specific
                    // guest fences and publish only dirty ranges; a queue-wide
                    // idle unnecessarily serialized presentation work too.
                    WaitForAllGuestSubmissionsForCpuVisibility();
                    WriteBackAllDirtyGuestBuffers();
                }
                TraceVulkanShader(
                    $"vk.compute_dispatch groups={work.GroupCountX}x" +
                    $"{work.GroupCountY}x{work.GroupCountZ} " +
                    $"base={work.BaseGroupX}x{work.BaseGroupY}x{work.BaseGroupZ} " +
                    $"textures={work.Textures.Count} cs=0x{work.ShaderAddress:X16} " +
                    $"batches={batchCount}");
            }
            catch (Exception exception)
            {
                if (TryMarkDeviceLost(exception))
                {
                    return;
                }

                Console.Error.WriteLine(
                    $"[LOADER][ERROR] Vulkan compute dispatch failed " +
                    $"cs=0x{work.ShaderAddress:X16}: {exception.Message}");
            }
            finally
            {
                _commandBuffer = _presentationCommandBuffer;
                if (!submitted && commandBuffer.Handle != 0)
                {
                    _vk.FreeCommandBuffers(
                        _device,
                        _commandPool,
                        1,
                        &commandBuffer);
                }

                if (!submitted && resources is not null)
                {
                    if (chunksSubmitted > 0)
                    {
                        // Earlier chunks were submitted with empty resource
                        // lists and may still execute against these
                        // pipelines/images; destroy only after every
                        // submission issued so far has completed.
                        _deferredResourceDestroys.Enqueue((resources, _submitTimeline));
                    }
                    else
                    {
                        DestroyTranslatedDrawResources(resources);
                    }
                }
            }
        }

        private bool TryValidateComputeDispatch(
            VulkanComputeGuestDispatch work,
            out string error)
        {
            if (work.LocalSizeX == 0 || work.LocalSizeY == 0 || work.LocalSizeZ == 0)
            {
                error = "zero-local-size";
                return false;
            }

            if (work.LocalSizeX > _maxComputeWorkGroupSizeX ||
                work.LocalSizeY > _maxComputeWorkGroupSizeY ||
                work.LocalSizeZ > _maxComputeWorkGroupSizeZ)
            {
                error =
                    $"local-size-exceeds-device({work.LocalSizeX}x{work.LocalSizeY}x{work.LocalSizeZ}>" +
                    $"{_maxComputeWorkGroupSizeX}x{_maxComputeWorkGroupSizeY}x{_maxComputeWorkGroupSizeZ})";
                return false;
            }

            var localInvocations =
                (ulong)work.LocalSizeX * work.LocalSizeY * work.LocalSizeZ;
            if (localInvocations > _maxComputeWorkGroupInvocations)
            {
                error =
                    $"local-invocations-exceed-device({localInvocations}>" +
                    $"{_maxComputeWorkGroupInvocations})";
                return false;
            }

            if ((ulong)work.BaseGroupX + work.GroupCountX > _maxComputeWorkGroupCountX ||
                (ulong)work.BaseGroupY + work.GroupCountY > _maxComputeWorkGroupCountY ||
                (ulong)work.BaseGroupZ + work.GroupCountZ > _maxComputeWorkGroupCountZ)
            {
                error =
                    $"group-range-exceeds-device(base={work.BaseGroupX}x{work.BaseGroupY}x{work.BaseGroupZ}," +
                    $"count={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ}," +
                    $"limit={_maxComputeWorkGroupCountX}x{_maxComputeWorkGroupCountY}x" +
                    $"{_maxComputeWorkGroupCountZ})";
                return false;
            }

            if (work.IsIndirect)
            {
                ulong totalWorkgroups;
                try
                {
                    totalWorkgroups = checked(
                        (ulong)work.GroupCountX * work.GroupCountY * work.GroupCountZ);
                }
                catch (OverflowException)
                {
                    error = "indirect-workgroup-count-overflow";
                    return false;
                }

                if (totalWorkgroups > MaxCredibleGuestWorkgroupsPerDispatch)
                {
                    error =
                        $"poisoned-indirect-workgroup-count({totalWorkgroups}>" +
                        $"{MaxCredibleGuestWorkgroupsPerDispatch})";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private void LogRejectedComputeDispatch(
            VulkanComputeGuestDispatch work,
            string reason)
        {
            if (_rejectedComputeDispatches.Count >= 256 ||
                !_rejectedComputeDispatches.Add(
                    (work.ShaderAddress,
                     work.GroupCountX,
                     work.GroupCountY,
                     work.GroupCountZ,
                     reason)))
            {
                return;
            }

            Console.Error.WriteLine(
                $"[LOADER][WARN] vk.compute_reject cs=0x{work.ShaderAddress:X16} " +
                $"source={(work.IsIndirect ? "indirect" : "direct")} " +
                $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                $"base={work.BaseGroupX}x{work.BaseGroupY}x{work.BaseGroupZ} " +
                $"local={work.LocalSizeX}x{work.LocalSizeY}x{work.LocalSizeZ} " +
                $"reason={reason}");
        }

        private void RecordGlobalBufferVisibilityBarrier(
            CommandBuffer commandBuffer,
            TranslatedDrawResources resources,
            PipelineStageFlags destinationStages)
        {
            if (resources.GlobalMemoryBuffers.Length == 0)
            {
                return;
            }

            // Queue submission order alone is not a shader-memory dependency.
            // This makes stores through any aliased guest view available to
            // later vertex/fragment/compute reads and writes on the same queue.
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                destinationStages,
                0,
                1,
                &barrier,
                0,
                null,
                0,
                null);
        }

        // Guest address ranges the GPU has written (shader-writable bindings).
        // Constant data inside these ranges is produced by earlier GPU work
        // (e.g. Unity's autoexposure result), so a parse-time CPU snapshot
        // would bake stale zeros; such bindings must stay live-aliased.
        private static readonly object _gpuWrittenRangeGate = new();
        private static readonly List<(ulong Start, ulong End)> _gpuWrittenGuestRanges = new();

        internal static void RecordGpuWrittenGuestRange(ulong start, ulong end)
        {
            if (end <= start)
            {
                return;
            }

            lock (_gpuWrittenRangeGate)
            {
                for (var index = _gpuWrittenGuestRanges.Count - 1; index >= 0; index--)
                {
                    var existing = _gpuWrittenGuestRanges[index];
                    if (end < existing.Start || existing.End < start)
                    {
                        continue;
                    }

                    start = Math.Min(start, existing.Start);
                    end = Math.Max(end, existing.End);
                    _gpuWrittenGuestRanges.RemoveAt(index);
                }

                _gpuWrittenGuestRanges.Add((start, end));
            }
        }

        internal static bool IntersectsGpuWrittenGuestRange(ulong start, ulong end)
        {
            lock (_gpuWrittenRangeGate)
            {
                foreach (var range in _gpuWrittenGuestRanges)
                {
                    if (start < range.End && range.Start < end)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void MarkGuestBufferDirty(
            GuestBufferAllocation allocation,
            ulong offset,
            ulong length)
        {
            if (length == 0)
            {
                return;
            }

            RecordGpuWrittenGuestRange(
                checked(allocation.BaseAddress + offset),
                checked(allocation.BaseAddress + offset + length));

            var start = offset;
            var end = checked(offset + length);
            for (var index = allocation.DirtyRanges.Count - 1; index >= 0; index--)
            {
                var existing = allocation.DirtyRanges[index];
                var existingEnd = existing.Offset + existing.Length;
                if (end < existing.Offset || existingEnd < start)
                {
                    continue;
                }

                start = Math.Min(start, existing.Offset);
                end = Math.Max(end, existingEnd);
                allocation.DirtyRanges.RemoveAt(index);
            }

            allocation.DirtyRanges.Add(new DirtyGuestBufferRange(start, end - start));
        }

        private void WriteBackAllDirtyGuestBuffers()
        {
            var memory = _guestMemory;
            if (memory is null)
            {
                return;
            }

            foreach (var allocation in _guestBufferAllocations)
            {
                if (allocation.DirtyRanges.Count != 0 &&
                    allocation.LastUseTimeline > _completedTimeline)
                {
                    // A mapped HOST_COHERENT allocation still cannot be read
                    // by the CPU while a shader may be writing it. Callers
                    // normally retire the relevant fences first; keep this
                    // helper fail-closed if a future path forgets to do so.
                    TraceVulkanShader(
                        $"vk.global_writeback_deferred base=0x{allocation.BaseAddress:X16} " +
                        $"last_use={allocation.LastUseTimeline} completed={_completedTimeline}");
                    continue;
                }

                for (var index = allocation.DirtyRanges.Count - 1; index >= 0; index--)
                {
                    var range = allocation.DirtyRanges[index];
                    if (range.Length == 0 || range.Length > int.MaxValue)
                    {
                        continue;
                    }

                    var mappedBytes = new ReadOnlySpan<byte>(
                        (void*)(allocation.Mapped + checked((nint)range.Offset)),
                        checked((int)range.Length));
                    var shadowBytes = allocation.Shadow.AsSpan(
                        checked((int)range.Offset),
                        mappedBytes.Length);
                    var guestAddress = allocation.BaseAddress + range.Offset;
                    var changedBytes = 0UL;
                    var changedRuns = 0;
                    var changedPages = 0;
                    var writtenRuns = 0;
                    var writtenPages = 0;
                    var failedRuns = 0;
                    var unreadablePages = 0;
                    var fallbackWrites = 0;
                    var firstChangedOffset = -1;
                    allocation.DirtyRanges.RemoveAt(index);

                    // A writable descriptor only identifies a potential write
                    // range. Publishing the entire mapped view would overwrite
                    // unrelated live CPU data with its old snapshot. Compare
                    // against the last synchronized image. For each changed
                    // page, start with current guest bytes and overlay only the
                    // shader changes before one bounded write. This preserves
                    // live CPU changes in unchanged bytes without degenerating
                    // into millions of writes for alternating output patterns.
                    const int pageSize = 4096;
                    const int unreadableMergeGap = 16;
                    var livePageBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(pageSize);
                    var pageRuns = new List<(int Start, int Length)>(64);
                    try
                    {
                        for (var pageStart = 0;
                             pageStart < mappedBytes.Length;
                             pageStart += pageSize)
                        {
                            var pageEnd = Math.Min(pageStart + pageSize, mappedBytes.Length);
                            pageRuns.Clear();
                            var cursor = pageStart;
                            while (cursor < pageEnd)
                            {
                                while (cursor < pageEnd &&
                                       mappedBytes[cursor] == shadowBytes[cursor])
                                {
                                    cursor++;
                                }

                                if (cursor == pageEnd)
                                {
                                    break;
                                }

                                var runStart = cursor;
                                while (cursor < pageEnd &&
                                       mappedBytes[cursor] != shadowBytes[cursor])
                                {
                                    cursor++;
                                }

                                var runLength = cursor - runStart;
                                pageRuns.Add((runStart, runLength));
                                changedRuns++;
                                changedBytes += (ulong)runLength;
                                if (firstChangedOffset < 0)
                                {
                                    firstChangedOffset = runStart;
                                }
                            }

                            if (pageRuns.Count == 0)
                            {
                                continue;
                            }

                            changedPages++;
                            var pageLength = pageEnd - pageStart;
                            var livePage = livePageBuffer.AsSpan(0, pageLength);
                            if (memory.TryRead(guestAddress + (ulong)pageStart, livePage))
                            {
                                foreach (var run in pageRuns)
                                {
                                    mappedBytes.Slice(run.Start, run.Length).CopyTo(
                                        livePage.Slice(run.Start - pageStart, run.Length));
                                }

                                if (memory.TryWrite(guestAddress + (ulong)pageStart, livePage))
                                {
                                    foreach (var run in pageRuns)
                                    {
                                        mappedBytes.Slice(run.Start, run.Length).CopyTo(
                                            shadowBytes.Slice(run.Start, run.Length));
                                    }

                                    writtenPages++;
                                    writtenRuns += pageRuns.Count;
                                    continue;
                                }

                                foreach (var run in pageRuns)
                                {
                                    failedRuns++;
                                    MarkGuestBufferDirty(
                                        allocation,
                                        range.Offset + (ulong)run.Start,
                                        (ulong)run.Length);
                                }

                                continue;
                            }

                            // A partial/unreadable edge cannot be safely
                            // reconstructed as a page. Fall back to bounded
                            // changed spans, coalescing only tiny gaps.
                            unreadablePages++;
                            for (var runIndex = 0; runIndex < pageRuns.Count; runIndex++)
                            {
                                var firstRunIndex = runIndex;
                                var mergedStart = pageRuns[runIndex].Start;
                                var mergedEnd = mergedStart + pageRuns[runIndex].Length;
                                while (runIndex + 1 < pageRuns.Count &&
                                       pageRuns[runIndex + 1].Start - mergedEnd <=
                                       unreadableMergeGap)
                                {
                                    runIndex++;
                                    mergedEnd = pageRuns[runIndex].Start +
                                        pageRuns[runIndex].Length;
                                }

                                var lastRunIndex = runIndex;
                                var mergedLength = mergedEnd - mergedStart;
                                var mergedLive = livePageBuffer.AsSpan(0, mergedLength);
                                if (memory.TryRead(
                                        guestAddress + (ulong)mergedStart,
                                        mergedLive))
                                {
                                    for (var overlayIndex = firstRunIndex;
                                         overlayIndex <= lastRunIndex;
                                         overlayIndex++)
                                    {
                                        var run = pageRuns[overlayIndex];
                                        mappedBytes.Slice(run.Start, run.Length).CopyTo(
                                            mergedLive.Slice(
                                                run.Start - mergedStart,
                                                run.Length));
                                    }

                                    fallbackWrites++;
                                    if (memory.TryWrite(
                                            guestAddress + (ulong)mergedStart,
                                            mergedLive))
                                    {
                                        for (var overlayIndex = firstRunIndex;
                                             overlayIndex <= lastRunIndex;
                                             overlayIndex++)
                                        {
                                            var run = pageRuns[overlayIndex];
                                            mappedBytes.Slice(run.Start, run.Length).CopyTo(
                                                shadowBytes.Slice(run.Start, run.Length));
                                        }

                                        writtenRuns += lastRunIndex - firstRunIndex + 1;
                                        continue;
                                    }
                                }

                                // Even the merged span crosses an unreadable
                                // edge. Exact changed runs remain safe because
                                // they never carry stale gap bytes.
                                for (var exactIndex = firstRunIndex;
                                     exactIndex <= lastRunIndex;
                                     exactIndex++)
                                {
                                    var run = pageRuns[exactIndex];
                                    var changed = mappedBytes.Slice(run.Start, run.Length);
                                    fallbackWrites++;
                                    if (memory.TryWrite(
                                            guestAddress + (ulong)run.Start,
                                            changed))
                                    {
                                        changed.CopyTo(shadowBytes.Slice(
                                            run.Start,
                                            run.Length));
                                        writtenRuns++;
                                    }
                                    else
                                    {
                                        failedRuns++;
                                        MarkGuestBufferDirty(
                                            allocation,
                                            range.Offset + (ulong)run.Start,
                                            (ulong)run.Length);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(livePageBuffer);
                    }

                    var probe = mappedBytes[..Math.Min(mappedBytes.Length, 256)];
                    var nonzero = 0;
                    foreach (var value in probe)
                    {
                        nonzero += value == 0 ? 0 : 1;
                    }

                    var firstForRange = _tracedGlobalWritebacks.Count < 256 &&
                        _tracedGlobalWritebacks.Add((guestAddress, range.Length));
                    var traceSmallMutation = range.Length <= 4096 &&
                        _tracedSmallGlobalWritebackEvents++ < 1024;
                    var traceLargeMutation = range.Length >= 1024 * 1024 &&
                        _tracedLargeGlobalWritebackEvents++ < 256;
                    if (firstForRange || traceSmallMutation || traceLargeMutation)
                    {
                        var head = firstChangedOffset >= 0
                            ? mappedBytes.Slice(
                                firstChangedOffset,
                                Math.Min(mappedBytes.Length - firstChangedOffset, 32))
                            : ReadOnlySpan<byte>.Empty;
                        TraceVulkanShader(
                            $"vk.global_writeback base=0x{guestAddress:X16} " +
                            $"potential_bytes={mappedBytes.Length} changed_bytes={changedBytes} " +
                            $"changed_runs={changedRuns} changed_pages={changedPages} " +
                            $"written_pages={writtenPages} written_runs={writtenRuns} " +
                            $"unreadable_pages={unreadablePages} " +
                            $"fallback_writes={fallbackWrites} failed_runs={failedRuns} " +
                            $"probe_nonzero={nonzero}/{probe.Length} " +
                            $"changed_head={Convert.ToHexString(head)}");
                    }
                }
            }
        }

        private void RecordChunkedComputeDispatch(
            CommandBuffer commandBuffer,
            VulkanComputeGuestDispatch work,
            uint zStart,
            uint zCount)
        {
            const uint maxWorkgroupsPerCommand = 4096;
            ulong commandCount = 0;
            var maxXChunk = Math.Max(
                1u,
                Math.Min(
                    work.GroupCountX,
                    Math.Min(_maxComputeWorkGroupCountX, maxWorkgroupsPerCommand)));
            for (var x = 0u; x < work.GroupCountX;)
            {
                var countX = Math.Min(maxXChunk, work.GroupCountX - x);
                var xyBudget = Math.Max(maxWorkgroupsPerCommand / countX, 1u);
                var maxYChunk = Math.Max(
                    1u,
                    Math.Min(
                        work.GroupCountY,
                        Math.Min(_maxComputeWorkGroupCountY, xyBudget)));
                for (var y = 0u; y < work.GroupCountY;)
                {
                    var countY = Math.Min(maxYChunk, work.GroupCountY - y);
                    var xyzBudget = Math.Max(xyBudget / countY, 1u);
                    var maxZChunk = Math.Max(
                        1u,
                        Math.Min(
                            zCount,
                            Math.Min(_maxComputeWorkGroupCountZ, xyzBudget)));
                    for (var z = 0u; z < zCount;)
                    {
                        var countZ = Math.Min(maxZChunk, zCount - z);
                        _vk.CmdDispatchBase(
                            commandBuffer,
                            checked(work.BaseGroupX + x),
                            checked(work.BaseGroupY + y),
                            checked(work.BaseGroupZ + zStart + z),
                            countX,
                            countY,
                            countZ);
                        commandCount++;
                        z += countZ;
                    }

                    y += countY;
                }

                x += countX;
            }

            if (commandCount > 1)
            {
                TraceVulkanShader(
                    $"vk.compute_chunked cs=0x{work.ShaderAddress:X16} " +
                    $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                    $"base={work.BaseGroupX}x{work.BaseGroupY}x{work.BaseGroupZ} " +
                    $"z_range={zStart}..{zStart + zCount} commands={commandCount} " +
                    $"command_budget={maxWorkgroupsPerCommand} " +
                    $"device_limit={_maxComputeWorkGroupCountX}x" +
                    $"{_maxComputeWorkGroupCountY}x{_maxComputeWorkGroupCountZ}");
            }
        }

        private void ExecuteOffscreenDraw(VulkanOffscreenGuestDraw work)
        {
            if (_deviceLost)
            {
                return;
            }

            var perfStart = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _perfDrawCount);
            PerfOverlay.RecordDraw();
            try
            {
                ExecuteOffscreenDrawCore(work);
            }
            finally
            {
                // Single atomic add per draw: staging -start/+end separately
                // let the stats window reset land between them and report
                // huge negative draw_ms values.
                Interlocked.Add(
                    ref _perfDrawTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp() - perfStart);
            }
        }

        private long _deferredDescriptorResolveCount;
        private long _deferredDescriptorFailCount;

        // Textures whose T# descriptor read zero at parse carry the guest
        // address the descriptor was loaded from. By execution time the
        // per-frame descriptor table has been written (CPU late-write or
        // ordered GPU work), so re-read it and swap the fallback for the
        // real render-target alias.
        private VulkanOffscreenGuestDraw ResolveDeferredTextureDescriptors(
            VulkanOffscreenGuestDraw work)
        {
            var textures = work.Draw.Textures;
            VulkanGuestDrawTexture[]? replaced = null;
            for (var index = 0; index < textures.Count; index++)
            {
                var texture = textures[index];
                if (!texture.IsFallback || texture.DeferredDescriptorAddress == 0)
                {
                    continue;
                }

                var descriptorBytes = new byte[32];
                if (_guestMemory?.TryRead(
                        texture.DeferredDescriptorAddress,
                        descriptorBytes) != true)
                {
                    if (Interlocked.Increment(ref _deferredDescriptorFailCount) <= 32)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] vk.deferred_texture_unreadable " +
                            $"table=0x{texture.DeferredDescriptorAddress:X16}");
                    }

                    continue;
                }

                var descriptorWords = new uint[8];
                for (var word = 0; word < 8; word++)
                {
                    descriptorWords[word] = System.Buffers.Binary.BinaryPrimitives
                        .ReadUInt32LittleEndian(descriptorBytes.AsSpan(word * 4, 4));
                }

                if (!AgcExports.TryResolveDeferredDrawTexture(
                        descriptorWords,
                        texture.IsStorage,
                        texture.MipLevel,
                        out var resolvedTexture))
                {
                    if (Interlocked.Increment(ref _deferredDescriptorFailCount) <= 32)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] vk.deferred_texture_unresolved " +
                            $"table=0x{texture.DeferredDescriptorAddress:X16} " +
                            $"words=[{string.Join(',', descriptorWords.Select(word => $"{word:X8}"))}]");
                    }

                    continue;
                }

                replaced ??= textures.ToArray();
                replaced[index] = resolvedTexture with { Sampler = texture.Sampler };
                if (Interlocked.Increment(ref _deferredDescriptorResolveCount) <= 64)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.deferred_texture_resolved " +
                        $"table=0x{texture.DeferredDescriptorAddress:X16} " +
                        $"tex=0x{resolvedTexture.Address:X16}:" +
                        $"{resolvedTexture.Width}x{resolvedTexture.Height}:" +
                        $"f{resolvedTexture.Format}/n{resolvedTexture.NumberType}");
                }
            }

            return replaced is null
                ? work
                : work with { Draw = work.Draw with { Textures = replaced } };
        }

        private void ExecuteOffscreenDrawCore(VulkanOffscreenGuestDraw work)
        {
            var format = GetRenderTargetFormat(work.Target.Format, work.Target.NumberType);
            if (format == Format.Undefined)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan skipped unsupported render target " +
                    $"addr=0x{work.Target.Address:X16} format={work.Target.Format} " +
                    $"number={work.Target.NumberType}");
                ReturnPooledGuestData(work.Draw);
                return;
            }

            // A sampled alias is handled below with an ordered snapshot of the
            // target's pre-draw contents. A storage-image alias is materially
            // different: the shader may write both the storage image and the
            // color attachment, so a one-way snapshot would silently discard
            // guest writes. Keep that case explicit until attachment feedback
            // loop support is enabled on the host device.
            if (work.Draw.Textures.Any(texture =>
                    texture.Address == work.Target.Address &&
                    texture.Address != 0 &&
                    texture.IsStorage))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan skipped storage render-target feedback loop " +
                    $"addr=0x{work.Target.Address:X16}; " +
                    "sampled aliases use snapshots, simultaneous storage writes require " +
                    "attachment-feedback-loop support");
                ReturnPooledGuestData(work.Draw);
                return;
            }

            work = ResolveDeferredTextureDescriptors(work);
            var effectiveTarget = work.Target.Address == 0 && work.DepthTarget is { } depthOnlyTarget
                ? GetDepthOnlyColorTarget(depthOnlyTarget)
                : work.Target;
            var target = GetOrCreateGuestImage(effectiveTarget, format);
            if (work.Target.Address != 0 &&
                TakeGuestImageInitialData(work.Target.Address) is { } initialData &&
                !target.Initialized &&
                (ulong)initialData.Length == (ulong)target.Width * target.Height * 4)
            {
                UploadGuestImageInitialData(target, initialData);
            }

            TranslatedDrawResources? resources = null;
            CommandBuffer commandBuffer = default;
            var submitted = false;
            try
            {
                EnsureGuestSubmissionCapacity();
                var extent = new Extent2D(target.Width, target.Height);
                GuestDepthResource? depth = null;
                DepthFramebufferResource? depthFramebuffer = null;
                if (work.DepthTarget is { } depthTarget &&
                    (work.Draw.RenderState.Depth.TestEnable ||
                     work.Draw.RenderState.Depth.WriteEnable))
                {
                    depth = GetOrCreateGuestDepth(depthTarget);
                    depthFramebuffer = GetOrCreateDepthFramebuffer(target, depth);
                }

                var activeRenderPass = depthFramebuffer is null
                    ? target.Initialized
                        ? target.RenderPass
                        : target.InitialRenderPass
                    : target.Initialized
                        ? depth!.Initialized
                            ? depthFramebuffer.LoadRenderPass
                            : depthFramebuffer.DepthClearRenderPass
                        : depth!.Initialized
                            ? depthFramebuffer.ColorClearRenderPass
                            : depthFramebuffer.BothClearRenderPass;
                var activeFramebuffer = depthFramebuffer?.Framebuffer ?? target.Framebuffer;
                var draw = work.Draw;
                TraceSelectedDrawVertexInputs(work);
                if (work.DepthTarget?.ReadOnly == true && draw.RenderState.Depth.WriteEnable)
                {
                    draw = draw with
                    {
                        RenderState = draw.RenderState with
                        {
                            Depth = draw.RenderState.Depth with { WriteEnable = false },
                        },
                    };
                }
                resources = CreateTranslatedDrawResources(
                    draw,
                    activeRenderPass,
                    extent,
                    format,
                    target,
                    hasDepthAttachment: depthFramebuffer is not null,
                    feedbackDepth: depth);
                resources.DebugName =
                    $"SharpEmu offscreen rt=0x{work.Target.Address:X16} " +
                    $"{work.Target.Width}x{work.Target.Height} fmt{work.Target.Format}";

                var traceSelectedDrawTarget = TraceSelectedDrawSources(work, resources);

                commandBuffer = BeginBatchedGuestCommands();
                _commandBuffer = commandBuffer;

                // Lifetime: recorded commands reference these resources, so
                // they join the batch before recording and are destroyed only
                // after the batch's fence signals.
                _batchResources.Add(resources);
                submitted = true;

                BeginDebugLabel(_commandBuffer, resources.DebugName);
                var hasStorageImages = false;
                var needsTextureUploads = false;
                var hasFeedbackSnapshots = false;
                var hasDepthTextures = false;
                foreach (var texture in resources.Textures)
                {
                    if (texture is null)
                    {
                        continue;
                    }

                    hasStorageImages |= texture.IsStorage;
                    needsTextureUploads |= texture.NeedsUpload;
                    hasFeedbackSnapshots |= texture.FeedbackSource is not null ||
                        texture.DepthFeedbackSource is not null;
                    hasDepthTextures |= texture.GuestDepth is not null;
                }

                if (hasStorageImages ||
                    needsTextureUploads ||
                    hasFeedbackSnapshots ||
                    hasDepthTextures ||
                    resources.GlobalMemoryBuffers.Length != 0 ||
                    !ReferenceEquals(_openPassTarget, target) ||
                    !ReferenceEquals(_openPassDepth, depth))
                {
                    CloseOpenTranslatedRenderPass();
                    RecordGlobalBufferVisibilityBarrier(
                        _commandBuffer,
                        resources,
                        PipelineStageFlags.VertexShaderBit |
                        PipelineStageFlags.FragmentShaderBit);
                    RecordRenderTargetFeedbackSnapshots(
                        resources,
                        PipelineStageFlags.FragmentShaderBit);
                    RecordDepthFeedbackSnapshots(
                        resources,
                        PipelineStageFlags.FragmentShaderBit);
                    RecordTextureUploads(resources, PipelineStageFlags.FragmentShaderBit);
                    RecordStorageImagesForWrite(resources, PipelineStageFlags.FragmentShaderBit);

                    var toColorAttachment = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = target.Initialized ? AccessFlags.ShaderReadBit : 0,
                        DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
                        OldLayout = target.Initialized
                            ? ImageLayout.ShaderReadOnlyOptimal
                            : ImageLayout.Undefined,
                        NewLayout = ImageLayout.ColorAttachmentOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = target.Image,
                        SubresourceRange = ColorSubresourceRange(),
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        target.Initialized
                            ? PipelineStageFlags.FragmentShaderBit
                            : PipelineStageFlags.TopOfPipeBit,
                        PipelineStageFlags.ColorAttachmentOutputBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &toColorAttachment);

                    if (depth is not null &&
                        depth.Layout == ImageLayout.ShaderReadOnlyOptimal)
                    {
                        var toDepthAttachment = new ImageMemoryBarrier
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = AccessFlags.ShaderReadBit,
                            DstAccessMask =
                                AccessFlags.DepthStencilAttachmentReadBit |
                                AccessFlags.DepthStencilAttachmentWriteBit,
                            OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                            NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = depth.Image,
                            SubresourceRange = new ImageSubresourceRange(
                                ImageAspectFlags.DepthBit,
                                0,
                                1,
                                0,
                                1),
                        };
                        _vk.CmdPipelineBarrier(
                            _commandBuffer,
                            PipelineStageFlags.FragmentShaderBit |
                            PipelineStageFlags.ComputeShaderBit,
                            PipelineStageFlags.EarlyFragmentTestsBit |
                            PipelineStageFlags.LateFragmentTestsBit,
                            0,
                            0,
                            null,
                            0,
                            null,
                            1,
                            &toDepthAttachment);
                        depth.Layout = ImageLayout.DepthStencilAttachmentOptimal;
                    }

                    BeginTranslatedRenderPass(
                        activeRenderPass,
                        activeFramebuffer,
                        extent,
                        hasDepthAttachment: depthFramebuffer is not null,
                        clearDepth: depth?.ClearDepth ?? 1f);
                    _openPassTarget = target;
                    _openPassDepth = depth;
                    if (depth is not null)
                    {
                        depth.Layout = ImageLayout.DepthStencilAttachmentOptimal;
                    }
                }

                RecordTranslatedDrawInPass(resources, extent);
                if (hasStorageImages)
                {
                    CloseOpenTranslatedRenderPass();
                    RecordStorageImagesForRead(resources, PipelineStageFlags.FragmentShaderBit);
                }

                EndDebugLabel(_commandBuffer);

                _batchTraceImages.AddRange(GetTraceImages(resources, target));
                if (++_batchDrawCount >= 64 || _flushEveryGuestDraw)
                {
                    FlushBatchedGuestCommands();
                }

                target.Initialized = true;
                if (depth is not null)
                {
                    depth.Initialized = true;
                }
                MarkSampledImagesInitialized(resources);
                MarkStorageImagesInitialized(resources, traceContents: false);

                if (work.PublishTarget && target.GuestFormat != 0)
                {
                    lock (_gate)
                    {
                        _availableGuestImages[target.Address] = target.GuestFormat;
                    }
                }
                var guestWritesMode = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_WRITES");
                var traceSmallWrites =
                    guestWritesMode == "small" &&
                    target.Width <= 512 && target.Height <= 256;
                // "large" captures the first two writes to every large target.
                // "large@N" captures only ordinal N, allowing a later stage in
                // a long 4K composite chain to be inspected without stalling
                // the queue for every preceding write.
                var traceLargeWriteOrdinal = 0L;
                if (guestWritesMode is not null &&
                    guestWritesMode.StartsWith("large@", StringComparison.Ordinal) &&
                    long.TryParse(
                        guestWritesMode.AsSpan("large@".Length),
                        out var parsedTraceLargeWriteOrdinal) &&
                    parsedTraceLargeWriteOrdinal > 0)
                {
                    traceLargeWriteOrdinal = parsedTraceLargeWriteOrdinal;
                }

                var traceLargeWrites =
                    (guestWritesMode == "large" || traceLargeWriteOrdinal != 0) &&
                    target.Width >= 2560 && target.Height >= 1440;
                if (ShouldTraceGuestImageWriteForDiagnostics(target.Address) || traceSmallWrites || traceLargeWrites)
                {
                    var writeCount = _tracedGuestWriteCounts.TryGetValue(
                        target.Address,
                        out var previousCount)
                        ? previousCount + 1
                        : 1;
                    _tracedGuestWriteCounts[target.Address] = writeCount;
                    var shouldTraceWrite = traceLargeWriteOrdinal != 0
                        ? writeCount == traceLargeWriteOrdinal
                        : writeCount <= (traceLargeWrites ? 2 : traceSmallWrites ? 48 : 3);
                    if (shouldTraceWrite)
                    {
                        _commandBuffer = _presentationCommandBuffer;
                        FlushBatchedGuestCommands();
                        Check(
                            _vk.QueueWaitIdle(_queue),
                            "vkQueueWaitIdle(guest write trace)");
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] vk.guest_write_sample " +
                            $"addr=0x{target.Address:X16} write={writeCount} " +
                            $"ps_bytes={work.Draw.PixelSpirv.Length}");
                        TraceGuestImageContents(target);
                    }
                }
                TraceVulkanShader(
                    $"vk.offscreen_draw addr=0x{target.Address:X16} " +
                    $"size={target.Width}x{target.Height} format={target.Format} " +
                    $"textures={work.Draw.Textures.Count}");
                if (_traceCopyExecutionPeriod > 0 &&
                    (resources is { Scissor: { X: 0, Y: 0, Width: 1, Height: 1 } } ||
                     (work.Draw.Textures.Count >= 5 && target.Width >= 1280)) &&
                    ++_traceExposureExecutionCount % 40 == 0)
                {
                    _commandBuffer = _presentationCommandBuffer;
                    FlushBatchedGuestCommands();
                    Check(
                        _vk.QueueWaitIdle(_queue),
                        "vkQueueWaitIdle(exposure exec trace)");
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.exposure_exec dst=0x{target.Address:X16} " +
                        $"ps={work.Draw.PixelSpirv.Length} " +
                        $"buffers={resources.GlobalMemoryBuffers.Length} " +
                        $"textures=[{string.Join('|', work.Draw.Textures.Select(entry => $"0x{entry.Address:X}:{entry.Width}x{entry.Height}"))}]");
                    for (var bufferIndex = 0;
                         bufferIndex < resources.GlobalMemoryBuffers.Length;
                         bufferIndex++)
                    {
                        var globalBuffer = resources.GlobalMemoryBuffers[bufferIndex];
                        if (globalBuffer is null ||
                            globalBuffer.Mapped == 0 ||
                            globalBuffer.GuestSize > 4096)
                        {
                            continue;
                        }

                        var wordCount = (int)Math.Min(globalBuffer.GuestSize / 4, 16);
                        var words = new uint[wordCount];
                        System.Runtime.InteropServices.Marshal.Copy(
                            globalBuffer.Mapped,
                            (int[])(object)words,
                            0,
                            wordCount);
                        var liveWords = new byte[Math.Min((int)globalBuffer.GuestSize, 16)];
                        var liveOk = globalBuffer.BaseAddress != 0 &&
                            _guestMemory?.TryRead(globalBuffer.BaseAddress, liveWords) == true;
                        var live = liveOk
                            ? string.Join(
                                ',',
                                Enumerable.Range(0, liveWords.Length / 4).Select(index =>
                                    BitConverter.ToSingle(liveWords, index * 4)
                                        .ToString("0.####e0")))
                            : "unreadable";
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] vk.exposure_buffer[{bufferIndex}] " +
                            $"base=0x{globalBuffer.BaseAddress:X} size={globalBuffer.GuestSize} " +
                            $"alias={(globalBuffer.Allocation is not null ? 1 : 0)} " +
                            $"gpuwr={(IntersectsGpuWrittenGuestRange(globalBuffer.BaseAddress, globalBuffer.BaseAddress + globalBuffer.GuestSize) ? 1 : 0)} " +
                            $"f32=[{string.Join(',', words.Select(word => BitConverter.UInt32BitsToSingle(word).ToString("0.####e0")))}] " +
                            $"live=[{live}]");
                    }
                    TraceGuestImageContents(target);
                }
                if (_traceCopyExecutionPeriod > 0 &&
                    work.Draw.Textures.Count >= 1 &&
                    work.Draw.VertexCount <= 6 &&
                    target.Width >= 1280 &&
                    ++_traceCopyExecutionCount % _traceCopyExecutionPeriod == 0)
                {
                    _commandBuffer = _presentationCommandBuffer;
                    FlushBatchedGuestCommands();
                    Check(
                        _vk.QueueWaitIdle(_queue),
                        "vkQueueWaitIdle(copy exec trace)");
                    var copySourceAddress = work.Draw.Textures[0].Address;
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.copy_exec ps={work.Draw.PixelSpirv.Length} " +
                        $"vs={work.Draw.VertexSpirv.Length} verts={work.Draw.VertexCount} " +
                        $"src=0x{copySourceAddress:X16} dst=0x{target.Address:X16} " +
                        $"textures=[{string.Join('|', work.Draw.Textures.Select(texture => $"0x{texture.Address:X}:{texture.Width}x{texture.Height}"))}]");
                    foreach (var tinyTexture in work.Draw.Textures)
                    {
                        if (tinyTexture.Width <= 2 &&
                            tinyTexture.Height <= 2 &&
                            tinyTexture.Address != 0 &&
                            _guestImages.TryGetValue(tinyTexture.Address, out var tinyImage) &&
                            tinyImage.Initialized)
                        {
                            TraceGuestImageContents(tinyImage);
                        }
                    }
                    if (_guestImages.TryGetValue(copySourceAddress, out var copySource) &&
                        copySource.Initialized)
                    {
                        TraceGuestImageContents(copySource);
                    }
                    TraceGuestImageContents(target);
                }
                if (traceSelectedDrawTarget)
                {
                    _commandBuffer = _presentationCommandBuffer;
                    FlushBatchedGuestCommands();
                    Check(
                        _vk.QueueWaitIdle(_queue),
                        "vkQueueWaitIdle(selected draw target trace)");
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.draw_target_readback " +
                        $"addr=0x{target.Address:X16} size={target.Width}x{target.Height}");
                    TraceGuestImageContents(target);
                }
            }
            catch (Exception exception)
            {
                if (TryMarkDeviceLost(exception))
                {
                    return;
                }

                if (!_guestImages.TryGetValue(work.Target.Address, out var failedTarget) ||
                    !failedTarget.Initialized)
                {
                    lock (_gate)
                    {
                        _availableGuestImages.Remove(work.Target.Address);
                    }
                }

                Console.Error.WriteLine(
                    $"[LOADER][ERROR] Vulkan offscreen draw failed " +
                    $"addr=0x{work.Target.Address:X16}: {exception.Message}");
            }
            finally
            {
                _commandBuffer = _presentationCommandBuffer;
                // The command buffer is the shared batch; it is submitted and
                // freed by FlushBatchedGuestCommands. Resources joined the
                // batch list before recording, so only pre-recording failures
                // (submitted still false) own their cleanup here.
                if (!submitted && resources is not null)
                {
                    DestroyTranslatedDrawResources(resources);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ExecuteGuestImageWrite(VulkanGuestImageWrite work)
        {
            if (_deviceLost || !_guestImages.TryGetValue(work.Address, out var target))
            {
                return;
            }

            if (work.Pixels is { } pixels)
            {
                if (pixels.Length > 0)
                {
                    UploadGuestImageInitialData(target, pixels);
                }

                return;
            }

            // Recorded into the shared batch command buffer: recording order
            // preserves queue-order semantics against earlier batched draws,
            // and the fill no longer costs a submit + full queue drain.
            var commandBuffer = BeginBatchedGuestCommands();
            CloseOpenTranslatedRenderPass();
            var toTransferDst = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = target.Initialized ? AccessFlags.ShaderReadBit : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = target.Initialized
                    ? ImageLayout.ShaderReadOnlyOptimal
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = ColorSubresourceRange(0, target.MipLevels),
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                target.Initialized
                    ? PipelineStageFlags.FragmentShaderBit
                    : PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransferDst);

            var clearValue = new ClearColorValue(
                (work.FillValue & 0xFF) / 255f,
                ((work.FillValue >> 8) & 0xFF) / 255f,
                ((work.FillValue >> 16) & 0xFF) / 255f,
                ((work.FillValue >> 24) & 0xFF) / 255f);
            var range = ColorSubresourceRange(0, target.MipLevels);
            _vk.CmdClearColorImage(
                commandBuffer,
                target.Image,
                ImageLayout.TransferDstOptimal,
                &clearValue,
                1,
                &range);

            var toShaderRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = ColorSubresourceRange(0, target.MipLevels),
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.FragmentShaderBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toShaderRead);
            target.Initialized = true;
        }

        private void UploadGuestImageInitialData(GuestImageResource target, byte[] pixels)
        {
            var byteCount = (ulong)pixels.Length;
            var staging = CreateBuffer(
                byteCount,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out var stagingMemory);
            try
            {
                void* mapped;
                Check(
                    _vk.MapMemory(_device, stagingMemory, 0, byteCount, 0, &mapped),
                    "vkMapMemory(guest image init)");
                fixed (byte* source = pixels)
                {
                    System.Buffer.MemoryCopy(source, mapped, pixels.Length, pixels.Length);
                }

                _vk.UnmapMemory(_device, stagingMemory);

                // Recorded into the shared batch; the staging buffer joins
                // the batch's retire list and is destroyed when the batch
                // fence signals, so the upload costs no queue drain.
                var commandBuffer = BeginBatchedGuestCommands();
                CloseOpenTranslatedRenderPass();

                var toTransferDst = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = target.Initialized ? AccessFlags.ShaderReadBit : 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = target.Initialized
                        ? ImageLayout.ShaderReadOnlyOptimal
                        : ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = target.Image,
                    SubresourceRange = ColorSubresourceRange(0, target.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    target.Initialized
                        ? PipelineStageFlags.FragmentShaderBit
                        : PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransferDst);

                var copyRegion = new BufferImageCopy
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit,
                        0,
                        0,
                        1),
                    ImageOffset = default,
                    ImageExtent = new Extent3D(target.Width, target.Height, 1),
                };
                _vk.CmdCopyBufferToImage(
                    commandBuffer,
                    staging,
                    target.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copyRegion);

                var toShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = target.Image,
                    SubresourceRange = ColorSubresourceRange(0, target.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toShaderRead);

                target.Initialized = true;
                _batchRetireBuffers.Add((staging, stagingMemory));
                staging = default;
                stagingMemory = default;
                if (_traceGuestImageEvents)
                {
                    Console.Error.WriteLine(
                        $"[GIMG] seeded addr=0x{target.Address:X} " +
                        $"{target.Width}x{target.Height}");
                }
            }
            finally
            {
                if (staging.Handle != 0)
                {
                    _vk.DestroyBuffer(_device, staging, null);
                }

                if (stagingMemory.Handle != 0)
                {
                    _vk.FreeMemory(_device, stagingMemory, null);
                }
            }
        }

        private GuestImageResource GetOrCreateGuestImage(
            VulkanGuestRenderTarget target,
            Format format)
        {
            var mipLevels = ClampMipLevels(target.Width, target.Height, target.MipLevels);
            var guestFormat = GetGuestTextureFormat(target.Format, target.NumberType);
            if (_guestImages.TryGetValue(target.Address, out var existing))
            {
                if (existing.Width == target.Width &&
                    existing.Height == target.Height &&
                    existing.MipLevels == mipLevels &&
                    existing.GuestFormat == guestFormat &&
                    existing.Format == format)
                {
                    if (existing.RenderPass.Handle == 0)
                    {
                        var attachmentView = existing.MipViews.Length > 0
                            ? existing.MipViews[0]
                            : existing.View;
                        var promoted = CreateRenderPassAndFramebuffer(
                            existing.Format,
                            attachmentView,
                            existing.Width,
                            existing.Height);
                        existing.RenderPass = promoted.RenderPass;
                        existing.InitialRenderPass = promoted.InitialRenderPass;
                        existing.Framebuffer = promoted.Framebuffer;
                        var promotedRenderPass = promoted.RenderPass;
                        var promotedInitialRenderPass = promoted.InitialRenderPass;
                        var promotedFramebuffer = promoted.Framebuffer;
                        var promotedName = GuestImageDebugName(target, format);
                        SetDebugName(ObjectType.RenderPass, promotedRenderPass.Handle, $"{promotedName} renderpass");
                        SetDebugName(ObjectType.RenderPass, promotedInitialRenderPass.Handle, $"{promotedName} initial-renderpass");
                        SetDebugName(ObjectType.Framebuffer, promotedFramebuffer.Handle, $"{promotedName} framebuffer");
                    }

                    return existing;
                }

                if (_traceGuestImageEvents)
                {
                    Console.Error.WriteLine(
                        $"[GIMG] recreate addr=0x{target.Address:X} " +
                        $"old={existing.Width}x{existing.Height}/{existing.Format}/m{existing.MipLevels} " +
                        $"new={target.Width}x{target.Height}/{format}/m{mipLevels} " +
                        $"initialized={existing.Initialized}");
                }

                DestroyGuestImage(existing);
                _guestImages.Remove(target.Address);
                lock (_gate)
                {
                    _availableGuestImages.Remove(target.Address);
                    _guestImageExtents.Remove(target.Address);
                }

                SharpEmu.HLE.GuestImageWriteTracker.Untrack(target.Address);
            }

            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags =
                    ImageCreateFlags.CreateMutableFormatBit |
                    ImageCreateFlags.CreateExtendedUsageBit,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(target.Width, target.Height, 1),
                MipLevels = mipLevels,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage =
                    ImageUsageFlags.ColorAttachmentBit |
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.StorageBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(offscreen)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(
                _vk.AllocateMemory(_device, &allocationInfo, null, out var memory),
                "vkAllocateMemory(offscreen)");
            Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory(offscreen)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = ColorSubresourceRange(0, mipLevels),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var view),
                "vkCreateImageView(offscreen)");

            var mipViews = new ImageView[mipLevels];
            for (uint mipLevel = 0; mipLevel < mipLevels; mipLevel++)
            {
                viewInfo.SubresourceRange = ColorSubresourceRange(mipLevel, 1);
                ImageView mipView;
                Check(
                    _vk.CreateImageView(
                        _device,
                        &viewInfo,
                        null,
                        out mipView),
                    "vkCreateImageView(offscreen mip)");
                mipViews[mipLevel] = mipView;
            }

            var (renderPass, initialRenderPass, framebuffer) =
                CreateRenderPassAndFramebuffer(
                    format,
                    mipViews[0],
                    target.Width,
                    target.Height);

            var resource = new GuestImageResource
            {
                Address = target.Address,
                Width = target.Width,
                Height = target.Height,
                MipLevels = mipLevels,
                GuestFormat = guestFormat,
                Format = format,
                Image = image,
                Memory = memory,
                View = view,
                MipViews = mipViews,
                RenderPass = renderPass,
                InitialRenderPass = initialRenderPass,
                Framebuffer = framebuffer,
            };
            var debugName = GuestImageDebugName(target, format);
            SetDebugName(ObjectType.Image, image.Handle, $"{debugName} image");
            SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} view");
            for (var mipLevel = 0; mipLevel < mipViews.Length; mipLevel++)
            {
                SetDebugName(
                    ObjectType.ImageView,
                    mipViews[mipLevel].Handle,
                    $"{debugName} mip{mipLevel}");
            }
            SetDebugName(ObjectType.RenderPass, renderPass.Handle, $"{debugName} renderpass");
            SetDebugName(ObjectType.RenderPass, initialRenderPass.Handle, $"{debugName} initial-renderpass");
            SetDebugName(ObjectType.Framebuffer, framebuffer.Handle, $"{debugName} framebuffer");
            _guestImages.Add(target.Address, resource);
            lock (_gate)
            {
                _guestImageExtents[target.Address] = (
                    target.Width,
                    target.Height,
                    GetTextureByteCount(target.Format, target.Width, target.Height));
            }

            if (target.Width <= 1920 && target.Height <= 1080)
            {
                SharpEmu.HLE.GuestImageWriteTracker.Track(
                    target.Address,
                    (ulong)target.Width * target.Height * GetTextureBytesPerPixel(target.Format));
            }

            if (_traceGuestImageEvents)
            {
                Console.Error.WriteLine(
                    $"[GIMG] created-as-rt addr=0x{target.Address:X} " +
                    $"{target.Width}x{target.Height} fmt={format}");
            }

            return resource;
        }

        private (RenderPass RenderPass, RenderPass InitialRenderPass, Framebuffer Framebuffer)
            CreateRenderPassAndFramebuffer(
            Format format,
            ImageView attachmentView,
            uint width,
            uint height)
        {
            var attachments = stackalloc AttachmentDescription[2];
            attachments[0] = new AttachmentDescription
            {
                Format = format,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.ColorAttachmentOptimal,
                FinalLayout = ImageLayout.ColorAttachmentOptimal,
            };
            var colorReference = new AttachmentReference
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };

            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorReference,
                PDepthStencilAttachment = null,
            };
            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
            };
            Check(
                _vk.CreateRenderPass(_device, &renderPassInfo, null, out var renderPass),
                "vkCreateRenderPass(offscreen)");

            // A newly allocated optimal-tiled image has undefined contents.
            // Loading from it makes every pixel outside the first draw's
            // coverage nondeterministic (and produced the solid red/blue
            // garbage frames seen on Demon's Souls). Use a render-pass-
            // compatible clear variant exactly once; initialized or seeded
            // images keep the LOAD pass so prior guest contents survive.
            attachments[0].LoadOp = AttachmentLoadOp.Clear;
            Check(
                _vk.CreateRenderPass(
                    _device,
                    &renderPassInfo,
                    null,
                    out var initialRenderPass),
                "vkCreateRenderPass(offscreen initial)");

            var framebufferAttachments = stackalloc ImageView[2];
            framebufferAttachments[0] = attachmentView;
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = 1,
                PAttachments = framebufferAttachments,
                Width = width,
                Height = height,
                Layers = 1,
            };
            Check(
                _vk.CreateFramebuffer(_device, &framebufferInfo, null, out var framebuffer),
                "vkCreateFramebuffer(offscreen)");

            return (
                renderPass,
                initialRenderPass,
                framebuffer);
        }

        private (Image Image, DeviceMemory Memory, ImageView View) CreateDepthAttachment(
            uint width,
            uint height)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = DepthFormat,
                Extent = new Extent3D(Math.Max(width, 1), Math.Max(height, 1), 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage =
                    ImageUsageFlags.DepthStencilAttachmentBit |
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(depth)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(_vk.AllocateMemory(_device, &memoryInfo, null, out var memory), "vkAllocateMemory(depth)");
            Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory(depth)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = DepthFormat,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
            };
            Check(_vk.CreateImageView(_device, &viewInfo, null, out var view), "vkCreateImageView(depth)");
            return (image, memory, view);
        }

        private GuestDepthResource GetOrCreateGuestDepth(VulkanGuestDepthTarget target)
        {
            var key = new GuestDepthKey(
                target.Address,
                target.ReadAddress,
                target.Width,
                target.Height,
                target.GuestFormat,
                target.SwizzleMode);
            if (_guestDepthImages.TryGetValue(key, out var existing))
            {
                existing.ClearDepth = target.ClearDepth;
                return existing;
            }

            var (image, memory, view) = CreateDepthAttachment(target.Width, target.Height);
            var resource = new GuestDepthResource
            {
                Key = key,
                Address = target.Address,
                ReadAddress = target.ReadAddress,
                WriteAddress = target.WriteAddress,
                Width = target.Width,
                Height = target.Height,
                GuestFormat = target.GuestFormat,
                SwizzleMode = target.SwizzleMode,
                Image = image,
                Memory = memory,
                View = view,
                ClearDepth = target.ClearDepth,
            };
            SetDebugName(
                ObjectType.Image,
                image.Handle,
                $"SharpEmu guest depth 0x{target.Address:X16} {target.Width}x{target.Height}");
            SetDebugName(
                ObjectType.ImageView,
                view.Handle,
                $"SharpEmu guest depth view 0x{target.Address:X16}");
            _guestDepthImages.Add(key, resource);
            if (_traceGuestImageEvents || _traceVulkanShaderEnabled)
            {
                Console.Error.WriteLine(
                    $"[GIMG] created-depth addr=0x{target.Address:X} " +
                    $"read=0x{target.ReadAddress:X} write=0x{target.WriteAddress:X} " +
                    $"{target.Width}x{target.Height} zfmt={target.GuestFormat} " +
                    $"sw={target.SwizzleMode} clear={target.ClearDepth:0.######}");
            }

            return resource;
        }

        private VulkanGuestRenderTarget GetDepthOnlyColorTarget(VulkanGuestDepthTarget depth)
        {
            var key = new GuestDepthKey(
                depth.Address,
                depth.ReadAddress,
                depth.Width,
                depth.Height,
                depth.GuestFormat,
                depth.SwizzleMode);
            if (!_depthOnlyColorAddresses.TryGetValue(key, out var address))
            {
                address = _nextDepthOnlyColorAddress;
                _nextDepthOnlyColorAddress = checked(_nextDepthOnlyColorAddress + 0x1000_0000UL);
                _depthOnlyColorAddresses.Add(key, address);
            }

            // The translated fragment module still declares a color output,
            // even for a guest depth-only pass.  A private, never-published
            // color attachment keeps that output legal while the persistent
            // guest DB surface remains the only observable result.
            return new VulkanGuestRenderTarget(
                address,
                depth.Width,
                depth.Height,
                Format: 10,
                NumberType: 0);
        }

        private DepthFramebufferResource GetOrCreateDepthFramebuffer(
            GuestImageResource color,
            GuestDepthResource depth)
        {
            if (color.DepthFramebuffers.TryGetValue(depth.Key, out var existing))
            {
                return existing;
            }

            if (depth.Width < color.Width || depth.Height < color.Height)
            {
                throw new InvalidOperationException(
                    $"guest depth 0x{depth.Address:X16} extent {depth.Width}x{depth.Height} " +
                    $"is smaller than color target 0x{color.Address:X16} " +
                    $"{color.Width}x{color.Height}");
            }

            var attachmentView = color.MipViews.Length > 0 ? color.MipViews[0] : color.View;
            var loadRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: false,
                clearDepth: false);
            var colorClearRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: true,
                clearDepth: false);
            var depthClearRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: false,
                clearDepth: true);
            var bothClearRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: true,
                clearDepth: true);
            var attachments = stackalloc ImageView[2];
            attachments[0] = attachmentView;
            attachments[1] = depth.View;
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = loadRenderPass,
                AttachmentCount = 2,
                PAttachments = attachments,
                Width = color.Width,
                Height = color.Height,
                Layers = 1,
            };
            Check(
                _vk.CreateFramebuffer(_device, &framebufferInfo, null, out var framebuffer),
                "vkCreateFramebuffer(offscreen depth)");
            var resource = new DepthFramebufferResource
            {
                Depth = depth,
                LoadRenderPass = loadRenderPass,
                ColorClearRenderPass = colorClearRenderPass,
                DepthClearRenderPass = depthClearRenderPass,
                BothClearRenderPass = bothClearRenderPass,
                Framebuffer = framebuffer,
            };
            var name = $"SharpEmu color 0x{color.Address:X16} depth 0x{depth.Address:X16}";
            SetDebugName(ObjectType.RenderPass, loadRenderPass.Handle, $"{name} load");
            SetDebugName(ObjectType.RenderPass, colorClearRenderPass.Handle, $"{name} color-clear");
            SetDebugName(ObjectType.RenderPass, depthClearRenderPass.Handle, $"{name} depth-clear");
            SetDebugName(ObjectType.RenderPass, bothClearRenderPass.Handle, $"{name} both-clear");
            SetDebugName(ObjectType.Framebuffer, framebuffer.Handle, $"{name} framebuffer");
            color.DepthFramebuffers.Add(depth.Key, resource);
            return resource;
        }

        private RenderPass CreateDepthRenderPass(
            Format colorFormat,
            bool clearColor,
            bool clearDepth)
        {
            var attachments = stackalloc AttachmentDescription[2];
            attachments[0] = new AttachmentDescription
            {
                Format = colorFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = clearColor ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.ColorAttachmentOptimal,
                FinalLayout = ImageLayout.ColorAttachmentOptimal,
            };
            attachments[1] = new AttachmentDescription
            {
                Format = DepthFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = clearDepth ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = clearDepth
                    ? ImageLayout.Undefined
                    : ImageLayout.DepthStencilAttachmentOptimal,
                FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
            };
            var colorReference = new AttachmentReference
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };
            var depthReference = new AttachmentReference
            {
                Attachment = 1,
                Layout = ImageLayout.DepthStencilAttachmentOptimal,
            };
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorReference,
                PDepthStencilAttachment = &depthReference,
            };
            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.LateFragmentTestsBit,
                DstStageMask =
                    PipelineStageFlags.EarlyFragmentTestsBit |
                    PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
                DstAccessMask =
                    AccessFlags.DepthStencilAttachmentReadBit |
                    AccessFlags.DepthStencilAttachmentWriteBit,
                DependencyFlags = DependencyFlags.ByRegionBit,
            };
            var createInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 2,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };
            Check(
                _vk.CreateRenderPass(_device, &createInfo, null, out var renderPass),
                "vkCreateRenderPass(offscreen depth)");
            return renderPass;
        }

        private static uint ClampMipLevels(uint width, uint height, uint requestedMipLevels)
        {
            var largestDimension = Math.Max(width, height);
            uint maximumMipLevels = 1;
            while (largestDimension > 1)
            {
                largestDimension >>= 1;
                maximumMipLevels++;
            }

            return Math.Min(Math.Max(requestedMipLevels, 1u), maximumMipLevels);
        }

        private void DestroyGuestImage(GuestImageResource resource)
        {
            foreach (var depthFramebuffer in resource.DepthFramebuffers.Values)
            {
                DestroyDepthFramebuffer(depthFramebuffer);
            }
            resource.DepthFramebuffers.Clear();

            foreach (var view in resource.FormatViews.Values)
            {
                if (view.Handle != 0)
                {
                    _vk.DestroyImageView(_device, view, null);
                }
            }
            resource.FormatViews.Clear();

            if (resource.Framebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(_device, resource.Framebuffer, null);
            }

            if (resource.RenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.RenderPass, null);
            }

            if (resource.InitialRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.InitialRenderPass, null);
            }

            if (resource.View.Handle != 0)
            {
                _vk.DestroyImageView(_device, resource.View, null);
            }

            foreach (var mipView in resource.MipViews)
            {
                if (mipView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, mipView, null);
                }
            }

            if (resource.Image.Handle != 0)
            {
                _vk.DestroyImage(_device, resource.Image, null);
            }

            if (resource.Memory.Handle != 0)
            {
                _vk.FreeMemory(_device, resource.Memory, null);
            }

        }

        private void DestroyDepthFramebuffer(DepthFramebufferResource resource)
        {
            if (resource.Framebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(_device, resource.Framebuffer, null);
            }

            if (resource.LoadRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.LoadRenderPass, null);
            }
            if (resource.ColorClearRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.ColorClearRenderPass, null);
            }
            if (resource.DepthClearRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.DepthClearRenderPass, null);
            }
            if (resource.BothClearRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.BothClearRenderPass, null);
            }
        }

        private void DestroyGuestDepth(GuestDepthResource resource)
        {
            foreach (var sampleView in resource.SampleViews.Values)
            {
                if (sampleView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, sampleView, null);
                }
            }
            resource.SampleViews.Clear();

            if (resource.View.Handle != 0)
            {
                _vk.DestroyImageView(_device, resource.View, null);
            }
            if (resource.Image.Handle != 0)
            {
                _vk.DestroyImage(_device, resource.Image, null);
            }
            if (resource.Memory.Handle != 0)
            {
                _vk.FreeMemory(_device, resource.Memory, null);
            }
        }

        private bool TryGetOrCreateGuestImageView(
            GuestImageResource resource,
            Format format,
            uint mipLevel,
            uint levelCount,
            uint dstSelect,
            out ImageView view)
        {
            try
            {
                view = GetOrCreateGuestImageView(resource, format, mipLevel, levelCount, dstSelect);
                return true;
            }
            catch (Exception exception)
            {
                view = default;
                TraceVulkanShader(
                    $"vk.texture_alias_view_failed addr=0x{resource.Address:X16} " +
                    $"image_format={resource.Format} view_format={format}: {exception.Message}");
                return false;
            }
        }

        private ImageView GetOrCreateGuestImageView(
            GuestImageResource resource,
            Format format,
            uint mipLevel,
            uint levelCount,
            uint dstSelect = 0xFAC)
        {
            if (mipLevel >= resource.MipLevels)
            {
                throw new InvalidOperationException(
                    $"View mip {mipLevel} exceeds image mip count {resource.MipLevels}.");
            }

            levelCount = Math.Max(levelCount, 1);
            levelCount = Math.Min(levelCount, resource.MipLevels - mipLevel);
            if (format == resource.Format && dstSelect == 0xFAC)
            {
                if (mipLevel == 0 && levelCount == resource.MipLevels)
                {
                    return resource.View;
                }

                if (levelCount == 1)
                {
                    return resource.MipViews[mipLevel];
                }
            }

            if (!IsCompatibleViewFormat(resource.Format, format))
            {
                throw new InvalidOperationException(
                    $"Incompatible image view format {format} for image {resource.Format}.");
            }

            var key = (format, mipLevel, levelCount, dstSelect);
            if (resource.FormatViews.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = resource.Image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = ToVkComponentMapping(dstSelect),
                SubresourceRange = ColorSubresourceRange(mipLevel, levelCount),
            };
            ImageView view;
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out view),
                "vkCreateImageView(guest alias)");
            resource.FormatViews.Add(key, view);
            SetDebugName(
                ObjectType.ImageView,
                view.Handle,
                $"SharpEmu guest 0x{resource.Address:X16} alias {format} mip{mipLevel}+{levelCount}");
            TraceVulkanShader(
                $"vk.texture_alias_view addr=0x{resource.Address:X16} " +
                $"image_format={resource.Format} view_format={format} " +
                $"mip={mipLevel} levels={levelCount} dst=0x{dstSelect:X3}");
            return view;
        }

        private static bool IsCompatibleViewFormat(Format imageFormat, Format viewFormat)
        {
            if (imageFormat == viewFormat)
            {
                return true;
            }

            var imageClass = GetFormatCompatibilityClass(imageFormat);
            return imageClass != 0 && imageClass == GetFormatCompatibilityClass(viewFormat);
        }

        private static uint GetFormatCompatibilityClass(Format format) =>
            format switch
            {
                Format.R8Unorm or
                Format.R8Uint or
                Format.R8Sint => 8,
                Format.R16Sfloat => 16,
                Format.R32Uint or
                Format.R32Sint or
                Format.R32Sfloat or
                Format.R16G16Unorm or
                Format.R16G16Uint or
                Format.R16G16Sint or
                Format.R16G16Sfloat or
                Format.R8G8B8A8Unorm or
                Format.R8G8B8A8Uint or
                Format.R8G8B8A8Sint or
                Format.A2R10G10B10UnormPack32 or
                Format.A2B10G10R10UnormPack32 or
                Format.B10G11R11UfloatPack32 => 32,
                Format.R32G32Uint or
                Format.R32G32Sint or
                Format.R32G32Sfloat or
                Format.R16G16B16A16Unorm or
                Format.R16G16B16A16Uint or
                Format.R16G16B16A16Sint or
                Format.R16G16B16A16Sfloat => 64,
                Format.R32G32B32Sfloat => 96,
                Format.R32G32B32A32Uint or
                Format.R32G32B32A32Sint or
                Format.R32G32B32A32Sfloat => 128,
                _ => 0,
            };

        private void Render(double _)
        {
            if (Volatile.Read(ref _presenterCloseRequested))
            {
                Console.Error.WriteLine("[LOADER][WARN] Vulkan VideoOut closing on host shutdown request.");
                _window.Close();
                return;
            }

            if (!_vulkanReady)
            {
                return;
            }

            // Reuse of a frame slot waits only on that slot's fence, keeping
            // up to MaxFramesInFlight frames pipelined between CPU and GPU.
            var frameSlot = _currentFrameSlot;
            if (!TryWaitFrameSlot(frameSlot, _frameSlotWaitBudgetNs))
            {
                // The GPU is still finishing this slot's previous frame (slow
                // compute backlog). Don't block the macOS main thread — return
                // to the Cocoa event pump so the window keeps handling input
                // (F1 overlay, drag, close) and redrawing. The frame is retried
                // next Render(); the fence signals once the GPU catches up.
                return;
            }

            _presentationCommandBuffer = _frameCommandBuffers[frameSlot];
            _commandBuffer = _presentationCommandBuffer;
            if (!_deviceLost)
            {
                CollectCompletedGuestSubmissions(waitForOldest: false);
            }

            EvictDirtyCachedTextures();
            var completedWork = 0;
            var renderWorkDeadline = _renderWorkBudgetTicks > 0
                ? System.Diagnostics.Stopwatch.GetTimestamp() + _renderWorkBudgetTicks
                : long.MaxValue;
            while (completedWork < MaxGuestWorkPerRender)
            {
                // Never block the macOS main thread waiting for in-flight GPU
                // work to drain. If submission is at capacity (a slow-compute
                // backlog), stop processing and let the event pump run; the
                // remaining queued work is picked up on later frames as the GPU
                // completions free up capacity (collected non-blockingly here).
                CollectCompletedGuestSubmissions(waitForOldest: false);
                if (_pendingGuestSubmissions.Count >= MaxInFlightGuestSubmissions)
                {
                    break;
                }

                if (!TryTakeGuestWork(out var pendingGuestWork))
                {
                    break;
                }

                var work = pendingGuestWork.Work;
                Volatile.Write(ref _activeGuestWorkDescription, work switch
                {
                    VulkanOffscreenGuestDraw draw =>
                        $"draw rt=0x{draw.Target.Address:X16} {draw.Target.Width}x{draw.Target.Height}",
                    VulkanComputeGuestDispatch compute =>
                        $"compute cs=0x{compute.ShaderAddress:X16} " +
                        $"groups={compute.GroupCountX}x{compute.GroupCountY}x{compute.GroupCountZ}",
                    VulkanGuestImageWrite imageWrite =>
                        $"image-write addr=0x{imageWrite.Address:X16}",
                    VulkanOrderedGuestAction orderedAction =>
                        $"ordered-action {orderedAction.DebugName}",
                    _ => work.GetType().Name,
                });

                var traceWork = ShouldTracePresentedGuestImageContentsForDiagnostics();
                var workStart = traceWork ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                if (traceWork && work is VulkanComputeGuestDispatch or VulkanOffscreenGuestDraw)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.render_work_enter #{completedWork} {work.GetType().Name}");
                }
                try
                {
                    switch (work)
                    {
                        case VulkanOffscreenGuestDraw offscreenDraw:
                            ExecuteOffscreenDraw(offscreenDraw);
                            break;
                        case VulkanComputeGuestDispatch computeDispatch:
                            ExecuteComputeDispatch(computeDispatch);
                            break;
                        case VulkanGuestImageWrite guestImageWrite:
                            ExecuteGuestImageWrite(guestImageWrite);
                            break;
                        case VulkanOrderedGuestAction orderedAction:
                            ExecuteOrderedGuestAction(orderedAction);
                            break;
                    }
                }
                finally
                {
                    Volatile.Write(ref _activeGuestWorkDescription, null);
                    CompleteGuestWork(pendingGuestWork);
                }

                if (workStart != 0)
                {
                    var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - workStart)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (elapsedMs > 250.0)
                    {
                        var desc = work switch
                        {
                            VulkanComputeGuestDispatch c => $"compute cs=0x{c.ShaderAddress:X16} groups={c.GroupCountX}x{c.GroupCountY}x{c.GroupCountZ}",
                            VulkanOffscreenGuestDraw d => $"draw rt=0x{d.Target.Address:X16} {d.Target.Width}x{d.Target.Height}",
                            _ => work.GetType().Name,
                        };
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] vk.slow_render_work {elapsedMs:F0}ms: {desc}");
                    }
                }

                completedWork++;

                // Return to the main-thread event pump + present once the
                // per-frame budget is spent; remaining guest work is drained
                // on subsequent Render() calls. Without this a compute-heavy
                // backlog freezes the window (macOS "Not Responding").
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= renderWorkDeadline)
                {
                    break;
                }
            }

            FlushBatchedGuestCommands();

            if (!TryTakePresentation(_presentedSequence, out var presentation))
            {
                if (_splashPresented && !_firstFramePresented)
                {
                    // Silk's macOS Vulkan loop can call Render continuously
                    // despite the requested frame limit. Do not burn a full
                    // core polling a static splash while the guest is still
                    // booting; a 16 ms pause remains responsive and is removed
                    // automatically when the first guest frame is available.
                    Thread.Sleep(16);
                }

                if (ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                    _presentNotTakenLoggedSequence != _presentedSequence)
                {
                    _presentNotTakenLoggedSequence = _presentedSequence;
                    long headRequired = -1, headSeq = -1;
                    int pendingCount;
                    long enqueued, completed;
                    int queued;
                    lock (_gate)
                    {
                        pendingCount = _pendingGuestImagePresentations.Count;
                        if (pendingCount > 0)
                        {
                            var head = _pendingGuestImagePresentations.Peek();
                            headRequired = head.RequiredGuestWorkSequence;
                            headSeq = head.Sequence;
                        }

                        enqueued = _enqueuedGuestWorkSequence;
                        completed = _completedGuestWorkSequence;
                        queued = _pendingGuestWork.Count;
                    }

                    Console.Error.WriteLine(
                        $"[LOADER][WARN] vk.present_not_taken seq={_presentedSequence} " +
                        $"pending={pendingCount} head_seq={headSeq} head_req={headRequired} " +
                        $"enqueued={enqueued} completed={completed} queued={queued} " +
                        "— presentation submitted but its required guest work isn't complete; nothing shown.");
                }

                return;
            }

            if (ShouldTracePresentedGuestImageContentsForDiagnostics())
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.present_taken addr=0x{presentation.GuestImageAddress:X16} " +
                    $"drawKind={presentation.DrawKind} hasPixels={presentation.Pixels is not null} " +
                    $"hasTranslatedDraw={presentation.TranslatedDraw is not null}");
            }

            if (presentation.Pixels is null &&
                presentation.DrawKind != GuestDrawKind.FullscreenBarycentric &&
                presentation.TranslatedDraw is null &&
                presentation.GuestImageAddress == 0)
            {
                return;
            }

            byte[]? pixels = null;
            if (presentation.Pixels is { } sourcePixels)
            {
                pixels = presentation.Width == _extent.Width && presentation.Height == _extent.Height
                    ? sourcePixels
                    : ScaleBgra(
                        sourcePixels,
                        presentation.Width,
                        presentation.Height,
                        _extent.Width,
                        _extent.Height);
                if ((ulong)pixels.Length > _stagingSize)
                {
                    return;
                }
            }

            TranslatedDrawResources? translatedResources = null;
            GuestImageResource? presentedGuestImage = null;
            if (presentation.GuestImageAddress != 0 &&
                (!_guestImages.TryGetValue(
                    presentation.GuestImageAddress,
                    out presentedGuestImage) ||
                 !presentedGuestImage.Initialized))
            {
                if (ShouldTracePresentedGuestImageContentsForDiagnostics())
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] vk.present_dropped addr=0x{presentation.GuestImageAddress:X16} " +
                        $"found={(presentedGuestImage is not null)} " +
                        $"initialized={(presentedGuestImage?.Initialized ?? false)} " +
                        $"— no swapchain present this frame (black).");
                }

                return;
            }
            if (presentedGuestImage is not null)
            {
                _directPresentationCount++;
                if (ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                    (_directPresentationCount is 1 or 30 or 120 ||
                     _directPresentationCount % 600 == 0))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.present_sample frame={_directPresentationCount} " +
                        $"addr=0x{presentedGuestImage.Address:X16}");
                    TraceGuestImageContents(presentedGuestImage);
                }
            }

            if (presentation.TranslatedDraw is { } translatedDraw)
            {
                try
                {
                    translatedResources = CreateTranslatedDrawResources(
                        translatedDraw,
                        _renderPass,
                        _extent,
                        _swapchainFormat);
                    if (ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                        !_firstGuestDrawPresented &&
                        translatedResources.Textures is
                        [
                        { GuestImage: { } guestImage },
                        ] &&
                        _tracedGuestImageContents.Add(guestImage.Address))
                    {
                        TraceGuestImageContents(guestImage);
                    }
                }
                catch (Exception exception)
                {
                    _presentedSequence = presentation.Sequence;
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] Vulkan VideoOut translated draw setup failed: {exception.Message}");
                    return;
                }
            }

            uint imageIndex;
            var acquireResult = _swapchainApi.AcquireNextImage(
                _device,
                _swapchain,
                ulong.MaxValue,
                _frameImageAvailable[frameSlot],
                default,
                &imageIndex);
            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                RecreateSwapchainResources("vkAcquireNextImageKHR", acquireResult);
                if (translatedResources is not null)
                {
                    DestroyTranslatedDrawResources(translatedResources);
                }

                return;
            }

            CheckSwapchainResult(acquireResult, "vkAcquireNextImageKHR");
            var recreateAfterPresent = acquireResult == Result.SuboptimalKhr;

            if (pixels is not null)
            {
                // The staging buffer is shared across frame slots; a CPU
                // pixel upload (splash / host frames) degrades to serial
                // presentation rather than corrupting an in-flight copy.
                WaitAllFrameSlots();
                void* mapped;
                Check(
                    _vk.MapMemory(_device, _stagingMemory, 0, (ulong)pixels.Length, 0, &mapped),
                    "vkMapMemory");
                fixed (byte* source = pixels)
                {
                    System.Buffer.MemoryCopy(source, mapped, pixels.Length, pixels.Length);
                }
                _vk.UnmapMemory(_device, _stagingMemory);
            }

            Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(_vk.BeginCommandBuffer(_commandBuffer, &beginInfo), "vkBeginCommandBuffer");

            PipelineStageFlags waitStage;
            if (pixels is not null)
            {
                RecordUpload(imageIndex);
                waitStage = PipelineStageFlags.TransferBit;
            }
            else if (presentation.DrawKind == GuestDrawKind.FullscreenBarycentric)
            {
                var clearValue = default(ClearValue);
                var renderPassInfo = new RenderPassBeginInfo
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = _renderPass,
                    Framebuffer = _framebuffers[imageIndex],
                    RenderArea = new Rect2D(new Offset2D(0, 0), _extent),
                    ClearValueCount = 1,
                    PClearValues = &clearValue,
                };
                _vk.CmdBeginRenderPass(
                    _commandBuffer,
                    &renderPassInfo,
                    SubpassContents.Inline);
                _vk.CmdBindPipeline(
                    _commandBuffer,
                    PipelineBindPoint.Graphics,
                    _barycentricPipeline);
                _vk.CmdDraw(_commandBuffer, 3, 1, 0, 0);
                _vk.CmdEndRenderPass(_commandBuffer);
                waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
            }
            else if (presentedGuestImage is not null)
            {
                RecordGuestImageBlit(imageIndex, presentedGuestImage);
                waitStage = PipelineStageFlags.TransferBit;
            }
            else if (translatedResources is not null)
            {
                RecordTranslatedDraw(imageIndex, translatedResources);
                waitStage = PipelineStageFlags.AllCommandsBit;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported translated guest draw: {presentation.DrawKind}.");
            }

            if (PerfOverlay.Enabled)
            {
                RecordOverlayBlit(imageIndex, frameSlot);
            }

            Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

            var imageAvailable = _frameImageAvailable[frameSlot];
            var commandBuffer = _commandBuffer;
            var renderFinished = _renderFinishedPerImage[imageIndex];
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &imageAvailable,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &renderFinished,
            };
            Check(
                _vk.QueueSubmit(_queue, 1, &submitInfo, _frameFences[frameSlot]),
                "vkQueueSubmit");
            _submitTimeline++;
            _frameTimelines[frameSlot] = _submitTimeline;
            _frameFencePending[frameSlot] = true;
            _frameTranslatedResources[frameSlot] = translatedResources;
            if (translatedResources is not null)
            {
                // CPU-side layout bookkeeping only; later command buffers are
                // recorded after this submission, so queue order makes the
                // flags valid before any dependent GPU work runs.
                MarkSampledImagesInitialized(translatedResources);
                MarkStorageImagesInitialized(translatedResources);
            }

            var swapchain = _swapchain;
            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderFinished,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex,
            };
            var presentResult = _swapchainApi.QueuePresent(_queue, &presentInfo);
            if (presentResult == Result.ErrorOutOfDateKhr)
            {
                // The submitted frame still executes; RecreateSwapchainResources
                // drains it (and every frame slot) before destroying anything.
                RecreateSwapchainResources("vkQueuePresentKHR", presentResult);
                return;
            }

            CheckSwapchainResult(presentResult, "vkQueuePresentKHR");
            recreateAfterPresent |= presentResult == Result.SuboptimalKhr;
            VideoOutExports.ReportPresentedFrame();
            PerfOverlay.RecordPresent();
            if (_swapchainReadbackPending || !_pendingAliasImageDumps.IsEmpty)
            {
                // Diagnostics read back GPU memory and need this frame done.
                WaitFrameSlot(frameSlot);
                if (_swapchainReadbackPending)
                {
                    TraceSwapchainReadback();
                }

                while (_pendingAliasImageDumps.TryDequeue(out var aliasImage))
                {
                    TraceGuestImageContents(aliasImage);
                }
            }

            CollectCompletedGuestSubmissions(waitForOldest: false);
            _imageInitialized[imageIndex] = true;
            _currentFrameSlot = (frameSlot + 1) % MaxFramesInFlight;
            _presentedSequence = presentation.Sequence;
            if (presentation.IsSplash && !_splashPresented)
            {
                _splashPresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented splash: " +
                    $"{presentation.Width}x{presentation.Height}");
            }
            else if (!presentation.IsSplash && !_firstFramePresented)
            {
                _firstFramePresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented first frame: " +
                    $"{presentation.Width}x{presentation.Height}");
            }

            if (pixels is null && !_firstGuestDrawPresented)
            {
                _firstGuestDrawPresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented guest frame: " +
                    (presentedGuestImage is not null
                        ? $"image=0x{presentedGuestImage.Address:X16} " +
                          $"{presentedGuestImage.Width}x{presentedGuestImage.Height}"
                        : presentation.TranslatedDraw is null
                        ? $"{presentation.DrawKind}"
                        : $"shader textures={presentation.TranslatedDraw.Textures.Count}"));
            }

            if (recreateAfterPresent)
            {
                RecreateSwapchainResources("present suboptimal", Result.SuboptimalKhr);
            }
        }

        private void TraceGuestImageContents(GuestImageResource image)
        {
            var bytesPerPixel = GetReadbackBytesPerPixel(image.Format);
            if (bytesPerPixel == 0)
            {
                Console.Error.WriteLine(
                    "[LOADER][TRACE] " +
                    $"vk.guest_image addr=0x{image.Address:X16} " +
                    $"format={image.Format} readback=unsupported");
                return;
            }

            var byteCount = checked((ulong)image.Width * image.Height * bytesPerPixel);
            var buffer = CreateBuffer(
                byteCount,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out var memory);
            try
            {
                Check(
                    _vk.ResetCommandBuffer(_commandBuffer, 0),
                    "vkResetCommandBuffer(guest readback)");
                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(
                    _vk.BeginCommandBuffer(_commandBuffer, &beginInfo),
                    "vkBeginCommandBuffer(guest readback)");

                var toTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderReadBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.FragmentShaderBit |
                    PipelineStageFlags.ComputeShaderBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransfer);

                var region = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(image.Width, image.Height, 1),
                };
                _vk.CmdCopyImageToBuffer(
                    _commandBuffer,
                    image.Image,
                    ImageLayout.TransferSrcOptimal,
                    buffer,
                    1,
                    &region);

                var toShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferReadBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferSrcOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit |
                    PipelineStageFlags.ComputeShaderBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toShaderRead);

                Check(
                    _vk.EndCommandBuffer(_commandBuffer),
                    "vkEndCommandBuffer(guest readback)");
                var commandBuffer = _commandBuffer;
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, default),
                    "vkQueueSubmit(guest readback)");
                Check(
                    _vk.QueueWaitIdle(_queue),
                    "vkQueueWaitIdle(guest readback)");

                void* mapped;
                Check(
                    _vk.MapMemory(_device, memory, 0, byteCount, 0, &mapped),
                    "vkMapMemory(guest readback)");
                try
                {
                    var bytes = new ReadOnlySpan<byte>(mapped, checked((int)byteCount));
                    if (GuestImageTraceInterval() is not null && bytesPerPixel == 4)
                    {
                        long r = 0, g = 0, b = 0, a = 0, samples = 0;
                        for (var offset = 0; offset + 4 <= bytes.Length; offset += 4 * 251)
                        {
                            r += bytes[offset];
                            g += bytes[offset + 1];
                            b += bytes[offset + 2];
                            a += bytes[offset + 3];
                            samples++;
                        }

                        if (samples > 0)
                        {
                            Console.Error.WriteLine(
                                $"[RB] addr=0x{image.Address:X} mean={r / samples},{g / samples},{b / samples},A{a / samples}");
                        }

                        if (++_intervalReadbackCount % 25 == 0)
                        {
                            DumpGuestImageBytes(image, bytes);
                        }

                        return;
                    }

                    var nonzeroBytes = 0L;
                    ulong hash = 14695981039346656037UL;
                    foreach (var value in bytes)
                    {
                        nonzeroBytes += value == 0 ? 0 : 1;
                        hash = (hash ^ value) * 1099511628211UL;
                    }

                    var nonblackPixels = CountNonblackPixels(
                        bytes,
                        image.Format,
                        bytesPerPixel);
                    var centerOffset = checked(
                        ((int)(image.Height / 2) * (int)image.Width +
                         (int)(image.Width / 2)) *
                        (int)bytesPerPixel);
                    var center = Convert.ToHexString(
                        bytes.Slice(centerOffset, (int)bytesPerPixel));
                    Console.Error.WriteLine(
                        "[LOADER][TRACE] " +
                        $"vk.guest_image addr=0x{image.Address:X16} " +
                        $"size={image.Width}x{image.Height} format={image.Format} " +
                        $"nonzero_bytes={nonzeroBytes}/{byteCount} " +
                        $"nonblack_pixels={nonblackPixels}/{(ulong)image.Width * image.Height} " +
                        $"center={center} hash=0x{hash:X16}");
                    DumpGuestImageBytes(image, bytes);
                }
                finally
                {
                    _vk.UnmapMemory(_device, memory);
                }
            }
            finally
            {
                _vk.DestroyBuffer(_device, buffer, null);
                _vk.FreeMemory(_device, memory, null);
            }
        }

        private static void DumpGuestImageBytes(
            GuestImageResource image,
            ReadOnlySpan<byte> bytes)
        {
            var directory =
                Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_DUMP_DIR");
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var sequence = Interlocked.Increment(ref _guestImageDumpSequence);
            var path = Path.Combine(
                directory,
                $"{sequence:D4}-0x{image.Address:X16}-{image.Width}x{image.Height}-{image.Format}.rgba");
            File.WriteAllBytes(path, bytes.ToArray());
        }

        // Metal cannot blend into integer render targets or 32-bit-per-channel
        // float targets (unsupported on Apple-family GPUs). Enabling blend on
        // one makes vkCreateGraphicsPipelines fail with ErrorInitializationFailed
        // (and trips a Metal "not blendable" validation assertion), so the draw
        // is silently dropped. Force blend off for those; blending on an integer
        // target is meaningless on real hardware anyway.
        private static bool IsBlendableFormat(Format format) =>
            format switch
            {
                Format.R8Uint or Format.R8Sint or
                Format.R8G8B8A8Uint or Format.R8G8B8A8Sint or
                Format.R16G16Uint or Format.R16G16Sint or
                Format.R16G16B16A16Uint or Format.R16G16B16A16Sint or
                Format.R32Uint or Format.R32Sint or
                Format.R32G32Uint or Format.R32G32Sint or
                Format.R32G32B32A32Uint or Format.R32G32B32A32Sint or
                Format.R32Sfloat or
                Format.R32G32Sfloat or
                Format.R32G32B32A32Sfloat => false,
                _ => true,
            };

        private static uint GetReadbackBytesPerPixel(Format format) =>
            format switch
            {
                Format.R8Unorm or
                Format.R8Uint or
                Format.R8Sint => 1,
                Format.R32Uint or
                Format.R32Sint or
                Format.R32Sfloat or
                Format.R16G16Uint or
                Format.R16G16Sint or
                Format.R16G16Sfloat or
                Format.R8G8B8A8Uint or
                Format.R8G8B8A8Sint or
                Format.R8G8B8A8Unorm or
                Format.A2R10G10B10UnormPack32 or
                Format.A2B10G10R10UnormPack32 or
                Format.B10G11R11UfloatPack32 => 4,
                Format.R16G16B16A16Uint or
                Format.R16G16B16A16Sint or
                Format.R16G16B16A16Sfloat => 8,
                Format.R32G32Uint or
                Format.R32G32Sint or
                Format.R32G32Sfloat => 8,
                Format.R32G32B32A32Uint or
                Format.R32G32B32A32Sint or
                Format.R32G32B32A32Sfloat => 16,
                _ => 0,
            };

        private static long CountNonblackPixels(
            ReadOnlySpan<byte> bytes,
            Format format,
            uint bytesPerPixel)
        {
            var count = 0L;
            for (var offset = 0; offset < bytes.Length; offset += (int)bytesPerPixel)
            {
                var pixel = bytes.Slice(offset, (int)bytesPerPixel);
                var hasColor = format switch
                {
                    Format.A2R10G10B10UnormPack32 or
                    Format.A2B10G10R10UnormPack32 =>
                        (BitConverter.ToUInt32(pixel) & 0x3FFFFFFFu) != 0,
                    Format.R8G8B8A8Uint or
                    Format.R8G8B8A8Sint or
                    Format.R8G8B8A8Unorm =>
                        pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0,
                    Format.R16G16B16A16Uint or
                    Format.R16G16B16A16Sint or
                    Format.R16G16B16A16Sfloat =>
                        pixel[..6].IndexOfAnyExcept((byte)0) >= 0,
                    _ => pixel.IndexOfAnyExcept((byte)0) >= 0,
                };
                count += hasColor ? 1 : 0;
            }

            return count;
        }

        private void RecordTranslatedDraw(uint imageIndex, TranslatedDrawResources resources)
        {
            BeginDebugLabel(_commandBuffer, "SharpEmu swapchain draw");
            RecordGlobalBufferVisibilityBarrier(
                _commandBuffer,
                resources,
                PipelineStageFlags.VertexShaderBit |
                PipelineStageFlags.FragmentShaderBit);
            RecordTextureUploads(resources, PipelineStageFlags.FragmentShaderBit);
            RecordStorageImagesForWrite(resources, PipelineStageFlags.FragmentShaderBit);
            RecordTranslatedGraphicsPass(
                resources,
                _renderPass,
                _framebuffers[imageIndex],
                _extent);
            RecordStorageImagesForRead(resources, PipelineStageFlags.FragmentShaderBit);
            EndDebugLabel(_commandBuffer);
        }

        private void RecordTextureUploads(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            foreach (var texture in resources.Textures)
            {
                if (texture.GuestDepth is { } depth)
                {
                    RecordGuestDepthForSampling(depth, shaderStage);
                }

                if (!texture.NeedsUpload)
                {
                    continue;
                }

                var toTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransfer);

                var copyRegion = new BufferImageCopy
                {
                    BufferRowLength = texture.RowLength > texture.Width
                        ? texture.RowLength
                        : 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(texture.Width, texture.Height, 1),
                };
                _vk.CmdCopyBufferToImage(
                    _commandBuffer,
                    texture.StagingBuffer,
                    texture.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copyRegion);

                var toShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toShaderRead);
                if (texture.Cached)
                {
                    // The queue executes command buffers in submission order,
                    // so once this upload is recorded every later draw can
                    // reuse the image without restaging it.
                    texture.NeedsUpload = false;
                }
            }
        }

        private void RecordGuestDepthForSampling(
            GuestDepthResource depth,
            PipelineStageFlags shaderStage)
        {
            if (depth.Layout == ImageLayout.ShaderReadOnlyOptimal)
            {
                return;
            }

            if (!depth.Initialized)
            {
                var depthRange = new ImageSubresourceRange(
                    ImageAspectFlags.DepthBit,
                    0,
                    1,
                    0,
                    1);
                var toTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = depth.Layout,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = depth.Image,
                    SubresourceRange = depthRange,
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransfer);
                var clearValue = new ClearDepthStencilValue(depth.ClearDepth, 0);
                _vk.CmdClearDepthStencilImage(
                    _commandBuffer,
                    depth.Image,
                    ImageLayout.TransferDstOptimal,
                    &clearValue,
                    1,
                    &depthRange);
                depth.Initialized = true;
                depth.Layout = ImageLayout.TransferDstOptimal;
            }

            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = depth.Layout == ImageLayout.TransferDstOptimal
                    ? AccessFlags.TransferWriteBit
                    : AccessFlags.DepthStencilAttachmentWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = depth.Layout,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = depth.Image,
                SubresourceRange = new ImageSubresourceRange(
                    ImageAspectFlags.DepthBit,
                    0,
                    1,
                    0,
                    1),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                depth.Layout == ImageLayout.TransferDstOptimal
                    ? PipelineStageFlags.TransferBit
                    : PipelineStageFlags.LateFragmentTestsBit,
                shaderStage,
                0,
                0,
                null,
                0,
                null,
                1,
                &barrier);
            depth.Layout = ImageLayout.ShaderReadOnlyOptimal;
        }

        private void RecordRenderTargetFeedbackSnapshots(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            foreach (var texture in resources.Textures)
            {
                if (texture.FeedbackSource is not { } source)
                {
                    continue;
                }

                // Initialize every destination mip to a deterministic zero.
                // Render-target writes currently populate mip 0; leaving the
                // remaining sampled mips undefined turns guest LOD selection
                // into driver-dependent colored garbage.
                var destinationToTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(0, source.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToTransfer);

                var clearValue = new ClearColorValue(0f, 0f, 0f, 0f);
                // Avoid overlapping a clear and copy on mip 0: without an
                // intervening dependency two transfer writes to one
                // subresource are not ordered merely because they were
                // recorded in that order. Initialized sources overwrite mip
                // 0 directly and clear only the otherwise undefined tail.
                var clearBaseMip = source.Initialized ? 1u : 0u;
                if (clearBaseMip < source.MipLevels)
                {
                    var destinationRange = ColorSubresourceRange(
                        clearBaseMip,
                        source.MipLevels - clearBaseMip);
                    _vk.CmdClearColorImage(
                        _commandBuffer,
                        texture.Image,
                        ImageLayout.TransferDstOptimal,
                        &clearValue,
                        1,
                        &destinationRange);
                }

                if (source.Initialized)
                {
                    var sourceToTransfer = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.ShaderReadBit,
                        DstAccessMask = AccessFlags.TransferReadBit,
                        OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                        NewLayout = ImageLayout.TransferSrcOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = source.Image,
                        // Only mip 0 has defined render-target contents.
                        SubresourceRange = ColorSubresourceRange(),
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        shaderStage |
                        PipelineStageFlags.ColorAttachmentOutputBit,
                        PipelineStageFlags.TransferBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &sourceToTransfer);

                    var copy = new ImageCopy
                    {
                        SrcSubresource = new ImageSubresourceLayers(
                            ImageAspectFlags.ColorBit,
                            0,
                            0,
                            1),
                        DstSubresource = new ImageSubresourceLayers(
                            ImageAspectFlags.ColorBit,
                            0,
                            0,
                            1),
                        Extent = new Extent3D(source.Width, source.Height, 1),
                    };
                    _vk.CmdCopyImage(
                        _commandBuffer,
                        source.Image,
                        ImageLayout.TransferSrcOptimal,
                        texture.Image,
                        ImageLayout.TransferDstOptimal,
                        1,
                        &copy);

                    var sourceToShaderRead = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.TransferReadBit,
                        DstAccessMask = AccessFlags.ShaderReadBit,
                        OldLayout = ImageLayout.TransferSrcOptimal,
                        NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = source.Image,
                        SubresourceRange = ColorSubresourceRange(),
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        PipelineStageFlags.TransferBit,
                        shaderStage,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &sourceToShaderRead);
                }

                var destinationToShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(0, source.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToShaderRead);

                TraceVulkanShader(
                    $"vk.feedback_snapshot_copy addr=0x{source.Address:X16} " +
                    $"size={source.Width}x{source.Height} initialized={source.Initialized}");
            }
        }

        private void RecordDepthFeedbackSnapshots(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            foreach (var texture in resources.Textures)
            {
                if (texture.DepthFeedbackSource is not { } source)
                {
                    continue;
                }

                var depthRange = new ImageSubresourceRange(
                    ImageAspectFlags.DepthBit,
                    0,
                    1,
                    0,
                    1);
                var destinationToTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = depthRange,
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToTransfer);

                if (source.Initialized)
                {
                    var sourceToTransfer = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = source.Layout == ImageLayout.ShaderReadOnlyOptimal
                            ? AccessFlags.ShaderReadBit
                            : AccessFlags.DepthStencilAttachmentWriteBit,
                        DstAccessMask = AccessFlags.TransferReadBit,
                        OldLayout = source.Layout,
                        NewLayout = ImageLayout.TransferSrcOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = source.Image,
                        SubresourceRange = depthRange,
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        shaderStage |
                        PipelineStageFlags.EarlyFragmentTestsBit |
                        PipelineStageFlags.LateFragmentTestsBit,
                        PipelineStageFlags.TransferBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &sourceToTransfer);
                    var copy = new ImageCopy
                    {
                        SrcSubresource = new ImageSubresourceLayers(
                            ImageAspectFlags.DepthBit,
                            0,
                            0,
                            1),
                        DstSubresource = new ImageSubresourceLayers(
                            ImageAspectFlags.DepthBit,
                            0,
                            0,
                            1),
                        Extent = new Extent3D(source.Width, source.Height, 1),
                    };
                    _vk.CmdCopyImage(
                        _commandBuffer,
                        source.Image,
                        ImageLayout.TransferSrcOptimal,
                        texture.Image,
                        ImageLayout.TransferDstOptimal,
                        1,
                        &copy);

                    var sourceToAttachment = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.TransferReadBit,
                        DstAccessMask =
                            AccessFlags.DepthStencilAttachmentReadBit |
                            AccessFlags.DepthStencilAttachmentWriteBit,
                        OldLayout = ImageLayout.TransferSrcOptimal,
                        NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = source.Image,
                        SubresourceRange = depthRange,
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        PipelineStageFlags.TransferBit,
                        PipelineStageFlags.EarlyFragmentTestsBit |
                        PipelineStageFlags.LateFragmentTestsBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &sourceToAttachment);
                    source.Layout = ImageLayout.DepthStencilAttachmentOptimal;
                }
                else
                {
                    var clearValue = new ClearDepthStencilValue(source.ClearDepth, 0);
                    _vk.CmdClearDepthStencilImage(
                        _commandBuffer,
                        texture.Image,
                        ImageLayout.TransferDstOptimal,
                        &clearValue,
                        1,
                        &depthRange);
                }

                var destinationToShader = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = depthRange,
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToShader);
            }
        }

        private void RecordStorageImagesForWrite(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            var transitioned = new HashSet<GuestImageResource>();
            foreach (var texture in resources.Textures)
            {
                if (!texture.IsStorage ||
                    texture.GuestImage is not { } guestImage ||
                    !transitioned.Add(guestImage))
                {
                    continue;
                }

                var barrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask =
                        guestImage.Initialized || guestImage.InitialUploadPending
                        ? AccessFlags.ShaderReadBit
                        : 0,
                    DstAccessMask =
                        AccessFlags.ShaderReadBit |
                        AccessFlags.ShaderWriteBit,
                    OldLayout =
                        guestImage.Initialized || guestImage.InitialUploadPending
                        ? ImageLayout.ShaderReadOnlyOptimal
                        : ImageLayout.Undefined,
                    NewLayout = ImageLayout.General,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = guestImage.Image,
                    SubresourceRange = ColorSubresourceRange(0, guestImage.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    guestImage.Initialized || guestImage.InitialUploadPending
                        ? shaderStage
                        : PipelineStageFlags.TopOfPipeBit,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);
            }
        }

        private void RecordStorageImagesForRead(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            var transitioned = new HashSet<GuestImageResource>();
            foreach (var texture in resources.Textures)
            {
                if (!texture.IsStorage ||
                    texture.GuestImage is not { } guestImage ||
                    !transitioned.Add(guestImage))
                {
                    continue;
                }

                var barrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask =
                        AccessFlags.ShaderReadBit |
                        AccessFlags.ShaderWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.General,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = guestImage.Image,
                    SubresourceRange = ColorSubresourceRange(0, guestImage.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    shaderStage,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);
            }
        }

        private void MarkStorageImagesInitialized(
            TranslatedDrawResources resources,
            bool traceContents = true)
        {
            List<GuestImageResource>? traceImages = null;
            lock (_gate)
            {
                foreach (var texture in resources.Textures)
                {
                    if (!texture.IsStorage ||
                        texture.Address == 0 ||
                        texture.GuestImage is not { } guestImage)
                    {
                        continue;
                    }

                    guestImage.Initialized = true;
                    guestImage.InitialUploadPending = false;
                    if (guestImage.GuestFormat != 0)
                    {
                        _availableGuestImages[texture.Address] = guestImage.GuestFormat;
                    }

                    if (traceContents &&
                        ShouldTraceGuestImageContents(guestImage))
                    {
                        traceImages ??= [];
                        traceImages.Add(guestImage);
                    }
                }
            }

            if (traceImages is null)
            {
                return;
            }

            foreach (var image in traceImages)
            {
                TraceGuestImageContents(image);
            }
        }

        private static void MarkSampledImagesInitialized(
            TranslatedDrawResources resources)
        {
            lock (_gate)
            {
                foreach (var texture in resources.Textures)
                {
                    if (!texture.NeedsUpload ||
                        texture.IsStorage ||
                        texture.Address == 0 ||
                        texture.GuestImage is not { } guestImage)
                    {
                        continue;
                    }

                    guestImage.Initialized = true;
                    guestImage.InitialUploadPending = false;
                }
            }
        }

        private bool ShouldTraceGuestImageContents(GuestImageResource image)
        {
            if (image.Address == 0)
            {
                return false;
            }

            var addressMatched = ShouldTraceGuestImageAddressForDiagnostics(image.Address);
            var broadTrace =
                ShouldTraceGuestImageContentsForDiagnostics() &&
                image.Width >= 1280 &&
                image.Height >= 720;
            if (GuestImageTraceInterval() is { } interval)
            {
                if (image.Width < 1280 || image.Height < 720)
                {
                    return false;
                }

                _globalGuestImageDrawCount++;
                if (_globalGuestImageDrawCount < GuestImageTraceStartAfter() ||
                    _intervalReadbackCount > 3000)
                {
                    return false;
                }

                var count = _guestImageTraceCounts.TryGetValue(image.Address, out var previous)
                    ? previous + 1
                    : 1;
                _guestImageTraceCounts[image.Address] = count;
                return count % interval == 0;
            }

            return (addressMatched || broadTrace) &&
                   _tracedGuestImageContents.Add(image.Address);
        }

        private readonly Dictionary<ulong, long> _guestImageTraceCounts = new();
        private long _globalGuestImageDrawCount;
        private long _intervalReadbackCount;

        private static long? _cachedGuestImageTraceInterval = long.MinValue;
        private static long _cachedGuestImageTraceStartAfter;

        // SHARPEMU_TRACE_GUEST_IMAGES=every:N[@M] — read back 1280x720+ guest
        // images every Nth draw into each, starting after M total such draws.
        private static long? GuestImageTraceInterval()
        {
            if (_cachedGuestImageTraceInterval == long.MinValue)
            {
                var mode = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
                long? interval = null;
                if (mode is not null && mode.StartsWith("every:", StringComparison.Ordinal))
                {
                    var spec = mode["every:".Length..];
                    var at = spec.IndexOf('@');
                    var intervalText = at < 0 ? spec : spec[..at];
                    if (long.TryParse(intervalText, out var parsed) && parsed > 0)
                    {
                        interval = parsed;
                    }

                    if (at >= 0 && long.TryParse(spec[(at + 1)..], out var after) && after > 0)
                    {
                        _cachedGuestImageTraceStartAfter = after;
                    }
                }

                _cachedGuestImageTraceInterval = interval;
            }

            return _cachedGuestImageTraceInterval;
        }

        private static long GuestImageTraceStartAfter()
        {
            _ = GuestImageTraceInterval();
            return _cachedGuestImageTraceStartAfter;
        }

        // Diagnostics toggles are read once: these run per draw / per cached
        // texture hit, and env lookups plus string parsing are far too
        // expensive there (and non-trivially so under Rosetta 2).
        private static readonly string? _traceGuestImagesMode =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
        private static readonly bool _traceGuestImagesEnabled =
            string.Equals(_traceGuestImagesMode, "1", StringComparison.Ordinal);
        private static readonly bool _tracePresentedGuestImagesEnabled =
            _traceGuestImagesEnabled ||
            string.Equals(_traceGuestImagesMode, "present", StringComparison.OrdinalIgnoreCase);
        private static readonly bool _traceScanoutSourcesEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_SCANOUT_SOURCES"),
                "1",
                StringComparison.Ordinal);
        private static readonly bool _traceFirst4kUfloatSourcesEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_FIRST_4K_UFLOAT_SOURCES"),
                "1",
                StringComparison.Ordinal);
        private static readonly bool _traceBc7VertexInputsEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_BC7_VERTEX_INPUTS"),
                "1",
                StringComparison.Ordinal);
        private static readonly int _trace4kUfloatSourcesOrdinal =
            int.TryParse(
                Environment.GetEnvironmentVariable(
                    "SHARPEMU_TRACE_4K_UFLOAT_SOURCES_ORDINAL"),
                out var trace4kOrdinal) && trace4kOrdinal > 0
                ? trace4kOrdinal
                : 1;
        private static readonly bool _traceVulkanResourcesEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_VK_RESOURCES"),
                "1",
                StringComparison.Ordinal);
        private static readonly bool _traceVulkanShaderEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"),
                "1",
                StringComparison.Ordinal) ||
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC_SHADER"),
                "1",
                StringComparison.Ordinal);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            string,
            (bool Wildcard, ulong[] Addresses)> _cachedAddressLists = new();

        private static bool ShouldTraceGuestImageContentsForDiagnostics() =>
            _traceGuestImagesEnabled;

        private void TraceSelectedDrawVertexInputs(VulkanOffscreenGuestDraw work)
        {
            var isSelectedBc7Draw =
                _traceBc7VertexInputsEnabled &&
                work.Draw.Textures.Any(static texture =>
                    texture.Format == 182 &&
                    texture.Width == 3840 &&
                    texture.Height == 2160);
            var isScanout =
                _traceScanoutSourcesEnabled &&
                (work.Target.Address == 0x000000EFC0000000UL ||
                 work.Target.Address == 0x000000EFC2000000UL);
            var is4kUfloat =
                _traceFirst4kUfloatSourcesEnabled &&
                work.Target.Width == 3840 &&
                work.Target.Height == 2160 &&
                work.Target.Format == 6;
            if (isSelectedBc7Draw)
            {
                if (Interlocked.Increment(ref _tracedSelectedVertexInputs) > 16)
                {
                    return;
                }
            }
            else if ((!isScanout && !is4kUfloat) || _tracedFirst4kVertexInputs)
            {
                return;
            }

            _tracedFirst4kVertexInputs |= !isSelectedBc7Draw;
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.draw_vertex_shader " +
                $"target=0x{work.Target.Address:X16} " +
                $"size={work.Target.Width}x{work.Target.Height} fmt={work.Target.Format} " +
                $"bc7={(isSelectedBc7Draw ? 1 : 0)}");
            var indices = new List<uint>();
            if (work.Draw.IndexBuffer is { Length: > 0 } indexBuffer)
            {
                var indexSize = indexBuffer.Is32Bit ? 4 : 2;
                var count = Math.Min(indexBuffer.Length / indexSize, 16);
                for (var index = 0; index < count; index++)
                {
                    var offset = index * indexSize;
                    indices.Add(indexBuffer.Is32Bit
                        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                            indexBuffer.Data.AsSpan(offset, indexSize))
                        : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
                            indexBuffer.Data.AsSpan(offset, indexSize)));
                }
            }
            else
            {
                for (uint index = 0; index < Math.Min(work.Draw.VertexCount, 16u); index++)
                {
                    indices.Add(index);
                }
            }

            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.draw_vertex_indices " +
                $"values=[{string.Join(',', indices)}]");
            foreach (var buffer in work.Draw.VertexBuffers)
            {
                var stride = buffer.Stride == 0
                    ? Math.Max(buffer.ComponentCount, 1) * sizeof(float)
                    : buffer.Stride;
                foreach (var vertexIndex in indices.Distinct())
                {
                    var offset = (ulong)buffer.OffsetBytes + vertexIndex * stride;
                    if (offset >= (ulong)buffer.Length)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] vk.draw_vertex_input loc={buffer.Location} " +
                            $"index={vertexIndex} offset={offset} bytes=oob/{buffer.Length}");
                        continue;
                    }

                    var length = (int)Math.Min(16UL, (ulong)buffer.Length - offset);
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.draw_vertex_input loc={buffer.Location} " +
                        $"index={vertexIndex} base=0x{buffer.BaseAddress:X16} " +
                        $"stride={stride} offset={offset} " +
                        $"fmt={buffer.DataFormat}/num={buffer.NumberFormat}x" +
                        $"{buffer.ComponentCount} bytes=" +
                        Convert.ToHexString(buffer.Data.AsSpan((int)offset, length)));
                }
            }

            for (var index = 0; index < work.Draw.GlobalMemoryBuffers.Count; index++)
            {
                var buffer = work.Draw.GlobalMemoryBuffers[index];
                var length = Math.Min(buffer.Length, 64);
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.draw_global_input index={index} " +
                    $"base=0x{buffer.BaseAddress:X16} bytes={buffer.Length} " +
                    $"writable={buffer.Writable} head=" +
                    Convert.ToHexString(buffer.Data.AsSpan(0, length)));
            }
        }

        private bool TraceSelectedDrawSources(
            VulkanOffscreenGuestDraw work,
            TranslatedDrawResources resources)
        {
            var isScanout =
                _traceScanoutSourcesEnabled &&
                !_tracedScanoutSources &&
                (work.Target.Address == 0x000000EFC0000000UL ||
                 work.Target.Address == 0x000000EFC2000000UL);
            var is4kUfloat =
                _traceFirst4kUfloatSourcesEnabled &&
                !_tracedFirst4kUfloatSources &&
                work.Target.Width == 3840 &&
                work.Target.Height == 2160 &&
                work.Target.Format == 6;
            var isFirst4kUfloat =
                is4kUfloat &&
                ++_seen4kUfloatSources == _trace4kUfloatSourcesOrdinal;
            if (!isScanout && !isFirst4kUfloat)
            {
                return false;
            }

            var sourceImages = resources.Textures
                .Select(static texture => texture.GuestImage)
                // This path is diagnostics-only. Include small temporal/LUT
                // sources too: a full-resolution pass can be blank because a
                // 240x135 producer is missing even when every 4K input is
                // populated.
                .Where(static image => image is not null)
                .Cast<GuestImageResource>()
                .Distinct()
                .ToArray();
            if (sourceImages.Length == 0)
            {
                return false;
            }

            _tracedScanoutSources |= isScanout;
            _tracedFirst4kUfloatSources |= isFirst4kUfloat;
            WaitForAllGuestSubmissionsForCpuVisibility();
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.draw_source_readback " +
                $"kind={(isScanout ? "scanout" : "first-4k-ufloat")} " +
                $"target=0x{work.Target.Address:X16} sources={sourceImages.Length}");
            for (var index = 0; index < resources.Textures.Length; index++)
            {
                var texture = resources.Textures[index];
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.draw_texture_binding index={index} " +
                    $"addr=0x{texture.Address:X16} size={texture.Width}x{texture.Height} " +
                    $"guest_image=0x{texture.GuestImage?.Address ?? 0:X16} " +
                    $"storage={texture.IsStorage} feedback={texture.FeedbackSource is not null} " +
                    $"sampler=[{texture.SamplerState.Word0:X8}," +
                    $"{texture.SamplerState.Word1:X8},{texture.SamplerState.Word2:X8}," +
                    $"{texture.SamplerState.Word3:X8}]");
            }

            var previousCommandBuffer = _commandBuffer;
            var readbackCommandBuffer = AllocateGuestCommandBuffer();
            try
            {
                _commandBuffer = readbackCommandBuffer;
                foreach (var image in sourceImages)
                {
                    TraceGuestImageContents(image);
                }
            }
            finally
            {
                _commandBuffer = previousCommandBuffer;
                ReleaseGuestCommandBuffer(readbackCommandBuffer);
            }

            return true;
        }

        private static bool ShouldTraceGuestImageAddressForDiagnostics(ulong address)
        {
            return AddressListContains(
                "SHARPEMU_TRACE_GUEST_IMAGE_ADDRS",
                address);
        }

        private static bool ShouldTraceGuestImageWriteForDiagnostics(ulong address)
        {
            return AddressListContains(
                "SHARPEMU_TRACE_GUEST_WRITES",
                address);
        }

        private static bool AddressListContains(
            string environmentVariable,
            ulong address)
        {
            var (wildcard, addresses) = _cachedAddressLists.GetOrAdd(
                environmentVariable,
                static name => ParseAddressList(Environment.GetEnvironmentVariable(name)));
            return wildcard || Array.IndexOf(addresses, address) >= 0;
        }

        private static (bool Wildcard, ulong[] Addresses) ParseAddressList(string? addresses)
        {
            if (string.IsNullOrWhiteSpace(addresses))
            {
                return (false, []);
            }

            var parsedAddresses = new List<ulong>();
            foreach (var token in addresses.Split(
                         [',', ';', ' ', '\t'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token == "*")
                {
                    return (true, []);
                }

                var span = token.AsSpan();
                if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    span = span[2..];
                }

                if (ulong.TryParse(
                        span,
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    parsedAddresses.Add(parsed);
                }
            }

            return (false, parsedAddresses.ToArray());
        }

        private static bool ShouldTracePresentedGuestImageContentsForDiagnostics() =>
            _tracePresentedGuestImagesEnabled;

        private static bool ShouldTraceVulkanResources() =>
            _traceVulkanResourcesEnabled;

        private void RecordTranslatedGraphicsPass(
            TranslatedDrawResources resources,
            RenderPass renderPass,
            Framebuffer framebuffer,
            Extent2D extent)
        {
            BeginTranslatedRenderPass(renderPass, framebuffer, extent);
            RecordTranslatedDrawInPass(resources, extent);
            _vk.CmdEndRenderPass(_commandBuffer);
        }

        private void BeginTranslatedRenderPass(
            RenderPass renderPass,
            Framebuffer framebuffer,
            Extent2D extent,
            bool hasDepthAttachment = false,
            float clearDepth = 1f)
        {
            var clearValues = stackalloc ClearValue[2];
            clearValues[0] = default;
            // Reverse-Z is not assumed; clear depth to 1.0 (far) so a standard
            // LessOrEqual/Less test keeps the nearest fragment.
            clearValues[1] = new ClearValue
            {
                DepthStencil = new ClearDepthStencilValue(clearDepth, 0),
            };
            var renderPassInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = renderPass,
                Framebuffer = framebuffer,
                RenderArea = new Rect2D(new Offset2D(0, 0), extent),
                ClearValueCount = hasDepthAttachment ? 2u : 1u,
                PClearValues = clearValues,
            };
            _vk.CmdBeginRenderPass(
                _commandBuffer,
                &renderPassInfo,
                SubpassContents.Inline);
        }

        private void RecordTranslatedDrawInPass(
            TranslatedDrawResources resources,
            Extent2D extent)
        {
            _vk.CmdBindPipeline(
                _commandBuffer,
                PipelineBindPoint.Graphics,
                resources.Pipeline);
            if (resources.DescriptorSet.Handle != 0)
            {
                var descriptorSet = resources.DescriptorSet;
                _vk.CmdBindDescriptorSets(
                    _commandBuffer,
                    PipelineBindPoint.Graphics,
                    resources.PipelineLayout,
                    0,
                    1,
                    &descriptorSet,
                    0,
                    null);
            }

            var drawScissor = ClampScissor(resources.Scissor, extent);
            if (drawScissor.Width == 0 || drawScissor.Height == 0)
            {
                return;
            }

            var drawViewport = ClampViewport(resources.Viewport, extent);
            if (ViewportDebugEpsilon != 0f)
            {
                drawViewport.X += ViewportDebugEpsilon;
                drawViewport.Y += ViewportDebugEpsilon;
            }
            _vk.CmdSetViewport(_commandBuffer, 0, 1, &drawViewport);
            if (resources.VertexBuffers.Length != 0)
            {
                var buffers = stackalloc VkBuffer[resources.VertexBuffers.Length];
                var offsets = stackalloc ulong[resources.VertexBuffers.Length];
                for (var index = 0; index < resources.VertexBuffers.Length; index++)
                {
                    buffers[index] = resources.VertexBuffers[index].Buffer;
                    offsets[index] = GetVertexBindingOffset(resources.VertexBuffers[index]);
                }

                _vk.CmdBindVertexBuffers(
                    _commandBuffer,
                    0,
                    (uint)resources.VertexBuffers.Length,
                    buffers,
                    offsets);
            }

            // Replaying a full-screen primitive once per 512x512 scissor tile
            // multiplies an ordinary 4K composite into 32 complete draws. On
            // MoltenVK this starves the render thread and makes the guest fall
            // behind its own flip queue. Vulkan clips a normal fullscreen draw
            // efficiently; keep tiling only as an explicit driver diagnostic.
            var maxPixelsPerDraw = Environment.GetEnvironmentVariable(
                "SHARPEMU_ENABLE_CHUNKED_DRAWS") == "1"
                ? 512u * 512u
                : uint.MaxValue;
            var rowsPerDraw = Math.Max(
                1u,
                Math.Min(drawScissor.Height, maxPixelsPerDraw / Math.Max(drawScissor.Width, 1u)));
            var drawCount = 0u;
            for (var y = 0u; y < drawScissor.Height; y += rowsPerDraw)
            {
                var scissor = new Rect2D(
                    new Offset2D(
                        drawScissor.X,
                        checked(drawScissor.Y + (int)y)),
                    new Extent2D(
                        drawScissor.Width,
                        Math.Min(rowsPerDraw, drawScissor.Height - y)));
                _vk.CmdSetScissor(_commandBuffer, 0, 1, &scissor);

                if (resources.IndexBuffer.Handle != 0)
                {
                    _vk.CmdBindIndexBuffer(
                        _commandBuffer,
                        resources.IndexBuffer,
                        0,
                        resources.Index32Bit ? IndexType.Uint32 : IndexType.Uint16);
                    _vk.CmdDrawIndexed(
                        _commandBuffer,
                        resources.VertexCount,
                        resources.InstanceCount,
                        0,
                        0,
                        0);
                }
                else
                {
                    _vk.CmdDraw(
                        _commandBuffer,
                        resources.VertexCount,
                        resources.InstanceCount,
                        0,
                        0);
                }

                drawCount++;
            }

            if (drawCount > 1)
            {
                TraceVulkanShader(
                    $"vk.graphics_chunked target={extent.Width}x{extent.Height} " +
                    $"draws={drawCount} rows={rowsPerDraw} " +
                    $"scissor={drawScissor.X},{drawScissor.Y},{drawScissor.Width}x{drawScissor.Height} " +
                    $"viewport={drawViewport.X:0.###},{drawViewport.Y:0.###}," +
                    $"{drawViewport.Width:0.###}x{drawViewport.Height:0.###} " +
                    $"name={resources.DebugName}");
            }
        }

        private void DestroyTranslatedDrawResources(TranslatedDrawResources resources)
        {
            foreach (var texture in resources.Textures)
            {
                if (texture is null || texture.Cached)
                {
                    continue;
                }

                if (texture.OwnsStorage && texture.View.Handle != 0)
                {
                    _vk.DestroyImageView(_device, texture.View, null);
                }

                if (texture.OwnsStorage && texture.Image.Handle != 0)
                {
                    _vk.DestroyImage(_device, texture.Image, null);
                }

                if (texture.OwnsStorage && texture.ImageMemory.Handle != 0)
                {
                    _vk.FreeMemory(_device, texture.ImageMemory, null);
                }

                if (texture.StagingBuffer.Handle != 0)
                {
                    _vk.DestroyBuffer(_device, texture.StagingBuffer, null);
                }

                if (texture.StagingMemory.Handle != 0)
                {
                    _vk.FreeMemory(_device, texture.StagingMemory, null);
                }

                if (texture.NeedsUpload &&
                    texture.GuestImage is { Initialized: false } guestImage)
                {
                    guestImage.InitialUploadPending = false;
                }
            }

            foreach (var globalBuffer in resources.GlobalMemoryBuffers)
            {
                if (globalBuffer is null || globalBuffer.Allocation is not null)
                {
                    continue;
                }

                RecycleHostBuffer(globalBuffer.Buffer, globalBuffer.Memory);
            }

            foreach (var vertexBuffer in resources.VertexBuffers)
            {
                if (vertexBuffer is null)
                {
                    continue;
                }

                RecycleHostBuffer(vertexBuffer.Buffer, vertexBuffer.Memory);
            }

            RecycleHostBuffer(resources.IndexBuffer, resources.IndexMemory);

            if (!resources.PipelineCached && resources.Pipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, resources.Pipeline, null);
            }

            if (resources.DescriptorPool.Handle != 0)
            {
                if (_recycledDescriptorPools.Count < 256)
                {
                    _recycledDescriptorPools.Push(resources.DescriptorPool);
                }
                else
                {
                    _vk.DestroyDescriptorPool(_device, resources.DescriptorPool, null);
                }
            }

            if (!resources.DescriptorLayoutCached &&
                resources.PipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(_device, resources.PipelineLayout, null);
            }

            if (!resources.DescriptorLayoutCached &&
                resources.DescriptorSetLayout.Handle != 0)
            {
                _vk.DestroyDescriptorSetLayout(_device, resources.DescriptorSetLayout, null);
            }
        }

        private void RecordUpload(uint imageIndex)
        {
            var oldLayout = _imageInitialized[imageIndex]
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.Undefined;
            var toTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _imageInitialized[imageIndex] ? AccessFlags.MemoryReadBit : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                _imageInitialized[imageIndex]
                    ? PipelineStageFlags.BottomOfPipeBit
                    : PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransfer);

            var copyRegion = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
            };
            _vk.CmdCopyBufferToImage(
                _commandBuffer,
                _stagingBuffer,
                _swapchainImages[imageIndex],
                ImageLayout.TransferDstOptimal,
                1,
                &copyRegion);

            var toPresent = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.BottomOfPipeBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toPresent);
        }

        private void RecordGuestImageBlit(
            uint imageIndex,
            GuestImageResource source)
        {
            var traceDestination =
                ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                !_tracedPresentedSwapchain;
            _tracedPresentedSwapchain |= traceDestination;
            BeginDebugLabel(
                _commandBuffer,
                $"SharpEmu present image 0x{source.Address:X16}");

            var sourceToTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderReadBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = source.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            var destinationToTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _imageInitialized[imageIndex]
                    ? AccessFlags.MemoryReadBit
                    : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = _imageInitialized[imageIndex]
                    ? ImageLayout.PresentSrcKhr
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            var barriers = stackalloc ImageMemoryBarrier[2];
            barriers[0] = sourceToTransfer;
            barriers[1] = destinationToTransfer;
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                2,
                barriers);

            var sourceOffsets = new ImageBlit.SrcOffsetsBuffer
            {
                Element0 = new Offset3D(0, 0, 0),
                Element1 = new Offset3D(
                    checked((int)source.Width),
                    checked((int)source.Height),
                    1),
            };
            var destinationOffsets = new ImageBlit.DstOffsetsBuffer
            {
                Element0 = new Offset3D(0, 0, 0),
                Element1 = new Offset3D(
                    checked((int)_extent.Width),
                    checked((int)_extent.Height),
                    1),
            };
            var region = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(
                    ImageAspectFlags.ColorBit,
                    0,
                    0,
                    1),
                SrcOffsets = sourceOffsets,
                DstSubresource = new ImageSubresourceLayers(
                    ImageAspectFlags.ColorBit,
                    0,
                    0,
                    1),
                DstOffsets = destinationOffsets,
            };
            // Nearest keeps integer upscales pixel-crisp, but any fractional
            // scale (e.g. a 3840x2160 guest frame into a 2560x1440 swapchain)
            // must blend neighbours or it silently drops every Nth source
            // row/column, which shreds 1-2px features in the guest frame.
            var isIntegerUpscale =
                source.Width != 0 && source.Height != 0 &&
                _extent.Width >= source.Width && _extent.Height >= source.Height &&
                _extent.Width % source.Width == 0 && _extent.Height % source.Height == 0;
            _vk.CmdBlitImage(
                _commandBuffer,
                source.Image,
                ImageLayout.TransferSrcOptimal,
                _swapchainImages[imageIndex],
                ImageLayout.TransferDstOptimal,
                1,
                &region,
                isIntegerUpscale ? Filter.Nearest : Filter.Linear);

            if (traceDestination)
            {
                var destinationToReadback = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = _swapchainImages[imageIndex],
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToReadback);

                var copyRegion = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
                };
                _vk.CmdCopyImageToBuffer(
                    _commandBuffer,
                    _swapchainImages[imageIndex],
                    ImageLayout.TransferSrcOptimal,
                    _stagingBuffer,
                    1,
                    &copyRegion);
                _swapchainReadbackPending = true;
            }

            var sourceToShaderRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferReadBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferSrcOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = source.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            var destinationToPresent = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = traceDestination
                    ? AccessFlags.TransferReadBit
                    : AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit,
                OldLayout = traceDestination
                    ? ImageLayout.TransferSrcOptimal
                    : ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            barriers[0] = sourceToShaderRead;
            barriers[1] = destinationToPresent;
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                2,
                barriers);
            EndDebugLabel(_commandBuffer);
        }

        private void TraceSwapchainReadback()
        {
            _swapchainReadbackPending = false;
            var byteCount = checked((ulong)_extent.Width * _extent.Height * 4);
            void* mapped;
            Check(
                _vk.MapMemory(_device, _stagingMemory, 0, byteCount, 0, &mapped),
                "vkMapMemory(swapchain readback)");
            try
            {
                var bytes = new ReadOnlySpan<byte>(mapped, checked((int)byteCount));
                var nonzeroBytes = 0L;
                var nonblackPixels = 0L;
                ulong hash = 14695981039346656037UL;
                for (var offset = 0; offset < bytes.Length; offset += 4)
                {
                    var b0 = bytes[offset];
                    var b1 = bytes[offset + 1];
                    var b2 = bytes[offset + 2];
                    var b3 = bytes[offset + 3];
                    nonzeroBytes += b0 == 0 ? 0 : 1;
                    nonzeroBytes += b1 == 0 ? 0 : 1;
                    nonzeroBytes += b2 == 0 ? 0 : 1;
                    nonzeroBytes += b3 == 0 ? 0 : 1;
                    nonblackPixels += b0 != 0 || b1 != 0 || b2 != 0 ? 1 : 0;
                    hash = (hash ^ b0) * 1099511628211UL;
                    hash = (hash ^ b1) * 1099511628211UL;
                    hash = (hash ^ b2) * 1099511628211UL;
                    hash = (hash ^ b3) * 1099511628211UL;
                }

                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.swapchain_image size={_extent.Width}x{_extent.Height} " +
                    $"format={_swapchainFormat} nonzero_bytes={nonzeroBytes}/{byteCount} " +
                    $"nonblack_pixels={nonblackPixels}/{(ulong)_extent.Width * _extent.Height} " +
                    $"hash=0x{hash:X16}");

                var dumpDir = Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_DUMP_DIR");
                if (!string.IsNullOrWhiteSpace(dumpDir))
                {
                    Directory.CreateDirectory(dumpDir);
                    var seq = Interlocked.Increment(ref _guestImageDumpSequence);
                    var path = Path.Combine(
                        dumpDir,
                        $"present-{seq:D4}-{_extent.Width}x{_extent.Height}-{_swapchainFormat}.bgra");
                    File.WriteAllBytes(path, bytes.ToArray());
                    Console.Error.WriteLine($"[LOADER][TRACE] vk.swapchain_dump path={path}");
                    // Re-arm so subsequent presented frames are captured too.
                    _tracedPresentedSwapchain = false;
                }
            }
            finally
            {
                _vk.UnmapMemory(_device, _stagingMemory);
            }
        }

        private Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                var fallbackWidth = _extent.Width != 0
                    ? _extent.Width
                    : DefaultWindowWidth;
                var fallbackHeight = _extent.Height != 0
                    ? _extent.Height
                    : DefaultWindowHeight;
                return new Extent2D(
                    ClampSurfaceExtent(
                        capabilities.CurrentExtent.Width,
                        fallbackWidth,
                        capabilities.MinImageExtent.Width,
                        capabilities.MaxImageExtent.Width),
                    ClampSurfaceExtent(
                        capabilities.CurrentExtent.Height,
                        fallbackHeight,
                        capabilities.MinImageExtent.Height,
                        capabilities.MaxImageExtent.Height));
            }

            var size = _window.FramebufferSize;
            return new Extent2D(
                ClampSurfaceExtent(
                    (uint)Math.Max(size.X, 1),
                    DefaultWindowWidth,
                    capabilities.MinImageExtent.Width,
                    capabilities.MaxImageExtent.Width),
                ClampSurfaceExtent(
                    (uint)Math.Max(size.Y, 1),
                    DefaultWindowHeight,
                    capabilities.MinImageExtent.Height,
                    capabilities.MaxImageExtent.Height));
        }

        private static uint ClampSurfaceExtent(
            uint value,
            uint fallback,
            uint minimum,
            uint maximum)
        {
            value = value <= 1 && fallback > 1 ? fallback : value;
            minimum = Math.Max(minimum, 1u);
            maximum = Math.Max(maximum, minimum);
            return Math.Clamp(value, minimum, maximum);
        }

        private static SurfaceFormatKHR ChooseSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> formats)
        {
            foreach (var format in formats)
            {
                if (format.Format is Format.B8G8R8A8Srgb or Format.B8G8R8A8Unorm &&
                    format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return format;
                }
            }

            return formats.Count > 0
                ? formats[0]
                : throw new InvalidOperationException("The Vulkan surface exposes no pixel formats.");
        }

        private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supported)
        {
            foreach (var candidate in new[]
                     {
                         CompositeAlphaFlagsKHR.OpaqueBitKhr,
                         CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
                         CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
                         CompositeAlphaFlagsKHR.InheritBitKhr,
                     })
            {
                if ((supported & candidate) != 0)
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("The Vulkan surface exposes no composite alpha mode.");
        }

        private static ImageSubresourceRange ColorSubresourceRange(
            uint baseMipLevel = 0,
            uint levelCount = 1) =>
            new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = baseMipLevel,
                LevelCount = levelCount,
                LayerCount = 1,
            };

        private static byte[] ScaleBgra(byte[] source, uint sourceWidth, uint sourceHeight, uint width, uint height)
        {
            var destination = new byte[checked((int)(width * height * 4))];
            for (uint y = 0; y < height; y++)
            {
                var sourceY = (uint)(((ulong)y * sourceHeight) / height);
                for (uint x = 0; x < width; x++)
                {
                    var sourceX = (uint)(((ulong)x * sourceWidth) / width);
                    var sourceOffset = checked((int)(((ulong)sourceY * sourceWidth + sourceX) * 4));
                    var destinationOffset = checked((int)(((ulong)y * width + x) * 4));
                    source.AsSpan(sourceOffset, 4).CopyTo(destination.AsSpan(destinationOffset, 4));
                }
            }

            return destination;
        }

        private void DisposeVulkan()
        {
            if (!_vulkanReady)
            {
                return;
            }

            if (_debugUtils is not null && _debugMessenger.Handle != 0)
            {
                _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            }
            _vulkanReady = false;
            _vk.DeviceWaitIdle(_device);
            SavePipelineCache(force: true);
            DrainFrameSlots();
            CollectCompletedGuestSubmissions(waitForOldest: false);
            ClearCachedTextureIdentities();
            foreach (var pipeline in _computePipelines.Values)
            {
                _vk.DestroyPipeline(_device, pipeline, null);
            }
            _computePipelines.Clear();
            foreach (var pipeline in _graphicsPipelines.Values)
            {
                _vk.DestroyPipeline(_device, pipeline, null);
            }
            _graphicsPipelines.Clear();
            foreach (var layout in _descriptorLayouts.Values)
            {
                _vk.DestroyPipelineLayout(_device, layout.PipelineLayout, null);
                if (layout.DescriptorSetLayout.Handle != 0)
                {
                    _vk.DestroyDescriptorSetLayout(
                        _device,
                        layout.DescriptorSetLayout,
                        null);
                }
            }
            _descriptorLayouts.Clear();
            foreach (var sampler in _samplers.Values)
            {
                _vk.DestroySampler(_device, sampler, null);
            }
            _samplers.Clear();
            _shaderDigests.Clear();
            WriteBackAllDirtyGuestBuffers();
            foreach (var allocation in _guestBufferAllocations)
            {
                DestroyGuestBufferAllocation(allocation);
            }
            _guestBufferAllocations.Clear();
            foreach (var allocation in _hostBufferAllocations.Values)
            {
                _vk.DestroyBuffer(_device, allocation.Buffer, null);
                _vk.FreeMemory(_device, allocation.Memory, null);
            }
            _hostBufferAllocations.Clear();
            _hostBufferPool.Clear();
            foreach (var guestImage in _guestImages.Values)
            {
                DestroyGuestImage(guestImage);
            }
            _guestImages.Clear();
            foreach (var guestDepth in _guestDepthImages.Values)
            {
                DestroyGuestDepth(guestDepth);
            }
            _guestDepthImages.Clear();
            lock (_gate)
            {
                _availableGuestImages.Clear();
            }
            DestroySwapchainResources();
            if (_device.Handle != 0)
            {
                if (_pipelineCache.Handle != 0)
                {
                    _vk.DestroyPipelineCache(_device, _pipelineCache, null);
                    _pipelineCache = default;
                }
                _vk.DestroyDevice(_device, null);
                _device = default;
            }
            if (_surface.Handle != 0)
            {
                _surfaceApi.DestroySurface(_instance, _surface, null);
                _surface = default;
            }
            if (_instance.Handle != 0)
            {
                _vk.DestroyInstance(_instance, null);
                _instance = default;
            }
        }

        private void RecreateSwapchainResources(string operation, Result result)
        {
            if (_device.Handle == 0)
            {
                return;
            }

            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(
                    _physicalDevice,
                    _surface,
                    out var capabilities),
                "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            var framebufferSize = _window.FramebufferSize;
            var hasFixedExtent = capabilities.CurrentExtent.Width != uint.MaxValue;
            var surfaceWidth = hasFixedExtent
                ? capabilities.CurrentExtent.Width
                : (uint)Math.Max(framebufferSize.X, 0);
            var surfaceHeight = hasFixedExtent
                ? capabilities.CurrentExtent.Height
                : (uint)Math.Max(framebufferSize.Y, 0);
            if (surfaceWidth <= 1 || surfaceHeight <= 1)
            {
                if (!_swapchainRecreateDeferred)
                {
                    _swapchainRecreateDeferred = true;
                    Console.Error.WriteLine(
                        $"[LOADER][INFO] Vulkan VideoOut deferred swapchain recreation: " +
                        $"surface={surfaceWidth}x{surfaceHeight}");
                }

                return;
            }

            _swapchainRecreateDeferred = false;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut recreating swapchain after {operation}: {result}");
            _vk.DeviceWaitIdle(_device);
            DrainFrameSlots();
            CollectCompletedGuestSubmissions(waitForOldest: false);
            DestroySwapchainResources();
            CreateSwapchain();
            CreateCommandResources();
            CreateGuestDrawResources();
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut recreated swapchain: " +
                $"{_extent.Width}x{_extent.Height}, format={_swapchainFormat}");
        }

        private void DestroySwapchainResources()
        {
            if (_stagingBuffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, _stagingBuffer, null);
                _stagingBuffer = default;
            }
            if (_stagingMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, _stagingMemory, null);
                _stagingMemory = default;
                _stagingSize = 0;
            }
            foreach (var semaphore in _frameImageAvailable)
            {
                if (semaphore.Handle != 0)
                {
                    _vk.DestroySemaphore(_device, semaphore, null);
                }
            }
            _frameImageAvailable = [];
            foreach (var semaphore in _renderFinishedPerImage)
            {
                if (semaphore.Handle != 0)
                {
                    _vk.DestroySemaphore(_device, semaphore, null);
                }
            }
            _renderFinishedPerImage = [];
            if (_overlayImage.Handle != 0)
            {
                _vk.DestroyImage(_device, _overlayImage, null);
                _overlayImage = default;
            }
            if (_overlayImageMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, _overlayImageMemory, null);
                _overlayImageMemory = default;
            }
            for (var slot = 0; slot < _overlayStagingBuffers.Length; slot++)
            {
                if (_overlayStagingBuffers[slot].Handle != 0)
                {
                    _vk.DestroyBuffer(_device, _overlayStagingBuffers[slot], null);
                }
                if (_overlayStagingMemory[slot].Handle != 0)
                {
                    _vk.FreeMemory(_device, _overlayStagingMemory[slot], null);
                }
            }
            _overlayStagingBuffers = [];
            _overlayStagingMemory = [];
            _overlayStagingMapped = [];
            _overlayImageInitialized = false;
            foreach (var fence in _frameFences)
            {
                if (fence.Handle != 0)
                {
                    _vk.DestroyFence(_device, fence, null);
                }
            }
            _frameFences = [];
            _frameFencePending = [];
            _frameTimelines = [];
            _frameTranslatedResources = [];
            while (_recycledGuestFences.TryPop(out var recycledFence))
            {
                _vk.DestroyFence(_device, recycledFence, null);
            }
            if (_barycentricPipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, _barycentricPipeline, null);
                _barycentricPipeline = default;
            }
            if (_pipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
                _pipelineLayout = default;
            }
            foreach (var framebuffer in _framebuffers)
            {
                if (framebuffer.Handle != 0)
                {
                    _vk.DestroyFramebuffer(_device, framebuffer, null);
                }
            }
            if (_renderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, _renderPass, null);
                _renderPass = default;
            }
            foreach (var imageView in _swapchainImageViews)
            {
                if (imageView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, imageView, null);
                }
            }
            if (_commandPool.Handle != 0)
            {
                // Destroying the pool frees every command buffer allocated
                // from it, including recycled and per-frame ones.
                _recycledGuestCommandBuffers.Clear();
                _frameCommandBuffers = [];
                _vk.DestroyCommandPool(_device, _commandPool, null);
                _commandPool = default;
                _commandBuffer = default;
                _presentationCommandBuffer = default;
            }
            if (_swapchain.Handle != 0)
            {
                _swapchainApi.DestroySwapchain(_device, _swapchain, null);
                _swapchain = default;
            }

            _swapchainImages = [];
            _swapchainImageViews = [];
            _framebuffers = [];
            _imageInitialized = [];
        }

        private static void CheckSwapchainResult(Result result, string operation)
        {
            if (result is Result.Success or Result.SuboptimalKhr)
            {
                return;
            }

            throw new InvalidOperationException($"{operation} failed with {result}.");
        }

        private static void Check(Result result, string operation)
        {
            if (result != Result.Success)
            {
                throw new InvalidOperationException($"{operation} failed with {result}.");
            }
        }

        private bool TryMarkDeviceLost(Exception exception)
        {
            if (!exception.Message.Contains(nameof(Result.ErrorDeviceLost), StringComparison.Ordinal))
            {
                return false;
            }

            _deviceLost = true;
            if (!_deviceLostLogged)
            {
                _deviceLostLogged = true;
                Console.Error.WriteLine(
                    "[LOADER][ERROR] Vulkan device lost; dropping subsequent guest GPU work. " +
                    exception.Message);
            }

            return true;
        }

        private static void TraceVulkanShader(string message)
        {
            if (!_traceVulkanShaderEnabled)
            {
                return;
            }

            Console.Error.WriteLine($"[LOADER][TRACE] {message}");
        }
    }
}
