using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Microverse.Scripts.Simulation
{
    /// <summary>
    /// "세포" 개체들이 서로 밀고 끌리며 상호작용하는 입자형 CA 시스템.
    /// 더블 버퍼 방식으로 안전하게 병렬 처리.
    /// </summary>
    public class CellAgents : MonoBehaviour
    {
        [Header("Agents")]
        public int agentCount = 600;
        public float radius = 0.08f;
        public float mass = 1f;
        [Range(0f, 2f)] public float sameAttract = 0.6f;
        [Range(0f, 2f)] public float diffRepel = 0.8f;
        [Range(0f, 5f)] public float stiffRepel = 3.0f;
        [Range(0f, 1f)] public float viscosity = 0.15f;
        [Range(0f, 2f)] public float noise = 0.25f;
        public float maxSpeed = 3f;

        [Header("World")]
        public Vector2 worldSize = new Vector2(16, 9);
        public bool wrapEdges = false;

        [Header("Time")]
        public float simHz = 120f;
        public int substeps = 1;

        [Header("Render")]
        public Mesh quadMesh;
        public Material instancedMat;
        public Gradient colorBySpecies;

        // ===== 내부 버퍼 =====
        NativeArray<float2> posCur, posNext;
        NativeArray<float2> velCur, velNext;
        NativeArray<byte> species;

        // ===== 공간 해시 =====
        struct HashHeader { public int start; public int count; }
        NativeArray<HashHeader> grid;
        NativeArray<int> indices;
        int cellsX, cellsY;
        float cellSize;

        // ===== 렌더링 =====
        Matrix4x4[] matrices;
        Vector4[] colors;
        MaterialPropertyBlock mpb;

        float dt;

        void Start()
        {
            Application.targetFrameRate = 120;
            dt = 1f / math.max(30f, simHz);

            if (quadMesh == null) quadMesh = BuildQuad();
            if (instancedMat == null) instancedMat = new Material(Shader.Find("Unlit/Color"));
            mpb = new MaterialPropertyBlock();
            matrices = new Matrix4x4[1023];
            colors = new Vector4[1023];

            posCur = new NativeArray<float2>(agentCount, Allocator.Persistent);
            posNext = new NativeArray<float2>(agentCount, Allocator.Persistent);
            velCur = new NativeArray<float2>(agentCount, Allocator.Persistent);
            velNext = new NativeArray<float2>(agentCount, Allocator.Persistent);
            species = new NativeArray<byte>(agentCount, Allocator.Persistent);

            cellSize = math.max(radius * 2f, 0.05f);
            cellsX = math.max(1, (int)math.ceil(worldSize.x / cellSize));
            cellsY = math.max(1, (int)math.ceil(worldSize.y / cellSize));
            grid = new NativeArray<HashHeader>(cellsX * cellsY, Allocator.Persistent);
            indices = new NativeArray<int>(agentCount, Allocator.Persistent);

            var rng = new Unity.Mathematics.Random(12345);
            for (int i = 0; i < agentCount; i++)
            {
                float x = (rng.NextFloat() - 0.5f) * (worldSize.x * 0.8f);
                float y = (rng.NextFloat() - 0.5f) * (worldSize.y * 0.8f);
                posCur[i] = new float2(x, y);
                velCur[i] = float2.zero;
                species[i] = (byte)(rng.NextFloat() < 0.5f ? 0 : 1);
            }
        }

        void OnDestroy()
        {
            if (posCur.IsCreated) posCur.Dispose();
            if (posNext.IsCreated) posNext.Dispose();
            if (velCur.IsCreated) velCur.Dispose();
            if (velNext.IsCreated) velNext.Dispose();
            if (species.IsCreated) species.Dispose();
            if (grid.IsCreated) grid.Dispose();
            if (indices.IsCreated) indices.Dispose();
        }

        void Update()
        {
            for (int s = 0; s < math.max(1, substeps); s++)
                Step();

            // 렌더링 (GPU 인스턴싱)
            int countInBatch = 0;
            for (int i = 0; i < agentCount; i++)
            {
                Vector3 p = new Vector3(posCur[i].x, posCur[i].y, 0f);
                float d = radius * 2f;
                matrices[countInBatch] = Matrix4x4.TRS(p, Quaternion.identity, new Vector3(d, d, 1f));

                Color c = colorBySpecies.Evaluate(species[i]);
                colors[countInBatch] = new Vector4(c.r, c.g, c.b, 1f);

                countInBatch++;
                if (countInBatch == 1023)
                {
                    mpb.SetVectorArray("_Color", colors);
                    Graphics.DrawMeshInstanced(quadMesh, 0, instancedMat,
                        matrices, countInBatch, mpb,
                        ShadowCastingMode.Off, false, 0, null,
                        LightProbeUsage.Off, null);
                    countInBatch = 0;
                }
            }

            if (countInBatch > 0)
            {
                mpb.SetVectorArray("_Color", colors);
                Graphics.DrawMeshInstanced(quadMesh, 0, instancedMat,
                    matrices, countInBatch, mpb,
                    ShadowCastingMode.Off, false, 0, null,
                    LightProbeUsage.Off, null);
            }
        }

        // ====== 시뮬 ======
        void Step()
        {
            // 1) 공간 해시(싱글 스레드 안정 버전)
            new CountJobSingle
            {
                W = worldSize,
                CellSize = cellSize,
                CellsX = cellsX,
                CellsY = cellsY,
                PosCur = posCur,
                Grid = grid
            }.Run();

            new FillJobSingle
            {
                W = worldSize,
                CellSize = cellSize,
                CellsX = cellsX,
                CellsY = cellsY,
                PosCur = posCur,
                Grid = grid,
                Indices = indices
            }.Run();

            // 2) Force & Integrate
            new ForceIntegrateJob
            {
                W = worldSize,
                Wrap = wrapEdges ? (byte)1 : (byte)0,
                dt = dt,
                mass = mass,
                radius = radius,
                sameAttract = sameAttract,
                diffRepel = diffRepel,
                stiffRepel = stiffRepel,
                viscosity = viscosity,
                noise = noise,
                maxSpeed = maxSpeed,

                CellsX = cellsX,
                CellsY = cellsY,
                CellSize = cellSize,
                Grid = grid,
                Indices = indices,
                PosCur = posCur,
                VelCur = velCur,
                PosNext = posNext,
                VelNext = velNext,
                Species = species
            }.Schedule(agentCount, 128).Complete();

            // 3) 버퍼 스왑
            (posCur, posNext) = (posNext, posCur);
            (velCur, velNext) = (velNext, velCur);
        }

        // ===== JOBS =====
        [BurstCompile]
        struct CountJobSingle : IJob
        {
            public float2 W;
            public float CellSize;
            public int CellsX, CellsY;
            [ReadOnly] public NativeArray<float2> PosCur;
            public NativeArray<HashHeader> Grid;

            public void Execute()
            {
                for (int c = 0; c < Grid.Length; c++)
                {
                    var h = Grid[c];
                    h.count = 0;
                    Grid[c] = h;
                }

                for (int i = 0; i < PosCur.Length; i++)
                {
                    int cid = CellOf(PosCur[i], W, CellSize, CellsX, CellsY);
                    var h = Grid[cid];
                    h.count++;
                    Grid[cid] = h;
                }
            }
        }

        [BurstCompile]
        struct FillJobSingle : IJob
        {
            public float2 W;
            public float CellSize;
            public int CellsX, CellsY;
            [ReadOnly] public NativeArray<float2> PosCur;
            public NativeArray<HashHeader> Grid;
            public NativeArray<int> Indices;

            public void Execute()
            {
                int run = 0;
                for (int c = 0; c < Grid.Length; c++)
                {
                    var h = Grid[c];
                    h.start = run;
                    run += h.count;
                    h.count = 0;
                    Grid[c] = h;
                }

                for (int i = 0; i < PosCur.Length; i++)
                {
                    int cid = CellOf(PosCur[i], W, CellSize, CellsX, CellsY);
                    var h = Grid[cid];
                    int dst = h.start + h.count;
                    Indices[dst] = i;
                    h.count++;
                    Grid[cid] = h;
                }
            }
        }

        [BurstCompile]
        struct ForceIntegrateJob : IJobParallelFor
        {
            public float2 W;
            public byte Wrap;
            public float dt, mass, radius, sameAttract, diffRepel, stiffRepel, viscosity, noise, maxSpeed;

            public int CellsX, CellsY;
            public float CellSize;

            [ReadOnly] public NativeArray<HashHeader> Grid;
            [ReadOnly] public NativeArray<int> Indices;
            [ReadOnly] public NativeArray<float2> PosCur;
            [ReadOnly] public NativeArray<float2> VelCur;
            [ReadOnly] public NativeArray<byte> Species;
            [WriteOnly] public NativeArray<float2> PosNext;
            [WriteOnly] public NativeArray<float2> VelNext;

            public void Execute(int i)
            {
                float2 p = PosCur[i];
                float2 v = VelCur[i];
                byte sp = Species[i];
                float2 f = float2.zero;

                int cx = CellCoord(p.x, W.x, CellSize, CellsX);
                int cy = CellCoord(p.y, W.y, CellSize, CellsY);

                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int nx = cx + ox, ny = cy + oy;
                        if ((uint)nx >= (uint)CellsX || (uint)ny >= (uint)CellsY) continue;
                        var h = Grid[ny * CellsX + nx];

                        for (int k = 0; k < h.count; k++)
                        {
                            int j = Indices[h.start + k];
                            if (j == i) continue;

                            float2 q = PosCur[j];
                            byte sj = Species[j];
                            float2 d = q - p;
                            float dist = math.length(d) + 1e-6f;
                            float2 n = d / dist;

                            float target = radius * 2f;
                            float pen = target - dist;
                            if (pen > 0f) f -= n * (pen * stiffRepel);

                            float desire = (sp == sj) ? (+sameAttract) : (-diffRepel);
                            f += n * desire * math.saturate((dist - target) / (target * 3f));
                        }
                    }

                f += -viscosity * v;
                float2 rnd = new float2(hash(i * 9283 + (int)(p.x * 113)), hash(i * 5311 + (int)(p.y * 73)));
                f += (rnd - 0.5f) * noise;

                v += (f / math.max(1e-3f, mass)) * dt;
                float spd = math.length(v);
                if (spd > maxSpeed) v *= (maxSpeed / spd);
                p += v * dt;

                if (Wrap == 1)
                {
                    p.x = Wrap01(p.x, W.x);
                    p.y = Wrap01(p.y, W.y);
                }
                else
                {
                    if (p.x < -W.x * 0.5f + radius) { p.x = -W.x * 0.5f + radius; v.x *= -0.3f; }
                    if (p.x > W.x * 0.5f - radius) { p.x = W.x * 0.5f - radius; v.x *= -0.3f; }
                    if (p.y < -W.y * 0.5f + radius) { p.y = -W.y * 0.5f + radius; v.y *= -0.3f; }
                    if (p.y > W.y * 0.5f - radius) { p.y = W.y * 0.5f - radius; v.y *= -0.3f; }
                }

                PosNext[i] = p;
                VelNext[i] = v;
            }

            static float Wrap01(float x, float range)
            {
                float half = range * 0.5f;
                if (x < -half) x += range;
                else if (x > half) x -= range;
                return x;
            }

            static int CellCoord(float v, float range, float cell, int cells)
            {
                float half = range * 0.5f;
                int c = (int)math.floor((v + half) / cell);
                return math.clamp(c, 0, cells - 1);
            }
        }

        // ===== 유틸 =====
        static int CellOf(float2 p, float2 W, float cell, int cx, int cy)
        {
            float2 half = W * 0.5f;
            int ix = math.clamp((int)math.floor((p.x + half.x) / cell), 0, cx - 1);
            int iy = math.clamp((int)math.floor((p.y + half.y) / cell), 0, cy - 1);
            return iy * cx + ix;
        }

        static Mesh BuildQuad()
        {
            var m = new Mesh();
            m.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0),
                new Vector3(0.5f, -0.5f, 0),
                new Vector3(0.5f, 0.5f, 0),
                new Vector3(-0.5f, 0.5f, 0)
            };
            m.uv = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(1, 1), new Vector2(0, 1)
            };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            return m;
        }

        static float hash(int x) => math.frac(math.sin(x) * 43758.5453f);

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1, 1, 1, 0.2f);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(worldSize.x, worldSize.y, 0));
        }
    }
}
