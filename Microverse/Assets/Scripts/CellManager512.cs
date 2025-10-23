using Unity.Burst; // C# 을 저수준 네이티브처럼 빠르게. array 를 병렬실행 가능하게함. job.Schedule
using Unity.Collections; // NativeArray<T> 같이 저수준의 Array 를 쓰겠다.
using Unity.Jobs; // 실제 병렬 시스템
using Unity.Mathematics; // float3, float4x4
using UnityEngine; 
using UnityEngine.Rendering; // 저수준 단계에서 렌더링 접근

namespace Microverse.Scripts.Simulation
{
    /// <summary>
    /// "세포" 개체들이 서로 밀고 끌리며 상호작용하는 입자형 CA 시스템.
    /// 더블 버퍼 방식으로 안전하게 병렬 처리.
    /// </summary>
    public class CellAgents : MonoBehaviour
    {
        [Header("Agents")]
        public int agentCount = 10_000;
        public float radius = 0.06f;
        public float mass = 1f;
        [Range(0f, 2f)] public float sameAttract = 0.6f; // 같은 종류의 입자끼리 끌림 정도
        [Range(0f, 2f)] public float diffRepel = 0.8f; // 다른 종류의 입자끼리 밀어내는 힘
        [Range(0f, 5f)] public float stiffRepel = 3.0f; // 강한 반발력
        [Range(0f, 1f)] public float viscosity = 0.15f; // 저항
        [Range(0f, 2f)] public float noise = 0.25f; // 움직임의 무작위성
        public float maxSpeed = 3f;

        [Header("World")]
        public Vector2 worldSize = new Vector2(20, 11.25f);
        public bool wrapEdges = false;

        [Header("Time")]
        public float simHz = 60f; // : 1초에 컴퓨터가 물리를 몇번 계산하나 /  FPS: 그 결과를 화면에 몇번 보여주나
        public int substeps = 1; // 그 1/60 을 몇번에 나눠서 계산하냐: substeps 가 4면 1/60 를 4번에 나눠 계산=물리

        [Header("Render")]
        public Mesh quadMesh;
        public Material instancedMat;
        public Gradient colorBySpecies;

        // ===== 내부 버퍼 =====
        NativeArray<float2> posCur, posNext;
        NativeArray<float2> velCur, velNext;
        NativeArray<byte> species;

        // ===== 공간 해시 =====
        struct HashHeader 
        { 
            public int start;  
            public int count;

            /// <summary>
            /// 일단 count 로 현재 셀에 들어있는 입자계산하고, indices 에 입자하나하나 넣음.
            /// 그리고 다음 셀로 넘어가면 몇번째부터 입자 넣어야하는지 모르니까 start 참고함. *그러면 start 는 마지막 위치 +1 이 되야겠지.
            /// start 위치에 다시 count 개수만큼 차곡차곡 쌓음. 그 마지막 위치 +1 을 또 start 가 기억. 
            /// 무한반복...
            /// </summary>

        }
        NativeArray<HashHeader> grid;
        // 모든 셀의 요소 인덱스를 '연속 메모리'로 납작하게(flat) 저장한 배열
        NativeArray<int> indices;
        int cellsX, cellsY;
        float cellSize; // 해시 격자의 한칸 크기/ 보통 cellsize 가 입자보다 크거나 같게 설정함.

        // 좀 뜬금없지만, 2d 건 3d 건 사실은 컴퓨터가 배열의 형태로 데이터를 읽는것 뿐임. 컴퓨터는 차원의 개념을 이해를 못함
        // 그리고 모니터도 사실은 배열여서 rgb 값을 할당하는것임.

        // ===== 렌더링 =====
        // 그래픽을 10000개의 입자에 하나하나 붗칠하면 컴퓨터 자살함. 그러니까 일관적으로 위치값하고 원하는 색상 요구하면 일괄 적용해주는거임
        Matrix4x4[] matrices; //위치
        Vector4[] colors;  //색
        MaterialPropertyBlock mpb; //위치&색

        float dt; // 1/60초

        void Start()
        {
            Application.targetFrameRate = 120;
            dt = 1f / math.max(30f, simHz); // 최소 시뮬레이션 횟수보장: 30회

            if (quadMesh == null) quadMesh = BuildQuad(); //quad 없으면 생성
            if (instancedMat == null) instancedMat = new Material(Shader.Find("Unlit/Color")); //material 없으면 생성
            mpb = new MaterialPropertyBlock(); // 세포별 색상을 셰이더로 넘길 프로퍼티 블록
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

            if (colorBySpecies == null || colorBySpecies.colorKeys == null || colorBySpecies.colorKeys.Length == 0)
            {
                var g = new Gradient();
                g.SetKeys(
                    new[]
                    {
            new GradientColorKey(new Color(1f, 0.85f, 0.2f), 0f), // 종 A = 노랑
            new GradientColorKey(new Color(0.2f, 0.8f, 1f), 1f)   // 종 B = 시안
                    },
                    new[]
                    {
            new GradientAlphaKey(1f, 0f),
            new GradientAlphaKey(1f, 1f)
                    }
                );
                colorBySpecies = g;
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
