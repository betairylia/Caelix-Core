using Unity.Mathematics;

namespace Caelix
{
    /// <summary>Experimental 16-bit scene colors. Zero is air; glass stores its canonical index.</summary>
    public static class PackedSceneColor
    {
        public static ushort Opaque(int r5, int g5, int b5) => (ushort)(0x8000 |
            (math.clamp(r5, 0, 31) << 10) | (math.clamp(g5, 0, 31) << 5) | math.clamp(b5, 0, 31));

        public static ushort Emissive(int r4, int g4, int b4, int strength) => (ushort)(0x4000 |
            (math.clamp(r4, 0, 15) << 10) | (math.clamp(g4, 0, 15) << 6) |
            (math.clamp(b4, 0, 15) << 2) | math.clamp(strength, 0, 3));

        public static bool IsEmissive(uint value) => (value & 0xC000u) == 0x4000u;
        public static float EmissionStrength(uint value) => math.pow(8f, value & 3u);

        public static int3 Color5(uint value)
        {
            if ((value & 0x8000u) != 0)
                return new int3((int)(value >> 10) & 31, (int)(value >> 5) & 31, (int)value & 31);
            if (IsEmissive(value))
                return new int3(((int)(value >> 10) & 15) * 31 / 15,
                    ((int)(value >> 6) & 15) * 31 / 15, ((int)(value >> 2) & 15) * 31 / 15);
            return (int3)math.round(SharedGlassPalette.Get(value & 255u).Tint * 31f);
        }
    }
}
