using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

using Microverse.Scripts.Simulation; // PlayerController
using Microverse.Scripts.Simulation.Runtime.Data;
using Microverse.Scripts.Simulation.Runtime.Jobs;
using Unity.Jobs;

namespace Microverse.Scripts.Simulation.Runtime.Systems
{
    /// <summary>
    /// CellSimulation: 분할 구조( Data/Modules/Jobs )를 오케스트레이션하고 렌더링까지 담당하는 메인 매니저
    /// </summary>
    public class CellSimulation : MonoBehaviour
    {
        [Header("Agents")]
        public int agentCount = 10_000;
        public float radius = 0.06f;
        public float mass = 1f;
        [Range(0f, 2f)] public float sameAttract = 0.0f;
        [Range(0f, 2f)] public float diffRepel = 0.8f;
        [Range(0f, 5f)] public float stiffRepel = 3.0f;
        [Range(0f, 1f)] public float viscosity = 0.15f;
        [Range(0f, 2f)] public float noise = 0.25f;
        public float maxSpeed = 3f;

        [Header("World")]
        public Vector2 worldSize = new Vector2(20, 11.25f);
        public bool wrapEdges = false;

        [Header("Time")]
        public float simHz = 60f;
        public int substeps = 1;

        [Header("Render")]
        public Mesh quadMesh;
        public Material instancedMat;
        public Gradient colorBySpecies;

        [Header("Player")]
        public PlayerController player;
        public bool playerAttract = false; // true=끌림 / false=밀침
        public float playerForce = 5f;
        public float playerRange = 1.0f;

        [Header("Gameplay")]
        public float playerEatRadius = 0.12f;
        public float growthPerEat = 0.01f;
        public float maxPlayerRadius = 0.8f;

        public float wbcChaseForce = 2.5f;
        public float wbcLatchRadius = 0.10f;
        public float wbcLatchSpring = 25f;
        public float wbcDPS = 4f;
        public int wbcMaxLatched = 6;
        public float latchTTLSeconds = 6f;
        public float shakeBreakSpeed = 2.0f;

        [Header("Solver")]
        [Range(0, 8)] public int solveIterations = 2;

        // ===== 내부 상태 =====
        Buffers B;
        GridData G;
        SimParams P;
        float dt;
        uint tick;

        // 이벤트 큐(잡 → 메인)
        NativeQueue<int> eatenQueue;     // 먹힌 일반 세포 인덱스
        NativeQueue<int> latchQueue;     // 라치된 백혈구 인덱스
        NativeQueue<int> unlatchQueue;   // 라치 해제 인덱스

        // 렌더링 캐시
        Matrix4x4[] matrices;
        Vector4[] colors;
        MaterialPropertyBlock mpb;

        void Start()
        {
            Application.targetFrameRate = 120;
            dt = 1f / math.max(30f, simHz);

            if (quadMesh == null) quadMesh = BuildQuad();
            if (instancedMat == null) instancedMat = new Material(Shader.Find("Unlit/Color"));
            if (colorBySpecies == null || colorBySpecies.colorKeys == null || colorBySpecies.colorKeys.Length == 0)
            {
                var g = new Gradient();
                g.SetKeys(
                    new[]
                    {
                        new GradientColorKey(new Color(1f, 0.85f, 0.2f), 0f), // 일반(노랑)
                        new GradientColorKey(new Color(0.95f, 0.2f, 0.2f), 1f) // 백혈구(빨강)
                    },
                    new[]
                    {
                        new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(1f, 1f)
                    }
                );
                colorBySpecies = g;
            }

            mpb = new MaterialPropertyBlock();
            matrices = new Matrix4x4[1023];
            colors = new Vector4[1023];

            // ===== 버퍼 할당 =====
            B.posCur = new NativeArray<float2>(agentCount, Allocator.Persistent);
            B.posNext = new NativeArray<float2>(agentCount, Allocator.Persistent);
            B.velCur = new NativeArray<float2>(agentCount, Allocator.Persistent);
            B.velNext = new NativeArray<float2>(agentCount, Allocator.Persistent);
            B.species = new NativeArray<byte>(agentCount, Allocator.Persistent);
            B.state = new NativeArray<byte>(agentCount, Allocator.Persistent);
            B.latchOffset = new NativeArray<float2>(agentCount, Allocator.Persistent);
            B.latchTTL = new NativeArray<float>(agentCount, Allocator.Persistent);
            B.posSnap = new NativeArray<float2>(agentCount, Allocator.Persistent);

            eatenQueue = new NativeQueue<int>(Allocator.Persistent);
            latchQueue = new NativeQueue<int>(Allocator.Persistent);
            unlatchQueue = new NativeQueue<int>(Allocator.Persistent);

            // ===== 그리드 =====
            G.worldSize = worldSize;
            G.cellSize = math.max(radius * 2f, 0.05f);
            G.cellsX = math.max(1, (int)math.ceil(worldSize.x / G.cellSize));
            G.cellsY = math.max(1, (int)math.ceil(worldSize.y / G.cellSize));
            G.grid = new NativeArray<HashHeader>(G.cellsX * G.cellsY, Allocator.Persistent);
            G.indices = new NativeArray<int>(agentCount, Allocator.Persistent);

            // ===== 에이전트 초기화 =====
            var rng = new Unity.Mathematics.Random(12345);
            for (int i = 0; i < agentCount; i++)
            {
                float x = (rng.NextFloat() - 0.5f) * (worldSize.x * 0.8f);
                float y = (rng.NextFloat() - 0.5f) * (worldSize.y * 0.8f);
                B.posCur[i] = new float2(x, y);
                B.velCur[i] = 0;

                // 종 초기화: 0=일반(80%), 1=백혈구(20%)
                B.species[i] = (byte)(rng.NextFloat() < 0.8f ? 0 : 1);
                B.state[i] = 0; // Alive
                B.latchOffset[i] = 0;
                B.latchTTL[i] = 0f;
            }

            if (player != null)
            {
                player.worldSize = worldSize;
                player.wrapEdges = wrapEdges;
            }
        }

        void OnDestroy()
        {
            B.Dispose();
            G.Dispose();
            if (eatenQueue.IsCreated) eatenQueue.Dispose();
            if (latchQueue.IsCreated) latchQueue.Dispose();
            if (unlatchQueue.IsCreated) unlatchQueue.Dispose();
        }

        void Update()
        {
            // SimParams 채우기
            P = new SimParams
            {
                worldSize = worldSize,
                wrapEdges = wrapEdges,
                dt = dt,
                radius = radius,
                mass = mass,
                sameAttract = sameAttract,
                diffRepel = diffRepel,
                stiffRepel = stiffRepel,
                viscosity = viscosity,
                noise = noise,
                maxSpeed = maxSpeed,

                playerEnabled = (byte)((player != null && player.enablePlayer) ? 1 : 0),
                playerPos = (player != null) ? player.Pos : 0,
                playerVel = (player != null) ? player.Vel : 0,
                playerRadius = (player != null) ? player.radius : 0f,
                playerForce = (playerAttract ? +playerForce : -playerForce),
                playerRange = playerRange,

                playerEatRadius = playerEatRadius,
                wbcChaseForce = wbcChaseForce,
                wbcLatchRadius = wbcLatchRadius,
                wbcLatchSpring = wbcLatchSpring,
                shakeBreakSpeed = shakeBreakSpeed,

                tick = tick
            };

            for (int s = 0; s < math.max(1, substeps); s++)
                Step();

            Draw();
        }

        // ====== 시뮬 한 스텝 ======
        void Step()
        {
            // 1) 공간 해시 (posCur 기준)
            new CountJobSingle { W = worldSize, CellSize = G.cellSize, CellsX = G.cellsX, CellsY = G.cellsY, PosCur = B.posCur, Grid = G.grid }.Run();
            new FillJobSingle { W = worldSize, CellSize = G.cellSize, CellsX = G.cellsX, CellsY = G.cellsY, PosCur = B.posCur, Grid = G.grid, Indices = G.indices }.Run();

            // 2) Force & Integrate
            new ForceIntegrateJob
            {
                // Sim
                P = P,

                // Grid
                CellsX = G.cellsX,
                CellsY = G.cellsY,
                CellSize = G.cellSize,
                Grid = G.grid,
                Indices = G.indices,

                // Buffers
                PosCur = B.posCur,
                VelCur = B.velCur,
                Species = B.species,
                State = B.state,
                PosNext = B.posNext,
                VelNext = B.velNext,

                LatchOffset = B.latchOffset,
                LatchTTL = B.latchTTL,

                // Events
                Eaten = eatenQueue.AsParallelWriter(),
                Latched = latchQueue.AsParallelWriter(),
                Unlatched = unlatchQueue.AsParallelWriter(),
            }.Schedule(agentCount, 128).Complete();

            // 2-1) 평균 속도 제거 (전역 순이동 제거)
            float2 vAvg = 0;
            for (int i = 0; i < agentCount; i++) vAvg += B.velNext[i];
            vAvg /= math.max(1, agentCount);
            for (int i = 0; i < agentCount; i++) B.velNext[i] -= vAvg;

            // ===== PBD =====
            new CopyJob { Src = B.posNext, Dst = B.posSnap }.Schedule(agentCount, 128).Complete();

            new CountJobSingle { W = worldSize, CellSize = G.cellSize, CellsX = G.cellsX, CellsY = G.cellsY, PosCur = B.posSnap, Grid = G.grid }.Run();
            new FillJobSingle { W = worldSize, CellSize = G.cellSize, CellsX = G.cellsX, CellsY = G.cellsY, PosCur = B.posSnap, Grid = G.grid, Indices = G.indices }.Run();

            for (int it = 0; it < math.max(1, solveIterations); it++)
            {
                new ProjectNoOverlapJob
                {
                    W = worldSize,
                    radius = radius,
                    CellsX = G.cellsX,
                    CellsY = G.cellsY,
                    CellSize = G.cellSize,
                    Grid = G.grid,
                    Indices = G.indices,
                    PosRead = B.posSnap,
                    PosWrite = B.posNext,
                    Wrap = (byte)(wrapEdges ? 1 : 0)
                }.Schedule(agentCount, 128).Complete();

                (B.posSnap, B.posNext) = (B.posNext, B.posSnap); // ping-pong
            }

            // 최종 확정
            (B.posCur, B.posSnap) = (B.posSnap, B.posCur);
            (B.velCur, B.velNext) = (B.velNext, B.velCur);

            // ===== 이벤트 처리 (메인 스레드) =====
            // 1) 먹기: 일반 세포 Dead 처리 + 플레이어 성장
            int eatenCount = 0;
            while (eatenQueue.TryDequeue(out int idxEat))
            {
                if (idxEat >= 0 && idxEat < agentCount && B.state[idxEat] == 0 && B.species[idxEat] == 0)
                {
                    B.state[idxEat] = 1; // Dead
                    eatenCount++;
                }
            }
            if (eatenCount > 0 && player != null)
                player.radius = math.min(maxPlayerRadius, player.radius + growthPerEat * eatenCount);

            // 2) 라치/언라치 처리
            int latchedNow = 0;
            while (latchQueue.TryDequeue(out int idxLa))
            {
                if (idxLa >= 0 && idxLa < agentCount && B.species[idxLa] == 1 && B.state[idxLa] == 0 && latchedNow < wbcMaxLatched)
                {
                    B.state[idxLa] = 2; // Latched
                    float angle = 2f * math.PI * (latchedNow / (float)math.max(1, wbcMaxLatched));
                    float attachR = (player != null ? player.radius : 0f) + radius * 1.2f;
                    B.latchOffset[idxLa] = new float2(math.cos(angle), math.sin(angle)) * attachR;
                    B.latchTTL[idxLa] = latchTTLSeconds;
                    latchedNow++;
                }
            }
            while (unlatchQueue.TryDequeue(out int idxUn))
            {
                if (idxUn >= 0 && idxUn < agentCount && B.state[idxUn] == 2)
                    B.state[idxUn] = 0; // 자유
            }

            // 3) 라치된 백혈구 → DPS/TTL
            if (player != null)
            {
                float dtLocal = dt;
                for (int i = 0; i < agentCount; i++)
                {
                    if (B.state[i] == 2 && B.species[i] == 1)
                    {
                        player.health = math.max(0f, player.health - wbcDPS * dtLocal);
                        B.latchTTL[i] -= dtLocal;
                        if (B.latchTTL[i] <= 0f)
                            B.state[i] = 1; // 수명 끝 → 소멸
                    }
                }
            }

            tick++;
        }

        // ===== 렌더링 =====
        void Draw()
        {
            int countInBatch = 0;
            for (int i = 0; i < agentCount; i++)
            {
                if (B.state[i] == 1) continue; // Dead skip

                Vector3 p = new Vector3(B.posCur[i].x, B.posCur[i].y, 0f);
                float d = radius * 2f;
                matrices[countInBatch] = Matrix4x4.TRS(p, Quaternion.identity, new Vector3(d, d, 1f));

                // 라치된 백혈구 강조색
                Color c = (B.state[i] == 2 && B.species[i] == 1)
                    ? new Color(1f, 0.4f, 0.4f, 1f)
                    : colorBySpecies.Evaluate(B.species[i]);
                colors[countInBatch] = new Vector4(c.r, c.g, c.b, 1f);

                countInBatch++;
                if (countInBatch == 1023)
                {
                    mpb.SetVectorArray("_Color", colors);
                    Graphics.DrawMeshInstanced(quadMesh, 0, instancedMat, matrices, countInBatch, mpb,
                        ShadowCastingMode.Off, false, 0, null, LightProbeUsage.Off, null);
                    countInBatch = 0;
                }
            }

            if (countInBatch > 0)
            {
                mpb.SetVectorArray("_Color", colors);
                Graphics.DrawMeshInstanced(quadMesh, 0, instancedMat, matrices, countInBatch, mpb,
                    ShadowCastingMode.Off, false, 0, null, LightProbeUsage.Off, null);
            }
        }

        // ===== 유틸 =====
        static Mesh BuildQuad()
        {
            var m = new Mesh();
            m.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0),
                new Vector3( 0.5f, -0.5f, 0),
                new Vector3( 0.5f,  0.5f, 0),
                new Vector3(-0.5f,  0.5f, 0)
            };
            m.uv = new[]
            {
                new Vector2(0,0), new Vector2(1,0),
                new Vector2(1,1), new Vector2(0,1)
            };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            return m;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1, 1, 1, 0.2f);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(worldSize.x, worldSize.y, 0));
        }
    }
}
