using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanRenderTargetExtentTests
{
    [Fact]
    public void MixedSizeAttachments_UseSmallestFramebufferExtent()
    {
        var targets = new[]
        {
            CreateTarget(0x1000, 3840, 2160),
            CreateTarget(0x2000, 1920, 1080),
            CreateTarget(0x3000, 2560, 720),
        };

        Assert.True(
            VulkanVideoPresenter.TryGetCompatibleRenderTargetExtent(
                targets,
                out var width,
                out var height));
        Assert.Equal(1920u, width);
        Assert.Equal(720u, height);
    }

    [Fact]
    public void AliasedColorAttachments_AreRejected()
    {
        var targets = new[]
        {
            CreateTarget(0x1000, 1920, 1080),
            CreateTarget(0x1000, 1920, 1080),
        };

        Assert.False(
            VulkanVideoPresenter.TryGetCompatibleRenderTargetExtent(
                targets,
                out var width,
                out var height));
        Assert.Equal(0u, width);
        Assert.Equal(0u, height);
    }

    [Fact]
    public void SyntheticSingleAttachment_AllowsZeroAddress()
    {
        var targets = new[]
        {
            CreateTarget(0, 320, 180),
        };

        Assert.True(
            VulkanVideoPresenter.TryGetCompatibleRenderTargetExtent(
                targets,
                out var width,
                out var height));
        Assert.Equal(320u, width);
        Assert.Equal(180u, height);
    }

    private static GuestRenderTarget CreateTarget(
        ulong address,
        uint width,
        uint height) =>
        new(
            address,
            width,
            height,
            Format: 10,
            NumberType: 0);
}
