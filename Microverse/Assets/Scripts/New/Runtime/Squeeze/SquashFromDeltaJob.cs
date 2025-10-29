using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Microverse.Scripts.Simulation.Runtime.Jobs
{
    [BurstCompile]
    public struct SquashFromDeltaJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> posBefore; // 이전 확정
        [ReadOnly] public NativeArray<float2> posAfter;  // 이번 확정(스왑 전)
        public NativeArray<float> squash;               // 0..1
        public NativeArray<float2> squashN;              // 방향(법선)
        public float radius;
        public float decay;  // 0.90~0.98 (복원 속도)
        public float gain;   // 0.5~1.2  (증폭)

        public void Execute(int i)
        {
            float2 d = posAfter[i] - posBefore[i];
            float len = math.length(d);
            float amp = math.saturate(len / (radius + 1e-6f));
            float2 n = (len > 1e-6f) ? (d / len) : squashN[i];

            float s = squash[i] * decay;
            s = math.lerp(s, amp, gain);

            squash[i] = s;
            squashN[i] = n;
        }
    }
}
