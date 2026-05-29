using System.Collections.Generic;
using UnityEngine;

namespace Bekkoloco
{
    internal static class ProceduralTileBottomFlatGenerator
    {
        internal static void Generate(ProceduralTileBottomContext context)
        {
            int vertexCount = context.Outline.Count;
            int baseIndex = context.Vertices.Count;
            var bottomOutline = new List<Vector2>(vertexCount);

            for (int i = 0; i < vertexCount; i++)
            {
                Vector2 point = context.Outline[i];
                bottomOutline.Add(point);
                context.Vertices.Add(new Vector3(point.x, 0f, -point.y));
                context.UVs.Add(new Vector2(point.x + 0.5f, point.y + 0.5f));
            }

            ProceduralTileMeshGenerator.TriangulateCap(
                bottomOutline,
                context.BottomTriangles,
                baseIndex,
                !context.IsClockwise);
        }
    }
}
