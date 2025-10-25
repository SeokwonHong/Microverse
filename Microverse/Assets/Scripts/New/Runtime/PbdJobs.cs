using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Microverse.Scripts.Simulation.Runtime.Data;
using static Microverse.Scripts.Simulation.Runtime.Data.GridUtil;

namespace Microverse.Scripts.Simulation.Runtime.Jobs
{
    [BurstCompile]
    public struct CopyJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Src;
        public NativeArray<float2> Dst;
        public void Execute(int i) => Dst[i] = Src[i];
    }

    [BurstCompile]
    public struct ProjectNoOverlapJob : IJobParallelFor
    {
        public float2 W;
        public float radius;
        public int CellsX, CellsY;
        public float CellSize;
        public byte Wrap;

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

            if (Wrap == 1)
            {
                p.x = Wrap01(p.x, W.x);
                p.y = Wrap01(p.y, W.y);
            }
            else
            {
                p.x = math.clamp(p.x, -W.x * 0.5f + radius, W.x * 0.5f - radius);
                p.y = math.clamp(p.y, -W.y * 0.5f + radius, W.y * 0.5f - radius);
            }
            PosWrite[i] = p;
        }
    }
}
