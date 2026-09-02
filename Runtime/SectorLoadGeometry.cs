using System.Collections.Generic;
using Unity.Mathematics;

namespace Caelix
{
    /// <summary>
    /// Pure geometry for load-radius streaming: which sector offsets to load, in what order.
    /// Lives in Core so the server-side streaming logic and the authoring loader share it.
    /// </summary>
    public static class SectorLoadGeometry
    {
        /// <summary>
        /// Generates all integer points that are within both a bounding box and a cylindrical volume (ignoring Y),
        /// ordered by Manhattan distance from the origin.
        /// </summary>
        /// <param name="bounds">The box bounds, representing the maximum absolute value for each coordinate.</param>
        /// <param name="radius">The radius of the cylinder in the XZ plane centered at the origin.</param>
        /// <param name="result">Output list to populate with generated points, ordered by Manhattan distance.</param>
        /// <remarks>
        /// This method generates points by iterating through Manhattan distance shells, ensuring
        /// points closer to the origin are added first. The Y component is ignored for distance checks,
        /// creating a cylindrical loading volume which is typical for voxel games.
        /// </remarks>
        public static void GeneratePointsInIntersection(int3 bounds, float radius, ref List<int3> result)
        {
            result.Clear();
            float radiusSqr = radius * radius;

            // Calculate the maximum Manhattan distance we need to check
            int maxDistance = bounds.x + bounds.y + bounds.z;

            // Process points by increasing Manhattan distance (ensures ordering by Manhattan distance)
            for (int manhattanDist = 0; manhattanDist <= maxDistance; manhattanDist++)
            {
                // For each possible Manhattan distance, generate all points with that distance
                for (int x = -math.min(manhattanDist, bounds.x); x <= math.min(manhattanDist, bounds.x); x++)
                {
                    int absX = math.abs(x);
                    int remainingDist = manhattanDist - absX;

                    for (int y = -math.min(remainingDist, bounds.y); y <= math.min(remainingDist, bounds.y); y++)
                    {
                        int absY = math.abs(y);
                        int zValue = remainingDist - absY;

                        // Check if z is within bounds
                        if (zValue >= 0 && zValue <= bounds.z)
                        {
                            // Create point with positive z
                            int3 point = new int3(x, y, zValue);
                            var point_noY = point;
                            point_noY.y = 0;

                            // Check if within sphere (Euclidean distance < radius)
                            if (math.lengthsq(point_noY) < radiusSqr)
                            {
                                result.Add(point);
                            }

                            // Create point with negative z (if z != 0 to avoid duplicates)
                            if (zValue > 0)
                            {
                                int3 negZPoint = new int3(x, y, -zValue);

                                // Check if within sphere
                                if (math.lengthsq(negZPoint) < radiusSqr)
                                {
                                    result.Add(negZPoint);
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
