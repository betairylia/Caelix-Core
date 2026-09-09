using Unity.Mathematics;

namespace Caelix
{
    /// <summary>
    /// Reads single blocks by position, across brick and storage boundaries.
    /// </summary>
    /// <remarks>
    /// Implemented by <see cref="VoxelNeighborhood"/> (entity-local block positions) and by
    /// <see cref="SectorNeighborhoodReaderHelper"/> (sector-local block positions). A consumer
    /// takes it as a generic constraint (<c>where TReader : struct, IBlockReader</c>) so the call
    /// stays a direct call under Burst and nothing is boxed. Each reader defines its own
    /// coordinate frame; the caller must pass positions in the frame of the reader it built.
    /// </remarks>
    public interface IBlockReader
    {
        /// <summary>The block at <paramref name="pos"/>, or <c>default</c> outside allocated storage.</summary>
        Block GetBlock(int3 pos);
    }
}
