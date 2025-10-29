using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Microverse.Scripts.Simulation.Runtime.Data;
using Microverse.Scripts.Simulation.Runtime.Modules;
using static Microverse.Scripts.Simulation.Runtime.Data.GridUtil;

namespace Microverse.Scripts.Simulation.Runtime.Jobs
{
    [BurstCompile]
    public struct ForceIntegrateJob : IJobParallelFor
    {
        // Sim
        public SimParams P;

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

        // Events
        public NativeQueue<int>.ParallelWriter Eaten;
        public NativeQueue<int>.ParallelWriter Latched;
        public NativeQueue<int>.ParallelWriter Unlatched;

        public void Execute(int i)
        {
            if (State[i] == 1) { PosNext[i] = PosCur[i]; VelNext[i] = 0; return; }

            float2 p = PosCur[i];
            float2 v = VelCur[i];
            byte sp = Species[i];
            byte st = State[i];
            float r = P.radius;

            float2 f = 0;

            // 이웃 스캔
            int cx = CellCoord(p.x, P.worldSize.x, CellSize, CellsX);
            int cy = CellCoord(p.y, P.worldSize.y, CellSize, CellsY);

            float2 sumSame = 0; int cntSame = 0;
            float2 sumDiff = 0; int cntDiff = 0;

            for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                {
                    int nx = cx + ox, ny = cy + oy;
                    if ((uint)nx >= (uint)CellsX || (uint)ny >= (uint)CellsY) continue;
                    var h = Grid[ny * CellsX + nx];

                    for (int k = 0; k < h.count; k++)
                    {
                        int j = Indices[h.start + k];
                        if (j == i || State[j] == 1) continue;

                        float2 q = PosCur[j];
                        byte sj = Species[j];

                        float2 d = q - p;
                        float dist = math.length(d) + 1e-6f;
                        float2 n = d / dist;
                        float target = r * 2f;

                        // 침투 반발
                        float pen = target - dist;
                        f += Forces.Repulsion(n, pen, P.stiffRepel);

                        // 보이드 계열 가중치
                        float deadCoh = target * 1.2f;
                        float maxCoh = target * 3f;
                        float wCoh = math.saturate((dist - deadCoh) / math.max(1e-6f, (maxCoh - deadCoh)));
                        float wSep = math.saturate((target - dist) / target);

                        if (sp == sj)
                        { if (P.sameAttract > 0f) { sumSame += n * wCoh; if (wCoh > 0f) cntSame++; } }
                        else
                        { if (P.diffRepel > 0f) { sumDiff += n * wSep; if (wSep > 0f) cntDiff++; } }
                    }
                }

            f += Forces.Cohesion(sumSame, cntSame, P.sameAttract, 0.6f);
            f += Forces.Separation(sumDiff, cntDiff, P.diffRepel, 0.8f);

            // 플레이어 상호작용
            if (P.playerEnabled == 1)
            {
                // 필드 힘
                f += PlayerInteract.PlayerField(p, P.playerPos, P.playerRadius, P.playerRange, P.stiffRepel, P.playerForce);

                // 먹기(일반세포)
                if (sp == 0 && st == 0)
                {
                    float eatR = P.playerRadius + r + P.playerEatRadius;
                    if (PlayerInteract.TryEat(p, P.playerPos, eatR, out bool eaten) && eaten)
                    {
                        PosNext[i] = p; VelNext[i] = 0;
                        Eaten.Enqueue(i);
                        return;
                    }
                }

                // 백혈구
                if (sp == 1 && P.wbcEnabled == 1)
                {
                    float2 dp = P.playerPos - p;
                    float dist = math.length(dp) + 1e-6f;
                    float2 n = dp / dist;

                    // 추적
                    f += n * P.wbcChaseForce;

                    if (st == 2) // 라치
                    {
                        f += PlayerInteract.LatchSpring(p, P.playerPos, LatchOffset[i], P.wbcLatchSpring);
                        if (math.length(P.playerVel) >= P.shakeBreakSpeed) Unlatched.Enqueue(i);
                    }
                    else // 라치 시도
                    {
                        if (dist <= (P.playerRadius + r + P.wbcLatchRadius))
                            Latched.Enqueue(i);
                    }
                }
            }

            // 점성/노이즈
            f += Forces.Viscosity(v, P.viscosity);
            uint s1 = (uint)i * 741103597u ^ P.tick * 1597334677u;
            uint s2 = (uint)i * 312680891u ^ P.tick * 747796405u;
            f += Forces.RandCircle(s1, s2) * P.noise;

            // 적분
            v += (f / math.max(1e-3f, P.mass)) * P.dt;
            float spd = math.length(v);
            if (spd > P.maxSpeed) v *= (P.maxSpeed / spd);
            p += v * P.dt;

            // 경계
            if (P.wrapEdges)
            {
                p.x = GridUtil.Wrap01(p.x, P.worldSize.x);
                p.y = GridUtil.Wrap01(p.y, P.worldSize.y);
            }
            else
            {
                float hx = P.worldSize.x * 0.5f, hy = P.worldSize.y * 0.5f;
                if (p.x < -hx + r) { p.x = -hx + r; v.x *= -0.9f; }
                if (p.x > hx - r) { p.x = hx - r; v.x *= -0.9f; }
                if (p.y < -hy + r) { p.y = -hy + r; v.y *= -0.9f; }
                if (p.y > hy - r) { p.y = hy - r; v.y *= -0.9f; }
            }

            PosNext[i] = p;
            VelNext[i] = v;
        }
    }
}
