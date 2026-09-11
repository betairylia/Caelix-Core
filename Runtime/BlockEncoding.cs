// #define CAELIX_PACKED_SCENE_COLOR

namespace Caelix
{
    /// <summary>Compile-time interpretation of Block. Match CaelixMaterialConfig.hlsl.</summary>
    public static class BlockEncoding
    {
#if CAELIX_PACKED_SCENE_COLOR
        public const bool PackedSceneColor = true;
        public const int OpaqueMask = 0xC000;
        public const uint TransparentMask = 0x00FF;
        public const int FaceShift = 8;
#else
        public const bool PackedSceneColor = false;
        public const int OpaqueMask = 0x8000;
        public const uint TransparentMask = 0x01FF;
        public const int FaceShift = 9;
#endif
        public static bool IsOpaque(uint value) => (value & OpaqueMask) != 0;
        public static uint TransparentId(uint value) => value & TransparentMask;
        public static uint WithFaces(uint faces, uint material) =>
            ((faces & 63u) << FaceShift) | TransparentId(material);
        public static uint Faces(uint value) => (value >> FaceShift) & 63u;
    }
}
