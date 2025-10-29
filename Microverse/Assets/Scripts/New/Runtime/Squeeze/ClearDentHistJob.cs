using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Microverse.Scripts.Simulation.Runtime.Jobs
{
    [BurstCompile]
    public struct ClearDentHistJob : IJobParallelFor
    {
        public NativeArray<float> Hist; // len = agentCount * 8
        public void Execute(int i) => Hist[i] = 0f;
    }
}
