using UnityEngine;

namespace Bekkoloco
{
    internal static class ProceduralTileBottomIslandGenerator
    {
        internal static void Generate(ProceduralTileBottomContext context)
        {
            int vertexCount = context.Outline.Count;
            int rings = Mathf.Max(1, context.Settings.bottomNoiseResolution);

            if (vertexCount < 3)
            {
                ProceduralTileBottomFlatGenerator.Generate(context);
                return;
            }

            Vector2 centroid = Vector2.zero;
            for (int i = 0; i < vertexCount; i++)
            {
                centroid += context.Outline[i];
            }
            centroid /= vertexCount;

            int baseIndex = context.Vertices.Count;
            bool flipWinding = !context.IsClockwise;

            if (rings == 1)
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    Vector2 point = context.Outline[i];
                    context.Vertices.Add(new Vector3(point.x, 0f, -point.y));
                    context.UVs.Add(new Vector2(point.x + 0.5f, point.y + 0.5f));
                }

                int centroidIndex = context.Vertices.Count;
                context.Vertices.Add(new Vector3(centroid.x, 0f, -centroid.y));
                context.UVs.Add(new Vector2(centroid.x + 0.5f, centroid.y + 0.5f));

                for (int i = 0; i < vertexCount; i++)
                {
                    int next = (i + 1) % vertexCount;
                    int currentIndex = baseIndex + i;
                    int nextIndex = baseIndex + next;

                    if (flipWinding)
                    {
                        context.BottomTriangles.Add(currentIndex);
                        context.BottomTriangles.Add(centroidIndex);
                        context.BottomTriangles.Add(nextIndex);
                    }
                    else
                    {
                        context.BottomTriangles.Add(currentIndex);
                        context.BottomTriangles.Add(nextIndex);
                        context.BottomTriangles.Add(centroidIndex);
                    }
                }

                return;
            }

            for (int ring = 0; ring <= rings; ring++)
            {
                float t = (float)ring / rings;

                for (int i = 0; i < vertexCount; i++)
                {
                    Vector2 point = Vector2.Lerp(context.Outline[i], centroid, t);
                    context.Vertices.Add(new Vector3(point.x, 0f, -point.y));
                    context.UVs.Add(new Vector2(point.x + 0.5f, point.y + 0.5f));
                }
            }

            int finalCentroidIndex = context.Vertices.Count;
            context.Vertices.Add(new Vector3(centroid.x, 0f, -centroid.y));
            context.UVs.Add(new Vector2(centroid.x + 0.5f, centroid.y + 0.5f));

            for (int ring = 0; ring < rings; ring++)
            {
                int ringA = baseIndex + ring * vertexCount;
                int ringB = baseIndex + (ring + 1) * vertexCount;

                for (int i = 0; i < vertexCount; i++)
                {
                    int next = (i + 1) % vertexCount;
                    int a1 = ringA + i;
                    int a2 = ringA + next;
                    int b1 = ringB + i;
                    int b2 = ringB + next;

                    if (flipWinding)
                    {
                        context.BottomTriangles.Add(a1);
                        context.BottomTriangles.Add(b1);
                        context.BottomTriangles.Add(a2);

                        context.BottomTriangles.Add(a2);
                        context.BottomTriangles.Add(b1);
                        context.BottomTriangles.Add(b2);
                    }
                    else
                    {
                        context.BottomTriangles.Add(a1);
                        context.BottomTriangles.Add(a2);
                        context.BottomTriangles.Add(b1);

                        context.BottomTriangles.Add(a2);
                        context.BottomTriangles.Add(b2);
                        context.BottomTriangles.Add(b1);
                    }
                }
            }

            int lastRingStart = baseIndex + rings * vertexCount;
            for (int i = 0; i < vertexCount; i++)
            {
                int next = (i + 1) % vertexCount;
                int currentIndex = lastRingStart + i;
                int nextIndex = lastRingStart + next;

                if (flipWinding)
                {
                    context.BottomTriangles.Add(currentIndex);
                    context.BottomTriangles.Add(finalCentroidIndex);
                    context.BottomTriangles.Add(nextIndex);
                }
                else
                {
                    context.BottomTriangles.Add(currentIndex);
                    context.BottomTriangles.Add(nextIndex);
                    context.BottomTriangles.Add(finalCentroidIndex);
                }
            }
        }
    }
}
