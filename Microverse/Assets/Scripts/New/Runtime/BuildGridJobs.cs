using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Microverse.Scripts.Simulation.Runtime.Data;
using static Microverse.Scripts.Simulation.Runtime.Data.GridUtil;

namespace Microverse.Scripts.Simulation.Runtime.Jobs
{
    [BurstCompile]
    public struct CountJobSingle : IJob
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
    public struct FillJobSingle : IJob
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
}
