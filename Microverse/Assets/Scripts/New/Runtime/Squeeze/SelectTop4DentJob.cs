using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Microverse.Scripts.Simulation.Runtime.Jobs
{
    [BurstCompile]
    public struct SelectTop4DentJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> dentHist; // len = agentCount * 8

        public NativeArray<float2> dir0, dir1, dir2, dir3;
        public NativeArray<float> amp0, amp1, amp2, amp3;

        public float ampMax; // ¿¹: 0.45f

        public void Execute(int i)
        {
            int baseIdx = i * 8;

            float v0 = -1e9f, v1 = -1e9f, v2 = -1e9f, v3 = -1e9f;
            int k0 = -1, k1 = -1, k2 = -1, k3 = -1;

            for (int k = 0; k < 8; k++)
            {
                float v = dentHist[baseIdx + k];

                if (v > v0) { v3 = v2; k3 = k2; v2 = v1; k2 = k1; v1 = v0; k1 = k0; v0 = v; k0 = k; }
                else if (v > v1) { v3 = v2; k3 = k2; v2 = v1; k2 = k1; v1 = v; k1 = k; }
                else if (v > v2) { v3 = v2; k3 = k2; v2 = v; k2 = k; }
                else if (v > v3) { v3 = v; k3 = k; }
            }

            float2 d0 = 0, d1 = 0, d2 = 0, d3 = 0;
            if (k0 >= 0) { float a = (2f * math.PI / 8f) * k0; d0 = new float2(math.cos(a), math.sin(a)); }
            if (k1 >= 0) { float a = (2f * math.PI / 8f) * k1; d1 = new float2(math.cos(a), math.sin(a)); }
            if (k2 >= 0) { float a = (2f * math.PI / 8f) * k2; d2 = new float2(math.cos(a), math.sin(a)); }
            if (k3 >= 0) { float a = (2f * math.PI / 8f) * k3; d3 = new float2(math.cos(a), math.sin(a)); }

            float a0 = math.min(math.max(v0, 0f), ampMax);
            float a1 = math.min(math.max(v1, 0f), ampMax);
            float a2 = math.min(math.max(v2, 0f), ampMax);
            float a3 = math.min(math.max(v3, 0f), ampMax);

            dir0[i] = d0; amp0[i] = a0;
            dir1[i] = d1; amp1[i] = a1;
            dir2[i] = d2; amp2[i] = a2;
            dir3[i] = d3; amp3[i] = a3;
        }
    }
}
