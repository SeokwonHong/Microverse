using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe; //  Restriction 해제
using Unity.Jobs;
using Unity.Mathematics;

namespace Microverse.Scripts.Simulation.Runtime.Jobs
{
    [BurstCompile]
    public struct AccumulateDentBinsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> before;
        [ReadOnly] public NativeArray<float2> after;

        [NativeDisableParallelForRestriction]  // i 슬라이스(8칸)만 접근
        public NativeArray<float> dentHist;    // len = agentCount * 8

        public float ampScale; // 보정 스케일 (ex: 1/radius)

        public void Execute(int i)
        {
            int baseIdx = i * 8;
            float2 d = after[i] - before[i];
            float len = math.length(d);
            if (len <= 1e-6f) return;

            float2 n = d / len;
            float ang = math.atan2(n.y, n.x);         // -pi..pi
            if (ang < 0) ang += 2f * math.PI;         // 0..2pi
            int bin = (int)math.floor(ang * (8f / (2f * math.PI)));
            bin = math.clamp(bin, 0, 7);

            dentHist[baseIdx + bin] += len * ampScale;
        }
    }
}
