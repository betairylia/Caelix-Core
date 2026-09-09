using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Caelix;

namespace Caelix.Tests
{
    public unsafe class BlockPackingTests
    {
        [Test]
        public void DefaultBlockIsEmpty()
        {
            var block = new Block();

            Assert.That(UnsafeUtility.SizeOf<Block>(), Is.EqualTo(sizeof(ushort)));
            Assert.That(block.data, Is.EqualTo((ushort)0));
            Assert.That(block.id, Is.EqualTo(0));
            Assert.That(block.isEmpty, Is.True);
        }

        [Test]
        public void IdConstructorStoresVisibleValue()
        {
            var block = new Block(0x1234);

            Assert.That(block.data, Is.EqualTo((ushort)0x1234));
            Assert.That(block.id, Is.EqualTo(0x1234));
        }

        [Test]
        public void IsEmptyRequiresAllDataBitsToBeZero()
        {
            var visible = new Block { data = 1 };

            Assert.That(Block.Empty.isEmpty, Is.True);
            Assert.That(visible.isEmpty, Is.False);
        }

        [Test]
        public void IsRendererEmptyUsesBlockIdOnly()
        {
            Assert.That(Block.Empty.isRendererEmpty, Is.True);
            Assert.That(new Block(1).isRendererEmpty, Is.False);
        }

        [Test]
        public void EqualityAndHashCodeUsePackedData()
        {
            var a = new Block { data = 0x1002 };
            var b = new Block { data = 0x1002 };
            var c = new Block { data = 0x1003 };

            Assert.That(a, Is.EqualTo(b));
            Assert.That(a == b, Is.True);
            Assert.That(a != c, Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void RgbEmissionConstructorPacksId()
        {
            var block = new Block(3, 4, 5, true);
            ushort expectedId = BlockEncoding.PackedSceneColor
                ? (ushort)(0x4000 | (1 << 10) | (1 << 6) | (2 << 2))
                : (ushort)((3 << 11) | (4 << 6) | (5 << 1) | 1);

            Assert.That(block.id, Is.EqualTo(expectedId));
        }

        [Test]
        public void AllOpaqueRgb555ColorsAreDistinctAndIncludeBlack()
        {
            for (int r = 0; r < 32; r++)
            for (int g = 0; g < 32; g++)
            for (int b = 0; b < 32; b++)
            {
                ushort value = PackedSceneColor.Opaque(r, g, b);
                Assert.That(value, Is.EqualTo(0x8000 + r * 1024 + g * 32 + b));
                Assert.That(PackedSceneColor.Color5(value), Is.EqualTo(new Unity.Mathematics.int3(r, g, b)));
            }
        }

        [Test]
        public void FacePackingPreservesAllTransparentIdentitiesAndSixDirections()
        {
            for (uint id = 0; id <= BlockEncoding.TransparentMask; id++)
            for (uint faces = 0; faces < 64; faces++)
            {
                uint value = Block.MaskTransparency(faces, id);
                Assert.That(BlockEncoding.IsOpaque(value), Is.False);
                Assert.That(BlockEncoding.TransparentId(value), Is.EqualTo(id));
                Assert.That(BlockEncoding.Faces(value), Is.EqualTo(faces));
            }
        }

        [Test]
        public void EmissionPrefixesAndStrengthsDoNotOverlapGlassOrOpaqueRgb()
        {
            for (int strength = 0; strength < 4; strength++)
            {
                ushort value = PackedSceneColor.Emissive(15, 15, 15, strength);
                Assert.That(value & 0xC000, Is.EqualTo(0x4000));
                Assert.That(PackedSceneColor.IsEmissive(value), Is.True);
                Assert.That(PackedSceneColor.EmissionStrength(value), Is.EqualTo(new[] { 1f, 8f, 64f, 512f }[strength]));
                if (BlockEncoding.PackedSceneColor) Assert.That(new Block(value).isOpaque, Is.True);
            }
        }

        [Test]
        public void FloatColorOneDoesNotOverflowIntoAnotherChannel()
        {
            Assert.That(new Block(1f, 1f, 1f, 0f), Is.EqualTo(new Block(31, 31, 31, false)));
        }
    }
}
