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

        public PlayerController player;      // 인스펙터에 드래그
        public bool playerAttract = false;  // true=끌림 / false=밀침
        public float playerForce = 5f;

        // ▼ 추가: 플레이어 힘이 미치는 최대 거리(멀리선 0)
        public float playerRange = 1.0f;

        // ===== 내부 버퍼 =====
        NativeArray<float2> posCur, posNext;
        NativeArray<float2> velCur, velNext;
        NativeArray<byte> species;

        // ▼ 추가: PBD Jacobi용 스냅샷 버퍼(예측 위치 복사본)
        NativeArray<float2> posSnap;

        // ▼ 추가: PBD 전 "예측 위치" 스냅샷(velocity matching 용)
        NativeArray<float2> posPred;

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

        // ▼ 추가: PBD 반복 횟수(업계 관행: 2~4)
        [Header("Solver")]
        [Range(0, 8)] public int solveIterations = 2;

        // ▼ 추가: PBD 이후 속도를 보정된 위치변화로 재계산(점착 강도)
        [Range(0f, 1f)] public float stickiness = 1.0f; // 1=완전 점착, 0=기존 속도 유지

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

            // ▼ 추가: 스냅샷 버퍼
            posSnap = new NativeArray<float2>(agentCount, Allocator.Persistent);

            // ▼ 추가: 예측 위치 스냅샷 버퍼(velocity matching 용)
            posPred = new NativeArray<float2>(agentCount, Allocator.Persistent);

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

            if (player != null)
            {
                player.worldSize = worldSize;
                player.wrapEdges = wrapEdges;
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

            // ▼ 추가
            if (posSnap.IsCreated) posSnap.Dispose();

            // ▼ 추가
            if (posPred.IsCreated) posPred.Dispose();
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

            if (player != null && player.enablePlayer)
            {
                var P = new Vector3(player.Pos.x, player.Pos.y, 0f);
                float d = player.radius * 2f;
                var M = Matrix4x4.TRS(P, Quaternion.identity, new Vector3(d, d, 1f));

                var pb = new MaterialPropertyBlock();
                pb.SetColor("_Color", Color.white);
                Graphics.DrawMesh(quadMesh, M, instancedMat, 0, null, 0, pb,
                    ShadowCastingMode.Off, false, null, LightProbeUsage.Off, null);
            }
        }

        // ====== 시뮬 ======
        void Step()
        {
            // 1) 공간 해시(싱글 스레드 안정 버전) — 현재 위치(posCur) 기준
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

            // 2) Force & Integrate → 예측 위치/속도(PosNext/VelNext)에 기록
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
                Species = species,

                PlayerEnabled = (byte)(player != null && player.enablePlayer ? 1 : 0),
                PlayerPos = (player != null ? player.Pos : float2.zero),
                PlayerRadius = (player != null ? player.radius : 0f),
                PlayerForce = (playerAttract ? +playerForce : -playerForce),
                PlayerRange = playerRange,

            }.Schedule(agentCount, 128).Complete();

            // ▼ 추가: PBD 전 "예측 위치" 스냅샷 저장(velocity matching 기준)
            new CopyJob { Src = posNext, Dst = posPred }.Schedule(agentCount, 128).Complete();

            // ===== 겹침 보정(PBD) =====
            // 2.1) 예측 위치 스냅샷(posSnap = posNext)
            new CopyJob { Src = posNext, Dst = posSnap }.Schedule(agentCount, 128).Complete();

            // 2.2) 스냅샷(=이웃 참조용) 기준으로 다시 그리드 빌드
            new CountJobSingle
            {
                W = worldSize,
                CellSize = cellSize,
                CellsX = cellsX,
                CellsY = cellsY,
                PosCur = posSnap,   // ★ 스냅샷 기준
                Grid = grid
            }.Run();

            new FillJobSingle
            {
                W = worldSize,
                CellSize = cellSize,
                CellsX = cellsX,
                CellsY = cellsY,
                PosCur = posSnap,   // ★ 스냅샷 기준
                Grid = grid,
                Indices = indices
            }.Run();

            // 2.3) Jacobi 투영 반복: read=posSnap, write=posNext → 스왑
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

                    PosRead = posSnap,  // 이웃 참조용(읽기 전용 스냅샷)
                    PosWrite = posNext  // 내가 보정할 위치(쓰기)
                }.Schedule(agentCount, 128).Complete();

                // 다음 반복을 위해 스냅샷↔작성 버퍼 스왑
                (posSnap, posNext) = (posNext, posSnap);

                // 스냅샷 기준으로 이웃 업데이트가 더 필요하면 Count/Fill을 매 반복마다 갱신 가능.
                // 보통 1회 스냅샷으로도 충분. 필요 시 여기에서 Count/Fill 재호출.
            }

            // 반복이 끝나면, 최종 위치가 posSnap에 있음(마지막에 핑퐁했기 때문)
            // posCur = 최종 위치가 되도록 스왑
            (posCur, posSnap) = (posSnap, posCur);

            // 속도는 ForceIntegrate에서 계산된 VelNext를 채택
            (velCur, velNext) = (velNext, velCur);

            // ▼ 추가: PBD 보정 이후 속도를 '보정된 위치 변화'로 재계산(점착/velocity matching)
            new RecomputeVelJob
            {
                Prev = posPred,     // PBD 전(예측) 위치
                Curr = posCur,      // PBD 후(최종) 위치
                PrevVel = velCur,   // 현재 속도(여기에 덮어씀)
                dt = dt,
                Stickiness = stickiness
            }.Schedule(agentCount, 128).Complete();
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

            // ▼ 추가: 플레이어 파라미터
            public byte PlayerEnabled;   // 1/0
            public float2 PlayerPos;
            public float PlayerRadius;
            public float PlayerForce;     // +끌림 / -밀침
            public float PlayerRange;     // 최대 영향 범위

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

                // ▼ 플레이어 영향 (범위 기반 감쇠)
                if (PlayerEnabled == 1)
                {
                    float2 dp = PlayerPos - p;
                    float dist = math.length(dp) + 1e-6f;
                    float2 n = dp / dist;

                    float contact = PlayerRadius + radius;

                    // 0) 범위 밖이면 영향 없음
                    if (dist <= PlayerRange)
                    {
                        // 겹치면 충돌성 반발(살짝 완화)
                        float pen = contact - dist;
                        if (pen > 0f)
                            f -= n * (pen * stiffRepel * 0.8f);

                        // contact 지점에서 최대, PlayerRange에서 0
                        float denom = math.max(1e-5f, PlayerRange - contact);
                        float w = math.saturate((PlayerRange - dist) / denom);
                        // 곡선이 더 부드럽길 원하면 w = w*w;

                        f += n * (PlayerForce * w);
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

        // ▼ 추가: 예측 위치 스냅샷 복사
        [BurstCompile]
        struct CopyJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> Src;
            public NativeArray<float2> Dst;

            public void Execute(int i) => Dst[i] = Src[i];
        }

        // ▼ 추가: PBD 위치 투영(겹침 제거) — Jacobi: read(스냅샷), write(작업 버퍼)
        [BurstCompile]
        struct ProjectNoOverlapJob : IJobParallelFor
        {
            public float2 W;
            public float radius;
            public int CellsX, CellsY;
            public float CellSize;

            [ReadOnly] public NativeArray<HashHeader> Grid;
            [ReadOnly] public NativeArray<int> Indices;
            [ReadOnly] public NativeArray<float2> PosRead;  // 이웃 참조(스냅샷)
            public NativeArray<float2> PosWrite;            // 내 위치 보정

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

                            float2 q = PosRead[j]; // 이웃은 스냅샷(읽기전용)
                            float2 d = p - q;
                            float dist = math.length(d) + 1e-6f;
                            float target = radius * 2f;

                            float pen = target - dist;
                            if (pen > 0f)
                            {
                                float2 n = d / dist;
                                // 양쪽 반씩 대신, 내 쪽만 0.5 비율로 이동(안정적)
                                p += n * (pen * 0.5f);
                            }
                        }
                    }

                // 경계 보정(랩 대신 클램프; 랩 쓰려면 Wrap01 사용)
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

        // ▼ 추가: PBD 이후 속도 재계산(velocity matching / 점착)
        [BurstCompile]
        struct RecomputeVelJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> Prev;   // PBD 전 위치(예측)
            [ReadOnly] public NativeArray<float2> Curr;   // PBD 후 최종 위치
            public NativeArray<float2> PrevVel;           // velCur 덮어쓰기
            public float dt;
            public float Stickiness; // 0..1

            public void Execute(int i)
            {
                float invDt = 1f / math.max(1e-6f, dt);
                float2 vFromProjection = (Curr[i] - Prev[i]) * invDt;

                // Stickiness=1이면 '완전 점착' — 보정으로 만들어진 속도를 100% 채택
                // Stickiness=0이면 기존 속도 유지
                PrevVel[i] = math.lerp(PrevVel[i], vFromProjection, Stickiness);
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
