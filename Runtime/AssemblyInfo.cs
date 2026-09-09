using System.Runtime.CompilerServices;

// The Core tests assert the sector-backend conversions (SectorKeys) that the brick-key facade is
// built on. They are internal on purpose: nothing outside Caelix-Core may know that a sector is
// 16 bricks wide.
[assembly: InternalsVisibleTo("Caelix.Core.Tests")]
