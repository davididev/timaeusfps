using System.Collections.Generic;
using UnityEngine;

namespace Bekkoloco
{
    internal static class ProceduralTileBottomBevelGenerator
    {
        internal static void Generate(ProceduralTileBottomContext context)
        {
            int vertexCount = context.Outline.Count;
            float inset = context.Settings.bottomBevelInset;
            float bevelDepth = context.Settings.bottomBevelDepth;
            int segments = context.Settings.bottomBevelSegments;
            BevelProfile profile = context.Settings.bottomBevelProfile;

            if (context.SkipSideWalls || inset <= 0f || segments < 1)
            {
                int baseIndex = context.Vertices.Count;
                var bottomOutline = new List<Vector2>(vertexCount);

                for (int i = 0; i < vertexCount; i++)
                {
                    Vector2 point = context.Outline[i];
                    bottomOutline.Add(point);
                    context.Vertices.Add(new Vector3(point.x, -bevelDepth, -point.y));
                    context.UVs.Add(new Vector2(point.x + 0.5f, point.y + 0.5f));
                }

                ProceduralTileMeshGenerator.TriangulateCap(
                    bottomOutline,
                    context.BottomTriangles,
                    baseIndex,
                    !context.IsClockwise);
                return;
            }

            var isExteriorVertex = new bool[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                int previousEdge = (i - 1 + vertexCount) % vertexCount;
                int currentEdge = i;
                bool previousIsExterior = context.ExteriorEdges == null || context.ExteriorEdges.Contains(previousEdge);
                bool currentIsExterior = context.ExteriorEdges == null || context.ExteriorEdges.Contains(currentEdge);
                isExteriorVertex[i] = previousIsExterior || currentIsExterior;
            }

            Vector2 centroid = Vector2.zero;
            for (int i = 0; i < vertexCount; i++)
            {
                centroid += context.Outline[i];
            }
            centroid /= vertexCount;

            int bevelBaseIndex = context.Vertices.Count;
            for (int segment = 0; segment <= segments; segment++)
            {
                float t = (float)segment / segments;
                float angle = t * Mathf.PI * 0.5f;
                float lerpFactor;
                float exteriorY;

                if (profile == BevelProfile.Convex)
                {
                    lerpFactor = (1f - Mathf.Cos(angle)) * inset;
                    exteriorY = -Mathf.Sin(angle) * bevelDepth;
                }
                else
                {
                    lerpFactor = Mathf.Sin(angle) * inset;
                    exteriorY = -(1f - Mathf.Cos(angle)) * bevelDepth;
                }

                float lerpT = Mathf.Clamp01(lerpFactor);
                float interiorY = -t * bevelDepth;

                for (int i = 0; i < vertexCount; i++)
                {
                    if (isExteriorVertex[i])
                    {
                        Vector2 point = Vector2.Lerp(context.Outline[i], centroid, lerpT);
                        context.Vertices.Add(new Vector3(point.x, exteriorY, -point.y));
                    }
                    else
                    {
                        Vector2 point = context.Outline[i];
                        context.Vertices.Add(new Vector3(point.x, interiorY, -point.y));
                    }

                    Vector2 uvPoint = context.Outline[i];
                    context.UVs.Add(new Vector2(uvPoint.x + 0.5f, uvPoint.y + 0.5f));
                }
            }

            for (int segment = 0; segment < segments; segment++)
            {
                int ringA = bevelBaseIndex + segment * vertexCount;
                int ringB = bevelBaseIndex + (segment + 1) * vertexCount;

                for (int i = 0; i < vertexCount; i++)
                {
                    int next = (i + 1) % vertexCount;

                    if (!context.IsClockwise)
                    {
                        context.BottomTriangles.Add(ringA + i);
                        context.BottomTriangles.Add(ringB + i);
                        context.BottomTriangles.Add(ringA + next);

                        context.BottomTriangles.Add(ringA + next);
                        context.BottomTriangles.Add(ringB + i);
                        context.BottomTriangles.Add(ringB + next);
                    }
                    else
                    {
                        context.BottomTriangles.Add(ringA + i);
                        context.BottomTriangles.Add(ringA + next);
                        context.BottomTriangles.Add(ringB + i);

                        context.BottomTriangles.Add(ringA + next);
                        context.BottomTriangles.Add(ringB + next);
                        context.BottomTriangles.Add(ringB + i);
                    }
                }
            }

            int innerRingStart = bevelBaseIndex + segments * vertexCount;
            var innerOutline = new List<Vector2>(vertexCount);

            for (int i = 0; i < vertexCount; i++)
            {
                if (isExteriorVertex[i])
                {
                    innerOutline.Add(Vector2.Lerp(context.Outline[i], centroid, Mathf.Clamp01(inset)));
                }
                else
                {
                    innerOutline.Add(context.Outline[i]);
                }
            }

            ProceduralTileMeshGenerator.TriangulateCap(
                innerOutline,
                context.BottomTriangles,
                innerRingStart,
                !context.IsClockwise);
        }
    }
}
