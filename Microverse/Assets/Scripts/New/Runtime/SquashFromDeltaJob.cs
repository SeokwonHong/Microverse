using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Microverse.Scripts.Simulation.Runtime.Jobs
{
    [BurstCompile]
    public struct SquashFromDeltaJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> posBefore; // 이전 프레임 확정 (B.posCur)
        [ReadOnly] public NativeArray<float2> posAfter;  // 이번 프레임 확정 (B.posSnap, 스왑 전)
        public NativeArray<float> squash;   // 0..1, 감쇠 누적
        public NativeArray<float2> squashN;  // 눌림 법선

        public float radius;   // 셀 반경
        public float decay;    // 0.90f
        public float gain;     // 0.50f

        public void Execute(int i)
        {
            float2 d = posAfter[i] - posBefore[i];
            float len = math.length(d);
            float amp = math.saturate(len / (radius + 1e-6f)); // 겹침 비율
            float2 n = (len > 1e-6f) ? d / len : squashN[i];

            float s = squash[i] * decay;     // 잔류
            s = math.lerp(s, amp, gain);     // 새 보정 반영

            squash[i] = s;
            squashN[i] = n;
        }
    }
}
