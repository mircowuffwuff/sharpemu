// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private int _rectangleGeometryStopped;

        /// <summary>
        /// Whether a failure to create a rectangle-list pipeline is one the presenter recovers from
        /// by drawing rectangles without the geometry stage.
        /// </summary>
        /// <remarks>
        /// Device loss is not: it still ends the frame through the presenter's own recovery.
        /// </remarks>
        internal bool IsGeometryPipelineRefusal(InvalidOperationException exception) =>
            exception is not VulkanDeviceLostException;

        /// <summary>
        /// Draws rectangle lists without the generated geometry stage from here on.
        /// </summary>
        /// <remarks>
        /// A driver can advertise geometryShader and still refuse the expansion stage
        /// RectangleGeometryShader generates: Qualcomm's proprietary Adreno driver fails
        /// vkCreateGraphicsPipelines with VK_ERROR_UNKNOWN for SPIR-V that spirv-val accepts and
        /// Mesa's Turnip compiles on the same GPU. The draw that finds out, and every draw after it,
        /// takes the path a device without geometry shaders takes, so a refusal costs one failed
        /// pipeline creation rather than the presenter.
        /// </remarks>
        internal void StopExpandingRectanglesWithGeometry(InvalidOperationException exception)
        {
            Volatile.Write(ref _supportsRectangleGeometry, false);
            if (Interlocked.Exchange(ref _rectangleGeometryStopped, 1) == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Rectangle lists are drawn without the geometry shader from now on: {exception.Message}");
            }
        }
    }
}
