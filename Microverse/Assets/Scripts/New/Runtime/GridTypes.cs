//Array 에 각 셀에 세포들의 개수들을 차례대로 넣음.



using Unity.Collections;
using Unity.Mathematics;

namespace Microverse.Scripts.Simulation.Runtime.Data
{
    // 공간 해시 헤더
    public struct HashHeader { public int start, count; }

    // 그리드 묶음
    public struct GridData
    {
        public NativeArray<HashHeader> grid;
        public NativeArray<int> indices;
        public int cellsX, cellsY;
        public float cellSize;
        public float2 worldSize;

        public void Dispose()
        {
            if (grid.IsCreated) grid.Dispose();
            if (indices.IsCreated) indices.Dispose();
        }
    }

    public static class GridUtil
    {
        public static int CellCoord(float v, float range, float cell, int cells)
        {
            float half = range * 0.5f;
            int c = (int)math.floor((v + half) / cell);
            return math.clamp(c, 0, cells - 1);
        }

        public static int CellOf(float2 p, float2 W, float cell, int cx, int cy)
        {
            float2 half = W * 0.5f;
            int ix = math.clamp((int)math.floor((p.x + half.x) / cell), 0, cx - 1);
            int iy = math.clamp((int)math.floor((p.y + half.y) / cell), 0, cy - 1);
            return iy * cx + ix;
        }

        public static float Wrap01(float x, float range)
        {
            float half = range * 0.5f;
            if (x < -half) x += range;
            else if (x > half) x -= range;
            return x;
        }
    }
}
