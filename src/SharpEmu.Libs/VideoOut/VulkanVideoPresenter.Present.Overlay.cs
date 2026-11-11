// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial manages presentation of the performance overlay.

        // Perf overlay: CPU-rasterized panel copied through per-slot staging
        // buffers into one image, then blitted onto the swapchain.
        private Image _overlayImage;
        private DeviceMemory _overlayImageMemory;
        private bool _overlayImageInitialized;
        private VkBuffer[] _overlayStagingBuffers = [];
        private DeviceMemory[] _overlayStagingMemory = [];
        private nint[] _overlayStagingMapped = [];

        /// <summary>
        /// The pixel format <see cref="PerfOverlay"/> writes its panel in.
        /// </summary>
        /// <remarks>
        /// Named rather than repeated as a literal because the transfer of that panel onto the
        /// presentation target has to know whether the two agree: they do on every desktop surface
        /// and never on an Android one, and the difference decides between a raw copy and a
        /// converting blit.
        /// </remarks>
        private const Format OverlayFormat = Format.B8G8R8A8Unorm;

        private void CreateOverlayResources()
        {
            const ulong overlayBytes = PerfOverlay.PixelBufferWidth * PerfOverlay.PixelBufferHeight * 4;
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = OverlayFormat,
                Extent = new Extent3D(PerfOverlay.PixelBufferWidth, PerfOverlay.PixelBufferHeight, 1),
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
                _deviceInfo.AllocateMemory(memoryInfo, out _overlayImageMemory),
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
            var rectangle = PerfOverlay.GetRectangle((int)_extent.Width, (int)_extent.Height);
            if (_overlayImage.Handle == 0 || _overlayStagingMapped.Length <= frameSlot ||
                rectangle.Width == 0 || rectangle.Height == 0)
            {
                return;
            }

            var pendingWork = _commandStream.PendingSubmissionCount;

            var pixels = new Span<byte>(
                (void*)_overlayStagingMapped[frameSlot],
                PerfOverlay.PixelBufferWidth * PerfOverlay.PixelBufferHeight * 4);
            PerfOverlay.Fill(pixels, pendingWork, _pendingGuestSubmissions.Count);
            var presentationTarget = PresentationTargetImage(imageIndex);

            var toTransferDst = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = _overlayImageInitialized ? AccessFlags2.TransferReadBit : 0,
                DstAccessMask = AccessFlags2.TransferWriteBit,
                OldLayout = _overlayImageInitialized
                    ? ImageLayout.TransferSrcOptimal
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _overlayImage,
                SubresourceRange = ColorSubresourceRange(),
            };
            VulkanSynchronization.PipelineBarrier(_vk,
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, &toTransferDst);

            var copyRegion = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(PerfOverlay.PixelBufferWidth, PerfOverlay.PixelBufferHeight, 1),
            };
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdCopyBufferToImage(
                    _commandBuffer,
                    _overlayStagingBuffers[frameSlot],
                    _overlayImage,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copyRegion);
            }

            var toTransferSrc = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstAccessMask = AccessFlags2.TransferReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _overlayImage,
                SubresourceRange = ColorSubresourceRange(),
            };
            var swapchainToDst = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags2.TransferWriteBit,
                OldLayout = PresentationTargetFinalLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = presentationTarget,
                SubresourceRange = ColorSubresourceRange(),
            };
            var preBlitBarriers = stackalloc ImageMemoryBarrier2[2] { toTransferSrc, swapchainToDst };
            VulkanSynchronization.PipelineBarrier(_vk,
                _commandBuffer,
                PipelineStageFlags.TransferBit | PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 2, preBlitBarriers);

            var panelWidth = rectangle.Width;
            var panelHeight = rectangle.Height;
            if (PresentationTargetFormat == OverlayFormat)
            {
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
                    DstOffset = new Offset3D(rectangle.Left, rectangle.Top, 0),
                    Extent = new Extent3D((uint)panelWidth, (uint)panelHeight, 1),
                };
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdCopyImage(
                        _commandBuffer,
                        _overlayImage,
                        ImageLayout.TransferSrcOptimal,
                        presentationTarget,
                        ImageLayout.TransferDstOptimal,
                        1,
                        &copy);
                }
            }
            else
            {
                // The presentation target is not B8G8R8A8, so the copy above would
                // reinterpret the overlay's bytes rather than convert them and the
                // panel would render with its red and blue channels exchanged.
                // vkCmdCopyImage is a raw byte transfer; vkCmdBlitImage matches
                // components by name, which is exactly the channel reorder needed.
                //
                // This is reachable wherever the surface exposes no B8G8R8A8 format
                // at all, which no desktop does and Android always does: an Android
                // swapchain offers R8G8B8A8 UNORM and SRGB and nothing else, so
                // ChooseSurfaceFormat falls through to formats[0] and lands on
                // R8G8B8A8Unorm. The copy path above is left untouched for every
                // host that does have a matching format, MoltenVK included.
                var sourceOffsets = new ImageBlit.SrcOffsetsBuffer
                {
                    Element0 = new Offset3D(0, 0, 0),
                    Element1 = new Offset3D(panelWidth, panelHeight, 1),
                };
                var destinationOffsets = new ImageBlit.DstOffsetsBuffer
                {
                    Element0 = new Offset3D(rectangle.Left, rectangle.Top, 0),
                    Element1 = new Offset3D(rectangle.Left + panelWidth, rectangle.Top + panelHeight, 1),
                };
                var blit = new ImageBlit
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    SrcOffsets = sourceOffsets,
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstOffsets = destinationOffsets,
                };
                // Nearest, because the regions are the same size: this blit exists
                // to convert, never to scale.
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdBlitImage(
                        _commandBuffer,
                        _overlayImage,
                        ImageLayout.TransferSrcOptimal,
                        presentationTarget,
                        ImageLayout.TransferDstOptimal,
                        1,
                        &blit,
                        Filter.Nearest);
                }
            }

            var presentationTargetToFinal = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstAccessMask = _hdrOutputActive ? AccessFlags2.ShaderReadBit : 0,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = PresentationTargetFinalLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = presentationTarget,
                SubresourceRange = ColorSubresourceRange(),
            };
            VulkanSynchronization.PipelineBarrier(_vk,
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                _hdrOutputActive
                    ? PipelineStageFlags.FragmentShaderBit
                    : PipelineStageFlags.BottomOfPipeBit,
                0, 0, null, 0, null, 1, &presentationTargetToFinal);
            _overlayImageInitialized = true;
        }
    }
}
