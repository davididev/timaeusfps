using System.Collections.Generic;
using UnityEngine;

namespace Bekkoloco
{
    internal sealed class ProceduralTileBottomContext
    {
        internal readonly List<Vector2> Outline;
        internal readonly List<Vector3> Vertices;
        internal readonly List<Vector2> UVs;
        internal readonly List<int> BottomTriangles;
        internal readonly ProceduralTileMeshGenerator.ProceduralMeshSettings Settings;
        internal readonly bool IsClockwise;
        internal readonly bool SkipSideWalls;
        internal readonly HashSet<int> ExteriorEdges;

        internal ProceduralTileBottomContext(
            List<Vector2> outline,
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> bottomTriangles,
            ProceduralTileMeshGenerator.ProceduralMeshSettings settings,
            bool isClockwise,
            bool skipSideWalls,
            HashSet<int> exteriorEdges)
        {
            Outline = outline;
            Vertices = vertices;
            UVs = uvs;
            BottomTriangles = bottomTriangles;
            Settings = settings;
            IsClockwise = isClockwise;
            SkipSideWalls = skipSideWalls;
            ExteriorEdges = exteriorEdges;
        }
    }

    internal static class ProceduralTileBottomCapGenerator
    {
        internal static void Generate(ProceduralTileBottomContext context)
        {
            switch (context.Settings.bottomMode)
            {
                case BottomMode.Flat:
                    ProceduralTileBottomFlatGenerator.Generate(context);
                    break;
                case BottomMode.Bevel:
                    ProceduralTileBottomBevelGenerator.Generate(context);
                    break;
                case BottomMode.IslandNoise:
                    ProceduralTileBottomIslandGenerator.Generate(context);
                    break;
            }
        }
    }
}
