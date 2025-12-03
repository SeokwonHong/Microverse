using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Jobs;

using Microverse.Scripts.Simulation;
using Microverse.Scripts.Simulation.Runtime.Data;
using Microverse.Scripts.Simulation.Runtime.Jobs;

namespace Microverse.Scripts.Simulation.Runtime.Systems
{

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
        public Material instancedMat; // "Unlit/CellSDFInstanced_Advanced" 권장
        public Gradient colorBySpecies;

        [Header("Player")]
        public PlayerController player;
        public bool playerAttract = false;
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

        [Header("WBC Settings")]
        public bool wbcEnabled = true;

        // ===== 내부 상태 =====
        Buffers B;
        GridData G;
        SimParams P;
        float dt;
        uint tick;

        NativeQueue<int> eatenQueue, latchQueue, unlatchQueue;

        // 렌더 캐시
        Matrix4x4[] matrices;
        Vector4[] colors;
        MaterialPropertyBlock mpb;

        // 덴트 히스토그램(프레임 임시)
        NativeArray<float> dentHist;

        // 덴트 per-batch 캐시
        Vector4[] dentDir0Arr, dentDir1Arr, dentDir2Arr, dentDir3Arr;
        float[] dentAmp0Arr, dentAmp1Arr, dentAmp2Arr, dentAmp3Arr;


        NativeArray<float> phasePerAgent;   // 길이 = agentCount
        Vector4[] phaseVecBatch;

        void Start()
        {

            
            Application.targetFrameRate = 120;
            dt = 1f / math.max(30f, simHz);

            if (quadMesh == null) quadMesh = BuildQuad();
            if (instancedMat == null) instancedMat = new Material(Shader.Find("Unlit/CellSDFInstanced_Advanced_Dent_Osc"));

            if (colorBySpecies == null || colorBySpecies.colorKeys == null || colorBySpecies.colorKeys.Length == 0)
            {
                var g = new Gradient();
                g.SetKeys(
                    new[]
                    {
                        new GradientColorKey(new Color(1f, 0.85f, 0.2f), 0f),
                        new GradientColorKey(new Color(0.95f, 0.2f, 0.2f), 1f)
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

            dentDir0Arr = new Vector4[1023];
            dentDir1Arr = new Vector4[1023];
            dentDir2Arr = new Vector4[1023];
            dentDir3Arr = new Vector4[1023];
            dentAmp0Arr = new float[1023];
            dentAmp1Arr = new float[1023];
            dentAmp2Arr = new float[1023];
            dentAmp3Arr = new float[1023];

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

            // ★ 스쿼시
            B.squash = new NativeArray<float>(agentCount, Allocator.Persistent);
            B.squashN = new NativeArray<float2>(agentCount, Allocator.Persistent);

            // ★ 덴트 결과
            B.dentDir0 = new NativeArray<float2>(agentCount, Allocator.Persistent);
            B.dentDir1 = new NativeArray<float2>(agentCount, Allocator.Persistent);
            B.dentDir2 = new NativeArray<float2>(agentCount, Allocator.Persistent);
            B.dentDir3 = new NativeArray<float2>(agentCount, Allocator.Persistent);
            B.dentAmp0 = new NativeArray<float>(agentCount, Allocator.Persistent);
            B.dentAmp1 = new NativeArray<float>(agentCount, Allocator.Persistent);
            B.dentAmp2 = new NativeArray<float>(agentCount, Allocator.Persistent);
            B.dentAmp3 = new NativeArray<float>(agentCount, Allocator.Persistent);

            dentHist = new NativeArray<float>(agentCount * 8, Allocator.Persistent);

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

                B.species[i] = (byte)(rng.NextFloat() < 0.8f ? 0 : 1);
                B.state[i] = 0;
                B.latchOffset[i] = 0;
                B.latchTTL[i] = 0f;

                B.squash[i] = 0f;
                B.squashN[i] = new float2(0, 1);
            }

            if (player != null)
            {
                player.worldSize = worldSize;
                player.wrapEdges = wrapEdges;
            }


            phasePerAgent = new NativeArray<float>(agentCount, Allocator.Persistent);
            phaseVecBatch = new Vector4[1023];

            // 에이전트별 랜덤 위상 0..1
            var rngPhase = new Unity.Mathematics.Random(0xBEEF1234);
            for (int i = 0; i < agentCount; i++)
                phasePerAgent[i] = rngPhase.NextFloat();
        }

        void OnDestroy()
        {
            B.Dispose();
            G.Dispose();
            if (eatenQueue.IsCreated) eatenQueue.Dispose();
            if (latchQueue.IsCreated) latchQueue.Dispose();
            if (unlatchQueue.IsCreated) unlatchQueue.Dispose();
            if (dentHist.IsCreated) dentHist.Dispose();
            if (phasePerAgent.IsCreated) phasePerAgent.Dispose();
        }

        void Update()
        {
            

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

                wbcEnabled = (byte)(wbcEnabled ? 1 : 0),


                tick = tick
            };

            for (int s = 0; s < math.max(1, substeps); s++)
                Step();

            Draw();
        }

        void Step()
        {
            // 1) 공간 해시
            new CountJobSingle { W = worldSize, CellSize = G.cellSize, CellsX = G.cellsX, CellsY = G.cellsY, PosCur = B.posCur, Grid = G.grid }.Run();
            new FillJobSingle { W = worldSize, CellSize = G.cellSize, CellsX = G.cellsX, CellsY = G.cellsY, PosCur = B.posCur, Grid = G.grid, Indices = G.indices }.Run();

            // 2) Force & Integrate
            new ForceIntegrateJob
            {
                P = P,
                CellsX = G.cellsX,
                CellsY = G.cellsY,
                CellSize = G.cellSize,
                Grid = G.grid,
                Indices = G.indices,
                PosCur = B.posCur,
                VelCur = B.velCur,
                Species = B.species,
                State = B.state,
                PosNext = B.posNext,
                VelNext = B.velNext,
                LatchOffset = B.latchOffset,
                LatchTTL = B.latchTTL,
                Eaten = eatenQueue.AsParallelWriter(),
                Latched = latchQueue.AsParallelWriter(),
                Unlatched = unlatchQueue.AsParallelWriter(),
            }.Schedule(agentCount, 128).Complete();

            // 2-1) 평균 속도 제거
            float2 vAvg = 0;
            for (int i = 0; i < agentCount; i++) vAvg += B.velNext[i];
            vAvg /= math.max(1, agentCount);
            for (int i = 0; i < agentCount; i++) B.velNext[i] -= vAvg;

            // ===== PBD =====
            new CopyJob { Src = B.posNext, Dst = B.posSnap }.Schedule(agentCount, 128).Complete();
            new CountJobSingle { W = worldSize, CellSize = G.cellSize, CellsX = G.cellsX, CellsY = G.cellsY, PosCur = B.posSnap, Grid = G.grid }.Run();
            new FillJobSingle { W = worldSize, CellSize = G.cellSize, CellsX = G.cellsX, CellsY = G.cellsY, PosCur = B.posSnap, Grid = G.grid, Indices = G.indices }.Run();

            new ClearDentHistJob { Hist = dentHist }.Schedule(agentCount * 8, 256).Complete();

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

                new AccumulateDentBinsJob
                {
                    before = B.posSnap,
                    after = B.posNext,
                    dentHist = dentHist,
                    ampScale = 1f / math.max(1e-6f, radius)
                }.Schedule(agentCount, 128).Complete();

                (B.posSnap, B.posNext) = (B.posNext, B.posSnap);
            }

            new SelectTop4DentJob
            {
                dentHist = dentHist,
                dir0 = B.dentDir0,
                dir1 = B.dentDir1,
                dir2 = B.dentDir2,
                dir3 = B.dentDir3,
                amp0 = B.dentAmp0,
                amp1 = B.dentAmp1,
                amp2 = B.dentAmp2,
                amp3 = B.dentAmp3,
                ampMax = 0.45f
            }.Schedule(agentCount, 128).Complete();

            new SquashFromDeltaJob
            {
                posBefore = B.posCur,
                posAfter = B.posSnap,
                squash = B.squash,
                squashN = B.squashN,
                radius = radius,
                decay = 0.98f, // ← 복원 느리게(더 말캉)
                gain = 1.15f  // ← 눌림 더 크게
            }.Schedule(agentCount, 128).Complete();

            // 확정 스왑 (한 번만)
            (B.posCur, B.posSnap) = (B.posSnap, B.posCur);
            (B.velCur, B.velNext) = (B.velNext, B.velCur);

            // ===== 이벤트 처리 (생략: 네 기존 코드와 동일) =====
            int eatenCount = 0;
            while (eatenQueue.TryDequeue(out int idxEat))
            {
                if (idxEat >= 0 && idxEat < agentCount && B.state[idxEat] == 0 && B.species[idxEat] == 0)
                { B.state[idxEat] = 1; eatenCount++; }
            }
            if (eatenCount > 0 && player != null)
                player.radius = math.min(maxPlayerRadius, player.radius + growthPerEat * eatenCount);

            int latchedNow = 0;
            while (latchQueue.TryDequeue(out int idxLa))
            {
                if (idxLa >= 0 && idxLa < agentCount && B.species[idxLa] == 1 && B.state[idxLa] == 0 && latchedNow < wbcMaxLatched)
                {
                    B.state[idxLa] = 2;
                    float angle = 2f * math.PI * (latchedNow / (float)math.max(1, wbcMaxLatched));
                    float attachR = (player != null ? player.radius : 0f) + radius * 1.2f;
                    B.latchOffset[idxLa] = new float2(math.cos(angle), math.sin(angle)) * attachR;
                    B.latchTTL[idxLa] = latchTTLSeconds;
                    latchedNow++;
                }
            }
            while (unlatchQueue.TryDequeue(out int idxUn))
            {
                if (idxUn >= 0 && idxUn < agentCount && B.state[idxUn] == 2) B.state[idxUn] = 0;
            }

            if (player != null)
            {
                float dtLocal = dt;
                for (int i = 0; i < agentCount; i++)
                {
                    if (B.state[i] == 2 && B.species[i] == 1)
                    {
                        player.health = math.max(0f, player.health - wbcDPS * dtLocal);
                        B.latchTTL[i] -= dtLocal;
                        if (B.latchTTL[i] <= 0f) B.state[i] = 1;
                    }
                }
            }

            tick++;
        }

        void Draw()
        {
            int countInBatch = 0;

            for (int i = 0; i < agentCount; i++)
            {
                if (B.state[i] == 1) continue;

                Vector3 p = new Vector3(B.posCur[i].x, B.posCur[i].y, 0f);

                // === 전체 스쿼시 TRS ===
                // ▼▼▼ 시각 과장계수: 더 많이 찌그러지게 하려면 두 배수 올리기 ▼▼▼
                float s = math.saturate(B.squash[i] * 0.60f); // 0.35 → 0.60 (스케일 업)
                s = math.saturate(s * 3.0f);                  // 2.0 → 3.0 (과장 업)


                const float SQUASH_VISUAL_MAX = 0.2f;
                s = math.min(s, SQUASH_VISUAL_MAX);
                float d = radius * 2f;
                float minor = d * (1f - s);
                float major = d * (1f + s);

                float2 nrm = B.squashN[i];
                if (math.lengthsq(nrm) < 1e-6f) nrm = new float2(0, 1);
                float2 tan = new float2(-nrm.y, nrm.x);
                Quaternion rot = Quaternion.FromToRotation(Vector3.right, new Vector3(tan.x, tan.y, 0));

                matrices[countInBatch] = Matrix4x4.TRS(p, rot, new Vector3(major, minor, 1f));

                // 색
                Color c = (B.state[i] == 2 && B.species[i] == 1)
                    ? new Color(1f, 0.4f, 0.4f, 1f)
                    : colorBySpecies.Evaluate(B.species[i]);
                colors[countInBatch] = new Vector4(c.r, c.g, c.b, 1f);

                // === 덴트 per-instance ===
                dentDir0Arr[countInBatch] = new Vector4(B.dentDir0[i].x, B.dentDir0[i].y, 0, 0);
                dentDir1Arr[countInBatch] = new Vector4(B.dentDir1[i].x, B.dentDir1[i].y, 0, 0);
                dentDir2Arr[countInBatch] = new Vector4(B.dentDir2[i].x, B.dentDir2[i].y, 0, 0);
                dentDir3Arr[countInBatch] = new Vector4(B.dentDir3[i].x, B.dentDir3[i].y, 0, 0);
                dentAmp0Arr[countInBatch] = B.dentAmp0[i];
                dentAmp1Arr[countInBatch] = B.dentAmp1[i];
                dentAmp2Arr[countInBatch] = B.dentAmp2[i];
                dentAmp3Arr[countInBatch] = B.dentAmp3[i];

                phaseVecBatch[countInBatch] = new Vector4(phasePerAgent[i], 0f, 0f, 0f);


                countInBatch++;
                if (countInBatch == 1023) { Flush(countInBatch); countInBatch = 0; }
            }

            if (countInBatch > 0) Flush(countInBatch);
        }

        void Flush(int n)
        {
            mpb.SetVectorArray("_Color", colors);
            mpb.SetVectorArray("_DentDir0", dentDir0Arr);
            mpb.SetVectorArray("_DentDir1", dentDir1Arr);
            mpb.SetVectorArray("_DentDir2", dentDir2Arr);
            mpb.SetVectorArray("_DentDir3", dentDir3Arr);
            mpb.SetFloatArray("_DentAmp0", dentAmp0Arr);
            mpb.SetFloatArray("_DentAmp1", dentAmp1Arr);
            mpb.SetFloatArray("_DentAmp2", dentAmp2Arr);
            mpb.SetFloatArray("_DentAmp3", dentAmp3Arr);
            mpb.SetVectorArray("_PhaseVec", phaseVecBatch);

            Graphics.DrawMeshInstanced(
                quadMesh, 0, instancedMat, matrices, n, mpb,
                ShadowCastingMode.Off, false, 0, null, LightProbeUsage.Off, null
            );
        }

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
