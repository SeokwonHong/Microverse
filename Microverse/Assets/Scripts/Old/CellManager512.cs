using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Microverse.Scripts.Simulation
{
    /// <summary>
    /// CellManager512: 입자형 세포 시뮬 + 플레이어 먹기/백혈구 라치까지 한 파일에 구현
    /// </summary>
    public class CellManager512 : MonoBehaviour
    {
        [Header("Agents")]
        public int agentCount = 10_000;
        public float radius = 0.06f;
        public float mass = 1f;
        [Range(0f, 2f)] public float sameAttract = 0.0f; // 집단 드리프트 방지를 위해 기본 0
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
        public PlayerController player;     // 인스펙터 연결
        public bool playerAttract = false; // true=끌림 / false=밀침(기존 기능 유지)
        public float playerForce = 5f;
        public float playerRange = 1.0f;

        [Header("Gameplay")]
        public float playerEatRadius = 0.12f;  // 플레이어가 일반 세포를 먹는 추가 여유 반경
        public float growthPerEat = 0.01f;   // 일반 세포 하나 먹을 때 플레이어 반지름 증가량
        public float maxPlayerRadius = 0.8f;   // 플레이어 최대 반지름

        public float wbcChaseForce = 2.5f;    // 백혈구(종1) 플레이어 추적 가중치
        public float wbcLatchRadius = 0.10f;   // 라치 판정 여유
        public float wbcLatchSpring = 25f;     // 라치 스프링 강성
        public float wbcDPS = 4f;      // 라치 1개당 초당 피해
        public int wbcMaxLatched = 6;       // 동시 라치 최대 수
        public float latchTTLSeconds = 6f;     // 라치 유지 시간
        public float shakeBreakSpeed = 2.0f;   // 플레이어 속도 임계(흔들어 떼기)

        // ===== 내부 버퍼 =====
        NativeArray<float2> posCur, posNext;
        NativeArray<float2> velCur, velNext;
        NativeArray<byte> species;

        // 상태: 0=Alive, 1=Dead, 2=Latched(백혈구 전용)
        NativeArray<byte> state;
        NativeArray<float2> latchOffset; // 라치된 백혈구의 플레이어 기준 고정 오프셋
        NativeArray<float> latchTTL;    // 라치 잔여 시간

        // PBD 스냅샷
        NativeArray<float2> posSnap;

        // 공간 해시
        struct HashHeader { public int start, count; }
        NativeArray<HashHeader> grid;
        NativeArray<int> indices;
        int cellsX, cellsY;
        float cellSize;

        // 렌더링
        Matrix4x4[] matrices;
        Vector4[] colors;
        MaterialPropertyBlock mpb;

        float dt;
        uint tick;

        // 이벤트 큐(잡 → 메인)
        NativeQueue<int> eatenQueue;     // 먹힌 일반 세포 인덱스
        NativeQueue<int> latchQueue;     // 라치된 백혈구 인덱스
        NativeQueue<int> unlatchQueue;   // 라치 해제 인덱스

        // 플레이어 속도 추정(컨트롤러가 Vel 제공하지 않을 때 사용)
        float2 prevPlayerPos;
        float2 estPlayerVel;

        [Header("Solver")]
        [Range(0, 8)] public int solveIterations = 2;

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

            state = new NativeArray<byte>(agentCount, Allocator.Persistent);
            latchOffset = new NativeArray<float2>(agentCount, Allocator.Persistent);
            latchTTL = new NativeArray<float>(agentCount, Allocator.Persistent);

            posSnap = new NativeArray<float2>(agentCount, Allocator.Persistent);

            eatenQueue = new NativeQueue<int>(Allocator.Persistent);
            latchQueue = new NativeQueue<int>(Allocator.Persistent);
            unlatchQueue = new NativeQueue<int>(Allocator.Persistent);

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

                // 종 초기화: 0=일반(80%), 1=백혈구(20%) 예시
                species[i] = (byte)(rng.NextFloat() < 0.8f ? 0 : 1);
                state[i] = 0; // Alive
                latchOffset[i] = float2.zero;
                latchTTL[i] = 0f;
            }

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

            if (player != null)
            {
                player.worldSize = worldSize;
                player.wrapEdges = wrapEdges;
                prevPlayerPos = player.Pos;
            }
        }

        void OnDestroy()
        {
            if (posCur.IsCreated) posCur.Dispose();
            if (posNext.IsCreated) posNext.Dispose();
            if (velCur.IsCreated) velCur.Dispose();
            if (velNext.IsCreated) velNext.Dispose();
            if (species.IsCreated) species.Dispose();
            if (state.IsCreated) state.Dispose();
            if (latchOffset.IsCreated) latchOffset.Dispose();
            if (latchTTL.IsCreated) latchTTL.Dispose();
            if (grid.IsCreated) grid.Dispose();
            if (indices.IsCreated) indices.Dispose();
            if (posSnap.IsCreated) posSnap.Dispose();

            if (eatenQueue.IsCreated) eatenQueue.Dispose();
            if (latchQueue.IsCreated) latchQueue.Dispose();
            if (unlatchQueue.IsCreated) unlatchQueue.Dispose();
        }

        void Update()
        {
            // 플레이어 속도 추정(컨트롤러가 Vel 속성을 제공하지 않을 경우 대비)
            if (player != null)
            {
                estPlayerVel = (player.Pos - prevPlayerPos) / math.max(1e-6f, Time.deltaTime);
                prevPlayerPos = player.Pos;
            }

            for (int s = 0; s < math.max(1, substeps); s++)
                Step();

            // ===== 렌더링 =====
            int countInBatch = 0;
            for (int i = 0; i < agentCount; i++)
            {
                if (state[i] == 1) continue; // Dead skip

                Vector3 p = new Vector3(posCur[i].x, posCur[i].y, 0f);
                float d = radius * 2f;
                matrices[countInBatch] = Matrix4x4.TRS(p, Quaternion.identity, new Vector3(d, d, 1f));

                // 라치된 백혈구는 강조색
                Color c = (state[i] == 2 && species[i] == 1)
                    ? new Color(1f, 0.4f, 0.4f, 1f)
                    : colorBySpecies.Evaluate(species[i]);
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
            // 1) 공간 해시 (posCur 기준)
            new CountJobSingle { W = worldSize, CellSize = cellSize, CellsX = cellsX, CellsY = cellsY, PosCur = posCur, Grid = grid }.Run();
            new FillJobSingle { W = worldSize, CellSize = cellSize, CellsX = cellsX, CellsY = cellsY, PosCur = posCur, Grid = grid, Indices = indices }.Run();

            // 2) Force & Integrate
            new ForceIntegrateJob
            {
                // World
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

                // Grid
                CellsX = cellsX,
                CellsY = cellsY,
                CellSize = cellSize,
                Grid = grid,
                Indices = indices,

                // Buffers
                PosCur = posCur,
                VelCur = velCur,
                Species = species,
                State = state,
                PosNext = posNext,
                VelNext = velNext,

                LatchOffset = latchOffset,
                LatchTTL = latchTTL,

                // Player
                PlayerEnabled = (byte)(player != null && player.enablePlayer ? 1 : 0),
                PlayerPos = (player != null ? player.Pos : float2.zero),
                PlayerRadius = (player != null ? player.radius : 0f),
                PlayerForce = (playerAttract ? +playerForce : -playerForce),
                PlayerRange = playerRange,
                VelPlayer = (player != null ? (player.Vel) : estPlayerVel),

                // Gameplay
                playerEatRadius = playerEatRadius,
                wbcChaseForce = wbcChaseForce,
                wbcLatchRadius = wbcLatchRadius,
                wbcLatchSpring = wbcLatchSpring,
                shakeBreakSpeed = shakeBreakSpeed,

                // Events
                Eaten = eatenQueue.AsParallelWriter(),
                Latched = latchQueue.AsParallelWriter(),
                Unlatched = unlatchQueue.AsParallelWriter(),

                // Noise
                Tick = tick
            }.Schedule(agentCount, 128).Complete();

            // 2-1) 평균 속도 제거 (전역 순이동 제거)
            {
                float2 vAvg = float2.zero;
                for (int i = 0; i < agentCount; i++) vAvg += velNext[i];
                vAvg /= math.max(1, agentCount);
                for (int i = 0; i < agentCount; i++) velNext[i] -= vAvg;
            }

            // ===== PBD =====
            new CopyJob { Src = posNext, Dst = posSnap }.Schedule(agentCount, 128).Complete();

            new CountJobSingle { W = worldSize, CellSize = cellSize, CellsX = cellsX, CellsY = cellsY, PosCur = posSnap, Grid = grid }.Run();
            new FillJobSingle { W = worldSize, CellSize = cellSize, CellsX = cellsX, CellsY = cellsY, PosCur = posSnap, Grid = grid, Indices = indices }.Run();

            for (int it = 0; it < math.max(1, solveIterations); it++)
            {
                new ProjectNoOverlapJob
                {
                    W = worldSize,
                    radius = radius,
                    CellsX = cellsX,
                    CellsY = cellsY,
                    CellSize = cellSize,
                    Grid = grid,
                    Indices = indices,
                    PosRead = posSnap,
                    PosWrite = posNext
                }.Schedule(agentCount, 128).Complete();

                (posSnap, posNext) = (posNext, posSnap);
            }

            // 최종 확정
            (posCur, posSnap) = (posSnap, posCur);
            (velCur, velNext) = (velNext, velCur);

            // ===== 이벤트 처리 (메인 스레드) =====
            // 1) 먹기: 일반 세포 Dead 처리 + 플레이어 성장
            int eatenCount = 0;
            while (eatenQueue.TryDequeue(out int idxEat))
            {
                if (idxEat >= 0 && idxEat < agentCount && state[idxEat] == 0 && species[idxEat] == 0)
                {
                    state[idxEat] = 1; // Dead
                    eatenCount++;
                }
            }
            if (eatenCount > 0 && player != null)
            {
                player.radius = math.min(maxPlayerRadius, player.radius + growthPerEat * eatenCount);
            }

            // 2) 라치/언라치 처리
            int latchedNow = 0;
            while (latchQueue.TryDequeue(out int idxLa))
            {
                if (idxLa >= 0 && idxLa < agentCount && species[idxLa] == 1 && state[idxLa] == 0 && latchedNow < wbcMaxLatched)
                {
                    state[idxLa] = 2; // Latched
                    float angle = 2f * math.PI * (latchedNow / (float)math.max(1, wbcMaxLatched));
                    float attachR = (player != null ? player.radius : 0f) + radius * 1.2f;
                    latchOffset[idxLa] = new float2(math.cos(angle), math.sin(angle)) * attachR;
                    latchTTL[idxLa] = latchTTLSeconds;
                    latchedNow++;
                }
            }
            while (unlatchQueue.TryDequeue(out int idxUn))
            {
                if (idxUn >= 0 && idxUn < agentCount && state[idxUn] == 2)
                {
                    state[idxUn] = 0; // 자유
                }
            }

            // 3) 라치된 백혈구 → DPS/TTL (★ 체력 감소 라인 여기!)
            float dtLocal = dt;
            int latchedTotal = 0;
            for (int i = 0; i < agentCount; i++)
            {
                if (state[i] == 2 && species[i] == 1)
                {
                    if (player != null) player.health = math.max(0f, player.health - wbcDPS * dtLocal);

                    latchTTL[i] -= dtLocal;
                    if (latchTTL[i] <= 0f)
                    {
                        state[i] = 1; // 수명 끝 → 소멸
                    }
                    else latchedTotal++;
                }
            }

            tick++;
        }

        // ===== JOBS =====
        [BurstCompile]
        struct CountJobSingle : IJob
        {
            public float2 W; public float CellSize; public int CellsX, CellsY;
            [ReadOnly] public NativeArray<float2> PosCur;
            public NativeArray<HashHeader> Grid;

            public void Execute()
            {
                for (int c = 0; c < Grid.Length; c++) { var h = Grid[c]; h.count = 0; Grid[c] = h; }
                for (int i = 0; i < PosCur.Length; i++)
                {
                    int cid = CellOf(PosCur[i], W, CellSize, CellsX, CellsY);
                    var h = Grid[cid]; h.count++; Grid[cid] = h;
                }
            }
        }

        [BurstCompile]
        struct FillJobSingle : IJob
        {
            public float2 W; public float CellSize; public int CellsX, CellsY;
            [ReadOnly] public NativeArray<float2> PosCur;
            public NativeArray<HashHeader> Grid;
            public NativeArray<int> Indices;

            public void Execute()
            {
                int run = 0;
                for (int c = 0; c < Grid.Length; c++) { var h = Grid[c]; h.start = run; run += h.count; h.count = 0; Grid[c] = h; }
                for (int i = 0; i < PosCur.Length; i++)
                {
                    int cid = CellOf(PosCur[i], W, CellSize, CellsX, CellsY);
                    var h = Grid[cid]; int dst = h.start + h.count; Indices[dst] = i; h.count++; Grid[cid] = h;
                }
            }
        }

        [BurstCompile]
        struct ForceIntegrateJob : IJobParallelFor
        {
            // World
            public float2 W; public byte Wrap;
            public float dt, mass, radius, sameAttract, diffRepel, stiffRepel, viscosity, noise, maxSpeed;

            // Grid
            public int CellsX, CellsY; public float CellSize;
            [ReadOnly] public NativeArray<HashHeader> Grid;
            [ReadOnly] public NativeArray<int> Indices;

            // Buffers
            [ReadOnly] public NativeArray<float2> PosCur;
            [ReadOnly] public NativeArray<float2> VelCur;
            [ReadOnly] public NativeArray<byte> Species;
            [ReadOnly] public NativeArray<byte> State;
            [WriteOnly] public NativeArray<float2> PosNext;
            [WriteOnly] public NativeArray<float2> VelNext;
    
            public NativeArray<float2> LatchOffset;
            public NativeArray<float> LatchTTL;

            // Player
            public byte PlayerEnabled;
            public float2 PlayerPos;
            public float PlayerRadius;
            public float PlayerForce;
            public float PlayerRange;
            public float2 VelPlayer;

            // Gameplay
            public float playerEatRadius;
            public float wbcChaseForce;
            public float wbcLatchRadius;
            public float wbcLatchSpring;
            public float shakeBreakSpeed;

            // Events
            public NativeQueue<int>.ParallelWriter Eaten;
            public NativeQueue<int>.ParallelWriter Latched;
            public NativeQueue<int>.ParallelWriter Unlatched;

            // Noise
            public uint Tick;
            static uint Hash(uint x)
            {
                x ^= 2747636419u; x *= 2654435769u;
                x ^= x >> 16; x *= 2654435769u;
                x ^= x >> 16; x *= 2654435769u;
                return x;
            }
            static float U01(uint x) => (Hash(x) & 0x00FFFFFF) / (float)0x01000000;
            static float2 RandCircle(uint a, uint b)
            {
                float ang = 6.28318530718f * U01(a);
                float r = U01(b) - 0.5f;
                return new float2(math.cos(ang), math.sin(ang)) * r;
            }

            public void Execute(int i)
            {
                if (State[i] == 1) { PosNext[i] = PosCur[i]; VelNext[i] = float2.zero; return; } // Dead skip

                float2 p = PosCur[i];
                float2 v = VelCur[i];
                byte sp = Species[i];
                byte st = State[i];

                float2 f = float2.zero;

                int cx = CellCoord(p.x, W.x, CellSize, CellsX);
                int cy = CellCoord(p.y, W.y, CellSize, CellsY);

                // --- 이웃 힘 누적 (stiffRepel + same/diff)
                float2 sumSame = float2.zero; int cntSame = 0;
                float2 sumDiff = float2.zero; int cntDiff = 0;

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
                            if (State[j] == 1) continue; // Dead 무시

                            float2 q = PosCur[j];
                            byte sj = Species[j];

                            float2 d = q - p;
                            float dist = math.length(d) + 1e-6f;
                            float2 n = d / dist;
                            float target = radius * 2f;

                            // 침투 반발
                            float pen = target - dist;
                            if (pen > 0f) f -= n * (pen * stiffRepel);

                            // cohesion/separation 가중치
                            float deadCoh = target * 1.2f;
                            float maxCoh = target * 3f;
                            float wCoh = math.saturate((dist - deadCoh) / math.max(1e-6f, (maxCoh - deadCoh)));
                            float wSep = math.saturate((target - dist) / target);

                            if (sp == sj)
                            {
                                if (sameAttract > 0f) { sumSame += n * wCoh; if (wCoh > 0f) cntSame++; }
                            }
                            else
                            {
                                if (diffRepel > 0f) { sumDiff += n * wSep; if (wSep > 0f) cntDiff++; }
                            }
                        }
                    }

                // 같은 종 끌림
                if (cntSame > 0 && sameAttract > 0f)
                {
                    float2 coh = (sumSame / cntSame) * sameAttract;
                    float m = math.length(coh), cap = 0.6f;
                    if (m > cap) coh *= (cap / m);
                    f += coh;
                }
                // 다른 종 분리
                if (cntDiff > 0 && diffRepel > 0f)
                {
                    float2 sep = (sumDiff / cntDiff) * (-diffRepel);
                    float m = math.length(sep), cap = 0.8f;
                    if (m > cap) sep *= (cap / m);
                    f += sep;
                }

                // --- 플레이어 상호작용 ---
                if (PlayerEnabled == 1)
                {
                    float2 dp = PlayerPos - p;
                    float dist = math.length(dp) + 1e-6f;
                    float2 n = dp / dist;
                    float contact = PlayerRadius + radius;

                    // (옵션) 플레이어 주변 힘
                    if (dist <= PlayerRange)
                    {
                        float pen = contact - dist;
                        if (pen > 0f) f -= n * (pen * stiffRepel * 0.8f);

                        float denom = math.max(1e-5f, PlayerRange - contact);
                        float w = math.saturate((PlayerRange - dist) / denom);
                        f += n * (PlayerForce * w);
                    }

                    // 먹기: 일반 세포
                    if (sp == 0 && st == 0)
                    {
                        float eatR = PlayerRadius + radius + playerEatRadius;
                        if (dist <= eatR)
                        {
                            PosNext[i] = p; VelNext[i] = float2.zero;
                            Eaten.Enqueue(i);
                            return;
                        }
                    }

                    // 백혈구 로직
                    if (sp == 1)
                    {
                        // 추적
                        f += n * wbcChaseForce;

                        if (st == 2) // 라치 상태
                        {
                            float2 target = PlayerPos + LatchOffset[i];
                            float2 err = target - p;
                            f += err * wbcLatchSpring;

                            if (math.length(VelPlayer) >= shakeBreakSpeed)
                                Unlatched.Enqueue(i);
                        }
                        else // 라치 시도
                        {
                            if (dist <= (PlayerRadius + radius + wbcLatchRadius))
                                Latched.Enqueue(i);
                        }
                    }
                }

                // 점성
                f += -viscosity * v;

                // 등방성 노이즈
                uint s1 = (uint)i * 741103597u ^ Tick * 1597334677u;
                uint s2 = (uint)i * 312680891u ^ Tick * 747796405u;
                f += RandCircle(s1, s2) * noise;

                // 적분
                v += (f / math.max(1e-3f, mass)) * dt;
                float spd = math.length(v);
                if (spd > maxSpeed) v *= (maxSpeed / spd);
                p += v * dt;

                // 경계
                if (Wrap == 1)
                {
                    p.x = Wrap01(p.x, W.x);
                    p.y = Wrap01(p.y, W.y);
                }
                else
                {
                    if (p.x < -W.x * 0.5f + radius) { p.x = -W.x * 0.5f + radius; v.x *= -0.9f; }
                    if (p.x > W.x * 0.5f - radius) { p.x = W.x * 0.5f - radius; v.x *= -0.9f; }
                    if (p.y < -W.y * 0.5f + radius) { p.y = -W.y * 0.5f + radius; v.y *= -0.9f; }
                    if (p.y > W.y * 0.5f - radius) { p.y = W.y * 0.5f - radius; v.y *= -0.9f; }
                }

                PosNext[i] = p;
                VelNext[i] = v;
            }

            static float Wrap01(float x, float range)
            {
                float half = range * 0.5f;
                if (x < -half) x += range; else if (x > half) x -= range;
                return x;
            }
            static int CellCoord(float v, float range, float cell, int cells)
            {
                float half = range * 0.5f;
                int c = (int)math.floor((v + half) / cell);
                return math.clamp(c, 0, cells - 1);
            }
        }

        [BurstCompile]
        struct CopyJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> Src;
            public NativeArray<float2> Dst;
            public void Execute(int i) => Dst[i] = Src[i];
        }

        [BurstCompile]
        struct ProjectNoOverlapJob : IJobParallelFor
        {
            public float2 W;
            public float radius;
            public int CellsX, CellsY;
            public float CellSize;

            [ReadOnly] public NativeArray<HashHeader> Grid;
            [ReadOnly] public NativeArray<int> Indices;
            [ReadOnly] public NativeArray<float2> PosRead;
            public NativeArray<float2> PosWrite;

            public void Execute(int i)
            {
                float2 p = PosWrite[i];

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

                            float2 q = PosRead[j];
                            float2 d = p - q;
                            float dist = math.length(d) + 1e-6f;
                            float target = radius * 2f;
                            float pen = target - dist;
                            if (pen > 0f)
                            {
                                float2 n = d / dist;
                                p += n * (pen * 0.5f);
                            }
                        }
                    }

                p.x = math.clamp(p.x, -W.x * 0.5f + radius, W.x * 0.5f - radius);
                p.y = math.clamp(p.y, -W.y * 0.5f + radius, W.y * 0.5f - radius);
                PosWrite[i] = p;
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

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1, 1, 1, 0.2f);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(worldSize.x, worldSize.y, 0));
        }
    }
}
