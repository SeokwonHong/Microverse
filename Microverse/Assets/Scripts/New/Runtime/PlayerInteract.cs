using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Microverse.Scripts.Simulation.Runtime.Modules
{
    internal static class PlayerInteract
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryEat(float2 p, float2 playerPos, float eatR, out bool eaten)
        {
            eaten = (math.length(playerPos - p) <= eatR);
            return eaten;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 PlayerField(float2 p, float2 playerPos, float playerR, float playerRange, float stiff, float playerForce)
        {
            float2 dp = playerPos - p;
            float dist = math.length(dp) + 1e-6f;
            float2 n = dp / dist;
            float contact = playerR;
            float2 f = 0;

            if (dist <= playerRange)
            {
                float pen = contact - dist;
                if (pen > 0f) f += -n * (pen * stiff * 0.8f);
                float denom = math.max(1e-5f, playerRange - contact);
                float w = math.saturate((playerRange - dist) / denom);
                f += n * (playerForce * w);
            }
            return f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 LatchSpring(float2 p, float2 playerPos, float2 latchOffset, float k)
        {
            float2 target = playerPos + latchOffset;
            float2 err = target - p;
            return err * k;
        }
    }
}
