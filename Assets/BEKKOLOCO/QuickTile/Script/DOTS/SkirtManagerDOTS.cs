// SkirtManagerDOTS.cs — types DOTS + systèmes (compat sans NonUniformScale)
// - Ajoute ownerId dans SkirtData pour permettre à SkirtManager (Mono) de retrouver son entité
// - Systèmes : ChangeDetection (tag) + Update (applique via LocalTransform)
// - Sans NonUniformScale : le stretch est uniforme (Scale global) au runtime DOTS

using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Collections;

namespace Bekkoloco.DOTS
{
    public struct SkirtData : IComponentData
    {
        public Entity skirtEntity;
        public Entity wallEntity;
        public Entity bottomEntity;

        // Wall Mode Settings
        public bool useWallStretching;
        public float targetWallHeight;

        // Legacy Settings (duplication mode)
        public int wallCount;
        public float wallStep;
        public float scaleValue;

        // State
        public bool isDirty;

        // Match avec le Mono
        public int ownerId;
    }

    public struct SkirtUpdateRequired : IComponentData { }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class SkirtChangeDetectionSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (data, entity) in SystemAPI
                         .Query<RefRO<SkirtData>>()
                         .WithNone<SkirtUpdateRequired>()
                         .WithEntityAccess())
            {
                if (data.ValueRO.isDirty)
                    ecb.AddComponent<SkirtUpdateRequired>(entity);
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SkirtChangeDetectionSystem))]
    public partial class SkirtUpdateSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (data, entity) in SystemAPI
                         .Query<RefRW<SkirtData>>()
                         .WithAll<SkirtUpdateRequired>()
                         .WithEntityAccess())
            {
                ref SkirtData d = ref data.ValueRW;
                if (d.useWallStretching)
                {
                    // Hauteur cible
                    float y = math.max(0, d.targetWallHeight);
                    float step = math.max(0.0001f, d.wallStep);
                    float sY = math.max(0.0001f, y / step); // facteur d'échelle

                    if (d.wallEntity != Entity.Null)
                    {
                        // ⚠️ Pas de NonUniformScale : on applique uniformément
                        var lt = new LocalTransform
                        {
                            Position = new float3(0, y * 0.5f, 0), // centre en mi-hauteur
                            Rotation = quaternion.identity,
                            Scale = sY
                        };
                        ecb.SetComponent(d.wallEntity, lt);
                    }

                    if (d.bottomEntity != Entity.Null)
                    {
                        var ltBottom = new LocalTransform
                        {
                            Position = new float3(0, -0.0001f, 0),
                            Rotation = quaternion.identity,
                            Scale = 1f
                        };
                        ecb.SetComponent(d.bottomEntity, ltBottom);
                    }
                }
                else
                {
                    float h = math.max(0, d.wallCount) * math.max(0.0001f, d.wallStep);

                    if (d.wallEntity != Entity.Null)
                    {
                        var lt = new LocalTransform
                        {
                            Position = new float3(0, h * 0.5f, 0),
                            Rotation = quaternion.identity,
                            Scale = 1f
                        };
                        ecb.SetComponent(d.wallEntity, lt);
                    }

                    if (d.bottomEntity != Entity.Null)
                    {
                        var ltBottom = new LocalTransform
                        {
                            Position = new float3(0, -h, 0),
                            Rotation = quaternion.identity,
                            Scale = 1f
                        };
                        ecb.SetComponent(d.bottomEntity, ltBottom);
                    }
                }

                d.isDirty = false;
                ecb.RemoveComponent<SkirtUpdateRequired>(entity);
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }
    }
}
